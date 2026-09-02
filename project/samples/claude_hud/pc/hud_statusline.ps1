# Claude Code statusLine -> HUD 디바이스로 사용량 전송 + 화면 statusline 라인 출력.
# 기존 statusLine 이 있으면 inner_statusline.txt 로 보존해 그 출력을 그대로 낸다.
# hud_url.txt: http://... (HTTP) 또는 serial:COMx (USB). 의존성 없음(PowerShell 내장).
$ErrorActionPreference = 'SilentlyContinue'
$hud = Join-Path $env:USERPROFILE ".claude\hud"

function Get-HudTarget {
  if ($env:CLAUDE_HUD_URL) { return $env:CLAUDE_HUD_URL.Trim() }
  $f = Join-Path $env:USERPROFILE ".claude\hud_url.txt"
  if (Test-Path $f) { $v = (Get-Content $f -Raw).Trim(); if ($v) { return $v } }
  return "http://claude-hud.local:8080"
}

function Send-Hud($ep, $obj) {
  $u = Get-HudTarget
  $json = ($obj | ConvertTo-Json -Compress -Depth 6)
  try {
    if ($u -like "serial:*") {
      $com = $u.Substring(7)
      $sp = New-Object System.IO.Ports.SerialPort($com, 115200, 'None', 8, 'One')
      $sp.WriteTimeout = 300; $sp.Open()
      $prefix = if ($ep -eq '/status') { 'S' } else { 'E' }
      $sp.WriteLine("$prefix $json"); $sp.Close()
    } else {
      Invoke-RestMethod -Uri "$u$ep" -Method Post -Body $json -ContentType "application/json" -TimeoutSec 1 | Out-Null
    }
  } catch {}
}

# statusLine JSON 읽기
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput())
$json = $reader.ReadToEnd()

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
  Send-Hud "/status" $p
} catch {}

# 화면 statusline 라인: 기존 statusLine 이 있으면 그 출력, 없으면 자체 요약
$innerFile = Join-Path $hud "inner_statusline.txt"
if (Test-Path $innerFile) {
  $inner = (Get-Content $innerFile -Raw).Trim()
  if ($inner) {
    $tmp = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    $line = cmd /c "type `"$tmp`" | $inner"
    Remove-Item $tmp -Force
    [Console]::Out.Write(($line -join "`n")); return
  }
}
[Console]::Out.Write(("[{0}] `${1:N3} | ctx {2:N0}%" -f $model, $cost, $ctx))
