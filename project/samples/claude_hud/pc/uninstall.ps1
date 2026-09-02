<#
  Claude HUD 발신부 해제 — install.ps1 이 만든 백업(settings.json.hudbak)으로 되돌린다.
  사용:  pwsh -ExecutionPolicy Bypass -File uninstall.ps1
#>
$ErrorActionPreference = 'Stop'
$claude = Join-Path $env:USERPROFILE ".claude"
$settingsPath = Join-Path $claude "settings.json"
$bak = "$settingsPath.hudbak"

if (Test-Path $bak) {
  Copy-Item $bak $settingsPath -Force
  Write-Host "[v] settings.json 을 백업으로 복원했습니다. Claude Code 재시작하세요."
} else {
  Write-Host "[!] 백업($bak)이 없습니다. settings.json 을 직접 확인하세요."
}
Write-Host "    (발신 스크립트 $($claude)\hud, hud_url.txt 는 그대로 둡니다. 필요시 수동 삭제)"
