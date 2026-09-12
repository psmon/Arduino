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

Wire format: [PROTOCOL.md](PROTOCOL.md). The device side is the `Chat` app in
`../claude_hud_amoled/components/brookesia_app_chat`, installed alongside the existing Claude HUD app.

## Requirements

- Windows 10 1903+ with Bluetooth LE (the host uses WinRT `Windows.Devices.Bluetooth`).
- .NET SDK 10 (`dotnet --list-sdks` → 10.0.x).
- `netclaw` on PATH (`netclaw chat -p --json "hi"` must work) — or any other CLI you register.
- Whisper model: `%USERPROFILE%\.ollama\models\agentzero\whisper\ggml-small.bin` (shared with AgentZeroLite).
  Missing → downloaded automatically on first start (`Stt:AutoDownload`, ~466 MB; `medium` ~1.5 GB).

## Install

```powershell
cd project/samples/amoled_chat_host
pwsh -ExecutionPolicy Bypass -File install.ps1 -AutoStart
```

The installer builds the host, **stops `ble_bridge.py` and removes its startup shortcut** (BLE has a single
owner), wires the Claude Code hooks by delegating to `claude_hud_amoled/pc/install.ps1` — the hook scripts
are unchanged because this host serves the same `POST /status` and `POST /event` on the same port — then
starts the host and optionally registers it to start at logon. Restart Claude Code afterwards.

`pwsh -File uninstall.ps1` reverses it (`-KeepHooks` leaves `settings.json` alone).

To run it by hand instead:

```powershell
pwsh -File start_host.ps1             # build (Release) + run in this console
pwsh -File start_host.ps1 -Background # detached, log in ~/.claude/hud/chat_host.log
```

Either way, open <http://127.0.0.1:8765/> — status, pairing, provider selection, text and voice tests, live log.

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
| POST | `/api/device/talk?ms=5000` | make the device record from its own mic and run the pipeline |
| POST | `/api/device/text` `{line}` | make the device send a prompt as if typed on it |
| POST | `/api/device/mode?voice=true` | flip the device's answer mode (text, or text plus speech) |
| POST | `/api/device/speak` `{line}` | synthesise text and play it on the device speaker |
| POST | `/api/device/stop` | cancel whatever is running |
| POST | `/api/device/clear` | clear the device screen |
| POST | `/api/session/reset` | abandon the CLI conversation and start a fresh session id |
| POST | `/status` · `/event` | HUD passthrough, identical to `ble_bridge.py` |
| GET | `/health` | `{ble, sent, dropped, device}` (old contract) |

The three `/api/device/*` routes drive the on-screen app remotely, which is how the voice path gets tested
without a hand on the device.

Verified 2026-09-13: `netclaw` en/ko answers (~3 s), Whisper small Korean transcript exact, voice-chat round
trip (STT 3–7 s + netclaw 3–20 s), ADPCM self-test 4:1 / 32.7 dB SNR.

## Spoken answers

With the device's answer mode set to text plus voice, the host synthesises the reply with Windows SAPI at
16 kHz mono, encodes it as IMA ADPCM and streams it as `0xA6` frames. The device buffers the whole utterance
in PSRAM and plays it through the speaker when the host says it is done — BLE delivers about 8 kB/s while the
speaker consumes 32 kB/s, so playing as it arrives would stutter. Measured: 9.5 s of Korean speech is 158
frames and lands in 677 ms.

The setting lives on the device (persisted in NVS) and rides along on each request as `"tts": true|false`.
The host reports whether it has any voice installed in its greeting, and the device hides the setting when
it does not. `POST /api/device/mode?voice=true` flips it remotely, and `POST /api/device/speak` plays
arbitrary text without involving a chat CLI.

## One request at a time, newest wins

A second question preempts the first instead of being refused, so the device no longer shows "host busy".
See PROTOCOL.md for why the abandoned CLI child is left to die on its own rather than killed.

## Things that bite

- **BLE has one owner.** `ble_bridge.py` and this host cannot both run. `install.ps1` stops the bridge.
- **Lines go out as raw UTF-8**, not `\uXXXX`. The default .NET encoder doubles a Korean payload, which
  pushes one reply line past a single BLE write; `ConversationService.LineJson` uses relaxed escaping.
- **The Korean LVGL font must be built uncompressed.** LVGL keeps the RLE reader for compressed glyphs in one
  global while the firmware renders with two draw units, so concurrent decompression scrambles most glyphs on
  screen. `claude_hud_amoled/tools/gen_font.py` passes `--no-compress` for that reason.
- **Whisper language auto-detect** on the `small` model sometimes reads synthetic English as Korean
  phonetics. Set `Stt:Language`, or send `lang` from the device, when the language is known.
