# Claude Code statusLine -> HUD 디바이스로 사용량 전송 + 화면 statusline 라인 출력.
# 기존 statusLine 이 있으면 inner_statusline.txt 로 보존해 그 출력을 그대로 낸다.
# hud_url.txt: http://... (HTTP) 또는 serial:COMx (USB). 의존성 없음(PowerShell 내장).
#
# 인코딩 원칙 (한글 깨짐 방지):
#  - stdin/stdout/시리얼/HTTP 전부 UTF-8 로 고정한다.
#  - 기존 statusLine 출력은 **디코딩하지 않고 바이트 그대로 통과**시킨다.
#    (콘솔 코드페이지가 949/437 이면 문자열로 캡처하는 순간 한글·박스문자가 깨진다)
$ErrorActionPreference = 'SilentlyContinue'
$hud = Join-Path $env:USERPROFILE ".claude\hud"
$Utf8 = New-Object System.Text.UTF8Encoding($false)

function Get-HudTarget {
  if ($env:CLAUDE_HUD_URL) { return $env:CLAUDE_HUD_URL.Trim() }
  $f = Join-Path $env:USERPROFILE ".claude\hud_url.txt"
  if (Test-Path $f) { $v = (Get-Content $f -Raw -Encoding UTF8).Trim(); if ($v) { return $v } }
  return "http://claude-hud.local:8080"
}

function Send-Hud($ep, $obj) {
  $u = Get-HudTarget
  $json = ($obj | ConvertTo-Json -Compress -Depth 6)
  try {
    if ($u -like "serial:*") {
      $com = $u.Substring(7)
      $sp = New-Object System.IO.Ports.SerialPort($com, 115200, 'None', 8, 'One')
      $sp.Encoding = New-Object System.Text.UTF8Encoding($false)  # 기본값 ASCII 면 한글이 '?' 로 감
      $sp.DtrEnable = $false; $sp.RtsEnable = $false   # ESP32 리셋 방지
      $sp.WriteTimeout = 300; $sp.Open()
      $prefix = if ($ep -eq '/status') { 'S' } else { 'E' }
      $sp.WriteLine("$prefix $json"); $sp.Close()
    } else {
      $body = [System.Text.Encoding]::UTF8.GetBytes($json)   # charset 명시 + 바이트 전송
      Invoke-RestMethod -Uri "$u$ep" -Method Post -Body $body `
        -ContentType "application/json; charset=utf-8" -TimeoutSec 1 | Out-Null
    }
  } catch {}
}

# statusLine JSON 읽기 (UTF-8 고정)
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $Utf8)
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

$stdout = [Console]::OpenStandardOutput()

# 화면 statusline 라인: 기존 statusLine 이 있으면 그 출력을 바이트 그대로 통과, 없으면 자체 요약
$innerFile = Join-Path $hud "inner_statusline.txt"
if (Test-Path $innerFile) {
  $inner = (Get-Content $innerFile -Raw -Encoding UTF8).Trim()
  if ($inner) {
    try {
      $psi = New-Object System.Diagnostics.ProcessStartInfo
      $psi.FileName  = 'cmd.exe'
      $psi.Arguments = '/d /c ' + $inner
      $psi.UseShellExecute = $false
      $psi.CreateNoWindow  = $true
      $psi.RedirectStandardInput  = $true
      $psi.RedirectStandardOutput = $true
      $proc = [System.Diagnostics.Process]::Start($psi)
      # stdin: 원본 JSON 을 UTF-8 바이트로 그대로 전달
      $inBytes = [System.Text.Encoding]::UTF8.GetBytes($json)
      $proc.StandardInput.BaseStream.Write($inBytes, 0, $inBytes.Length)
      $proc.StandardInput.BaseStream.Flush()
      $proc.StandardInput.Close()
      # stdout: BaseStream 으로 복사 = 디코딩/재인코딩 없음 (한글·ANSI·박스문자 안전)
      $proc.StandardOutput.BaseStream.CopyTo($stdout)
      $stdout.Flush()
      $proc.WaitForExit(5000) | Out-Null
      return
    } catch {}
  }
}
$line = "[{0}] `${1:N3} | ctx {2:N0}%" -f $model, $cost, $ctx
$b = $Utf8.GetBytes($line)
$stdout.Write($b, 0, $b.Length); $stdout.Flush()
