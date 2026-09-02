#!/usr/bin/env python
# 디바이스 데모 — 가짜 Claude 세션(진행 이벤트 + 사용량)을 순서대로 POST 해서
# 두 화면이 갱신되는지 라이브 세션 없이 확인한다.
# 사용: python demo.py http://<device-ip>:8080   (인자 없으면 CLAUDE_HUD_URL/기본값)
import sys, os, json, time, urllib.request
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

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


SESS = [
    {"id": "sess-arduino", "label": "Arduino", "model": "Opus 4.8", "cost": 0.412, "ctx": 37},
    {"id": "sess-webapp",  "label": "webapp",  "model": "Sonnet 5", "cost": 0.088, "ctx": 12},
    {"id": "sess-docs",    "label": "docs",    "model": "Haiku 4.5", "cost": 0.019, "ctx": 5},
]


def main():
    print("[*] target:", URL)
    # 각 세션 사용량 + 계정 공용 rate limit (마지막 status 에 실어보냄)
    for i, s in enumerate(SESS):
        p = {"session": s["id"], "label": s["label"], "model": s["model"],
             "cost_usd": s["cost"], "context_used_pct": s["ctx"]}
        if i == 0:  # 계정 공용 한도는 한 번만
            p.update({"rl5h_used_pct": 24, "rl5h_reset_in": 3600,
                      "rl7d_used_pct": 41, "rl7d_reset_in": 172800})
        post("/status", p)
        print("[>] /status  %-8s $%.3f ctx%d%%" % (s["label"], s["cost"], s["ctx"]))

    # 여러 세션의 진행 이벤트를 번갈아 전송
    seq = [
        ("sess-arduino", "tool", "Edit", "claude_hud.ino", "Edit claude_hud.ino"),
        ("sess-webapp",  "tool", "Bash", "npm test",       "Bash npm test"),
        ("sess-docs",    "tool", "Read", "README.md",      "Read README.md"),
        ("sess-arduino", "tool", "Bash", "arduino compile","Bash compile"),
        ("sess-webapp",  "done", "",     "",               "turn complete"),
        ("sess-arduino", "subagent", "Explore", "",        "subagent Explore"),
        ("sess-docs",    "tool", "Edit", "setup.md",       "Edit setup.md"),
    ]
    for sid, t, tool, target, msg in seq:
        lab = next(x["label"] for x in SESS if x["id"] == sid)
        post("/event", {"session": sid, "label": lab, "type": t,
                        "tool": tool, "target": target, "msg": msg})
        print("[>] /event  %-8s %-9s %s" % (lab, t, msg))
        time.sleep(1.5)

    print("[v] 데모 완료 - SESSIONS 화면에 3개 세션이 종합 표시되는지 확인")


if __name__ == "__main__":
    main()
