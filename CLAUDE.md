# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Embedded experiments for two Waveshare boards: the original **ESP32-S3-LCD-1.28** (round 240×240 GC9A01
LCD, QMI8658 IMU; Arduino) and, since 2026-09, the **ESP32-S3-Touch-AMOLED-1.75C** (466×466 round AMOLED;
ESP-IDF + ESP-Brookesia) which is now the **primary device** — see the last section. There are **no tests or
linter**. LCD-1.28 sketches are compiled/uploaded through the **Arduino IDE 2.x** GUI or `arduino-cli`; the
AMOLED project builds with `idf.py`. Runnable PC-side code in-repo is the Python BLE tool (`ble_pc/`), the
PowerShell HUD senders for the old board (`claude_hud/pc/`) and the BLE bridge + installer for the new one
(`claude_hud_amoled/pc/`). `README.md` holds the end-user setup guide (IDE 2.3.10, ESP32
core 3.3.11, Arduino_GFX + U8g2 libraries, CH343 driver, board options).

## Layout convention

- `project/samples/<case>/` — one folder per experiment. This is the current **sample-collection
  stage**; add new experiments here, case by case.
- `project/<name>/` — reserved for meaningful features once promoted out of `samples/`.
- Arduino requires each `.ino` to sit in a folder of the **same name** (e.g. `ble_lcd/ble_lcd.ino`).

## Hardware facts that bite (verified via esptool on the real board)

- **LCD pins are NON-obvious and commonly mis-documented.** Correct for this unit:
  DC=8, CS=9, SCK=10, MOSI=11, **RST=14** (not 12; 12 is MISO), **BL=2** (not 40).
  Wrong RST → black screen (panel never resets); wrong BL → black screen (backlight never lit),
  even though the sketch runs fine. These two were the cause of a long black-screen debug.
- Chip: ESP32-S3, **16MB Quad flash**, **2MB QSPI PSRAM**, Wi-Fi + **BLE only** (no Bluetooth Classic).
- USB-serial is a **CH343** bridge → its own COM port. **The number differs per machine — always probe,
  never hardcode** (measured: COM6 on the first dev machine, COM3 on `SAM`; low COM numbers may also be
  motherboard `ACPI\PNP0501` ports or Bluetooth). Identify it by VID, not by number:
  `Get-CimInstance Win32_PnPEntity | ? { $_.PNPDeviceID -match 'VID_1A86' -and $_.Name -match 'COM\d+' }`
  → `USB-Enhanced-SERIAL CH343(COMx)`, `VID_1A86&PID_55D3`. Docs below use COM6 as a placeholder.
- Arduino board options: **ESP32S3 Dev Module**, Flash 16MB, PSRAM **QSPI PSRAM**.
  `ble_lcd` is large (BLE + graphics) and **requires Partition Scheme = "Huge APP (3MB No OTA)"**
  or it fails with "Sketch too big".

## BLE architecture (ble_lcd ↔ ble_pc)

`ble_lcd.ino` is a BLE **Nordic UART Service (NUS)** server advertising as `ESP32-S3-LCD`.
The PC scripts in `ble_pc/` are `bleak` clients. They are coupled by shared UUIDs — if you
change any, change both sides:
- Service `6E400001-…`, RX (write, phone/PC→device) `6E400002-…`, TX (notify, device→PC) `6E400003-…`
- Device receives a UTF-8 string on RX → `showText()` renders it on the LCD → echoes `OK: <text>` on TX.
- **Transmission is UTF-8-clean for every language; on-screen rendering is limited to the loaded
  font's glyphs.** `showText()` uses U8g2 `u8g2_font_unifont_t_korean1` (Latin + Korean). Other
  scripts (CJK, Cyrillic) transmit fine but render as boxes until a per-script font is selected.
- Arduino_GFX color macros are `RGB565_BLACK`/`RGB565_WHITE` (not `BLACK`/`WHITE`); raw hex `0x0000`/`0xFFFF` also works.

## claude_hud — attaching a *new* PC to an already-flashed board

`project/samples/claude_hud/` is the physical Claude Code dashboard. A PC can be a **sender-only
machine**: the board keeps whatever firmware it has, so that PC needs **no Arduino IDE, no
arduino-cli, no Python** — the senders (`pc/send_event.ps1`, `pc/hud_statusline.ps1`) are pure
built-in PowerShell. Full procedure + troubleshooting: **`project/samples/claude_hud/pc/ATTACH.md`**.

```powershell
pwsh -File project/samples/claude_hud/pc/find_port.ps1                       # probe CH343 COM port
pwsh -ExecutionPolicy Bypass -File project/samples/claude_hud/pc/install.ps1 -Url serial:auto
```
- `install.ps1` writes `~/.claude/hud_url.txt` (`serial:COMx` or `http://<ip>:8080`), copies the
  senders to `~/.claude/hud/`, and **merges** `~/.claude/settings.json` (backup `.hudbak`): hooks are
  appended without touching existing ones, and an existing `statusLine` is **wrapped, not replaced**
  (original command preserved in `~/.claude/hud/inner_statusline.txt`). Idempotent — re-run when the
  COM number changes. Requires a Claude Code restart to take effect.
- Wire format on serial is one line, 115200: `S {json}` = status, `E {json}` = event. Set
  **`DtrEnable=$false; RtsEnable=$false`** on any hand-rolled sender or the ESP32 resets on every open.
- USB serial is single-owner: any open Serial Monitor silently blackholes transmission.

## Commands

**Build/upload sketches:** either the Arduino IDE 2.x GUI, or **headless via `arduino-cli`**
(verified working — see `CLIBUILD.md` for the full command set). Fast path reuses the local
core+libraries, e.g. compile+upload hello_lcd:
`arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/hello_lcd`.
`ble_lcd` needs `,PartitionScheme=huge_app,CDCOnBoot=cdc` appended to the FQBN.
Each sketch also has a `sketch.yaml` profile, but profile builds pull libraries from the index —
the manually-installed U8g2 (dev 2.37.1) is ahead of the index (2.36.19), so prefer the `--fqbn` path.
If upload hangs at `Connecting...`, put the board in download mode: hold **BOOT**, tap **RESET**,
release BOOT, upload again.

**PC BLE tool** (Windows; from `project/samples/ble_pc/`):
```powershell
python -m venv venv
venv\Scripts\python -m pip install -r requirements.txt
venv\Scripts\python send_ble.py "안녕하세요"   # one-shot; no arg = interactive prompt loop
venv\Scripts\python greet.py                    # send 7-language greetings in sequence
```
The `venv/` is git-ignored — recreate it with the commands above.

**Direct board diagnostics** (Windows): the Arduino IDE **Serial Monitor holds the COM port**;
close it before running esptool or the PC BLE tool. esptool ships with the ESP32 core, e.g.
`…\Arduino15\packages\esp32\tools\esptool_py\<ver>\esptool.exe --port COM6 flash-id`
to read chip/flash, and a 115200 serial read distinguishes a clean boot from a crash/boot-loop.

## Git

Public repo `psmon/Arduino`, default branch `main`, `gh` CLI authenticated. Normal flow:
`git add -A && git commit -m "…" && git push`.

## Second device: ESP32-S3-Touch-AMOLED-1.75C (primary since 2026-09-10)

- Board: ESP32-S3R8, CO5300 466×466 AMOLED, touch CST9217, 32MB flash, 8MB octal PSRAM, BLE 5 + WiFi, aluminum
  case. Native USB-Serial-JTAG → **COM7** (VID 303A; no CH343). Opening COM7 with DTR/RTS low does NOT reset the
  board; an RTS pulse does (use it to capture boot logs). COM6 stays the LCD-1.28.
- Firmware is **ESP-IDF v5.5.5, not Arduino**: `project/samples/claude_hud_amoled/` = Waveshare's ESP-Brookesia
  phone demo + our `components/brookesia_app_claude_hud` app. Toolchain lives in `C:\esp\v5.5.5`; always activate
  with `. .\idf-env.ps1` from that project (it pins `IDF_PYTHON_ENV_PATH` to the py3.12 venv — plain `export.ps1`
  fails). `brookesia_core` is referenced from the Waveshare repo clone at `C:\esp\ws-amoled-175c`
  (`WS_AMOLED_REPO`), not vendored. Build: `idf.py -p COM7 build flash` (app-only: `app-flash`).
- **Adding an app (expected to happen repeatedly):** one folder `components/brookesia_app_<name>/` with a class
  deriving `esp_brookesia::systems::phone::App` (`run()`/`back()`), registered at the end of the .cpp with
  `ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR`; 112×112 ARGB8888 icon (`tools/gen_icon.py`); component CMake needs
  `WHOLE_ARCHIVE`; declare `waveshare/esp32_s3_touch_amoled_1_75c` in its `idf_component.yml` if it calls BSP
  functions. `main/main.cpp` stays untouched. Apps are statically linked → every change reflashes the whole app
  image (incremental build is 1–2 min).
- **Transport is BLE only** (user decision: no WiFi/USB rx, no fallbacks; failures = warning log + INFO tile).
  PC side is `claude_hud_amoled/pc/`: `ble_bridge.py` keeps one BLE connection and serves
  `http://127.0.0.1:8765`; hooks/statusLine POST there; `install.ps1` merges settings.json (backup `.amoledbak`,
  scripts in `~/.claude/hud_amoled/`). Keep this **strictly separate** from the old board's `claude_hud/pc`
  (USB+BLE+HTTP) — do not share files between the two devices' code or installers.
- Factory-recovery image: Waveshare repo `Firmware/*FactoryOnly*.bin` at offset 0x0.

## akka / AskBot — the AMOLED board as a peer in a .NET actor system (since 2026-09-13)

`project/samples/akka/` (host + portable C++ client module + a headless ESP-IDF example) and the firmware app
`claude_hud_amoled/components/brookesia_app_askbot/`. The board joins a .NET 10 + **Akka 1.6 nightly**
`ActorSystem` over classic remoting and registers a **client actor** at
`akka.tcp://askbot-device@<ip>:2553/user/chat`, so the host pushes stages and reply chunks to it as ordinary
actor messages. Same conversation flow as the Chat app; **WiFi TCP, not BLE** — Akka remoting is TCP, so the
BLE-only rule of the other apps does not apply, and WiFi only starts when AskBot is first opened.

- **Akka.Remote does run under Native AOT**, with two workarounds, both in `host/`: HOCON resolves its
  provider/transport/serializers *by type name*, so the assemblies need `TrimmerRootAssembly` (3.6 MB → 26 MB);
  and `Props.Create<T>()` — including the `() => new T()` lambda form — is `Activator.CreateInstance`, so actors
  must be created with `Props.CreateBy` + an explicit producer (`AotProps.cs`). `PublishAot` is opt-in
  (`-p:AskBotAot=true`). Akka 1.6 is nightly-only (nuget.org stable is 1.5.71); `host/nuget.config` adds the feed.
- **.NET cannot run on the ESP32-S3** — Native AOT has no Xtensa/bare-metal target, and nanoFramework's nanoCLR
  is an IL interpreter with non-netstandard class libs. Actors stay on the PC; the device speaks the wire protocol.
- `pwsh -File project/samples/akka/run_test.ps1 [-Aot]` is the whole verification: builds both sides, starts the
  host, runs the raw protocol (`askbot_cli`) and the full chat flow (`askbot_chat`, offline `echo` provider), and
  exits non-zero if anything goes unanswered. `cpp/build.ps1 -Test` runs the PDU unit tests.
- Wire facts that bite (handshake carries scheme `tcp` while paths use `akka.tcp`; `seq` must be written as
  `ulong.MaxValue`; string = serializer 17 / manifest `S`; byte[] = serializer 4) are in
  `project/samples/akka/PROTOCOL.md` — read it before touching `cpp/src/akka_wire.cpp`.
- Voice (mic/STT/TTS) is **not** ported yet: that is still the BLE Chat app's job. Windows `System.Speech` is
  COM-based and will not survive AOT, so the voice path needs an out-of-process synthesiser.
