# Arduino — Waveshare ESP32-S3-LCD-1.28 / ESP32-S3-Touch-AMOLED-1.75C

한국어 · *[English](README.en.md)*

> **두 기기**를 다룬다. ① 원조 **LCD-1.28** (Arduino IDE, 아래 안내) ② 2026-09 도입한 **AMOLED-1.75C** (ESP-IDF + ESP-Brookesia, [아래 절](#amoled-175c-esp-idf--esp-brookesia--주력-기기) 참고). 현재 **주력은 AMOLED-1.75C** 이며 Brookesia 앱을 계속 증설할 예정.

Waveshare **ESP32-S3-LCD-1.28** (원형 240×240 GC9A01 IPS LCD, QMI8658 IMU) 보드로
기본 화면 출력부터 **BLE로 PC에서 글자를 받아 화면에 표시**하는 것까지 케이스별로 모은 저장소.

- 지금은 **샘플 수집 단계** → 모든 예제는 `project/samples/<케이스>` 아래에 둔다.
- 의미 있는 완성 기능은 이후 `project/<이름>` 으로 승격한다.

---

## 준비물 (처음 시작하는 사람용)

### 1. 하드웨어
- Waveshare **ESP32-S3-LCD-1.28** 보드
- **USB-C 케이블** (데이터 전송용)
- PC: Windows 10/11 (이 저장소는 Windows 11 + 내장/USB 블루투스로 테스트됨)

### 2. Arduino IDE 2.x 설치  (이 저장소는 **2.3.10** 로 검증)
**방법 A — winget (권장, 빠름)**
```powershell
winget install --id ArduinoSA.IDE.stable -e --accept-package-agreements --accept-source-agreements
```
**방법 B — 수동**: https://www.arduino.cc/en/software → "Arduino IDE 2.x" 다운로드·설치

### 3. ESP32 보드 코어 설치  (검증: esp32 by Espressif **3.3.11**)
1. Arduino IDE → **File → Preferences → Additional boards manager URLs** 에 추가:
   ```
   https://raw.githubusercontent.com/espressif/arduino-esp32/gh-pages/package_esp32_index.json
   ```
2. 왼쪽 **Boards Manager** → `esp32` 검색 → **"esp32 by Espressif Systems"** 설치 (수백 MB)

### 4. 라이브러리 설치  (Library Manager, 책 아이콘)
| 라이브러리 | 용도 |
|---|---|
| **GFX Library for Arduino** (Arduino_GFX, by moononournation) | GC9A01 LCD 구동 |
| **U8g2** (by oliver) | 한글 등 다국어 유니폰트 (`ble_lcd`에서 사용) |

### 5. USB 드라이버 / 포트
- 보드의 USB-Serial 칩은 **CH343** → Windows 11은 보통 자동 인식, **COM 포트**로 잡힘
- 안 잡히면 WCH CH343 드라이버 설치: https://www.wch.cn/downloads/CH343SER_EXE.html
- 장치관리자에서 `USB-Enhanced-SERIAL CH343 (COMx)` 확인

### 6. 보드/옵션 선택  (Tools 메뉴)
| 항목 | 값 |
|---|---|
| Board | **ESP32S3 Dev Module** |
| Port | CH343 로 잡힌 **COMx** |
| Flash Size | **16MB (128Mb)** |
| PSRAM | **QSPI PSRAM** |
| USB CDC On Boot | Enabled (선택) |
| Partition Scheme | 기본. 단 **`ble_lcd`는 "Huge APP (3MB No OTA/1MB SPIFFS)"** 필수 |

### 7. (선택) PC BLE 도구용 Python
`project/samples/ble_pc` 를 쓰려면 Python 3.x 필요:
```powershell
cd project\samples\ble_pc
python -m venv venv
venv\Scripts\python -m pip install -r requirements.txt
```

---

## 검증된 하드웨어 / LCD 핀맵 (esptool 로 확인)
- 칩: ESP32-S3 (rev v0.2), Wi-Fi + **BLE 5** (Bluetooth Classic 미지원)
- Flash **16MB (Quad)**, PSRAM **2MB (QSPI, 내장)**, USB-Serial **CH343**

| 신호 | GPIO | 비고 |
|---|---|---|
| DC | 8 | |
| CS | 9 | |
| SCK | 10 | |
| MOSI | 11 | |
| **RST** | **14** | 흔히 잘못 알려진 12 아님 (12는 MISO) |
| **BL** | **2** | 흔히 잘못 알려진 40 아님 |

---

## 샘플 (`project/samples/`)
| 케이스 | 설명 |
|---|---|
| **`hello_lcd/`** | GC9A01 화면에 `HELLO` 출력 — 기본 동작 확인 |
| **`ble_lcd/`** | BLE Nordic UART Service(NUS) 서버. 폰/PC에서 보낸 UTF-8 문자열을 원형 화면에 표시. 한글은 U8g2 유니폰트(`u8g2_font_unifont_t_korean1`)로 렌더링. **Huge APP 파티션 필요** |
| **`ble_pc/`** | PC(Windows)에서 BLE로 글자를 보내는 Python 도구(`bleak`)<br>· `send_ble.py "text"` — 한 번 전송 / 인자 없으면 대화식<br>· `greet.py` — 7개국 인사말 연속 전송(폰트 커버리지 테스트)<br>· `ascii_art.py` — 자유 텍스트 · ASCII 아트 · 표정/스피너/바운스 애니메이션 |
| **`claude_hud/`** | (LCD-1.28) Claude Code 진행상황+사용량 HUD. USB 시리얼 + BLE + WiFi HTTP 3종 수신. PC 설치기 `pc/install.ps1` |
| **`claude_hud_amoled/`** | **(AMOLED-1.75C, ESP-IDF) 주력.** ESP-Brookesia phone UI 에 앱 3개(Claude HUD / Chat / Settings) 추가. **BLE 단일 전송**. 앱 증설의 기준 프로젝트 |
| **`amoled_chat_host/`** | **(AMOLED-1.75C, PC 측) 음성 챗봇 호스트.** ASP.NET Core 가 BLE 연결을 쥐고 기기 마이크 음성을 Whisper 로 받아쓴 뒤 등록된 챗 CLI(`netclaw chat -p` 기본)에 물어 답을 화면으로 돌려준다. HUD 훅용 `/status`·`/event` 도 그대로 제공하므로 `claude_hud_amoled/pc/ble_bridge.py` 를 **대체**한다 |
| **`selfcheck/`** | 배포 후 **셀프체크/스모크 테스트**. 펌웨어가 `[SELFCHECK]` 상태를 Serial로 내보내고 `selfcheck.ps1`이 PASS/FAIL 판정(종료코드). 자세히는 해당 폴더 README |

### ble_pc 사용 예
```powershell
cd project\samples\ble_pc
venv\Scripts\python send_ble.py "안녕하세요"
venv\Scripts\python greet.py
```

---

## CLI 빌드/업로드 (arduino-cli) — IDE 없이

IDE GUI 없이 **arduino-cli**로 헤드리스 빌드·업로드가 가능하다(검증됨). 이미 설치된
ESP32 코어·라이브러리를 그대로 재사용한다. 설치·명령어 전체는 **[CLIBUILD.md](CLIBUILD.md)** 참고.
```powershell
# hello_lcd 컴파일 + 업로드 (한 방)
arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/hello_lcd
```

---

## AMOLED-1.75C (ESP-IDF + ESP-Brookesia) — 주력 기기

Waveshare **ESP32-S3-Touch-AMOLED-1.75C**: 466×466 원형 AMOLED(CO5300), ESP32-S3R8, 32MB flash, 8MB PSRAM,
BLE 5 / WiFi, 알루미늄 케이스. 네이티브 USB(VID 303A) → 이 PC 에서 **COM7**. Arduino IDE 가 아니라 **ESP-IDF v5.5.5** 로 빌드한다.

### 준비 (한 번만)
1. ESP-IDF v5.5.5 — `C:\esp\v5.5.5` (Espressif `eim` 설치기로 설치됨. eim 0.1.7 은 Python venv 를 안 만들어서
   `idf_tools.py install-python-env` 를 따로 실행함 → `project/samples/claude_hud_amoled/idf-env.ps1` 이 이 경로들을 모두 설정)
2. Waveshare 보드 저장소 — `git clone --depth 1 https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-1.75C C:\esp\ws-amoled-175c`
   (Brookesia 코어 44MB 를 여기서 참조. 다른 경로면 env `WS_AMOLED_REPO`)
3. 공장 펌웨어 복구용: 위 저장소 `Firmware/*FactoryOnly*.bin` → `esptool write_flash 0x0`

### 빌드 / 플래시
```powershell
cd project\samples\claude_hud_amoled
. .\idf-env.ps1                      # ESP-IDF 활성화
idf.py set-target esp32s3            # 최초 1회
idf.py -p COM7 build flash           # 전체 / 이후엔 idf.py -p COM7 app-flash (앱 파티션만, ~20s)
```

### 앱(위젯) 증설 규칙
- 앱 = `components/brookesia_app_<이름>/` 폴더 하나. `esp_brookesia::systems::phone::App` 상속, `run()`/`back()` 구현,
  파일 끝 `ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(...)` 로 등록 → 런처에 자동 추가. `main/main.cpp` 는 건드리지 않는다.
- 아이콘은 112×112 ARGB8888 LVGL C 배열 (`tools/gen_icon.py` 참고). 컴포넌트 CMake 에 `WHOLE_ARCHIVE` 필수.
- 앱은 펌웨어에 정적 링크된다 → 앱 추가/수정 시 **기존 앱 포함 이미지 전체를 다시 빌드·플래시** (증분 빌드라 1~2분).
- 템플릿: `components/brookesia_app_claude_hud/` (UI + BLE 수신) 또는 Waveshare 저장소의 `brookesia_app_squareline_demo`.

### PC 연동 (Claude Code → 기기, BLE 전용)
```powershell
cd project\samples\claude_hud_amoled\pc
pwsh -ExecutionPolicy Bypass -File install.ps1 -AutoStart   # settings.json 백업(.amoledbak)+훅/statusLine 병합, 로그인시 브리지 자동시작
pwsh -File start_bridge.ps1                                 # BLE 브리지(상시 연결) 실행, http://127.0.0.1:8765/health 로 확인
```
훅/statusLine 은 localhost 브리지에만 POST 하고 브리지가 BLE 로 전달한다. BLE 가 끊기면 메시지는 버리고 `~/.claude/hud/ble_bridge.log` 에 경고만 남긴다(폴백 없음).
이전 LCD-1.28 용 `claude_hud/pc` 와는 **완전히 별개** (설치 경로 `~/.claude/hud_amoled/`).

## 메모
- BLE 통신·UTF-8은 **모든 언어 정상 전송**됨. 화면 표시는 폰트가 가진 글리프에 한함
  (기본 `korean1` 폰트 = 라틴 + 한글). 다국어 표시는 문자별 폰트 자동 선택으로 확장 가능.
- 업로드가 `Connecting...`에서 멈추면 **BOOT 누른 채 RESET 한 번 → BOOT 떼고** 다시 Upload.
