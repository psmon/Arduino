# claude_hud_amoled — Brookesia apps for the AMOLED-1.75C (Bluetooth only)

*[한국어](README.md) · English*

An ESP-IDF project that adds our own apps to the **ESP-Brookesia phone UI**, the factory firmware of the
Waveshare **ESP32-S3-Touch-AMOLED-1.75C** (466×466 round AMOLED, ESP32-S3R8, 32MB flash, 8MB PSRAM). Each
app gets an icon in the launcher.

> **This board is BLE-only.** No WiFi, no USB receive, no fallback — the overhead is not worth it here. A
> BLE failure is a warning in the log and a line on the INFO tile, nothing more.
> Code and PC installer are **completely separate** from the older `project/samples/claude_hud` (LCD-1.28,
> USB + BLE); the two share no files.

## Three apps

| App | Component | What it does |
|-----|-----------|--------------|
| **Claude HUD** | `brookesia_app_claude_hud` | CREW / SESSIONS / USAGE / INFO tiles, fed by `S` and `E` lines |
| **Chat** | `brookesia_app_chat` | hold-to-talk voice chat: mic → ADPCM → BLE, answer on screen and optionally through the speaker |
| **Settings** | `brookesia_app_settings` | touch sliders for speaker volume, mic gain and brightness, plus the answer mode and a test tone |

The BLE stack (`hud_ble.cpp`) is shared; whichever app is installed first starts it. On-screen text is
**English**. The Korean font stays loaded because the host sends Korean answers that have to render.

### Claude HUD tiles

| Tile | Contents |
|---|---|
| CREW | one animated ASCII character per session, driven by that session's activity energy |
| SESSIONS | active session count, `host:label activity` per session (green working, blue done, amber idle), outer ring = any activity |
| USAGE | cumulative cost, session count, context used %, 5h/7d limit bars |
| INFO | BLE state (advertising / connected / error), receive count and time since last, uptime, brightness slider |

### Physical buttons

This board exposes exactly **one** button to software: **BOOT (GPIO0)**. `BSP_CAPS_BUTTONS` is 0, and the
other key on the case is RESET, wired to the chip's reset line where firmware can never see it. So one
button carries both directions: a short press raises the volume by 5, a long press lowers it by 5 and
repeats while held. Everything else is on the touch screen.

## Transport: BLE NUS (same protocol as claude_hud)
- Advertises as `claude-hud`; service `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`; one line per write to
  RX (`…0002`) as `S {json}` or `E {json}`.
- Advertises from boot and re-advertises when the central disconnects. Requests MTU 512.
- The aluminium case affects range; measure it rather than assuming.

The chat and speech additions (`R`, `A`, `H`, `C` lines and the `0xA5`/`0xA6` binary frames) are documented
in [`../amoled_chat_host/PROTOCOL.md`](../amoled_chat_host/PROTOCOL.md).

## Connecting a PC

> **The recommended path moved to [`../amoled_chat_host`](../amoled_chat_host/README.md).** That host serves
> the same endpoints on the same port (8765) and handles voice chat as well. BLE admits exactly one owner,
> so **the two must not run together**; `amoled_chat_host/install.ps1` reuses the hook wiring below and stops
> the Python bridge. The `pc/` path here stays as the lighter option when voice is not wanted.

```
Claude Code hooks/statusLine ─(localhost HTTP)─► ble_bridge.py ─(BLE, kept open)─► device
```
Connecting over BLE per hook would cost seconds each time, so **one bridge holds the connection** and the
hooks only POST to `http://127.0.0.1:8765`. When BLE is down the bridge drops the message and logs a warning
to `~/.claude/hud/ble_bridge.log`. There is no fallback.

```powershell
cd project/samples/claude_hud_amoled/pc
pwsh -ExecutionPolicy Bypass -File install.ps1 [-AutoStart]   # backs up settings.json (.amoledbak), merges hooks/statusLine
pwsh -File start_bridge.ps1                                    # creates the venv, installs bleak, runs the bridge
Invoke-RestMethod http://127.0.0.1:8765/health                 # expect {"ble":true,...}, then restart Claude Code
pwsh -File uninstall.ps1                                       # undo
```
Manual test:
`Invoke-RestMethod -Uri http://127.0.0.1:8765/status -Method Post -ContentType application/json -Body '{"session":"t1","label":"test","cost_usd":1.25,"context_used_pct":30}'`

## Build and flash (ESP-IDF v5.5.5, Windows)
```powershell
cd project/samples/claude_hud_amoled
. .\idf-env.ps1                     # activates C:\esp\v5.5.5 and sets WS_AMOLED_REPO
idf.py set-target esp32s3           # first time only
idf.py -p COM7 build flash monitor  # console is on the native USB port (COM7)
```
- `brookesia_core` (44 MB) is referenced from the Waveshare board repo through `EXTRA_COMPONENT_DIRS`
  (`C:\esp\ws-amoled-175c`, or wherever `WS_AMOLED_REPO` points), not copied in.
- The BSP (`waveshare/esp32_s3_touch_amoled_1_75c`), LVGL 9.5 and friends are fetched by the component
  manager on the first build into `managed_components/` (git-ignored).
- Factory recovery: write `Firmware/ESP32-S3-Touch-AMOLED-1.75C-FactoryOnly-260114.bin` from the Waveshare
  repo with `esptool write_flash 0x0`.

## Layout
```
CMakeLists.txt / sdkconfig.defaults / partitions.csv (factory 8M) / idf-env.ps1
main/main.cpp                        Waveshare's demo, untouched (the registry installs our apps)
components/brookesia_app_claude_hud/
  claude_hud_app.{hpp,cpp}           phone::App - starts BLE on init, builds the tile UI on run
  hud_state.{hpp,cpp}                session / limit model and cJSON parsing, behind a mutex
  hud_ble.cpp                        NimBLE NUS server, shared by every app
  assets/font_nanum_18.c             NanumGothic subset, built uncompressed on purpose (see below)
components/brookesia_app_chat/
  chat_app.{hpp,cpp}                 hold-to-talk UI, conversation bubbles, answer-mode pill
  chat_core.{hpp,cpp}                protocol state, mic capture, ADPCM, speech playback
components/brookesia_app_settings/
  settings_app.{hpp,cpp}             volume / mic gain / brightness sliders
  boot_button.cpp                    BOOT (GPIO0) as the volume key
tools/  gen_icon*.py, gen_font.py, gen_mic_glyph.py
pc/     ble_bridge.py and the HUD-only installer (superseded by ../amoled_chat_host)
```

## Two constraints worth knowing before you edit this

- **The Korean font must be generated uncompressed.** LVGL keeps the RLE reader for compressed glyphs in one
  global (`LV_GLOBAL_DEFAULT()->font_fmt_rle`) while this project renders with
  `CONFIG_LV_DRAW_SW_DRAW_UNIT_CNT=2`. Two draw units decompressing at once corrupt each other and most
  glyphs come out as garbage. `tools/gen_font.py` passes `--no-compress` for exactly that reason.
- **The NimBLE host task needs 8 KB.** The chat app's line and frame hooks run on that task, on top of its
  600-byte reassembly buffer, cJSON parsing and log formatting. The 4 KB default overflowed and rebooted the
  board mid-answer.
