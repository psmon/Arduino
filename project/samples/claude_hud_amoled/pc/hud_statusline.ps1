# Claude Code statusLine -> local BLE bridge (http://127.0.0.1:8765/status) + prints the statusline text.
# If an existing statusLine was wrapped by install.ps1, its command is in inner_statusline.txt and its
# output is passed through as raw bytes via temp files (no decode/re-encode, so Korean / ANSI / box
# characters survive a 949/437 console code page; works on Windows PowerShell 5.1 and pwsh 7).
# Bluetooth-only device: no serial/http-to-device paths here.
$ErrorActionPreference = 'SilentlyContinue'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$hud = Join-Path $env:USERPROFILE ".claude\hud_amoled"
$bridge = if ($env:CLAUDE_HUD_BRIDGE) { $env:CLAUDE_HUD_BRIDGE.Trim() } else { 'http://127.0.0.1:8765' }

$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $Utf8)
$json = $reader.ReadToEnd()
$stdout = [Console]::OpenStandardOutput()

$model = "-"; $cost = 0.0; $ctx = 0.0
try {
  $d = $json | ConvertFrom-Json
  if ($d.model.display_name) { $model = "$($d.model.display_name)" } elseif ($d.model.name) { $model = "$($d.model.name)" }
  if ($d.cost.total_cost_usd) { $cost = [double]$d.cost.total_cost_usd }
  if ($d.context_window.used_percentage) { $ctx = [double]$d.context_window.used_percentage }
  $p = @{ session="$($d.session_id)"; model=$model; cost_usd=$cost; context_used_pct=$ctx; host=$env:COMPUTERNAME }
  if ($d.workspace.repo.name) { $p.label = "$($d.workspace.repo.name)" }
  elseif ($d.workspace.current_dir) { $p.label = Split-Path "$($d.workspace.current_dir)" -Leaf }
  if ($p.label -and $p.label.Length -gt 13) { $p.label = $p.label.Substring(0, 13) }
  if ($d.rate_limits.five_hour) {
    $p.rl5h_used_pct = [double]$d.rate_limits.five_hour.used_percentage
    $p.rl7d_used_pct = [double]$d.rate_limits.seven_day.used_percentage
  }
  $body = $Utf8.GetBytes(($p | ConvertTo-Json -Compress -Depth 6))
  Invoke-RestMethod -Uri "$bridge/status" -Method Post -Body $body -ContentType 'application/json; charset=utf-8' -TimeoutSec 1 | Out-Null
} catch {}

# Existing statusLine: feed it the original JSON and copy its stdout bytes straight through.
$innerFile = Join-Path $hud "inner_statusline.txt"
if (Test-Path $innerFile) {
  $inner = (Get-Content $innerFile -Raw -Encoding UTF8).Trim()
  if ($inner) {
    $tag = [System.IO.Path]::GetRandomFileName()
    $inF = Join-Path $env:TEMP "hud_sl_in_$tag"
    $outF = Join-Path $env:TEMP "hud_sl_out_$tag"
    try {
      [System.IO.File]::WriteAllBytes($inF, $Utf8.GetBytes($json))
      cmd /d /c "type `"$inF`" | $inner > `"$outF`"" 2>$null | Out-Null
      if (Test-Path $outF) {
        $bytes = [System.IO.File]::ReadAllBytes($outF)
        if ($bytes.Length -gt 0) { $stdout.Write($bytes, 0, $bytes.Length); $stdout.Flush() }
      }
    } catch {}
    Remove-Item $inF, $outF -Force -ErrorAction SilentlyContinue
    if ($bytes -and $bytes.Length -gt 0) { return }
  }
}
$line = "[{0}] `${1:N3} | ctx {2:N0}%" -f $model, $cost, $ctx
$b = $Utf8.GetBytes($line)
$stdout.Write($b, 0, $b.Length); $stdout.Flush()
