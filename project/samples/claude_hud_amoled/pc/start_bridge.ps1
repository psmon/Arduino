# Start (or restart) the BLE bridge in the background using this folder's venv.
#   pwsh -File start_bridge.ps1            # background (hidden window), log: ~/.claude/hud/ble_bridge.log
#   pwsh -File start_bridge.ps1 -Foreground # run in this console (Ctrl+C to stop)
param([switch]$Foreground)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$venv = Join-Path $here 'venv'
$py = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path $py)) {
  Write-Host "[+] creating venv + bleak ..."
  python -m venv $venv
  & $py -m pip install -q -r (Join-Path $here 'requirements.txt')
}
# stop a previous instance
Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe'" |
  Where-Object { $_.CommandLine -match 'ble_bridge\.py' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

$script = Join-Path $here 'ble_bridge.py'
if ($Foreground) {
  & $py $script
} else {
  $pyw = Join-Path $venv 'Scripts\pythonw.exe'
  Start-Process -FilePath $pyw -ArgumentList "`"$script`"" -WindowStyle Hidden
  Start-Sleep 2
  try { $h = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/health' -TimeoutSec 2; Write-Host "[v] bridge up: $($h | ConvertTo-Json -Compress)" }
  catch { Write-Host "[!] bridge not answering yet; check $env:USERPROFILE\.claude\hud\ble_bridge.log" }
}
