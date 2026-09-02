#!/usr/bin/env python
# Claude Code hook 스크립트 — hook 이벤트 JSON(stdin)을 디바이스 POST /event 로 전달.
# 진행상황 화면(무엇을 하는 중)에 활동을 흘려보낸다. 표준 라이브러리만 사용.
#
# 설정 예 (~/.claude/settings.json 의 hooks 각 이벤트에):
#   { "type": "command",
#     "command": "python \"C:\\...\\pc\\send_event.py\"" }
# 디바이스 주소: 환경변수 CLAUDE_HUD_URL (기본 http://claude-hud.local:8080)
import sys, os, json, urllib.request

URL = os.environ.get("CLAUDE_HUD_URL", "http://claude-hud.local:8080")

TYPE_MAP = {
    "UserPromptSubmit": "prompt_start",
    "PreToolUse": "tool",
    "PostToolUse": "tool_end",
    "Stop": "done",
    "SubagentStop": "subagent",
    "SessionStart": "session",
}


def post(path, obj):
    try:
        data = json.dumps(obj).encode("utf-8")
        req = urllib.request.Request(URL + path, data=data,
                                     headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=1.5)
    except Exception:
        pass


def main():
    try:
        d = json.loads(sys.stdin.read() or "{}")
    except Exception:
        d = {}

    ev = d.get("hook_event_name", "")
    tool = d.get("tool_name", "")
    ti = d.get("tool_input") or {}
    target = ti.get("file_path") or ti.get("command") or ""
    target = os.path.basename(str(target))[:40]

    t = TYPE_MAP.get(ev, ev.lower() or "event")
    if ev == "PreToolUse":
        msg = ("%s %s" % (tool, target)).strip()
    elif ev == "PostToolUse":
        msg = "done %s" % tool
    elif ev == "UserPromptSubmit":
        msg = "new prompt"
    elif ev == "Stop":
        msg = "turn complete"
    elif ev == "SubagentStop":
        msg = "subagent %s" % (d.get("agent_type", ""))
    elif ev == "SessionStart":
        msg = "session start"
    else:
        msg = ev

    post("/event", {"type": t, "tool": tool, "target": target,
                    "msg": msg, "session": d.get("session_id", "")})


if __name__ == "__main__":
    main()
