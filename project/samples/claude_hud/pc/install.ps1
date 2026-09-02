<#
  Claude HUD 발신부 설치기 (Windows / PowerShell 7 권장: pwsh).
  이 머신의 Claude Code 진행상황+사용량을 HUD 디바이스로 보내도록 설정한다.
  기존 settings.json 은 백업 후 병합(덮어쓰지 않음). 여러 번 실행해도 안전(idempotent).

  사용:
    # WiFi(리모트) — 디바이스 IP
    pwsh -ExecutionPolicy Bypass -File install.ps1 -Url http://192.168.0.88:8080
    # USB(유선) — 디바이스가 연결된 COM 포트
    pwsh -ExecutionPolicy Bypass -File install.ps1 -Url serial:COM6

  해제:  pwsh -File uninstall.ps1
  적용:  설정 후 Claude Code 재시작
#>
param(
  [Parameter(Mandatory = $true)][string]$Url
)
$ErrorActionPreference = 'Stop'

# serial:auto / serial  -> CH343 COM 포트 자동 검출
if ($Url -eq 'serial:auto' -or $Url -eq 'serial') {
  $port = (& (Join-Path $PSScriptRoot 'find_port.ps1') | Select-Object -Last 1)
  if (-not $port -or "$port" -notmatch '^COM\d+$') { throw "COM 포트 자동검출 실패 (find_port.ps1). USB/드라이버 확인." }
  $Url = "serial:$("$port".Trim())"
  Write-Host "[+] COM 자동검출 -> $Url"
}

$claude = Join-Path $env:USERPROFILE ".claude"
$hud = Join-Path $claude "hud"
New-Item -ItemType Directory -Force -Path $hud | Out-Null

# 1) 발신 스크립트 복사
foreach ($f in @("send_event.ps1", "hud_statusline.ps1")) {
  Copy-Item (Join-Path $PSScriptRoot $f) (Join-Path $hud $f) -Force
}
Write-Host "[+] 발신 스크립트 -> $hud"

# 2) 디바이스 주소
Set-Content -Path (Join-Path $claude "hud_url.txt") -Value $Url -NoNewline -Encoding utf8
Write-Host "[+] 디바이스 주소 -> $Url"
if ($Url -like "serial:*") {
  Write-Host "[!] USB(시리얼) 모드: 한 번에 한 프로세스만 COM 사용 가능. 동시 세션이 많으면 일부 이벤트가 누락될 수 있음(무해)."
}

# 3) settings.json 백업 + 병합
$settingsPath = Join-Path $claude "settings.json"
if (Test-Path $settingsPath) {
  Copy-Item $settingsPath "$settingsPath.hudbak" -Force
  $cfg = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
  Write-Host "[+] settings.json 백업 -> settings.json.hudbak"
} else {
  $cfg = @{}
}
if (-not $cfg.ContainsKey('hooks') -or $null -eq $cfg['hooks']) { $cfg['hooks'] = @{} }

# 3a) hooks 추가 (기존 유지, 중복 방지)
$sendPath = Join-Path $hud "send_event.ps1"
$events = @("UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SubagentStop", "SessionStart", "SessionEnd")
foreach ($ev in $events) {
  if (-not $cfg.hooks.ContainsKey($ev) -or $null -eq $cfg.hooks[$ev]) { $cfg.hooks[$ev] = @() }
  $has = $false
  foreach ($grp in @($cfg.hooks[$ev])) {
    foreach ($h in @($grp.hooks)) {
      if (($h.args -join ' ') -match 'send_event\.ps1') { $has = $true }
    }
  }
  if (-not $has) {
    $entry = @{ matcher = '*'; hooks = @(@{ type = 'command'; command = 'powershell';
        args = @('-NoProfile', '-File', $sendPath); timeout = 5; async = $true }) }
    $cfg.hooks[$ev] = @($cfg.hooks[$ev]) + $entry
  }
}
Write-Host "[+] hooks 추가/확인 완료 (기존 hook 보존)"

# 3b) statusLine 설정 (기존 있으면 래핑 보존, 없으면 설정)
$statusScript = Join-Path $hud "hud_statusline.ps1"
$ourCmd = "powershell -NoProfile -File `"$statusScript`""
$innerFile = Join-Path $hud "inner_statusline.txt"
if ($cfg.ContainsKey('statusLine') -and $cfg['statusLine']) {
  $existingCmd = "$($cfg.statusLine.command)"
  if ($existingCmd -notmatch 'hud_statusline\.ps1') {
    # 기존 statusLine 을 inner 로 보존 -> 우리 스크립트가 그 출력을 그대로 표시
    $inner = $existingCmd
    if ($cfg.statusLine.args) { $inner += ' ' + ((@($cfg.statusLine.args) | ForEach-Object { '"' + $_ + '"' }) -join ' ') }
    Set-Content -Path $innerFile -Value $inner -NoNewline -Encoding utf8
    $cfg['statusLine'] = @{ type = 'command'; command = $ourCmd }
    Write-Host "[+] 기존 statusLine 을 래핑(보존): $inner"
  } else {
    Write-Host "[=] statusLine 이미 HUD 설정됨 (변경 없음)"
  }
} else {
  if (Test-Path $innerFile) { Remove-Item $innerFile -Force }
  $cfg['statusLine'] = @{ type = 'command'; command = $ourCmd }
  Write-Host "[+] statusLine 설정 (자체 요약 라인 표시)"
}

# 4) 저장
$cfg | ConvertTo-Json -Depth 100 | Set-Content -Path $settingsPath -Encoding utf8
Write-Host ""
Write-Host "[v] 설치 완료. Claude Code 를 재시작하면 이 머신($env:COMPUTERNAME)의 세션이 HUD 에 표시됩니다."
Write-Host "    되돌리기: pwsh -File `"$PSScriptRoot\uninstall.ps1`""
