<#
  claude_hud_amoled 해제 — install.ps1 백업(settings.json.amoledbak) 복원, 브리지 종료, 자동시작 제거.
  사용:  pwsh -ExecutionPolicy Bypass -File uninstall.ps1
#>
$ErrorActionPreference = 'Stop'
$claude = Join-Path $env:USERPROFILE ".claude"
$settingsPath = Join-Path $claude "settings.json"
$bak = "$settingsPath.amoledbak"
if (Test-Path $bak) { Copy-Item $bak $settingsPath -Force; Write-Host "[v] settings.json 복원. Claude Code 재시작하세요." }
else { Write-Host "[!] 백업($bak) 없음. settings.json 을 직접 확인하세요." }
Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe'" |
  Where-Object { $_.CommandLine -match 'ble_bridge\.py' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "[v] 브리지 종료 (pid $($_.ProcessId))" }
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'claude_hud_ble_bridge.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "[v] 자동시작 제거" }
Write-Host "    ($claude\hud_amoled 은 그대로 둡니다. 필요시 수동 삭제)"
