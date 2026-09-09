# claude_hud_amoled — Claude Code HUD as an ESP-Brookesia app (Bluetooth only)

Waveshare **ESP32-S3-Touch-AMOLED-1.75C**(466×466 원형 AMOLED, ESP32-S3R8, 32MB flash, 8MB PSRAM)의
공장 펌웨어인 **ESP-Brookesia phone UI**에 "Claude HUD" 앱(위젯)을 추가한 ESP-IDF 프로젝트.
런처에 아이콘이 생기고, 열면 3개 타일을 좌우 스와이프로 본다.

> **이 기기는 BLE 단일 전송이다.** WiFi/USB 수신, 폴백 없음(오버헤드 회피). BLE 실패는 경고 로그 + INFO 타일 표시만.
> 이전 보드 `project/samples/claude_hud`(USB+BLE)와 **코드/PC 설치부가 완전히 분리**되어 있다(공유 파일 없음).

| 타일 | 내용 |
|---|---|
| SESSIONS | 활성 세션 수, 세션별 `host:label 활동` (초록=작업중, 파랑=완료, 주황=idle), 바깥 링 = 전체 활동 여부 |
| USAGE | 누적 cost($), 세션 수, ctx 사용%, 5h/7d 한도 사용% 막대 |
| INFO | BLE 상태(advertising/connected/에러), 수신 카운트·마지막 수신 경과, 업타임, 밝기 슬라이더 |

## 통신: BLE NUS (프로토콜은 claude_hud 와 동일)
- 광고명 `claude-hud`, 서비스 `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`, RX(`…0002`)에 `S {json}` / `E {json}` 한 줄씩 write.
- 부팅 즉시 광고. 중앙 장치가 끊기면 자동 재광고. MTU 512 요청.
- 알루미늄 케이스라 BLE 거리는 실측 필요.

## PC 연동 (`pc/`, 이 기기 전용)
```
Claude Code hooks/statusLine ─(localhost HTTP)─► ble_bridge.py ─(BLE, 상시 연결)─► 기기
```
훅마다 BLE를 새로 붙이면 수 초씩 걸리므로 **브리지 1개가 연결을 유지**하고 훅은 `http://127.0.0.1:8765`에만 POST 한다.
브리지는 BLE가 끊기면 메시지를 버리고 `~/.claude/hud/ble_bridge.log`에 WARNING 만 남긴다(폴백 없음).

```powershell
cd project/samples/claude_hud_amoled/pc
pwsh -ExecutionPolicy Bypass -File install.ps1 [-AutoStart]   # settings.json 백업(.amoledbak)+병합, hooks/statusLine
pwsh -File start_bridge.ps1                                    # venv+bleak 자동 준비, 브리지 백그라운드 실행
Invoke-RestMethod http://127.0.0.1:8765/health                 # {"ble":true,...} 확인 후 Claude Code 재시작
pwsh -File uninstall.ps1                                       # 되돌리기
```
수동 테스트: `Invoke-RestMethod -Uri http://127.0.0.1:8765/status -Method Post -ContentType application/json -Body '{"session":"t1","label":"test","cost_usd":1.25,"context_used_pct":30}'`

## 빌드 / 플래시 (ESP-IDF v5.5.5, Windows)
```powershell
cd project/samples/claude_hud_amoled
. .\idf-env.ps1                     # C:\esp\v5.5.5 활성화 (+ WS_AMOLED_REPO)
idf.py set-target esp32s3           # 최초 1회
idf.py -p COM7 build flash monitor  # 콘솔은 네이티브 USB(COM7)
```
- `brookesia_core`(44MB)는 복사하지 않고 Waveshare 보드 저장소(`C:\esp\ws-amoled-175c`, env `WS_AMOLED_REPO`)를 `EXTRA_COMPONENT_DIRS`로 참조.
- BSP(`waveshare/esp32_s3_touch_amoled_1_75c`), LVGL 9.5 등은 첫 빌드 때 컴포넌트 매니저가 받는다(`managed_components/`, git-ignore).
- 공장 펌웨어 복구: Waveshare 저장소 `Firmware/ESP32-S3-Touch-AMOLED-1.75C-FactoryOnly-260114.bin` 을 `esptool write_flash 0x0`.

## 구조
```
CMakeLists.txt / sdkconfig.defaults / partitions.csv(factory 8M) / idf-env.ps1
main/main.cpp                      Waveshare 데모 그대로 (registry 로 앱 자동 설치)
components/brookesia_app_claude_hud/
  claude_hud_app.{hpp,cpp}         phone::App (init: BLE 시작, run: LVGL 타일 UI, 0.5s 타이머 갱신)
  hud_state.{hpp,cpp}              세션/한도 모델 + cJSON 파싱 (뮤텍스)
  hud_ble.cpp                      NimBLE NUS 서버 (유일한 전송)
  assets/…_112_112.c               런처 아이콘 (tools/gen_icon.py)
pc/  ble_bridge.py, start_bridge.ps1, send_event.ps1, hud_statusline.ps1, install.ps1, uninstall.ps1
```
