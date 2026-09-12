# amoled_chat_host — PC host for the AMOLED voice chatbot

The ESP32-S3-Touch-AMOLED-1.75C cannot run .NET (Xtensa, no NativeAOT target), so the "execution" side lives
on the PC: an **ASP.NET Core host** that keeps one Bluetooth LE connection to the device, turns the device's
voice into text with **on-device Whisper**, asks a **registered chat CLI** (`netclaw chat -p` by default) and
sends the answer back over the same BLE link.

```
 AMOLED device (ESP-IDF, BLE NUS)                  PC host (ChatHost, ASP.NET Core, Windows)
 ┌──────────────────────────┐   BLE NUS    ┌──────────────────────────────────────────────┐
 │ chat UI · mic → ADPCM    │ ───────────▶ │ BleLink ─▶ ConversationService                │
 │ shows stt/think/reply    │ ◀─────────── │   ├─ Whisper.net (ggml-small, CPU, offline)   │
 └──────────────────────────┘              │   └─ chat CLI registry: netclaw │ claude │ …  │
                                           │ http://127.0.0.1:8765  (web UI + API)         │
                                           │   /status /event  ← Claude Code HUD hooks     │
                                           └──────────────────────────────────────────────┘
```

Wire format: [PROTOCOL.md](PROTOCOL.md). Firmware side (chat app + mic streaming) is the next step and is
**not** in this folder yet — the host is complete and tested through its HTTP API and web UI.

## Requirements

- Windows 10 1903+ with Bluetooth LE (the host uses WinRT `Windows.Devices.Bluetooth`).
- .NET SDK 10 (`dotnet --list-sdks` → 10.0.x).
- `netclaw` on PATH (`netclaw chat -p --json "hi"` must work) — or any other CLI you register.
- Whisper model: `%USERPROFILE%\.ollama\models\agentzero\whisper\ggml-small.bin` (shared with AgentZeroLite).
  Missing → downloaded automatically on first start (`Stt:AutoDownload`, ~466 MB; `medium` ~1.5 GB).

## Run

```powershell
cd project/samples/amoled_chat_host
pwsh -File start_host.ps1            # build (Release) + run in this console
pwsh -File start_host.ps1 -Background # start detached, log in ~/.claude/hud/chat_host.log
```
Then open <http://127.0.0.1:8765/> — status, pairing, provider selection, text and voice tests, live log.

**Only one program can own the BLE link.** Stop the old `claude_hud_amoled/pc/ble_bridge.py` before starting
the host; the host serves the same `POST /status` / `POST /event` endpoints on the same port, so the Claude
Code HUD hooks keep working unchanged.

## Pairing the device

"Pairing" is application-level: the host remembers one device address and reconnects to it only.
NUS needs no OS-level Bluetooth bonding.

1. Power the device (it advertises as `claude-hud`).
2. Web UI → **Scan** → **Pair** on the device row. Stored in `%LOCALAPPDATA%\AmoledChatHost\state.json`.
3. Until something is paired, the host auto-connects to the first device named `Ble:DeviceName`.

API: `GET /api/ble/scan?seconds=5`, `POST /api/ble/pair {address,name}`, `DELETE /api/ble/pair`,
`POST /api/ble/connect`, `POST /api/ble/disconnect`, `POST /api/ble/send {line}` (raw line for testing).

## Registering chat CLIs

`ChatHost/appsettings.json → Chat:Providers`. Each entry is a process the host runs per request:

```jsonc
"netclaw": {
  "Command": "netclaw",
  "Args": [ "chat", "-p", "--json", "{session-args}" ],   // {session-args} → SessionArgs when a session id exists
  "SessionArgs": [ "--resume", "{session}" ],             // one netclaw session per device: amoled-<bt address>
  "PromptVia": "arg",                                     // "arg" (last argument) or "stdin"
  "Output": "json", "ResponseField": "response",          // or "text" = whole stdout
  "TimeoutSec": 120
}
```
Bundled: `netclaw` (default), `claude` (Claude Code CLI via stdin, `--output-format json` → `result`),
`echo` (offline loopback). Switch the default in the web UI or `PUT /api/providers/default {"name":"claude"}`
(persisted). `Chat:ReplyStyle` is prepended to every prompt so answers fit the 466 px round screen.

## STT

`Stt:Model` = `tiny | base | small | medium`, `Stt:Language` = `auto | ko | en | …`, `Stt:ModelDir` empty =
AgentZeroLite's model folder. Measured on this machine (CPU, small): 3.4 s of Korean speech → 3.4 s;
language auto-detect on the small model sometimes mis-hears synthetic English as Korean phonetics — set
`Stt:Language` (or send `lang` from the device) when the language is known.

## HTTP API (all JSON)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/status` | BLE / pairing / STT / provider / last transcript & reply |
| GET | `/api/log?n=150` | log tail |
| GET | `/api/providers` · PUT `/api/providers/default` | list / choose chat CLI |
| POST | `/api/chat` `{text, provider?, session?, toDevice?}` | run the CLI (optionally push the reply to the device) |
| POST | `/api/stt` (body `audio/wav`, or `?raw=1&rate=16000&ch=1` PCM16) | transcription only |
| POST | `/api/voice-chat?lang=ko&toDevice=1` (body `audio/wav`) | STT → chat → reply |
| POST | `/api/stt/preload` | load the Whisper model now |
| GET | `/api/selftest/adpcm` | codec round trip (reference for the firmware encoder) |
| POST | `/status` · `/event` | HUD passthrough, identical to `ble_bridge.py` |
| GET | `/health` | `{ble, sent, dropped, device}` (old contract) |

Verified 2026-09-13: `netclaw` en/ko answers (~3 s), Whisper small Korean transcript exact, voice-chat round
trip (STT 3–7 s + netclaw 3–20 s), ADPCM self-test 4:1 / 32.7 dB SNR.

## Next: firmware

`claude_hud_amoled` needs a `brookesia_app_chat` app: chat bubble UI, hold-to-talk button, mic capture via
`bsp_audio_codec_microphone_init()` (16 kHz mono), IMA ADPCM blocks streamed as `0xA5` notifications, and
rendering of the `A` stages. The BLE layer (`hud_ble.cpp`) only needs a `notify(bytes)` helper and a hook so
`R`/`A` lines reach the chat app instead of the HUD state.
