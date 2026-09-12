# Arduino — Waveshare ESP32-S3-LCD-1.28 / ESP32-S3-Touch-AMOLED-1.75C

*[한국어](README.md) · English*

> **Two boards live here.** ① the original **LCD-1.28** (Arduino IDE, first half of this page)
> ② the **AMOLED-1.75C** added in September 2026 (ESP-IDF + ESP-Brookesia,
> [second half](#amoled-175c-esp-idf--esp-brookesia--the-main-board)). The AMOLED-1.75C is the **main
> board** now, and Brookesia apps keep being added to it.

A case-by-case collection built around the Waveshare **ESP32-S3-LCD-1.28** (round 240×240 GC9A01 IPS LCD,
QMI8658 IMU), from putting the first pixel on screen to receiving text over BLE from a PC.

- Still the **sample-collecting stage** — every experiment lives under `project/samples/<case>`.
- Anything that grows into a real feature is promoted to `project/<name>`.

---

## What you need (starting from nothing)

### 1. Hardware
- Waveshare **ESP32-S3-LCD-1.28** board
- A **USB-C cable** that carries data
- A PC running Windows 10/11 (this repo is tested on Windows 11 with built-in and USB Bluetooth)

### 2. Arduino IDE 2.x (verified with **2.3.10**)
**A — winget (recommended)**
```powershell
winget install --id ArduinoSA.IDE.stable -e --accept-package-agreements --accept-source-agreements
```
**B — by hand**: https://www.arduino.cc/en/software → download "Arduino IDE 2.x"

### 3. ESP32 board core (verified: esp32 by Espressif **3.3.11**)
1. Arduino IDE → **File → Preferences → Additional boards manager URLs**, add:
   ```
   https://raw.githubusercontent.com/espressif/arduino-esp32/gh-pages/package_esp32_index.json
   ```
2. **Boards Manager** → search `esp32` → install **"esp32 by Espressif Systems"** (several hundred MB)

### 4. Libraries (Library Manager, the book icon)
| Library | Why |
|---|---|
| **GFX Library for Arduino** (Arduino_GFX, by moononournation) | drives the GC9A01 LCD |
| **U8g2** (by oliver) | multi-script unifonts, including Hangul (used by `ble_lcd`) |

### 5. USB driver and port
- The board's USB-serial chip is a **CH343**. Windows 11 usually picks it up and assigns a **COM port**.
- If it does not, install the WCH CH343 driver: https://www.wch.cn/downloads/CH343SER_EXE.html
- Device Manager should show `USB-Enhanced-SERIAL CH343 (COMx)`.

### 6. Board options (Tools menu)
| Item | Value |
|---|---|
| Board | **ESP32S3 Dev Module** |
| Port | the **COMx** the CH343 took |
| Flash Size | **16MB (128Mb)** |
| PSRAM | **QSPI PSRAM** |
| USB CDC On Boot | Enabled (optional) |
| Partition Scheme | default, except **`ble_lcd` needs "Huge APP (3MB No OTA/1MB SPIFFS)"** |

### 7. (Optional) Python, for the PC-side BLE tool
`project/samples/ble_pc` needs Python 3.x:
```powershell
cd project\samples\ble_pc
python -m venv venv
venv\Scripts\python -m pip install -r requirements.txt
```

---

## Verified hardware and LCD pinout (read back with esptool)
- Chip: ESP32-S3 (rev v0.2), Wi-Fi + **BLE 5** (no Bluetooth Classic)
- Flash **16MB (Quad)**, PSRAM **2MB (QSPI, in package)**, USB-serial **CH343**

| Signal | GPIO | Note |
|---|---|---|
| DC | 8 | |
| CS | 9 | |
| SCK | 10 | |
| MOSI | 11 | |
| **RST** | **14** | not 12, which is what most sources say (12 is MISO) |
| **BL** | **2** | not 40, which is what most sources say |

Both of those cost a long black-screen debugging session: the wrong RST leaves the panel unreset and the
wrong BL leaves the backlight off, and in either case the sketch runs perfectly while the screen stays dark.

---

## Samples (`project/samples/`)
| Case | What it is |
|---|---|
| **`hello_lcd/`** | prints `HELLO` on the GC9A01 — the "is anything working" check |
| **`ble_lcd/`** | BLE Nordic UART Service server. Shows a UTF-8 string sent from a phone or PC on the round screen; Hangul renders through the U8g2 unifont (`u8g2_font_unifont_t_korean1`). **Needs the Huge APP partition** |
| **`ble_pc/`** | Python (`bleak`) tools that send text over BLE from Windows<br>· `send_ble.py "text"` — one shot, or interactive with no argument<br>· `greet.py` — greetings in seven languages in a row (font coverage test)<br>· `ascii_art.py` — free text, ASCII art, and face/spinner/bounce animations |
| **`claude_hud/`** | (LCD-1.28) a physical HUD for Claude Code progress and usage. Receives over USB serial, BLE and WiFi HTTP. PC installer at `pc/install.ps1` |
| **`claude_hud_amoled/`** | **(AMOLED-1.75C, ESP-IDF) the main project.** Three ESP-Brookesia apps: Claude HUD, Chat (hold-to-talk voice) and Settings. **BLE is the only transport.** The reference for adding more apps |
| **`amoled_chat_host/`** | **(AMOLED-1.75C, PC side) the voice chatbot host.** An ASP.NET Core service holds the BLE link, transcribes the device's microphone with Whisper, asks a registered chat CLI (`netclaw chat -p` by default) and sends the answer back to the screen, optionally spoken. It also serves the HUD's `/status` and `/event`, so it **replaces** `claude_hud_amoled/pc/ble_bridge.py` |
| **`selfcheck/`** | post-deploy **smoke test**. The firmware emits `[SELFCHECK]` lines on serial and `selfcheck.ps1` turns them into a PASS/FAIL exit code. See that folder's README |

### Using ble_pc
```powershell
cd project\samples\ble_pc
venv\Scripts\python send_ble.py "안녕하세요"
venv\Scripts\python greet.py
```

---

## Building from the CLI (arduino-cli), no IDE

Headless build and upload with **arduino-cli** works, reusing the ESP32 core and libraries that are already
installed. Full command set in **[CLIBUILD.md](CLIBUILD.md)**.
```powershell
# compile and upload hello_lcd in one go
arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/hello_lcd
```

---

## AMOLED-1.75C (ESP-IDF + ESP-Brookesia) — the main board

Waveshare **ESP32-S3-Touch-AMOLED-1.75C**: a 466×466 round AMOLED (CO5300), ESP32-S3R8, 32MB flash,
8MB PSRAM, BLE 5 and WiFi, aluminium case. Native USB (VID 303A), which is **COM7** on this PC. Built with
**ESP-IDF v5.5.5**, not the Arduino IDE.

### One-time setup
1. ESP-IDF v5.5.5 at `C:\esp\v5.5.5` (installed with Espressif's `eim`; eim 0.1.7 skips creating the Python
   venv, so `idf_tools.py install-python-env` was run separately —
   `project/samples/claude_hud_amoled/idf-env.ps1` sets all of these paths for you)
2. The Waveshare board repo:
   `git clone --depth 1 https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-1.75C C:\esp\ws-amoled-175c`
   (the 44 MB Brookesia core is referenced from there, not vendored; point `WS_AMOLED_REPO` elsewhere if you
   cloned it elsewhere)
3. Factory recovery image: `Firmware/*FactoryOnly*.bin` in that repo → `esptool write_flash 0x0`

### Build and flash
```powershell
cd project\samples\claude_hud_amoled
. .\idf-env.ps1                      # activate ESP-IDF
idf.py set-target esp32s3            # first time only
idf.py -p COM7 build flash           # everything; afterwards idf.py -p COM7 app-flash (~20 s)
```

### Rules for adding an app
- An app is one folder, `components/brookesia_app_<name>/`. Derive from
  `esp_brookesia::systems::phone::App`, implement `run()`/`back()`, and register at the end of the .cpp with
  `ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(...)` — the launcher picks it up. `main/main.cpp` stays untouched.
- The icon is a 112×112 ARGB8888 LVGL C array (see `tools/gen_icon*.py`). The component's CMake needs
  `WHOLE_ARCHIVE`.
- Apps are statically linked, so adding or changing one **rebuilds and reflashes the whole image** (an
  incremental build is 1–2 minutes).
- Templates: `components/brookesia_app_chat/` (UI + BLE + audio) or `brookesia_app_claude_hud/` (UI + BLE).

### Connecting a PC (Claude Code → device, BLE only)
```powershell
cd project\samples\amoled_chat_host
pwsh -ExecutionPolicy Bypass -File install.ps1 -AutoStart
```
This builds the ASP.NET host, stops the older Python bridge (BLE admits exactly one owner), wires the Claude
Code hooks, and starts the host. Hooks and statusLine only POST to the localhost host, which forwards over
BLE; if BLE is down the message is dropped and a warning is logged, with no fallback transport. Kept
**entirely separate** from the LCD-1.28's `claude_hud/pc` (installed under `~/.claude/hud_amoled/`).

The lighter, HUD-only alternative is still there: `claude_hud_amoled/pc/install.ps1` plus `start_bridge.ps1`.

## Notes
- BLE and UTF-8 carry **every language** correctly. What appears on screen is limited to the glyphs the
  loaded font has (the default `korean1` font covers Latin plus Hangul). Per-script font selection would
  extend that.
- If an upload stalls at `Connecting...`, hold **BOOT**, tap **RESET**, release BOOT, and upload again.
