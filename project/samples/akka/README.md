# akka — an ESP32-S3 as a peer in a .NET Akka actor system

The device joins a .NET 10 `ActorSystem` over Akka.NET classic remoting and takes
part as a **client actor**: it has an address, the host `Tell`s it, and it answers.
The question this sample exists to settle is whether the actor model is usable from
a small device at all.

```
[ESP32-S3-Touch-AMOLED-1.75C]                         [PC]
 AskBot app (LVGL, C++)                  host/  AskBot.Host.exe  (Native AOT, 26 MB, no runtime needed)
 client actor:                            .NET 10 + Akka 1.6
  akka.tcp://askbot-device@ip:2553          /user/chat  ChatActor  -> chat CLI (netclaw / claude / echo)
  /user/chat        ── TCP (WiFi) ──        /user/ask   AskActor   (echo, smoke test)
```

## Status

| | |
|---|---|
| Akka.Remote under Native AOT | **works**, with two workarounds (below) |
| C++ peer: associate, heartbeat, tell/ask | **verified** on the PC, both JIT and AOT hosts |
| Chat flow (hello / text / streamed reply / cancel / newsession) | **verified** end to end, offline `echo` provider |
| Device app `brookesia_app_askbot` | **compiles** into the firmware (3.7 MB of an 8 MB partition); not yet run on hardware |
| Voice (mic → STT, spoken answer) | **not implemented** — the Chat app still owns that, over BLE |

```powershell
pwsh -File project/samples/akka/run_test.ps1        # framework-dependent host
pwsh -File project/samples/akka/run_test.ps1 -Aot   # Native AOT host
```

Both layers run: `askbot_cli` for the raw protocol, `askbot_chat` for the
conversation flow. No board and no LLM required.

## Making Akka.NET work under Native AOT

Two things break, and the failures are not obvious from the analyzer warnings:

**1. HOCON loads types by name.** `akka.actor.provider = remote` becomes
`Type.GetType("Akka.Remote.RemoteActorRefProvider, Akka.Remote")`. Nothing
references it statically, so the trimmer drops it and the published binary dies at
startup:

```
Akka.Configuration.ConfigurationException: 'akka.actor.provider' is not a valid
type name : 'Akka.Remote.RemoteActorRefProvider, Akka.Remote'
```

Fix: root the assemblies (`AskBot.Host.csproj`) — `Akka`, `Akka.Remote`,
`Newtonsoft.Json`, `Google.Protobuf`, `DotNetty.*`. Cost: 3.6 MB → 26.3 MB.

**2. `Props.Create<T>()` is reflection.** Every overload in 1.6 — *including* the
`Props.Create(() => new T())` lambda form, which is decomposed into a
`NewExpression` plus arguments — ends at `ActivatorProducer`, i.e.
`Activator.CreateInstance`:

```
MissingMethodException: No parameterless constructor defined for type 'AskActor'
```

Fix: `Props.CreateBy(IIndirectActorProducer)` with a producer that closes over a
real `new` — see `AotProps.cs`. That is the one creation path with no reflection in
it.

With those two, remoting starts, associates, serializes and round-trips inside a
single 26 MB exe. Upstream's own AOT epic
([akka.net#7246](https://github.com/akkadotnet/akka.net/issues/7246)) is still open
against the 1.6 milestone, and its remaining items are what these workarounds paper
over. `PublishAot` is therefore opt-in: `-p:AskBotAot=true`.

Akka 1.6 itself is nightly-only — nuget.org's latest stable is 1.5.71 (2026-08-27),
so `host/nuget.config` adds the official feedz.io feed and the project pins
`1.6.0-beta20260912000155`.

## Why the actors run on the PC

.NET 10 Native AOT targets Windows / Linux / macOS / iOS / Android on x64, arm64,
arm and x86. There is no Xtensa backend and no bare-metal target, and the only
C#-on-ESP32 path — nanoFramework — is an IL interpreter with its own
non-netstandard class library that cannot load Akka.NET. So the device speaks the
remoting protocol from C++ instead, which turns out to be enough to be a peer.

One TCP connection is enough because `use-passive-connections` defaults to on: the
host reuses the connection the device opened for its own outbound traffic, so the
device needs no listening socket. Wire details, including the `tcp` vs `akka.tcp`
scheme trap, are in [PROTOCOL.md](PROTOCOL.md).

## Layout

```
host/                        .NET 10 + Akka 1.6 remoting host
  nuget.config               the Akka nightly feed
  AskBot.Host/
    AotProps.cs              AOT-safe actor creation
    Program.cs               ActorSystem, remoting, /user/ask + /user/chat
    Actors/AskActor.cs       echo actor (protocol smoke test)
    Actors/ChatActor.cs      the device-facing conversation actor
    Chat/CliProvider.cs      runs netclaw / claude / a script as a child process
    Chat/HostConfig.cs       appsettings.json via JsonDocument (AOT-safe)
    Chat/Json.cs             tiny writer + UTF-8-safe chunking
    appsettings.json         providers; default is the offline "echo"

cpp/                         the module: portable C++17, no ESP-IDF dependency
  include/akka/
    pb.h                     minimal protobuf writer/reader
    akka_wire.h              Akka PDUs (associate, heartbeat, envelope, ack)
    transport.h              IByteStream — the only platform seam
    remote_client.h          association, heartbeats, tell/ask, local actors
  tools/askbot_cli.cpp       raw protocol harness
  tools/askbot_chat.cpp      device simulator: the full chat flow from the PC
  tests/                     PDU codec round trips (ctest)
  build.ps1                  MSVC + Ninja

esp32/                       standalone ESP-IDF project (headless, no UI)
  components/akka_remote/    wraps ../../../cpp — no second copy of the protocol
  main/main.cpp              WiFi STA -> associate -> ask every 5s

../claude_hud_amoled/components/brookesia_app_askbot/   the device app
  askbot_core.cpp            WiFi + association + client actor + chat state
  askbot_app.cpp             LVGL UI, same layout language as the Chat app
  Kconfig.projbuild          SSID/password, host IP/port, system names
```

## Running it

**Host** (from `host/`):

```powershell
dotnet run --project AskBot.Host/AskBot.Host.csproj -c Release -- --host <LAN-IP> --port 2552
# or the AOT binary, after run_test.ps1 -Aot has published it:
#   AskBot.Host/bin/Release/net10.0/win-x64/publish/AskBot.Host.exe --host <LAN-IP>
```

`--host` is the address the host *advertises*; a board cannot reach `127.0.0.1`, so
pass the LAN IP when talking to hardware. `--provider netclaw` (or `claude`) swaps
the CLI; the default `echo` needs nothing installed.

**Device simulator** (from `cpp/`):

```powershell
.\build.ps1 -Test
.\build\askbot_chat.exe                 # interactive: type a question, /new, /cancel
.\build\askbot_cli.exe --verbose        # raw protocol
```

**Device app** — build the firmware as usual from `../claude_hud_amoled`
(`idf.py -p COM7 build flash`) after setting SSID, password and host IP in
`idf.py menuconfig` → *AskBot (Akka remoting app)*. WiFi comes up the first time
the app is opened, so the rest of the firmware stays BLE-only until then; the
existing claude_hud, Chat and Settings apps are untouched.

## Next steps

1. Flash and use AskBot on the board (needs WiFi credentials).
2. Voice parity with the Chat app: mic frames and spoken answers as `byte[]`
   messages (serializer 4), host-side Whisper STT and TTS. Note that Windows
   `System.Speech` is COM-based and will not survive AOT — that path needs an
   out-of-process voice or a different synthesiser.
3. An on-screen keyboard (`lv_keyboard`) so questions are not limited to presets.
