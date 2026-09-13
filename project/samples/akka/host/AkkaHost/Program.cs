using Akka.Actor;
using Akka.Configuration;
using AkkaHost.Actors;
using AkkaHost.Ble;
using AkkaHost.Chat;
using AkkaHost.Hud;
using AkkaHost.Voice;

namespace AkkaHost;

/// <summary>
/// One host for both watch apps.
///
/// The device has a single BLE link, so a single process owns it. AskBot rides it as an
/// Akka remoting peer (its PDUs tunnelled through <see cref="BleTunnel"/>), while the Chat
/// app rides it as line protocol that <see cref="BleChatProxy"/> turns into messages for the
/// same <see cref="ChatActor"/>. Both apps therefore share one conversation engine, one chat
/// CLI and one SuperTonic voice, and neither firmware app had to change.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var bind = Arg(args, "--bind") ?? "127.0.0.1";
        var advertise = Arg(args, "--host") ?? "127.0.0.1";
        var port = int.Parse(Arg(args, "--port") ?? "2552");
        var sysName = Arg(args, "--system") ?? "AskBot";
        var settings = Arg(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var deviceName = Arg(args, "--device") ?? "claude-hud";
        var noBle = HasFlag(args, "--no-ble");
        var hudPort = int.TryParse(Arg(args, "--hud-port"), out var hudValue) ? hudValue : 8765;
        var noHud = HasFlag(args, "--no-hud");

        var config = HostConfig.Load(settings);
        var provider = Arg(args, "--provider");
        if (provider != null && !config.TrySetDefaultProvider(provider))
        {
            Console.Error.WriteLine($"unknown provider '{provider}'. known: {string.Join(", ", config.Providers.Keys)}");
            return 2;
        }

        using var voice = new VoiceSynth(config.Voice,
            (level, message) => Console.WriteLine($"[voice/{level}] {message}"));
        using var stt = new Stt(config.Stt, (level, message) => Console.WriteLine($"[stt/{level}] {message}"));

        // Quick standalone check of the speech path: synthesize to a WAV and exit.
        //   AkkaHost.exe --speak "안녕하세요" --out out.wav
        var speakText = Arg(args, "--speak");
        if (speakText != null)
        {
            if (!voice.Available)
            {
                Console.Error.WriteLine($"voice unavailable: {voice.Status}");
                return 3;
            }
            var outPath = Arg(args, "--out") ?? "speak.wav";
            var speakVoice = Arg(args, "--voice");
            var speakLang = Arg(args, "--lang");
            var speech = voice.Synthesize(speakText, 0, speakVoice, speakLang);
            File.WriteAllBytes(outPath, DeviceAudio.ToWav(speech.Pcm16));
            Console.WriteLine($"wrote {outPath}: {speech.DurationMs} ms, {speech.Frames.Count} ADPCM frames " +
                              $"at {speech.Rate} Hz, voice {speakVoice ?? voice.VoiceId}, " +
                              $"lang {speakLang ?? voice.LanguageId}");
            return 0;
        }

        // Dumps exactly what the tokenizer sees. Korean is the case that needs checking: the
        // indexer has no composed Hangul at all (every syllable maps to -1) and expects the
        // NFKD-decomposed jamo instead.
        var tokenText = Arg(args, "--tokens");
        if (tokenText != null)
        {
            var pre = SuperTonicText.Preprocess(tokenText, Arg(args, "--lang") ?? "ko");
            var tokenizer = SuperTonicTokenizer.Load(voice.ModelDirectory);
            var tokens = tokenizer.Encode(pre);
            Console.WriteLine($"preprocessed ({pre.Length} chars): {pre}");
            Console.WriteLine($"codepoints: {string.Join(" ", pre.Select(c => $"U+{(int)c:04X}"))}");
            Console.WriteLine($"ids       : {string.Join(" ", tokens)}");
            Console.WriteLine($"unmapped  : {tokens.Count(t => t <= 0)} of {tokens.Length}");
            return 0;
        }

        // The other half of the speech check: transcribe a WAV. Pairing it with --speak says
        // whether what the watch plays is actually intelligible, instead of guessing from a
        // duration.
        //   AkkaHost.exe --speak "…" --out x.wav   then   AkkaHost.exe --hear x.wav
        var hearPath = Arg(args, "--hear");
        if (hearPath != null)
        {
            if (!stt.Available)
            {
                Console.Error.WriteLine($"stt unavailable: {stt.Status}");
                return 3;
            }
            var wav = File.ReadAllBytes(hearPath);
            // Skip the 44-byte canonical header; these are our own files.
            var pcm = wav.Length > 44 ? wav[44..] : wav;
            var heard = stt.TranscribeAsync(pcm, Arg(args, "--lang")).GetAwaiter().GetResult();
            Console.WriteLine($"heard: {(heard.Length == 0 ? "(nothing)" : heard)}");
            return 0;
        }

        // Classic remoting (akka.tcp://) on purpose: Artery exists on the 1.6 branch but ships
        // disabled and is not wire-compatible with the protocol the device's C++ client speaks.
        var hocon = ConfigurationFactory.ParseString($@"
            akka {{
              loglevel = INFO
              actor.provider = remote
              remote {{
                dot-netty.tcp {{
                  hostname = ""{bind}""
                  public-hostname = ""{advertise}""
                  port = {port}
                }}
              }}
            }}");

        using var system = ActorSystem.Create(sysName, hocon);

        // AotProps, not Props.Create<T>(): see AotProps.cs - the reflection path dies in a
        // Native AOT binary.
        var ask = system.ActorOf(AotProps.Of(() => new AskActor()), "ask");
        var announce = Arg(args, "--announce");
        var talkOnConnect = int.TryParse(Arg(args, "--talk"), out var talkValue) ? talkValue : 0;
        var chat = system.ActorOf(AotProps.Of(() => new ChatActor(config, voice, announce, stt, talkOnConnect)),
            "chat");
        stt.Preload();

        Console.WriteLine($"AkkaHost up as akka.tcp://{sysName}@{advertise}:{port}");
        Console.WriteLine($"  /user/ask    echo actor (protocol smoke test)");
        Console.WriteLine($"  /user/chat   conversation actor, shared by AskBot and Chat");
        Console.WriteLine($"providers: {string.Join(", ", config.Providers.Keys)} (default: {config.DefaultProvider})");
        Console.WriteLine($"voice out: {voice.Status}");
        Console.WriteLine($"  voices:  {string.Join(" ", voice.AvailableVoices)}");
        Console.WriteLine($"voice in:  {stt.Status}");
        if (announce != null) Console.WriteLine($"announce on connect: {announce}");

        BleLink? link = null;
        BleTunnel? tunnel = null;
        HudEndpoint? hud = null;
        if (!noBle)
        {
            link = new BleLink((level, message) => Console.WriteLine($"[ble/{level}] {message}"));
            tunnel = new BleTunnel(link, advertise == "0.0.0.0" ? "127.0.0.1" : advertise, port,
                (level, message) => Console.WriteLine($"[tunnel/{level}] {message}"));

            var proxy = system.ActorOf(AotProps.Of(() => new BleChatProxy(link, chat)), "ble-chat");
            link.LineReceived += line => proxy.Tell(new BleChatProxy.Line(line));
            // 0xA5 microphone frames belong to the Chat app; 0xAB tunnel chunks are the
            // BleTunnel's and are already claimed there.
            link.FrameReceived += frame =>
            {
                if (frame.Length > 0 && frame[0] == BleTags.MicFrame) proxy.Tell(new BleChatProxy.MicFrame(frame));
            };
            link.Connected += () => proxy.Tell(new BleChatProxy.Greet());

            // Any remote-control command, e.g.
            //   --cmd "{\"cmd\":\"mode\",\"voice\":true}" --cmd "{\"cmd\":\"text\",\"text\":\"hi\"}"
            var commands = args.Select((a, i) => (a, i)).Where(x => x.a == "--cmd" && x.i + 1 < args.Length)
                .Select(x => args[x.i + 1]).ToList();
            if (commands.Count > 0)
            {
                link.Connected += () => _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    foreach (var json in commands)
                    {
                        proxy.Tell(new BleChatProxy.Command(json));
                        await Task.Delay(800);
                    }
                });
            }

            // Test aid, and a real capability: the host can start an utterance itself.
            var talkMs = Arg(args, "--talk");
            if (talkMs != null && int.TryParse(talkMs, out var ms))
            {
                link.Connected += () => _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);   // let the greeting settle first
                    proxy.Tell(new BleChatProxy.Talk(ms));
                });
            }

            // The third app on the same link: Claude Code's statusLine and hooks post to the
            // local port they were installed against, and the HUD app renders the S/E lines.
            if (!noHud)
            {
                var hudActor = system.ActorOf(AotProps.Of(() => new HudActor(link)), "hud");
                hud = new HudEndpoint(hudActor, hudPort,
                    (level, message) => Console.WriteLine($"[hud/{level}] {message}"));
                if (!hud.Start())
                {
                    hud.Dispose();
                    hud = null;
                }
            }

            _ = Task.Run(() => KeepLinkUpAsync(link, deviceName));
            Console.WriteLine($"BLE: keeping a link to '{deviceName}' (AskBot tunnel + Chat protocol + Claude HUD)");
        }
        else
        {
            Console.WriteLine("BLE: disabled (--no-ble); only network peers can reach this host");
        }

        Console.WriteLine();

        if (Console.IsInputRedirected)
        {
            // Started as a background host: stay up until killed.
            Console.WriteLine("stdin is not a console - running headless until terminated.");
            system.WhenTerminated.GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine("Commands: <text> asks the echo actor, 'ble' prints link stats, empty line quits.");
            while (true)
            {
                Console.Write("host> ");
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line)) break;

                if (line.Trim() == "ble")
                {
                    Console.WriteLine(link is null
                        ? "  BLE disabled"
                        : $"  connected={link.IsConnected} {link.DeviceName} mtu={link.MaxPdu} " +
                          $"sent={link.Sent} dropped={link.Dropped} lines={link.RxLines} frames={link.RxFrames} " +
                          $"tunnel={(tunnel?.Open == true ? "open" : "closed")} " +
                          $"toAkka={tunnel?.ToAkka ?? 0} toDevice={tunnel?.ToDevice ?? 0}");
                    continue;
                }

                var reply = ask.Ask<string>(line, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Console.WriteLine($"  <- {reply}");
            }
        }

        hud?.Dispose();
        if (tunnel != null) tunnel.DisposeAsync().GetAwaiter().GetResult();
        link?.Dispose();
        system.Terminate().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// Connects to the watch and reconnects after drops. The device advertises only while
    /// nothing is connected, so a failed attempt must let go of the handles (BleLink does)
    /// or every later scan comes back empty.
    /// </summary>
    private static async Task KeepLinkUpAsync(BleLink link, string deviceName)
    {
        while (true)
        {
            try
            {
                if (!link.IsConnected)
                {
                    var hit = await link.FindByNameAsync(deviceName, 6);
                    if (hit != null) await link.ConnectAsync(hit.Address, hit.Name, hit.AddressType);
                    else Console.WriteLine($"[ble/info] no device named '{deviceName}' in range");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ble/warn] connect loop: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
