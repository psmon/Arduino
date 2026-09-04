# 새 컴퓨터에 HUD 붙이기 (발신 전용 / Arduino SDK 불필요)

이미 `claude_hud` 펌웨어가 올라간 보드를 **다른 컴퓨터**에 USB로 꽂아, 그 머신의 Claude Code
진행상황·사용량을 HUD로 보내는 절차. 빌드는 다른 곳에서 하므로 이 머신에는
**Arduino IDE / arduino-cli / Python 모두 필요 없다.** 발신부는 PowerShell 내장 기능만 쓴다.

> 필요한 것: Windows + PowerShell(설치기는 pwsh 7, 실행 hook 은 Windows PowerShell 5.1) + 데이터용 USB 케이블.

## 0. 전제 확인
- 보드에 `claude_hud` 펌웨어가 이미 있음 (부팅 시 LCD에 화면이 뜸). 이 문서는 **펌웨어를 건드리지 않는다.**
- 리포는 이 머신에 clone 되어 있거나, 최소한 `pc/` 폴더만 복사돼 있으면 된다.

## 1. COM 포트 확인 — **머신마다 번호가 다르다** (반드시 실측)
보드의 USB-Serial 은 WCH **CH343** (`VID_1A86&PID_55D3`). 그 COM 포트를 찾는다.
```powershell
# 방법 A: 리포 스크립트 (성공 시 stdout 은 포트명만, 종료코드 0)
pwsh -File project\samples\claude_hud\pc\find_port.ps1

# 방법 B: 수동 확인 (VID 로 판별 — 이름만 보면 블루투스 COM 과 헷갈린다)
Get-CimInstance Win32_PnPEntity |
  Where-Object { $_.PNPDeviceID -match 'VID_1A86' -and $_.Name -match 'COM\d+' } |
  Select-Object Name, DeviceID, Status
```
기대 출력 예:
```
Name     : USB-Enhanced-SERIAL CH343(COM3)
DeviceID : USB\VID_1A86&PID_55D3\5B91044740
Status   : OK
```
`ACPI\PNP0501` 로 나오는 COM1/COM2 같은 것은 메인보드 내장 포트, 보드가 아니다.
아무것도 안 잡히면 → CH343 드라이버 설치: <https://www.wch.cn/downloads/CH343SER_EXE.html>

### 실측 기록 (머신별로 다름 — 하드코딩 금지)
| 머신 | 포트 |
|---|---|
| 최초 개발 머신 | COM6 |
| `SAM` (2026-09 부착) | COM3 |

## 2. 설치 (권장: 포트 자동검출)
```powershell
pwsh -ExecutionPolicy Bypass -File project\samples\claude_hud\pc\install.ps1 -Url serial:auto
```
`serial:auto` 가 `find_port.ps1` 로 CH343 포트를 찾아 `serial:COMx` 로 기록한다.
포트가 바뀌면 **같은 명령을 다시 실행**하면 된다(idempotent). 직접 지정도 가능: `-Url serial:COM3`.

설치기가 하는 일:
| 대상 | 내용 |
|---|---|
| `~/.claude/hud/` | `send_event.ps1`, `hud_statusline.ps1` 복사 |
| `~/.claude/hud_url.txt` | `serial:COM3` 기록 (전송 대상) |
| `~/.claude/settings.json` | hooks 7종 추가(기존 hook 보존·중복 방지), statusLine 설정 |
| `~/.claude/settings.json.hudbak` | 설치 전 백업 |

**기존 statusLine 이 있으면 덮어쓰지 않고 래핑한다.** 원래 명령은 `~/.claude/hud/inner_statusline.txt`
에 보존되고, 터미널에는 원래 상태줄이 그대로 나오면서 같은 JSON 이 HUD 로도 전송된다.
(예: AgentZeroLite `az-hud-wrapper.js` 사용 중이던 머신에서 정상 래핑 확인됨.)

## 3. 검증 (Claude Code 재시작 전에 미리 확인 가능)
```powershell
# 3a. 보드가 시리얼을 받는지 직접 확인 — LCD 가 갱신되면 OK
$sp = New-Object System.IO.Ports.SerialPort('COM3',115200,'None',8,'One')
$sp.DtrEnable=$false; $sp.RtsEnable=$false   # DTR/RTS 켜면 ESP32 가 리셋된다
$sp.WriteTimeout=500; $sp.Open()
$sp.WriteLine('S {"session":"t","model":"Opus 5","cost_usd":1.23,"context_used_pct":42,"label":"test","host":"' + $env:COMPUTERNAME + '"}')
$sp.Close()

# 3b. 설치된 statusLine 스크립트 (원래 상태줄이 출력되면 래핑 성공)
'{"session_id":"v1","model":{"display_name":"Opus 5"},"cost":{"total_cost_usd":0.42},"context_window":{"used_percentage":17},"workspace":{"current_dir":"D:\code"}}' |
  powershell -NoProfile -File "$env:USERPROFILE\.claude\hud\hud_statusline.ps1"

# 3c. 설치된 hook 스크립트 (exit 0 이면 OK, 출력은 없음)
'{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"verify"},"session_id":"v1","cwd":"D:\code"}' |
  powershell -NoProfile -File "$env:USERPROFILE\.claude\hud\send_event.ps1"
```
LCD 에 `host`(= `$env:COMPUTERNAME`)와 `label`, cost/ctx 가 뜨면 연결 완료.

## 4. 적용
**Claude Code 재시작.** hooks / statusLine 은 재시작 후부터 동작한다.

## 트러블슈팅
| 증상 | 원인 / 조치 |
|---|---|
| 아무것도 안 뜸 | 다른 프로그램이 COM 을 점유. **COM 은 한 번에 한 프로세스만** — Arduino IDE Serial Monitor, `arduino-cli monitor`, 터미널 프로그램을 모두 닫는다 |
| 전송할 때마다 화면이 리셋/재부팅 | DTR/RTS 가 켜져 있음. 발신 스크립트는 둘 다 `false` 로 두므로, 직접 짠 코드라면 그 부분을 확인 |
| `find_port.ps1` 이 못 찾음 | 케이블이 충전 전용일 수 있음(데이터 케이블 사용) / CH343 드라이버 미설치 |
| 포트는 맞는데 LCD 무반응 | 보드에 올라간 펌웨어가 `claude_hud` 가 아닐 수 있음. 다른 곳에서 재빌드·업로드 필요 |
| 이벤트가 가끔 누락 | 정상. USB 모드는 동시 세션이 많으면 COM 경합으로 일부가 조용히 버려진다(무해) |
| 상태줄이 사라짐 | `~/.claude/settings.json.hudbak` 로 복원하거나 `uninstall.ps1` 실행 |

## 해제
```powershell
pwsh -File project\samples\claude_hud\pc\uninstall.ps1   # 백업 복원
```

## USB 대신 다른 전송을 쓰려면
| 상황 | 선택 |
|---|---|
| 집처럼 PC↔디바이스 통신이 열린 WiFi | `install.ps1 -Url http://<디바이스IP>:8080` |
| 사무실 게스트망 등 클라이언트 격리 | **USB (이 문서)** — 네트워크와 무관 |
| 케이블도 네트워크도 못 쓸 때 | BLE (`pc/send_ble.py`, bleak 필요 → Python 필요) |
