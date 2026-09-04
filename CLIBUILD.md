# CLIBUILD.md — arduino-cli 로 빌드/업로드 (IDE 없이)

Arduino IDE GUI 없이 **arduino-cli** 로 헤드리스 빌드·업로드하는 방법.
이 저장소에서 **실제로 검증된 명령어**만 정리했다. (보드: Waveshare ESP32-S3-LCD-1.28)

---

## 0. arduino-cli 설치

**A) 단독 설치 (권장)** — 최신 바이너리를 받아 PATH에 둔다.
```powershell
# 다운로드/압축해제 (예: C:\Users\<you>\tools\arduino-cli.exe)
Invoke-WebRequest "https://downloads.arduino.cc/arduino-cli/arduino-cli_latest_Windows_64bit.zip" -OutFile "$env:TEMP\acli.zip"
Expand-Archive "$env:TEMP\acli.zip" -DestinationPath "C:\Users\$env:USERNAME\tools" -Force
# 사용자 PATH에 tools 폴더 추가 후 새 터미널에서 'arduino-cli' 사용
```

**B) Arduino IDE 2.x 번들 사용** — 설치돼 있으면 아래 exe를 그대로 호출.
```
...\WinGet\Packages\ArduinoSA.IDE.stable_*\resources\app\lib\backend\resources\arduino-cli.exe
```

> 이 세션 검증 버전: **arduino-cli 1.5.2**. 데이터 폴더(코어)와 라이브러리는
> IDE와 **같은 위치**(`%LOCALAPPDATA%\Arduino15`, `Documents\Arduino\libraries`)를
> 공유하므로, IDE로 이미 설치한 ESP32 코어·라이브러리를 그대로 재사용한다.

---

## 1. 환경 확인
```powershell
arduino-cli version
arduino-cli core list          # esp32:esp32 3.3.11 보여야 함
arduino-cli lib list           # GFX Library for Arduino, U8g2 보여야 함
arduino-cli board list         # 연결된 보드/포트 확인 (CH343 = 우리 보드)
```

## 2. 보드 포트 찾기 (Windows)
```powershell
# CH343 이 우리 보드. 이름 대신 VID 로 판별하는 편이 확실하다(블루투스/내장 COM 과 혼동 방지).
Get-CimInstance Win32_PnPEntity | ? { $_.PNPDeviceID -match 'VID_1A86' -and $_.Name -match 'COM\d+' } | % Name
```
**COM 번호는 머신마다 다르다** — 실측: 최초 개발 머신 **COM6**, `SAM` **COM3**.
아래 예시의 `COM6` 은 자리표시자이니 위 명령으로 확인한 값으로 바꿔 쓸 것.

---

## 3. 빌드 (compile)

보드 옵션은 **FQBN** 으로 넘긴다. 스케치별 FQBN:

| 스케치 | FQBN |
|---|---|
| `hello_lcd` | `esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M` |
| `ble_lcd` | `esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app,CDCOnBoot=cdc` |

```powershell
# 가장 쉬운 예제
arduino-cli compile --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/hello_lcd

# BLE + LCD (Huge APP 파티션 필수)
arduino-cli compile --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app,CDCOnBoot=cdc" project/samples/ble_lcd
```
성공 예시 출력: `스케치는 프로그램 저장 공간 361704 바이트(27%)를 사용...`

## 4. 컴파일 + 업로드 (한 방) — 검증됨
```powershell
arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/hello_lcd
```
성공 예시 출력:
```
Wrote 361856 bytes ... in 2.6 seconds
Hash of data verified.
Hard resetting via RTS pin...
```

## 5. 업로드만 (이미 컴파일된 경우)
```powershell
arduino-cli upload -p COM6 --fqbn "<위 FQBN>" project/samples/hello_lcd
```

## 6. 시리얼 모니터
```powershell
arduino-cli monitor -p COM6 -c baudrate=115200
```

---

## 7. (선택) 재현 빌드 — `sketch.yaml` 프로파일

각 샘플의 `sketch.yaml` 에 보드옵션·코어·라이브러리 버전이 고정돼 있어,
`--fqbn` 없이 빌드할 수 있다. 격리 환경에 고정 버전을 **새로 받아** 빌드하므로
최초 1회는 느리다.
```powershell
arduino-cli compile project/samples/hello_lcd     # default_profile 사용
```
**주의:** 프로파일은 라이브러리를 **인덱스에서** 받는다. 수동 설치한 U8g2 는
개발판(2.37.1)이라 인덱스 최신(2.36.19)과 핀이 어긋나 `ble_lcd` 프로파일 빌드는
실패한다. 프로파일로 쓰려면 `ble_lcd/sketch.yaml` 의 `U8g2 (2.37.1)` 을 인덱스에
존재하는 버전(예: `2.36.19`)으로 맞추고 `arduino-cli lib update-index` 를 먼저 실행.
> 일상 개발에는 **로컬 설치를 재사용하는 `--fqbn` 방식(3~5)** 이 빠르고 확실하다.

---

## 8. 문제 해결
- **업로드가 `Connecting...` 에서 멈춤** → 보드를 다운로드 모드로: **BOOT 누른 채 RESET 한 번 → BOOT 떼고** 다시 업로드.
- **포트 사용 중(Access denied)** → Arduino IDE Serial Monitor / PC BLE 도구 / `arduino-cli monitor` 가 COM 포트를 잡고 있음. 먼저 닫을 것.
- **코어/라이브러리 안 보임** → `arduino-cli core update-index` / `arduino-cli lib update-index` 후 재확인.
