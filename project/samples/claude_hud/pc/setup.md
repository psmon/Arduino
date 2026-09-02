# PC 연동 설정 (Windows)

이 PC의 Claude Code가 진행상황·사용량을 디바이스로 보내도록 설정한다.

## 1. 디바이스 주소 확인
`claude_hud` 펌웨어를 업로드하면 부팅 후 화면/시리얼에 **IP** 와 `claude-hud.local` 이 뜬다.
그 주소를 환경변수로 지정(현재 세션 + 영구):
```powershell
setx CLAUDE_HUD_URL "http://192.168.1.50:8080"   # 실제 IP 로 교체 (새 터미널부터 적용)
$env:CLAUDE_HUD_URL = "http://192.168.1.50:8080"  # 현재 터미널 즉시 적용
```
> mDNS가 되면 `http://claude-hud.local:8080` 도 가능(스크립트 기본값). 안정성은 IP 가 낫다.

## 2. 데이터 흐르는지 먼저 테스트 (라이브 세션 없이)
```powershell
python project\samples\claude_hud\pc\demo.py http://192.168.1.50:8080
```
→ 화면이 진행(활동)↔사용량으로 갱신되면 파이프라인 정상.

## 3. Claude Code 에 연결 (statusline + hooks)
**먼저 백업**: `copy "%USERPROFILE%\.claude\settings.json" "%USERPROFILE%\.claude\settings.json.bak"`

### 3a. 기존 statusLine 이 없는 경우 (새 설정)
`pc\hooks.snippet.json` 내용을 `~/.claude/settings.json` 에 병합.
- `statusLine` → statusline.py 가 model/cost/context/rate_limits 를 `/status` 로 (사용량 화면)
- `hooks` → send_event.py 가 도구/프롬프트/턴 종료를 `/event` 로 (진행 화면)

### 3b. 이미 다른 statusLine/HUD 를 쓰는 경우 (래핑 — 권장)
기존 statusLine 을 **덮어쓰지 말고 감싼다**. `pc\statusline_wrapper.ps1` 이 기존 HUD 라인을
그대로 출력하면서 같은 JSON 을 디바이스로도 전송한다(fire 후 짧은 타임아웃).
1. `statusline_wrapper.ps1` 안의 기존 HUD 명령줄(예: `node az-hud-wrapper.js ...`)을 본인 것으로 맞춘다.
2. settings.json 의 `statusLine.command` 를
   `powershell -NoProfile -File "C:\...\pc\statusline_wrapper.ps1"` 로 바꾼다.
3. hooks 는 **각 이벤트의 `hooks` 배열에 send_event.py 항목을 추가**(기존 항목 유지):
   ```json
   { "type":"command", "command":"python",
     "args":["C:\\...\\pc\\send_event.py"], "timeout":5, "async":true }
   ```

`python` 이 PATH에 없으면 `python` 을 전체 경로나 `py` 로 바꾼다.
병합 후 **Claude Code 재시작**하면 이후 매 응답/도구사용마다 디바이스로 전송된다.
여러 세션이 동시에 돌면 session_id 로 구분되어 디바이스에 종합 표시된다.

## 4. 실제 statusline JSON 스키마 확인 (필드 검증)
`statusline.py` 는 받은 원본 JSON을 `~/.claude/hud_statusline_last.json` 에 저장한다.
한 턴 지난 뒤 그 파일을 열어 `rate_limits` / `context_window` / `cost` 실제 필드를 확인.
필드명이 다르면 `statusline.py` 의 추출부만 맞추면 된다.

## 참고
- 전송은 평문 HTTP(로컬 LAN 전용). 디바이스를 인터넷에 노출하지 말 것.
- statusline/hook 스크립트는 네트워크 오류를 모두 무시하므로 Claude 작동을 방해하지 않는다.
