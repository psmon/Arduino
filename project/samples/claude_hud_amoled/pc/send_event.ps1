# Claude Code hook -> local BLE bridge (http://127.0.0.1:8765/event). Bluetooth-only device.
# The bridge forwards over BLE; if BLE is down it drops the message and logs a warning.
# All errors here are silently ignored so a hook can never disturb Claude.
# Encoding: stdin and HTTP body are fixed to UTF-8 so Korean paths/commands survive.
$ErrorActionPreference = 'SilentlyContinue'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$bridge = if ($env:CLAUDE_HUD_BRIDGE) { $env:CLAUDE_HUD_BRIDGE.Trim() } else { 'http://127.0.0.1:8765' }

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

$json = @{ type=$type; tool=$tool; target=$target; msg=$msg; session="$($d.session_id)";
           label=$label; host=$env:COMPUTERNAME } | ConvertTo-Json -Compress -Depth 6
$body = $Utf8.GetBytes($json)   # send bytes with explicit charset
try { Invoke-RestMethod -Uri "$bridge/event" -Method Post -Body $body -ContentType 'application/json; charset=utf-8' -TimeoutSec 1 | Out-Null } catch {}
