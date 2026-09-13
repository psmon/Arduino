# akka — an ESP32-S3 as a peer in a .NET Akka actor system

The device joins a .NET 10 `ActorSystem` over Akka.NET classic remoting and takes
part as a **client actor**: it has an address, the host `Tell`s it, and it answers.
The question this sample exists to settle is whether the actor model is usable from
a small device at all.

```
[ESP32-S3-Touch-AMOLED-1.75C]                        [PC]  host/  AkkaHost.exe
                                                     .NET 10 + Akka 1.6
 AskBot app   Akka PDUs --0xAB--\                     BleLink (owns the single BLE link)
                                 >--- one BLE link ---+-- BleTunnel --> TCP 2552 --> /user/chat
 Chat app     R/A lines, 0xA6 --/                      +-- BleChatProxy ----------->  ChatActor
                                                                                       |
 Settings app (owns a WiFi service, off by default)                        chat CLI + SuperTonic
```

One host, one link, both apps. AskBot is a real remoting peer whose PDUs are tunnelled over
BLE; the Chat app keeps its line protocol and a proxy actor turns it into the same messages.
Neither firmware app needed changing for that, and there is no WiFi anywhere in the path.

## Status

| | |
|---|---|
| Akka.Remote under Native AOT | **works** for the remoting core, with two workarounds (below); the BLE central pulls in WinRT, so AOT is unverified since |
| C++ peer: associate, heartbeat, tell/ask | **verified** on the PC, both JIT and AOT hosts |
| Chat flow (hello / text / streamed reply / cancel / newsession) | **verified** end to end, offline `echo` provider |
| Spoken answers (SuperTonic → ADPCM → `byte[]` messages) | **verified** end to end, inside the AOT binary too |
| On the watch: AskBot associates and speaks | **verified on hardware** over BLE, no WiFi |
| On the watch: the Chat app served by the same actors | **verified on hardware** (same answer, same voice) |
| Microphone → STT | **not implemented** — speech input is still the Chat app's job, over BLE |

```powershell
pwsh -File project/samples/akka/run_test.ps1        # framework-dependent host
pwsh -File project/samples/akka/run_test.ps1 -Aot   # Native AOT host
```

Three layers run: `askbot_cli` for the raw protocol, `askbot_chat` for the
conversation flow, and the spoken answer (`--tts`), which is decoded back to a WAV so
it can be listened to. No board and no LLM required; `-NoVoice` skips the audio leg.

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

## Speech

Answers can come back as audio. The host synthesizes with **SuperTonic-3** - four ONNX
graphs through `Microsoft.ML.OnnxRuntime`, no Python, no espeak-ng, no COM - resamples
44.1 kHz to 16 kHz, encodes IMA ADPCM and pushes one block per `byte[]` message
(serializer 4). The device decodes into PSRAM and plays the utterance when the host says
it is complete.

The model is the one **AgentZeroLite already installed** under
`%LOCALAPPDATA%\AgentZeroLite\models\supertonic` (383 MB, 10 voices, 31 languages).
This project never downloads it; if it is absent, `hostinfo` reports `tts:false`, the
device hides its voice toggle, and answers stay text - the same degradation the BLE host
applies.

Measured on this machine: ~0.9 s one-time model load, ~4 s of CPU for ~20 s of Korean
audio at 8 denoising steps. It also runs **inside the Native AOT binary** (27.6 MB exe
plus a 13.5 MB native `onnxruntime.dll`), which is the reason SuperTonic was the right
choice: Windows `System.Speech` is COM-based and does not survive AOT at all.

Try it without any of the rest:

```powershell
AskBot.Host.exe --speak "안녕하세요. 액터 모델로 대답합니다." --out hello.wav
```

## Why BLE and not WiFi

WiFi worked - the watch associated four seconds after power-on - and then tore the screen.
Bringing the station up left ~7 KB of internal DMA heap, and the BSP flushes a PSRAM draw
buffer of 46.6 KB per transfer, so the LCD could no longer borrow a DMA buffer:

```
E spi_master: setup_dma_priv_buffer: Failed to allocate priv TX buffer
E esp_lvgl:bridge_v9: Draw bitmap failed: ESP_ERR_NO_MEM
```

Raising `CONFIG_SPIRAM_MALLOC_RESERVE_INTERNAL` to 128 KB and pushing WiFi/LWIP buffers into
PSRAM fixed the failures, but the radio was still paying for itself twice: memory pressure
plus a second transport on a device whose BLE link was already up for the HUD.

Akka does not need IP - it needs an ordered, reliable byte stream, which BLE already is. The
device's stream implementation moved from `MakeTcpStream()` to `MakeBleStream()` and nothing
else changed: not the PDU codec, not the association, not the chat protocol. WiFi remains
available as a device service in the Settings app, off unless an SSID is configured.

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
host/                        .NET 10 + Akka 1.6 host for both watch apps
  nuget.config               the Akka nightly feed
  AkkaHost/
    Ble/BleLink.cs           WinRT BLE central; owns the watch's single link
    Ble/BleTunnel.cs         0xAB chunks <-> this host's own remoting port
    Actors/BleChatProxy.cs   the Chat app as an actor: line protocol <-> ChatActor
    AotProps.cs              AOT-safe actor creation
    Program.cs               ActorSystem, remoting, /user/ask + /user/chat
    Actors/AskActor.cs       echo actor (protocol smoke test)
    Actors/ChatActor.cs      the device-facing conversation actor
    Chat/CliProvider.cs      runs netclaw / claude / a script as a child process
    Chat/HostConfig.cs       appsettings.json via JsonDocument (AOT-safe)
    Chat/Json.cs             tiny writer + UTF-8-safe chunking
    Voice/SuperTonic.cs      SuperTonic-3 ONNX pipeline (ported, MIT, Supertone Inc)
    Voice/DeviceAudio.cs     44.1k -> 16k resampler, IMA ADPCM, frame builder
    Voice/VoiceSynth.cs      lazy model load, style cache, device-ready frames
    appsettings.json         providers; default is the offline "echo"

pc/ble_akka_bridge.py        standalone BLE->TCP bridge (only needed without AkkaHost)

cpp/                         the module: portable C++17, no ESP-IDF dependency
  include/askbot/
    ima_adpcm.h              ADPCM decoder + WAV writer, shared with the firmware
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
  askbot_core.cpp            association + client actor + chat state + speaker playback
  askbot_ble_stream.cpp      akka::IByteStream over the shared NUS link (0xAB chunks)
  askbot_app.cpp             LVGL UI, same layout language as the Chat app
  Kconfig.projbuild          SSID/password, host IP/port, system names, volume
```

## Running it

**Host** (from `host/`):

```powershell
dotnet run --project AkkaHost/AkkaHost.csproj -c Release
```

That is all: it listens on `akka.tcp://AskBot@127.0.0.1:2552`, connects to the watch over
BLE and serves both apps. Useful flags:

| flag | effect |
|---|---|
| `--provider netclaw` | use a real chat CLI instead of the offline `echo` loopback |
| `--announce "…"` | say something to a device as soon as it connects (push notification, and the way to test screen + speaker without touching the watch) |
| `--no-ble` | skip the BLE central; only network peers reach the host (what `run_test.ps1` uses) |
| `--device claude-hud` | advertised name to connect to |

`AkkaHost` must be the only process holding the watch's BLE link: `amoled_chat_host`'s
ChatHost does the same job for the Chat app alone, so run one or the other.

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

1. Microphone → STT, the other half of Chat parity: the `0xA5` frames already arrive at
   the host, and Whisper `ggml-small.bin` is installed next to the SuperTonic bundle.
   Wiring it up serves both apps at once, since both share `ChatActor`.
2. Re-check Native AOT now that WinRT is in the picture (`-p:AkkaHostAot=true`).
3. An on-screen keyboard (`lv_keyboard`) so questions are not limited to presets.
