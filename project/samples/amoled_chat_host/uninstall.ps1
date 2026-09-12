<#
  amoled_chat_host 해제 — 호스트를 내리고 자동시작을 지운다.
  settings.json 은 claude_hud_amoled/pc/uninstall.ps1 이 백업(.amoledbak)에서 되돌린다.

  사용:  pwsh -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepHooks]
#>
param([switch]$KeepHooks)
$ErrorActionPreference = 'Stop'

Get-Process ChatHost -ErrorAction SilentlyContinue | ForEach-Object {
  Stop-Process -Id $_.Id -Force; Write-Host "[v] ChatHost 종료 (pid $($_.Id))"
}

$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'amoled_chat_host.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "[v] 자동시작 제거" }

if ($KeepHooks) {
  Write-Host "[=] -KeepHooks: settings.json 그대로 둠 (훅이 8765 로 POST 하지만 받는 쪽이 없어 조용히 실패)"
} else {
  $hudPc = Resolve-Path (Join-Path $PSScriptRoot '..\claude_hud_amoled\pc') -ErrorAction SilentlyContinue
  if ($hudPc) {
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hudPc 'uninstall.ps1')
  } else {
    Write-Warning "claude_hud_amoled/pc 를 찾지 못했습니다 — settings.json.amoledbak 을 직접 복원하세요"
  }
}

Write-Host "    (%LOCALAPPDATA%\AmoledChatHost\state.json 의 페어링 정보는 남겨 둡니다)"
