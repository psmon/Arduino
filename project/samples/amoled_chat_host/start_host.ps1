# Build and run the AMOLED chat host.
#   pwsh -File start_host.ps1               -> build (Release) + run in this console (Ctrl+C stops)
#   pwsh -File start_host.ps1 -Background   -> start detached; log at ~/.claude/hud/chat_host.log
#   pwsh -File start_host.ps1 -Stop         -> stop a detached host
# Only one process may own the BLE link: stop claude_hud_amoled/pc/ble_bridge.py first.
param([switch]$Background, [switch]$Stop, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'ChatHost'
$logDir = Join-Path $HOME '.claude\hud'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'chat_host.log'

if ($Stop) {
    $p = Get-Process ChatHost -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force; "stopped ChatHost (pid $($p.Id -join ','))" } else { "ChatHost not running" }
    return
}

if (Get-Process ChatHost -ErrorAction SilentlyContinue) {
    Write-Warning "ChatHost is already running (use -Stop first)."
    return
}
if (Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -match 'ble_bridge\.py' }) {
    Write-Warning "ble_bridge.py is running and owns the BLE link - stop it first."
}

if (-not $NoBuild) {
    dotnet build $proj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if ($Background) {
    $exe = Get-ChildItem (Join-Path $proj 'bin\Release') -Recurse -Filter ChatHost.exe | Select-Object -First 1
    if (-not $exe) { throw "ChatHost.exe not found - build first" }
    # --contentRoot keeps appsettings.json + wwwroot resolution on the project folder.
    $p = Start-Process -FilePath $exe.FullName -ArgumentList "--contentRoot `"$proj`"" -WorkingDirectory $proj `
        -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
    "ChatHost started (pid $($p.Id)) -> http://127.0.0.1:8765  log: $log"
} else {
    dotnet run --project $proj -c Release --no-build
}
