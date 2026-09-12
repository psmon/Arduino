using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.Extensions.Options;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ChatHost.Ble;

public sealed record ScanResult(ulong Address, string Name, short Rssi, BluetoothAddressType AddressType)
{
    public string AddressHex => Address.ToString("X12");
}

/// <summary>
/// BLE central for the Nordic UART Service exposed by the AMOLED firmware (hud_ble.cpp).
/// One connection at a time; writes go to RX (6E400002), notifications arrive on TX (6E400003).
/// Uses WinRT Windows.Devices.Bluetooth — no OS pairing/bonding is required for NUS.
/// </summary>
public sealed class BleLink : IDisposable
{
    public static readonly Guid NusService = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid NusRx      = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid NusTx      = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    private readonly ILogger<BleLink> _log;
    private readonly BleOptions _opt;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private BluetoothLEDevice? _dev;
    private GattSession? _session;
    private GattDeviceService? _svc;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _tx;
    private readonly StringBuilder _lineBuf = new();

    public bool IsConnected { get; private set; }
    public string DeviceName { get; private set; } = "";
    public ulong Address { get; private set; }
    public string AddressHex => Address.ToString("X12");
    public int MaxPdu => _session?.MaxPduSize ?? 23;
    public long Sent, Dropped, RxLines, RxFrames;
    public DateTime? ConnectedAt { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>A complete text line from the device (without the trailing newline).</summary>
    public event Action<string>? LineReceived;
    /// <summary>A raw binary audio frame from the device (starts with 0xA5).</summary>
    public event Action<byte[]>? FrameReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    public BleLink(IOptions<BleOptions> opt, ILogger<BleLink> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    // ---------------------------------------------------------------- scan

    public async Task<List<ScanResult>> ScanAsync(int seconds, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, ScanResult>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            lock (found)
            {
                if (found.TryGetValue(e.BluetoothAddress, out var prev) && string.IsNullOrEmpty(name))
                    name = prev.Name;                       // scan-response without a name: keep the earlier one
                found[e.BluetoothAddress] = new ScanResult(e.BluetoothAddress, name ?? "", e.RawSignalStrengthInDBm, e.BluetoothAddressType);
            }
        };
        watcher.Start();
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), ct); }
        finally { watcher.Stop(); }
        lock (found)
            return found.Values.Where(r => !string.IsNullOrEmpty(r.Name)).OrderByDescending(r => r.Rssi).ToList();
    }

    public async Task<ScanResult?> FindByNameAsync(string name, int seconds, CancellationToken ct = default)
    {
        var all = await ScanAsync(seconds, ct);
        return all.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------- connect

    /// <param name="addressType">From the advertisement (Public/Random). The wrong type fails with 0x80070016.</param>
    public async Task<bool> ConnectAsync(ulong address, string? name, BluetoothAddressType addressType = BluetoothAddressType.Public,
        CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        BluetoothLEDevice? dev = null;
        GattSession? session = null;
        GattDeviceService? svc = null;
        try
        {
            if (IsConnected && Address == address) return true;
            await DisconnectCoreAsync();

            _log.LogInformation("BLE connecting to {Addr} ({Name}, {Type})", address.ToString("X12"), name ?? "?", addressType);
            dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (dev is null) { Fail("device not reachable (FromBluetoothAddressAsync returned null)"); return false; }

            session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            var active = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> onStatus = (s, e) =>
            {
                _log.LogInformation("BLE session status: {Status} (error {Err})", e.Status, e.Error);
                if (e.Status == GattSessionStatus.Active) active.TrySetResult(true);
            };
            session.SessionStatusChanged += onStatus;
            session.MaintainConnection = true;
            // Like bleak: wait for the link to become Active before touching GATT, otherwise discovery
            // can race the connection and fail with 0x80070016 (ERROR_BAD_COMMAND).
            if (session.SessionStatus != GattSessionStatus.Active)
            {
                var done = await Task.WhenAny(active.Task, Task.Delay(TimeSpan.FromSeconds(12), ct));
                if (done != active.Task)
                    _log.LogWarning("BLE session did not become Active within 12 s (status={Status}); trying discovery anyway", session.SessionStatus);
            }
            session.SessionStatusChanged -= onStatus;
            _log.LogInformation("BLE session {Status}, mtu={Mtu}", session.SessionStatus, session.MaxPduSize);

            svc = await DiscoverNusAsync(dev, ct);
            if (svc is null) { Fail("NUS service not found"); return false; }

            var rxRes = await svc.GetCharacteristicsForUuidAsync(NusRx, BluetoothCacheMode.Uncached);
            var txRes = await svc.GetCharacteristicsForUuidAsync(NusTx, BluetoothCacheMode.Uncached);
            if (rxRes.Status != GattCommunicationStatus.Success || rxRes.Characteristics.Count == 0 ||
                txRes.Status != GattCommunicationStatus.Success || txRes.Characteristics.Count == 0)
            { Fail("NUS RX/TX characteristics not found"); return false; }

            var rx = rxRes.Characteristics[0];
            var tx = txRes.Characteristics[0];
            tx.ValueChanged += OnValueChanged;
            var cccd = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
                _log.LogWarning("Subscribe to TX notifications failed: {Status} (device→host traffic will not arrive)", cccd);

            dev.ConnectionStatusChanged += OnConnectionStatusChanged;

            _dev = dev; _session = session; _svc = svc; _rx = rx; _tx = tx;
            dev = null; session = null; svc = null;          // owned by the fields now
            Address = address;
            DeviceName = string.IsNullOrEmpty(_dev.Name) ? (name ?? "") : _dev.Name;
            IsConnected = true;
            ConnectedAt = DateTime.Now;
            LastError = "";
            _lineBuf.Clear();
            _log.LogInformation("BLE connected: {Name} [{Addr}] mtu={Mtu}", DeviceName, AddressHex, MaxPdu);
            Connected?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains("BleLink"))?.Trim();
            _log.LogWarning("BLE connect exception at: {Frame}", frame ?? ex.StackTrace);
            await DisconnectCoreAsync();
            return false;
        }
        finally
        {
            // A failed attempt must release everything, otherwise MaintainConnection keeps the link
            // alive, the device stops advertising and every later scan comes back empty.
            try { svc?.Dispose(); session?.Dispose(); dev?.Dispose(); } catch { }
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Service discovery with retries. On Windows the first GetGattServices* call right after the link comes up
    /// can fail with COMException 0x80070016 (ERROR_BAD_COMMAND) — the stack is still resolving the peer —
    /// and simply needs to be retried (Microsoft Q&A 2280559). Falls back from ForUuid/Uncached to a full
    /// Uncached and finally Cached enumeration.
    /// </summary>
    private async Task<GattDeviceService?> DiscoverNusAsync(BluetoothLEDevice dev, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            var mode = attempt <= 4 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            try
            {
                GattDeviceServicesResult res = attempt is 3 or 4   // full enumeration first: ForUuid fails with 0x80070016 here
                    ? await dev.GetGattServicesForUuidAsync(NusService, mode)
                    : await dev.GetGattServicesAsync(mode);
                if (res.Status == GattCommunicationStatus.Success)
                {
                    var hit = res.Services.FirstOrDefault(x => x.Uuid == NusService);
                    if (hit != null)
                    {
                        if (attempt > 1) _log.LogInformation("BLE service discovery ok on attempt {N} ({Mode})", attempt, mode);
                        foreach (var other in res.Services) if (!ReferenceEquals(other, hit)) other.Dispose();
                        return hit;
                    }
                    _log.LogWarning("BLE discovery attempt {N}: {Count} services, NUS not among them", attempt, res.Services.Count);
                }
                else
                    _log.LogWarning("BLE discovery attempt {N}: status {Status} (protocolError {Err})", attempt, res.Status, res.ProtocolError);
            }
            catch (Exception ex)
            {
                _log.LogWarning("BLE discovery attempt {N} ({Mode}): {Type} 0x{H:X8} {Msg}", attempt, mode, ex.GetType().Name, ex.HResult, ex.Message);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
        }
        return null;
    }

    private void Fail(string msg)
    {
        LastError = msg;
        _log.LogWarning("BLE connect failed: {Msg}", msg);
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _connectLock.Release(); }
    }

    private Task DisconnectCoreAsync()
    {
        var was = IsConnected;
        IsConnected = false;
        try
        {
            if (_tx != null) _tx.ValueChanged -= OnValueChanged;
            if (_dev != null) _dev.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _svc?.Dispose();
            _session?.Dispose();
            _dev?.Dispose();
        }
        catch (Exception ex) { _log.LogDebug(ex, "dispose"); }
        _tx = null; _rx = null; _svc = null; _session = null; _dev = null;
        if (was)
        {
            _log.LogInformation("BLE disconnected from {Name}", DeviceName);
            Disconnected?.Invoke();
        }
        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && IsConnected)
        {
            _log.LogWarning("BLE link dropped ({Name}); reconnect worker will retry", DeviceName);
            _ = DisconnectAsync();
        }
    }

    // ------------------------------------------------------------- receive

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] data;
        try { data = args.CharacteristicValue.ToArray(); }
        catch { return; }
        if (data.Length == 0) return;

        if (NusFrames.IsAudioFrame(data))
        {
            Interlocked.Increment(ref RxFrames);
            FrameReceived?.Invoke(data);
            return;
        }

        string text;
        lock (_lineBuf)
        {
            _lineBuf.Append(Encoding.UTF8.GetString(data));
            if (_lineBuf.Length > 16384) _lineBuf.Clear();
            text = _lineBuf.ToString();
        }
        int nl;
        while ((nl = text.IndexOf('\n')) >= 0)
        {
            var line = text[..nl].TrimEnd('\r');
            text = text[(nl + 1)..];
            lock (_lineBuf) { _lineBuf.Clear(); _lineBuf.Append(text); }
            if (line.Length == 0) continue;
            Interlocked.Increment(ref RxLines);
            try { LineReceived?.Invoke(line); }
            catch (Exception ex) { _log.LogError(ex, "line handler"); }
        }
    }

    // ---------------------------------------------------------------- send

    /// <summary>Write one text line (newline appended) to NUS RX, chunked to the negotiated MTU.</summary>
    public async Task<bool> SendLineAsync(string line, CancellationToken ct = default)
    {
        if (!line.EndsWith('\n')) line += "\n";
        return await SendRawAsync(Encoding.UTF8.GetBytes(line), ct);
    }

    public async Task<bool> SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        var rx = _rx;
        if (!IsConnected || rx is null)
        {
            Interlocked.Increment(ref Dropped);
            return false;
        }
        int chunk = Math.Clamp(MaxPdu - 3, 20, 509);
        await _writeLock.WaitAsync(ct);
        try
        {
            for (int off = 0; off < data.Length; off += chunk)
            {
                int n = Math.Min(chunk, data.Length - off);
                var buf = new byte[n];
                System.Buffer.BlockCopy(data, off, buf, 0, n);
                var st = await rx.WriteValueAsync(buf.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                if (st != GattCommunicationStatus.Success)
                {
                    _log.LogWarning("BLE write failed: {Status}", st);
                    Interlocked.Increment(ref Dropped);
                    return false;
                }
            }
            Interlocked.Increment(ref Sent);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("BLE write exception: {Msg}", ex.Message);
            Interlocked.Increment(ref Dropped);
            return false;
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose() => DisconnectCoreAsync().GetAwaiter().GetResult();
}

/// <summary>
/// Keeps the link up: connects to the paired device (or, when nothing is paired, to the first device
/// advertising Ble:DeviceName) and retries after drops.
/// </summary>
public sealed class BleConnectionWorker(BleLink link, PairingStore store, IOptions<BleOptions> opt, ILogger<BleConnectionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = opt.Value;
        if (!o.AutoConnect) { log.LogInformation("Ble:AutoConnect=false — connect from the web UI"); return; }
        await Task.Delay(500, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!link.IsConnected)
                {
                    var paired = store.Device;
                    if (paired != null)
                    {
                        await link.ConnectAsync(paired.Address, paired.Name, paired.AddressType, ct);
                    }
                    else
                    {
                        var hit = await link.FindByNameAsync(o.DeviceName, o.ScanSeconds, ct);
                        if (hit != null) await link.ConnectAsync(hit.Address, hit.Name, hit.AddressType, ct);
                        else log.LogInformation("No device named '{Name}' in range; pair one from the web UI or power the device", o.DeviceName);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning("connect loop: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, o.ReconnectSeconds)), ct);
        }
    }
}
