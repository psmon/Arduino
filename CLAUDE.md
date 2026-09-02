# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Embedded experiments for the **Waveshare ESP32-S3-LCD-1.28** (round 240×240 GC9A01 LCD,
QMI8658 IMU). There is **no in-repo build system, tests, or linter** — Arduino sketches are
compiled/uploaded through the **Arduino IDE 2.x** GUI, and the only runnable code in-repo is
the PC-side Python BLE tool. `README.md` holds the end-user setup guide (IDE 2.3.10, ESP32
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
- USB-serial is a **CH343** bridge → its own COM port (COM6 on the current machine; COM4/COM5 are Bluetooth).
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
