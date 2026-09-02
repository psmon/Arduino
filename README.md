# Arduino — Waveshare ESP32-S3-LCD-1.28

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
| **`ble_pc/`** | PC(Windows)에서 BLE로 글자를 보내는 Python 도구(`bleak`)<br>· `send_ble.py "text"` — 한 번 전송 / 인자 없으면 대화식<br>· `greet.py` — 7개국 인사말 연속 전송(폰트 커버리지 테스트) |

### ble_pc 사용 예
```powershell
cd project\samples\ble_pc
venv\Scripts\python send_ble.py "안녕하세요"
venv\Scripts\python greet.py
```

---

## 메모
- BLE 통신·UTF-8은 **모든 언어 정상 전송**됨. 화면 표시는 폰트가 가진 글리프에 한함
  (기본 `korean1` 폰트 = 라틴 + 한글). 다국어 표시는 문자별 폰트 자동 선택으로 확장 가능.
- 업로드가 `Connecting...`에서 멈추면 **BOOT 누른 채 RESET 한 번 → BOOT 떼고** 다시 Upload.
