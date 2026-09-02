#!/usr/bin/env python
# 디바이스 데모 — 가짜 Claude 세션(진행 이벤트 + 사용량)을 순서대로 POST 해서
# 두 화면이 갱신되는지 라이브 세션 없이 확인한다.
# 사용: python demo.py http://<device-ip>:8080   (인자 없으면 CLAUDE_HUD_URL/기본값)
import sys, os, json, time, urllib.request

URL = (sys.argv[1] if len(sys.argv) > 1
       else os.environ.get("CLAUDE_HUD_URL", "http://claude-hud.local:8080"))


def post(path, obj):
    data = json.dumps(obj).encode("utf-8")
    req = urllib.request.Request(URL + path, data=data,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=2.0) as r:
            return r.status
    except Exception as e:
        print("  ! POST %s 실패: %s" % (path, e))
        return None


def main():
    print("[*] target:", URL)
    # 사용량(화면2) 세팅
    post("/status", {"model": "Opus 4.8", "cost_usd": 0.412, "context_used_pct": 37,
                     "rl5h_used_pct": 24, "rl5h_reset_in": 3600,
                     "rl7d_used_pct": 41, "rl7d_reset_in": 172800, "branch": "main"})
    print("[>] /status (usage) 전송")

    # 진행 이벤트(화면1) 시퀀스
    seq = [
        ("prompt_start", "", "", "new prompt"),
        ("tool", "Read", "claude_hud.ino", "Read claude_hud.ino"),
        ("tool", "Edit", "claude_hud.ino", "Edit claude_hud.ino"),
        ("tool", "Bash", "arduino-cli compile", "Bash compile"),
        ("tool_end", "Bash", "", "done Bash"),
        ("subagent", "Explore", "", "subagent Explore"),
        ("done", "", "", "turn complete"),
    ]
    for t, tool, target, msg in seq:
        post("/event", {"type": t, "tool": tool, "target": target, "msg": msg})
        print("[>] /event %-13s %s" % (t, msg))
        time.sleep(2.0)

    print("[v] 데모 완료 — 화면이 진행<->사용량 자동 순환하는지 확인")


if __name__ == "__main__":
    main()
