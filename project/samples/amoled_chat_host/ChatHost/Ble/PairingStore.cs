using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Devices.Bluetooth;

namespace ChatHost.Ble;

/// <summary>
/// Persisted host state: which device is paired (selected) and which chat provider is the default.
/// Lives at %LOCALAPPDATA%\AmoledChatHost\state.json.
/// "Pairing" here is an application-level selection: NUS needs no OS-level bonding, the host simply
/// remembers the device address and reconnects to that one device only.
/// </summary>
public sealed class PairingStore
{
    public sealed class PairedDevice
    {
        public ulong Address { get; set; }
        public string Name { get; set; } = "";
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BluetoothAddressType AddressType { get; set; } = BluetoothAddressType.Public;
        public DateTime PairedAt { get; set; }
        [JsonIgnore] public string AddressHex => Address.ToString("X12");
    }

    public sealed class StateFile
    {
        public PairedDevice? Device { get; set; }
        public string? Provider { get; set; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private StateFile _state = new();

    public PairingStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmoledChatHost");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "state.json");
        try
        {
            if (File.Exists(_path))
                _state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path)) ?? new StateFile();
        }
        catch { _state = new StateFile(); }
    }

    public string FilePath => _path;
    public PairedDevice? Device { get { lock (_lock) return _state.Device; } }
    public string? Provider { get { lock (_lock) return _state.Provider; } }

    public void Pair(ulong address, string name, BluetoothAddressType addressType)
    {
        lock (_lock)
        {
            _state.Device = new PairedDevice { Address = address, Name = name, AddressType = addressType, PairedAt = DateTime.Now };
            Save();
        }
    }

    public void Unpair()
    {
        lock (_lock) { _state.Device = null; Save(); }
    }

    public void SetProvider(string? name)
    {
        lock (_lock) { _state.Provider = name; Save(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
