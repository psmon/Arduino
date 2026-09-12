<#
  amoled_chat_host 설치기 — AMOLED-1.75C 의 BLE 링크 주인을 ChatHost 로 넘긴다.

  ChatHost 는 ble_bridge.py 와 같은 포트(8765)에 같은 엔드포인트(POST /status, /event)를 제공하므로
  Claude Code 훅/statusLine 스크립트는 바꿀 필요가 없다. 훅 배선은 claude_hud_amoled/pc/install.ps1
  에 그대로 위임하고(로직 중복 방지), 이 스크립트는 "누가 BLE 를 잡을지"만 바꾼다.

  BLE 는 소유자가 하나뿐이다 — 파이썬 브리지를 반드시 내린다.

  사용:
    pwsh -ExecutionPolicy Bypass -File install.ps1              # 빌드 + 훅 배선 + 지금 실행
    pwsh -ExecutionPolicy Bypass -File install.ps1 -AutoStart   # + 로그인 시 자동 시작
    pwsh -ExecutionPolicy Bypass -File install.ps1 -SkipHooks   # 호스트만 (settings.json 안 건드림)
  해제:
    pwsh -File uninstall.ps1
  적용: Claude Code 재시작
#>
param([switch]$AutoStart, [switch]$SkipHooks, [switch]$NoBuild, [switch]$NoStart)
$ErrorActionPreference = 'Stop'

$here     = $PSScriptRoot
$proj     = Join-Path $here 'ChatHost'
$hudPcDir = Resolve-Path (Join-Path $here '..\claude_hud_amoled\pc') -ErrorAction SilentlyContinue
$startupDir = [Environment]::GetFolderPath('Startup')
$pyLnk    = Join-Path $startupDir 'claude_hud_ble_bridge.lnk'
$hostLnk  = Join-Path $startupDir 'amoled_chat_host.lnk'

# ---------------------------------------------------------------- 1. 빌드
if (-not $NoBuild) {
  Write-Host "[+] ChatHost 빌드 (Release) ..."
  dotnet build $proj -c Release --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "빌드 실패 — .NET SDK 10 이 필요합니다 (dotnet --list-sdks)" }
}
$exe = Get-ChildItem (Join-Path $proj 'bin\Release') -Recurse -Filter ChatHost.exe -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $exe) { throw "ChatHost.exe 를 찾을 수 없습니다 (-NoBuild 없이 다시 실행하세요)" }
Write-Host "[+] $($exe.FullName)"

# ---------------------------------------------------------------- 2. 파이썬 브리지 정리 (BLE 단일 소유자)
$py = Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe'" |
      Where-Object { $_.CommandLine -match 'ble_bridge\.py' }
foreach ($p in $py) {
  Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
  Write-Host "[+] ble_bridge.py 종료 (pid $($p.ProcessId)) — BLE 는 프로세스 하나만 잡을 수 있음"
}
if (Test-Path $pyLnk) { Remove-Item $pyLnk -Force; Write-Host "[+] 파이썬 브리지 자동시작 제거" }

# ---------------------------------------------------------------- 3. 훅/statusLine (기존 설치기에 위임)
if ($SkipHooks) {
  Write-Host "[=] -SkipHooks: settings.json 건드리지 않음"
} elseif (-not $hudPcDir) {
  Write-Warning "claude_hud_amoled/pc 를 찾지 못해 훅 배선을 건너뜁니다 (호스트는 정상 동작)"
} else {
  # 같은 포트/같은 엔드포인트라 발신 스크립트는 그대로 재사용된다. -AutoStart 는 넘기지 않는다
  # (그 옵션은 파이썬 브리지용 바로가기를 만든다).
  Write-Host "[+] 훅/statusLine 배선 -> claude_hud_amoled/pc/install.ps1"
  & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hudPcDir 'install.ps1')
  if ($LASTEXITCODE -ne 0) { throw "훅 설치 실패" }
}

# ---------------------------------------------------------------- 4. 자동 시작
if ($AutoStart) {
  $ws = New-Object -ComObject WScript.Shell
  $s = $ws.CreateShortcut($hostLnk)
  $s.TargetPath = $exe.FullName
  $s.Arguments = "--contentRoot `"$proj`""
  $s.WorkingDirectory = $proj
  $s.WindowStyle = 7            # minimized
  $s.Save()
  Write-Host "[+] 로그인 시 자동 시작 -> $hostLnk"
}

# ---------------------------------------------------------------- 5. 실행
if (-not $NoStart) {
  Get-Process ChatHost -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  $logDir = Join-Path $HOME '.claude\hud'
  New-Item -ItemType Directory -Force $logDir | Out-Null
  $log = Join-Path $logDir 'chat_host.log'
  Start-Process -FilePath $exe.FullName -ArgumentList "--contentRoot `"$proj`"" -WorkingDirectory $proj `
    -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err" | Out-Null
  Start-Sleep -Seconds 3
  try {
    $h = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/health' -TimeoutSec 3
    Write-Host "[v] 호스트 기동: BLE=$($h.ble) device=$($h.device)"
  } catch {
    Write-Warning "호스트가 아직 응답하지 않습니다 — 로그: $log"
  }
}

Write-Host ""
Write-Host "[v] 설치 완료"
Write-Host "    웹 UI   : http://127.0.0.1:8765/   (기기 페어링 · 챗 CLI 선택 · 음성 테스트)"
Write-Host "    로그    : $HOME\.claude\hud\chat_host.log"
Write-Host "    Claude Code 를 재시작하면 HUD 훅이 이 호스트를 통해 기기로 갑니다."
