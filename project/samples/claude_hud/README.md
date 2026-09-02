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

## 엔드포인트
| 메서드 | 경로 | 용도 |
|---|---|---|
| POST | `/status` | 사용량/모델/비용 (statusline) |
| POST | `/event` | 진행 활동 (hooks) |
| GET | `/health` | 셀프체크 JSON |
| GET | `/` | 안내 |

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
