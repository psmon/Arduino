#!/usr/bin/env python
# Claude Code statusLine 스크립트 — stdin 으로 받은 세션 JSON에서 model/cost/context/
# rate_limits 를 뽑아 디바이스로 POST /status 하고, 로컬 statusline 한 줄을 출력한다.
# 표준 라이브러리만 사용(의존성 없음). statusline 은 절대 느리거나 실패해선 안 되므로
# 모든 네트워크 오류는 조용히 무시한다.
#
# 설정 예 (~/.claude/settings.json):
#   "statusLine": { "type": "command",
#     "command": "python \"C:\\code\\psmon\\Arduino\\project\\samples\\claude_hud\\pc\\statusline.py\"" }
# 디바이스 주소는 환경변수 CLAUDE_HUD_URL 로 지정 (기본 http://claude-hud.local:8080)
import sys, os, json, time, urllib.request


def resolve_url():
    u = os.environ.get("CLAUDE_HUD_URL")
    if u:
        return u.rstrip("/")
    try:
        p = os.path.join(os.path.expanduser("~"), ".claude", "hud_url.txt")
        with open(p, encoding="utf-8") as f:
            v = f.read().strip()
            if v:
                return v.rstrip("/")
    except Exception:
        pass
    return "http://claude-hud.local:8080"


URL = resolve_url()


def num(x, d=0.0):
    try:
        return float(x)
    except Exception:
        return d


def post(path, obj):
    try:
        data = json.dumps(obj).encode("utf-8")
        req = urllib.request.Request(URL + path, data=data,
                                     headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=0.7)
    except Exception:
        pass  # statusline 은 절대 막히면 안 됨


def main():
    raw = sys.stdin.read()
    try:
        d = json.loads(raw)
    except Exception:
        d = {}

    # 실제 필드 스키마 검증용으로 원본 JSON 저장
    try:
        p = os.path.join(os.path.expanduser("~"), ".claude", "hud_statusline_last.json")
        with open(p, "w", encoding="utf-8") as f:
            f.write(raw)
    except Exception:
        pass

    model = ((d.get("model") or {}).get("display_name")
             or (d.get("model") or {}).get("name") or "-")
    cost = (d.get("cost") or {}).get("total_cost_usd", 0)
    cw = d.get("context_window") or {}
    ctx_used = cw.get("used_percentage", 0)

    payload = {"model": model, "cost_usd": num(cost), "context_used_pct": num(ctx_used),
               "session": d.get("session_id", "")}

    # 친근한 라벨: repo 이름 > 작업폴더 이름
    ws = d.get("workspace") or {}
    repo0 = ws.get("repo") or {}
    label = repo0.get("name") or os.path.basename(str(ws.get("current_dir") or d.get("cwd") or ""))
    if label:
        payload["label"] = label[:13]

    def reset_in(node):
        r = node.get("resets_at")
        return max(0, int(r - time.time())) if r is not None else 0

    rl = d.get("rate_limits") or {}
    fh, sd = rl.get("five_hour"), rl.get("seven_day")
    if fh or sd:
        fh, sd = fh or {}, sd or {}
        payload.update({
            "rl5h_used_pct": num(fh.get("used_percentage", 0)), "rl5h_reset_in": reset_in(fh),
            "rl7d_used_pct": num(sd.get("used_percentage", 0)), "rl7d_reset_in": reset_in(sd),
        })

    repo = (d.get("workspace") or {}).get("repo") or {}
    if repo.get("branch"):
        payload["branch"] = repo["branch"]

    post("/status", payload)

    # Claude Code 에 표시할 로컬 statusline 텍스트(필수 출력)
    line = "[%s] $%.3f | ctx %d%%" % (model, num(cost), int(num(ctx_used)))
    if "rl5h_used_pct" in payload:
        line += " | 5h %d%% 7d %d%%" % (int(payload["rl5h_used_pct"]), int(payload["rl7d_used_pct"]))
    sys.stdout.write(line)


if __name__ == "__main__":
    main()
