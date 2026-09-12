using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatHost;
using ChatHost.Ble;
using ChatHost.Chat;
using ChatHost.Conversation;
using ChatHost.Logging;
using ChatHost.Stt;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue<int>("Host:Port", 8765);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var ring = new RingLog();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddProvider(new RingLoggerProvider(ring));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.Configure<HttpHostOptions>(builder.Configuration.GetSection("Host"));
builder.Services.Configure<BleOptions>(builder.Configuration.GetSection("Ble"));
builder.Services.Configure<SttOptions>(builder.Configuration.GetSection("Stt"));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));

builder.Services.AddSingleton(ring);
builder.Services.AddSingleton<PairingStore>();
builder.Services.AddSingleton<BleLink>();
builder.Services.AddSingleton<ChatProviderRegistry>();
builder.Services.AddSingleton<WhisperLocalStt>();
builder.Services.AddSingleton<WindowsTts>();
builder.Services.AddSingleton<ISpeechToText>(sp => sp.GetRequiredService<WhisperLocalStt>());
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddHostedService<BleConnectionWorker>();
builder.Services.AddHostedService<SttPreloadWorker>();

var app = builder.Build();
var jsonOpt = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };

// ConversationService subscribes to BLE events in its constructor — force creation before the link connects.
app.Services.GetRequiredService<ConversationService>();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- status / log

app.MapGet("/health", (BleLink link) => Results.Json(new
{
    ble = link.IsConnected, sent = link.Sent, dropped = link.Dropped, device = link.DeviceName,
}));

app.MapGet("/api/status", (BleLink link, PairingStore store, ChatProviderRegistry chat, WhisperLocalStt stt, WindowsTts tts, ConversationService conv) =>
    Results.Json(new
    {
        host = Environment.MachineName,
        port,
        ble = new
        {
            connected = link.IsConnected, device = link.DeviceName, address = link.IsConnected ? link.AddressHex : null,
            mtu = link.MaxPdu, sent = link.Sent, dropped = link.Dropped, rxLines = link.RxLines, rxFrames = link.RxFrames,
            since = link.ConnectedAt, lastError = link.LastError,
        },
        paired = store.Device is null ? null : new { address = store.Device.AddressHex, name = store.Device.Name, addressType = store.Device.AddressType.ToString(), at = store.Device.PairedAt },
        stateFile = store.FilePath,
        chat = new { provider = chat.DefaultName, providers = chat.All.Select(p => p.Name) },
        stt = new { provider = stt.ProviderName, ready = stt.IsReady, status = stt.Status, modelPath = stt.ModelPath, downloaded = stt.IsModelDownloaded() },
        tts = new { available = tts.Available, voices = tts.Voices },
        conversation = new { busy = conv.IsBusy, busyId = conv.BusyId, handled = conv.Handled, errors = conv.Errors,
                             preempted = conv.Preempted, session = conv.DeviceSession, capture = conv.CaptureState,
                             lastTranscript = conv.LastTranscript, lastReply = conv.LastReply, device = conv.LastDeviceInfo },
    }, jsonOpt));

app.MapGet("/api/log", (int? n) => Results.Json(ring.Tail(n is > 0 and <= 400 ? n.Value : 120)));

// ---------------------------------------------------------------- BLE / pairing

app.MapGet("/api/ble/scan", async (BleLink link, int? seconds, CancellationToken ct) =>
{
    var list = await link.ScanAsync(seconds ?? 5, ct);
    return Results.Json(list.Select(r => new { address = r.AddressHex, name = r.Name, rssi = r.Rssi, addressType = r.AddressType.ToString() }));
});

app.MapPost("/api/ble/pair", async (PairRequest req, BleLink link, PairingStore store, CancellationToken ct) =>
{
    if (!TryParseAddress(req.Address, out var addr)) return Results.BadRequest(new { error = "address must be 12 hex digits" });
    var type = string.Equals(req.AddressType, "Random", StringComparison.OrdinalIgnoreCase)
        ? Windows.Devices.Bluetooth.BluetoothAddressType.Random : Windows.Devices.Bluetooth.BluetoothAddressType.Public;
    store.Pair(addr, req.Name ?? "", type);
    var ok = await link.ConnectAsync(addr, req.Name, type, ct);
    return Results.Json(new { paired = true, connected = ok, error = ok ? null : link.LastError });
});

app.MapDelete("/api/ble/pair", async (PairingStore store, BleLink link) =>
{
    store.Unpair();
    await link.DisconnectAsync();
    return Results.Json(new { paired = false });
});

app.MapPost("/api/ble/connect", async (BleLink link, PairingStore store, CancellationToken ct) =>
{
    var d = store.Device;
    if (d is null) return Results.BadRequest(new { error = "nothing paired" });
    var ok = await link.ConnectAsync(d.Address, d.Name, d.AddressType, ct);
    return Results.Json(new { connected = ok, error = ok ? null : link.LastError });
});

app.MapPost("/api/ble/disconnect", async (BleLink link) => { await link.DisconnectAsync(); return Results.Json(new { connected = false }); });

app.MapPost("/api/ble/send", async (LineRequest req, BleLink link) =>
    Results.Json(new { ok = await link.SendLineAsync(req.Line ?? "") }));

// ---------------------------------------------------------------- chat providers

app.MapGet("/api/providers", (ChatProviderRegistry chat) =>
    Results.Json(chat.All.Select(p => new { name = p.Name, description = p.Description, isDefault = p.Name.Equals(chat.DefaultName, StringComparison.OrdinalIgnoreCase) })));

app.MapPut("/api/providers/default", (NameRequest req, ChatProviderRegistry chat) =>
    chat.SetDefault(req.Name ?? "") ? Results.Json(new { provider = chat.DefaultName }) : Results.NotFound(new { error = "unknown provider" }));

app.MapPost("/api/chat", async (ChatRequest req, ConversationService conv, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "text required" });
    try
    {
        var r = await conv.AskAsync(req.Text, req.Provider, req.Session ?? "amoled-web", ct);
        bool sent = false;
        if (req.ToDevice == true) sent = await conv.SendReplyAsync(0, r.Response);
        return Results.Json(new { provider = r.Provider, response = r.Response, ms = r.ElapsedMs, sentToDevice = sent });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// ---------------------------------------------------------------- STT

app.MapPost("/api/stt", async (HttpRequest http, ConversationService conv, CancellationToken ct) =>
{
    var (pcm, err) = await ReadAudioAsync(http, ct);
    if (pcm is null) return Results.BadRequest(new { error = err });
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var text = await conv.TranscribeAsync(pcm, http.Query["lang"], ct);
        return Results.Json(new { text, ms = sw.ElapsedMilliseconds, seconds = pcm.Length / 32000.0 });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/voice-chat", async (HttpRequest http, ConversationService conv, CancellationToken ct) =>
{
    var (pcm, err) = await ReadAudioAsync(http, ct);
    if (pcm is null) return Results.BadRequest(new { error = err });
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var text = await conv.TranscribeAsync(pcm, http.Query["lang"], ct);
        long sttMs = sw.ElapsedMilliseconds;
        if (string.IsNullOrWhiteSpace(text)) return Results.Json(new { transcript = "", response = "", sttMs, error = "no speech recognized" });
        var r = await conv.AskAsync(text, http.Query["provider"], http.Query["session"].FirstOrDefault() ?? "amoled-web", ct);
        bool sent = false;
        if (http.Query["toDevice"] == "1") sent = await conv.SendReplyAsync(0, r.Response);
        return Results.Json(new { transcript = text, response = r.Response, provider = r.Provider, sttMs, chatMs = r.ElapsedMs, sentToDevice = sent });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/stt/preload", async (ISpeechToText stt, CancellationToken ct) =>
    Results.Json(new { ready = await stt.EnsureReadyAsync(null, ct), status = stt.Status }));

// Codec self-test: 1 s sine → IMA ADPCM blocks → decode → SNR. Reference for the firmware encoder.
app.MapGet("/api/selftest/adpcm", () =>
{
    const int n = 16_000;
    var src = new short[n];
    for (int i = 0; i < n; i++) src[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 12000);
    int pred = 0, idx = 0;
    using var dec = new MemoryStream();
    int blocks = 0;
    for (int off = 0; off < n; off += 480)
    {
        var blk = ImaAdpcm.EncodeBlock(src.AsSpan(off, Math.Min(480, n - off)), ref pred, ref idx);
        ImaAdpcm.DecodeBlock(blk, dec);
        blocks++;
    }
    var outPcm = dec.ToArray();
    double sig = 0, noise = 0;
    for (int i = 0; i < n; i++)
    {
        double a = src[i], b = (short)(outPcm[i * 2] | (outPcm[i * 2 + 1] << 8));
        sig += a * a; noise += (a - b) * (a - b);
    }
    return Results.Json(new { blocks, bytesIn = n * 2, bytesOut = blocks * 4 + n / 2, snrDb = 10 * Math.Log10(sig / Math.Max(noise, 1)) });
});

// ---------------------------------------------------------------- device remote control ("C" lines)

// Ask the device to record from its own microphone for N ms and run the full voice pipeline.
// Same path the on-screen hold-to-talk button takes - useful to test without touching the device.
app.MapPost("/api/device/talk", async (BleLink link, int? ms) =>
{
    int d = Math.Clamp(ms ?? 3000, 300, 60000);
    var ok = await link.SendLineAsync($"C {{\"cmd\":\"talk\",\"ms\":{d}}}");
    return ok ? Results.Json(new { ok = true, ms = d }) : Results.Json(new { ok = false, error = "BLE not connected" }, statusCode: 503);
});

// Make the device send a text prompt as if it had been typed on the device.
app.MapPost("/api/device/text", async (LineRequest req, BleLink link) =>
{
    if (string.IsNullOrWhiteSpace(req.Line)) return Results.BadRequest(new { error = "line = the prompt text" });
    var js = new JsonObject { ["cmd"] = "text", ["text"] = req.Line };
    var ok = await link.SendLineAsync("C " + js.ToJsonString(
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return ok ? Results.Json(new { ok = true }) : Results.Json(new { ok = false, error = "BLE not connected" }, statusCode: 503);
});

// Speak arbitrary text on the device speaker (no chat CLI involved).
app.MapPost("/api/device/speak", async (LineRequest req, ConversationService conv, HttpRequest http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Line)) return Results.BadRequest(new { error = "line = the text to speak" });
    try { await conv.SpeakAsync(req.Line, http.Query["lang"], ct); return Results.Json(new { ok = true }); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// Flip the device's answer mode remotely (same setting as the pill on its screen).
app.MapPost("/api/device/mode", async (BleLink link, bool? voice) =>
{
    var ok = await link.SendLineAsync($"C {{\"cmd\":\"mode\",\"voice\":{((voice ?? false) ? "true" : "false")}}}");
    return ok ? Results.Json(new { ok = true, voice = voice ?? false })
              : Results.Json(new { ok = false, error = "BLE not connected" }, statusCode: 503);
});

// Abandon the current CLI conversation and start a fresh one (loses that session's history).
app.MapPost("/api/session/reset", (ConversationService conv) =>
    Results.Json(new { session = conv.RotateSession("reset from the host") }));

// Ask the device to start a new conversation (same thing its "new chat" pill does).
app.MapPost("/api/device/newchat", async (BleLink link) =>
    Results.Json(new { ok = await link.SendLineAsync("C {\"cmd\":\"newchat\"}") }));

// Device audio settings, the same values the Settings app's sliders move.
app.MapPost("/api/device/volume", async (BleLink link, int v) =>
    Results.Json(new { ok = await link.SendLineAsync($"C {{\"cmd\":\"vol\",\"v\":{Math.Clamp(v, 0, 100)}}}") }));

app.MapPost("/api/device/micgain", async (BleLink link, int db) =>
    Results.Json(new { ok = await link.SendLineAsync($"C {{\"cmd\":\"gain\",\"db\":{Math.Clamp(db, 0, 60)}}}") }));

app.MapPost("/api/device/tone", async (BleLink link) =>
    Results.Json(new { ok = await link.SendLineAsync("C {\"cmd\":\"tone\"}") }));

// Stop whatever the host is doing for the device right now.
app.MapPost("/api/device/stop", (ConversationService conv) =>
    Results.Json(new { cancelled = conv.CancelCurrent("stopped from the host") }));

app.MapPost("/api/device/clear", async (BleLink link) =>
    Results.Json(new { ok = await link.SendLineAsync("C {\"cmd\":\"clear\"}") }));

// ---------------------------------------------------------------- HUD passthrough (old ble_bridge.py contract)

app.MapPost("/status", async (HttpRequest http, ConversationService conv) => await Passthrough(http, conv, 'S'));
app.MapPost("/event",  async (HttpRequest http, ConversationService conv) => await Passthrough(http, conv, 'E'));

app.Logger.LogInformation("AMOLED chat host listening on http://127.0.0.1:{Port}  (web UI: /  api: /api/status)", port);
app.Run();

// ================================================================= helpers

static async Task<IResult> Passthrough(HttpRequest http, ConversationService conv, char tag)
{
    using var sr = new StreamReader(http.Body, Encoding.UTF8);
    var body = (await sr.ReadToEndAsync()).Trim();
    if (body.Length == 0 || body[0] != '{') return Results.BadRequest(new { error = "JSON object body required" });
    var ok = await conv.SendHudAsync(tag, body);
    return ok ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, error = "BLE not connected (dropped)" }, statusCode: 503);
}

/// <summary>Accepts audio/wav (raw body), multipart "file", or raw PCM16 with ?raw=1&rate=16000&ch=1. Returns 16k mono PCM16.</summary>
static async Task<(byte[]? pcm, string? error)> ReadAudioAsync(HttpRequest http, CancellationToken ct)
{
    byte[] bytes;
    if (http.HasFormContentType)
    {
        var form = await http.ReadFormAsync(ct);
        var f = form.Files.FirstOrDefault();
        if (f is null) return (null, "multipart form needs a file");
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }
    else
    {
        using var ms = new MemoryStream();
        await http.Body.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }
    if (bytes.Length == 0) return (null, "empty body");
    try
    {
        if (http.Query["raw"] == "1")
        {
            int rate = int.TryParse(http.Query["rate"], out var r) ? r : 16_000;
            int ch = int.TryParse(http.Query["ch"], out var c) ? c : 1;
            return (AudioConvert.Pcm16To16kMono(bytes, rate, ch), null);
        }
        return (AudioConvert.WavTo16kMono(bytes), null);
    }
    catch (Exception ex) { return (null, "audio decode failed: " + ex.Message); }
}

static bool TryParseAddress(string? s, out ulong addr)
{
    addr = 0;
    if (string.IsNullOrWhiteSpace(s)) return false;
    var hex = s.Replace(":", "").Replace("-", "").Trim();
    return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out addr);
}

record PairRequest(string? Address, string? Name, string? AddressType);
record LineRequest(string? Line);
record NameRequest(string? Name);
record ChatRequest(string? Text, string? Provider, string? Session, bool? ToDevice);
