# statusLine 래퍼 — 기존 AgentZeroLite HUD 라인을 그대로 출력하면서, 같은 stdin JSON 을
# 물리 디바이스로도 전송(POST /status). 디바이스 전송은 fire-and-forget(HUD 지연 없음).
$ErrorActionPreference = 'SilentlyContinue'

# stdin 전체 읽기 (파이프/리다이렉트 모두 안전)
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput())
$json = $reader.ReadToEnd()

# 1) 화면 HUD 라인 = 기존 az-hud-wrapper 출력 (파이프 캡처)
$line = $json | node "C:\Users\psmon\AppData\Local\AgentZeroLite\statusline\az-hud-wrapper.js" --account claude 2>$null

# 2) 디바이스 전송 (동기 파이프, 짧은 POST 타임아웃, 출력 버림)
$json | python "C:\code\psmon\Arduino\project\samples\claude_hud\pc\statusline.py" 2>$null | Out-Null

[Console]::Out.Write($line)
