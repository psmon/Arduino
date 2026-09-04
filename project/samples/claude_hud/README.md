# claude_hud — Claude Code 진행상황 + 사용량 HUD

이 PC에서 도는 Claude Code의 **진행상황**(무엇을 하는 중)과 **사용량**(누적 비용 + 5h/7d 한도
잔여)을 LAN HTTP로 받아 원형 LCD에 표시하는 물리 대시보드.

```
Claude Code (이 PC)
  ├─ statusLine 스크립트 ─ model/cost/context/rate_limits ─► POST /status
  └─ hooks(PreToolUse/PostToolUse/Stop/…) ─ 활동 ──────────► POST /event
                                                    │ (LAN HTTP)
                                                    ▼
  ESP32-S3-(Touch-)LCD-1.28  ── WiFi STA, HTTP 서버 :8080 ── 원형 LCD
```

## 화면
- **진행(Screen 0)**: model, 상태(idle/tool/thinking/done), 현재 활동, 경과시간, branch, 상태 링
- **사용량(Screen 1)**: 누적 cost($), context 사용%, 5h·7d 한도 사용% 막대 (없으면 cost+ctx)
- Phase A는 터치 전이라 **6초 자동 순환**. Phase B에서 터치 스와이프로 전환 예정.

## 엔드포인트 / 프로토콜 (전송 3종)
| 전송 | 방식 | 용도 |
|---|---|---|
| **HTTP(WiFi/리모트)** | POST `/status`, POST `/event`, GET `/health`, GET `/` | 네트워크로 원격 전송 |
| **USB(유선)** | 시리얼 한 줄: `S {json}` = status, `E {json}` = event (115200) | WiFi 없이 유선 |
| **BLE(무선)** | NUS(`6E400001…`) RX 에 `S {json}`/`E {json}` 기록. 광고명 `claude-hud` | 케이블·네트워크 없이 무선 (격리망에서도 OK) |

BLE 발신은 `pc/send_ble.py`(bleak 필요; `ble_pc/venv` 재사용 가능): `python send_ble.py demo`.
**부팅 시 BLE 를 가장 먼저 시작**(기본 전송) — WiFi 유무와 무관하게 즉시 광고하고, WiFi 는
부가로 접속(10초 타임아웃, 실패해도 BLE/USB 로 동작).

두 전송을 **동시에 받아도 안전**하다. 세션은 `session_id`로 키잉되어 같은 세션은 슬롯 하나를
갱신(중복/이중합산 없음), 다른 세션(다른 머신)은 각각 표시된다. `host` 필드로 어느 머신인지 구분.

### 네트워크 환경별 전송 선택 (실측 정리)
| 환경 | HTTP(WiFi) | USB(유선) | 비고 |
|---|---|---|---|
| **집** | ✅ 가능 | ✅ | 공유기 방화벽을 직접 설정해 PC↔디바이스 통신 허용 |
| **사무실(게스트/보안망)** | ❌ 차단 | ✅ **권장** | 클라이언트 격리(AP isolation)로 PC와 디바이스가 서로 다른 서브넷 → HTTP 불가. 유선은 네트워크와 무관하게 동작 |

> 요점: **격리·보안 강화된 네트워크에선 USB 시리얼 모드**(`hud_url.txt = serial:COM6` 또는 `serial:auto`)를 쓴다.
> 디바이스의 멀티 WiFi 접속 자체는 어디서든 되지만, PC→디바이스 전송이 막히는 것은 별개다.

## 여러 컴퓨터에서 보내기 (설치기)
> 이미 펌웨어가 올라간 보드를 **새 컴퓨터에 USB로 부착**하는 전체 절차(포트 실측 → 설치 → 검증 →
> 트러블슈팅)는 **[`pc/ATTACH.md`](pc/ATTACH.md)** 참고. 그 머신엔 **Arduino IDE/arduino-cli/Python이
> 필요 없다** — 발신부는 PowerShell 내장 기능만 쓴다.

다른 컴퓨터에서도 그 머신의 Claude Code를 이 HUD로 보내려면 `pc/install.ps1` 실행:
```powershell
# WiFi(리모트) — 디바이스 IP
pwsh -ExecutionPolicy Bypass -File pc/install.ps1 -Url http://192.168.0.88:8080
# USB(유선) — COM 포트 자동 검출 (권장, 컴퓨터마다 번호 다름)
pwsh -ExecutionPolicy Bypass -File pc/install.ps1 -Url serial:auto
# USB(유선) — 포트를 직접 지정
pwsh -ExecutionPolicy Bypass -File pc/install.ps1 -Url serial:COM3
```
> `serial:auto` 는 보드의 **CH343(WCH VID_1A86)** COM 포트를 자동으로 찾는다(`pc/find_port.ps1`).
> 포트만 확인하려면: `pwsh -File pc/find_port.ps1`
> **COM 번호는 머신마다 다르다** — 최초 개발 머신 COM6, `SAM` COM3. 하드코딩하지 말고 `serial:auto` 를 쓸 것.
- 발신부(`send_event.ps1`, `hud_statusline.ps1`)를 `~/.claude/hud/`에 설치, `~/.claude/hud_url.txt`에 주소 기록
- `settings.json`을 **백업(.hudbak) 후 병합** — 기존 statusLine은 래핑해 보존, hooks는 추가만(중복 방지, idempotent)
- **의존성 없음**(PowerShell 내장). 적용하려면 Claude Code 재시작
- **인코딩은 전 구간 UTF-8 고정.** 래핑된 기존 statusLine 출력은 문자열로 캡처하지 않고
  **바이트 그대로 통과**시킨다(콘솔 코드페이지 949/437 에서 한글·`│`·`█` 깨짐 방지).
  시리얼도 `SerialPort.Encoding` 을 UTF-8 로 바꾼다(기본값 us-ascii → 한글이 `?`). 상세: [`pc/ATTACH.md`](pc/ATTACH.md#인코딩-규칙-한글-깨짐-방지--반드시-지킬-것)
- 해제: `pwsh -File pc/uninstall.ps1` (백업 복원)

## 빌드/업로드 (arduino-cli)
```powershell
# 1) secrets.h 준비: secrets.h.example 을 복사해 WiFi SSID/PW 채우기 (secrets.h 는 gitignore)
# 2) 빌드+업로드
arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app" project/samples/claude_hud
```
부팅 후 화면에 IP / `claude-hud.local` 표시. 라이브러리: Arduino_GFX, ArduinoJson (내장 WiFi/WebServer/ESPmDNS).

## PC 연동 & 테스트
`pc/` 폴더 참고:
- `pc/demo.py <url>` — 가짜 세션으로 두 화면 갱신 테스트(라이브 세션 불필요)
- `pc/statusline.py`, `pc/send_event.py`, `pc/hooks.snippet.json`, `pc/setup.md` — 실제 연동

## 화면 조작 (터치)
- 좌/우 스와이프 = SESSIONS ↔ USAGE 전환
- 아래→위 스와이프 = SETTINGS (IP/버전/세션수/밝기 + web 주소)
- SETTINGS에서 좌/우 탭 = 밝기 조절, 아래로 스와이프 = 복귀
- 설정 중 텍스트 입력(WiFi 등)은 브라우저(`http://<ip>:8080/`)에서 (예정)

## 상태 / 로드맵
- **Phase A**: WiFi + HTTP 서버 + 2화면 + `/health`. ✅
- **Phase B**: CST816S 터치 제스처 + 설정화면 + **더블버퍼(무깜박임)** + **U8g2 한글 폰트**. ✅ 실기 검증됨
- **다음(B2)**: 브라우저 설정 웹페이지(WiFi 등 텍스트 입력) + NVS 영구저장
- **Phase C**: 원호 게이지/스피너, 활동 히스토리

## 메모
- `rate_limits`(5h/7d)는 Pro/Max에서만 올 수 있음 → 없으면 cost+context%로 폴백.
- statusline은 API 응답 때만 갱신 → 유휴 시 화면은 마지막 상태 유지.
- 평문 HTTP·로컬 전용. `secrets.h` 는 절대 커밋 금지(gitignore됨).
