using Akka.Actor;
using Akka.Configuration;
using AkkaHost.Actors;
using AkkaHost.Ble;
using AkkaHost.Chat;
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

        var config = HostConfig.Load(settings);
        var provider = Arg(args, "--provider");
        if (provider != null && !config.TrySetDefaultProvider(provider))
        {
            Console.Error.WriteLine($"unknown provider '{provider}'. known: {string.Join(", ", config.Providers.Keys)}");
            return 2;
        }

        using var voice = new VoiceSynth(config.Voice,
            (level, message) => Console.WriteLine($"[voice/{level}] {message}"));

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
            var speech = voice.Synthesize(speakText, 0);
            File.WriteAllBytes(outPath, DeviceAudio.ToWav(speech.Pcm16));
            Console.WriteLine($"wrote {outPath}: {speech.DurationMs} ms, {speech.Frames.Count} ADPCM frames " +
                              $"at {speech.Rate} Hz");
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
        var chat = system.ActorOf(AotProps.Of(() => new ChatActor(config, voice, announce)), "chat");

        Console.WriteLine($"AkkaHost up as akka.tcp://{sysName}@{advertise}:{port}");
        Console.WriteLine($"  /user/ask    echo actor (protocol smoke test)");
        Console.WriteLine($"  /user/chat   conversation actor, shared by AskBot and Chat");
        Console.WriteLine($"providers: {string.Join(", ", config.Providers.Keys)} (default: {config.DefaultProvider})");
        Console.WriteLine($"voice: {voice.Status}");
        if (announce != null) Console.WriteLine($"announce on connect: {announce}");

        BleLink? link = null;
        BleTunnel? tunnel = null;
        if (!noBle)
        {
            link = new BleLink((level, message) => Console.WriteLine($"[ble/{level}] {message}"));
            tunnel = new BleTunnel(link, advertise == "0.0.0.0" ? "127.0.0.1" : advertise, port,
                (level, message) => Console.WriteLine($"[tunnel/{level}] {message}"));

            var proxy = system.ActorOf(AotProps.Of(() => new BleChatProxy(link, chat)), "ble-chat");
            link.LineReceived += line => proxy.Tell(new BleChatProxy.Line(line));
            link.Connected += () => proxy.Tell(new BleChatProxy.Greet());

            _ = Task.Run(() => KeepLinkUpAsync(link, deviceName));
            Console.WriteLine($"BLE: keeping a link to '{deviceName}' (AskBot tunnel + Chat protocol)");
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
