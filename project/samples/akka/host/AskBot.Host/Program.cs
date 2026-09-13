using Akka.Actor;
using Akka.Configuration;
using AskBot.Host.Actors;
using AskBot.Host.Chat;
using AskBot.Host.Voice;

namespace AskBot.Host;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var bind = Arg(args, "--bind") ?? "0.0.0.0";
        var advertise = Arg(args, "--host") ?? "127.0.0.1";
        var port = int.Parse(Arg(args, "--port") ?? "2552");
        var sysName = Arg(args, "--system") ?? "AskBot";
        var settings = Arg(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        var config = HostConfig.Load(settings);
        var provider = Arg(args, "--provider");
        if (provider != null && !config.TrySetDefaultProvider(provider))
        {
            Console.Error.WriteLine($"unknown provider '{provider}'. known: {string.Join(", ", config.Providers.Keys)}");
            return 2;
        }

        using var voice = new VoiceSynth(config.Voice,
            (level, message) => Console.WriteLine($"[voice/{level}] {message}"));

        // Quick standalone check of the speech path: synthesize to a WAV and exit, no
        // ActorSystem, no device.
        //   AskBot.Host.exe --speak "안녕하세요" out.wav
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

        // Everything here is classic remoting (akka.tcp://), on purpose: Artery exists on
        // the 1.6 branch but ships disabled and is not wire-compatible with the classic
        // protocol the C++ client speaks.
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

        // AotProps, not Props.Create<T>(): see AotProps.cs - the reflection path dies
        // in a Native AOT binary.
        var ask = system.ActorOf(AotProps.Of(() => new AskActor()), "ask");
        system.ActorOf(AotProps.Of(() => new ChatActor(config, voice)), "chat");

        Console.WriteLine("AskBot host up. Remote actor paths for clients:");
        Console.WriteLine($"  akka.tcp://{sysName}@{advertise}:{port}/user/ask    (echo actor, smoke test)");
        Console.WriteLine($"  akka.tcp://{sysName}@{advertise}:{port}/user/chat   (chat protocol)");
        Console.WriteLine($"providers: {string.Join(", ", config.Providers.Keys)} (default: {config.DefaultProvider})");
        Console.WriteLine($"voice: {voice.Status}");
        Console.WriteLine();

        if (Console.IsInputRedirected)
        {
            // Started as a background host (no console to read from): just stay up
            // until the process is killed, so the C++ client has something to talk to.
            Console.WriteLine("stdin is not a console - running headless until terminated.");
            system.WhenTerminated.GetAwaiter().GetResult();
            return 0;
        }

        Console.WriteLine("Type text to exercise the echo actor locally, or just leave it running");
        Console.WriteLine("for the device. Ctrl+C / empty line + Enter to quit.");

        while (true)
        {
            Console.Write("local> ");
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line)) break;

            var reply = ask.Ask<string>(line, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Console.WriteLine($"  <- {reply}");
        }

        system.Terminate().GetAwaiter().GetResult();
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
