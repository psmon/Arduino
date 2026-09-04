# Claude Code hook -> HUD 디바이스로 활동 이벤트 전송 (PowerShell, 의존성 없음).
# hud_url.txt 가 http://... 이면 HTTP POST, serial:COMx 이면 USB 시리얼("E {json}").
# 모든 오류는 조용히 무시 (hook 이 Claude 작동을 방해하지 않도록).
# 인코딩: stdin/시리얼/HTTP 전부 UTF-8 고정 (한글 경로·파일명·명령이 깨지지 않도록).
$ErrorActionPreference = 'SilentlyContinue'
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

# hook JSON 읽기
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $Utf8)
$raw = $reader.ReadToEnd()
try { $d = $raw | ConvertFrom-Json } catch { return }

$ev   = "$($d.hook_event_name)"
$tool = "$($d.tool_name)"
$target = ""
if ($d.tool_input.file_path) { $target = Split-Path "$($d.tool_input.file_path)" -Leaf }
elseif ($d.tool_input.command) { $target = "$($d.tool_input.command)" }
if ($target.Length -gt 40) { $target = $target.Substring(0, 40) }

$map = @{ UserPromptSubmit='prompt_start'; PreToolUse='tool'; PostToolUse='tool_end';
          Stop='done'; SubagentStop='subagent'; SessionStart='session'; SessionEnd='idle' }
$type = if ($map.ContainsKey($ev)) { $map[$ev] } else { $ev.ToLower() }
switch ($ev) {
  'PreToolUse'      { $msg = "$tool $target".Trim() }
  'PostToolUse'     { $msg = "done $tool" }
  'UserPromptSubmit'{ $msg = "new prompt" }
  'Stop'            { $msg = "turn complete" }
  'SubagentStop'    { $msg = "subagent $($d.agent_type)" }
  'SessionStart'    { $msg = "session start" }
  default           { $msg = $ev }
}

$label = ""
if ($d.cwd) { $label = Split-Path "$($d.cwd)" -Leaf }
if ($label.Length -gt 13) { $label = $label.Substring(0, 13) }

Send-Hud "/event" @{ type=$type; tool=$tool; target=$target; msg=$msg;
                     session="$($d.session_id)"; label=$label; host=$env:COMPUTERNAME }
