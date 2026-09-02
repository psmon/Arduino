# Arduino — Waveshare ESP32-S3-LCD-1.28 experiments

Waveshare **ESP32-S3-LCD-1.28** (round 240×240 GC9A01 IPS LCD, QMI8658 IMU) 보드로
기본 화면 출력부터 **BLE로 PC에서 글자를 받아 화면에 표시**하는 것까지 다룬다.

## 하드웨어 (esptool로 확인)
- 칩: ESP32-S3 (rev v0.2), Wi-Fi + **BLE 5** (Bluetooth Classic 미지원)
- Flash: **16MB (Quad)**, PSRAM: **2MB (QSPI, 내장)**
- USB-Serial: **CH343** 브리지 (자체 COM 포트)

## 검증된 LCD 핀맵 (GC9A01, Arduino_GFX)
| 신호 | GPIO | 비고 |
|---|---|---|
| DC | 8 | |
| CS | 9 | |
| SCK | 10 | |
| MOSI | 11 | |
| **RST** | **14** | 흔히 잘못 알려진 12 아님 (12는 MISO) |
| **BL** | **2** | 흔히 잘못 알려진 40 아님 |

## 폴더
- **`hello_lcd/`** — GC9A01 화면에 `HELLO` 출력 (기본 동작 확인)
- **`ble_lcd/`** — BLE Nordic UART Service(NUS) 서버. 폰/PC에서 보낸 UTF-8 문자열을
  원형 화면에 표시. 한글은 U8g2 유니폰트(`u8g2_font_unifont_t_korean1`)로 렌더링.
- **`ble_pc/`** — PC(Windows)에서 BLE로 글자를 보내는 Python 도구 (`bleak`)
  - `send_ble.py "text"` — 한 번 전송 / 인자 없으면 대화식
  - `greet.py` — 7개국 인사말 연속 전송(커버리지 테스트)

## Arduino IDE 설정
- Board: **ESP32S3 Dev Module**, Port: CH343 COM
- Flash Size: **16MB**, PSRAM: **QSPI PSRAM**
- `ble_lcd`는 크기가 커서 Partition Scheme: **Huge APP (3MB No OTA)** 필요
- 라이브러리: **GFX Library for Arduino** (Arduino_GFX), **U8g2** (한글 폰트)

## PC 도구 사용
```
cd ble_pc
python -m venv venv
venv\Scripts\python -m pip install -r requirements.txt
venv\Scripts\python send_ble.py "안녕하세요"
```

## 메모
- BLE 통신·UTF-8은 모든 언어 정상 전송됨. 화면 표시는 폰트가 가진 글리프에 한함
  (기본 korean1 폰트 = 라틴 + 한글). 다국어 표시는 문자별 폰트 자동 선택으로 확장 가능.
