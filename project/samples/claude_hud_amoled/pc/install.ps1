<#
  claude_hud_amoled PC 설치기 (Bluetooth 전용 기기용; 기존 claude_hud(USB+BLE) 설치와 완전히 분리됨).
  - 발신 스크립트를 ~/.claude/hud_amoled/ 에 복사
  - settings.json 을 백업(.amoledbak) 후 hooks 추가 + statusLine 래핑 (idempotent)
  - BLE 브리지(start_bridge.ps1)는 별도로 띄운다 (로그인 시 자동 시작은 -AutoStart)

  사용:  pwsh -ExecutionPolicy Bypass -File install.ps1 [-AutoStart]
  해제:  pwsh -File uninstall.ps1
  적용:  Claude Code 재시작
#>
param([switch]$AutoStart)
$ErrorActionPreference = 'Stop'

$claude = Join-Path $env:USERPROFILE ".claude"
$hud = Join-Path $claude "hud_amoled"
New-Item -ItemType Directory -Force -Path $hud | Out-Null

foreach ($f in @("send_event.ps1", "hud_statusline.ps1")) {
  Copy-Item (Join-Path $PSScriptRoot $f) (Join-Path $hud $f) -Force
}
Write-Host "[+] 발신 스크립트 -> $hud"

$settingsPath = Join-Path $claude "settings.json"
if (Test-Path $settingsPath) {
  Copy-Item $settingsPath "$settingsPath.amoledbak" -Force
  $cfg = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
  Write-Host "[+] settings.json 백업 -> settings.json.amoledbak"
} else { $cfg = @{} }
if (-not $cfg.ContainsKey('hooks') -or $null -eq $cfg['hooks']) { $cfg['hooks'] = @{} }

$sendPath = Join-Path $hud "send_event.ps1"
$events = @("UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SubagentStop", "SessionStart", "SessionEnd")
foreach ($ev in $events) {
  if (-not $cfg.hooks.ContainsKey($ev) -or $null -eq $cfg.hooks[$ev]) { $cfg.hooks[$ev] = @() }
  $has = $false
  foreach ($grp in @($cfg.hooks[$ev])) { foreach ($h in @($grp.hooks)) {
    if (($h.args -join ' ') -match 'hud_amoled\\send_event\.ps1') { $has = $true } } }
  if (-not $has) {
    $entry = @{ matcher = '*'; hooks = @(@{ type = 'command'; command = 'powershell';
        args = @('-NoProfile', '-File', $sendPath); timeout = 5; async = $true }) }
    $cfg.hooks[$ev] = @($cfg.hooks[$ev]) + $entry
  }
}
Write-Host "[+] hooks 추가/확인 완료 (기존 hook 보존)"

$statusScript = Join-Path $hud "hud_statusline.ps1"
$ourCmd = "powershell -NoProfile -File `"$statusScript`""
$innerFile = Join-Path $hud "inner_statusline.txt"
if ($cfg.ContainsKey('statusLine') -and $cfg['statusLine']) {
  $existingCmd = "$($cfg.statusLine.command)"
  if ($existingCmd -notmatch 'hud_amoled\\hud_statusline\.ps1') {
    $inner = $existingCmd
    if ($cfg.statusLine.args) { $inner += ' ' + ((@($cfg.statusLine.args) | ForEach-Object { '"' + $_ + '"' }) -join ' ') }
    Set-Content -Path $innerFile -Value $inner -NoNewline -Encoding utf8
    $cfg['statusLine'] = @{ type = 'command'; command = $ourCmd }
    Write-Host "[+] 기존 statusLine 을 래핑(보존): $inner"
  } else { Write-Host "[=] statusLine 이미 설정됨" }
} else {
  if (Test-Path $innerFile) { Remove-Item $innerFile -Force }
  $cfg['statusLine'] = @{ type = 'command'; command = $ourCmd }
  Write-Host "[+] statusLine 설정"
}
$cfg | ConvertTo-Json -Depth 100 | Set-Content -Path $settingsPath -Encoding utf8

if ($AutoStart) {
  $startup = [Environment]::GetFolderPath('Startup')
  $lnk = Join-Path $startup 'claude_hud_ble_bridge.lnk'
  $ws = New-Object -ComObject WScript.Shell
  $s = $ws.CreateShortcut($lnk)
  $s.TargetPath = 'pwsh.exe'
  $s.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'start_bridge.ps1')`""
  $s.Save()
  Write-Host "[+] 로그인 시 브리지 자동 시작 -> $lnk"
}

Write-Host ""
Write-Host "[v] 설치 완료. 1) pwsh -File `"$PSScriptRoot\start_bridge.ps1`" 로 BLE 브리지 실행  2) Claude Code 재시작"
