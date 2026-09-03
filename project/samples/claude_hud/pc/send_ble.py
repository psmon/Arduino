#!/usr/bin/env python
# claude_hud 에 BLE(무선)로 세션 전송. 디바이스가 광고하는 'claude-hud' 의 NUS RX 에
# "S {json}"(status) / "E {json}"(event) 를 쓴다. HTTP·USB 와 동일 프로토콜.
# 필요: pip install bleak  (또는 ble_pc/venv 재사용)
#
# 사용:
#   python send_ble.py demo                      # 데모 세션 몇 개 전송
#   python send_ble.py status '{"session":...}'  # S 라인 직접
#   python send_ble.py event  '{"session":...}'  # E 라인 직접
import asyncio, sys, json, os
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass
from bleak import BleakScanner, BleakClient

NAME = "claude-hud"
NUS_RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"
HOST = os.environ.get("COMPUTERNAME") or "PC"


async def connect():
    dev = await BleakScanner.find_device_by_name(NAME, timeout=10.0)
    if not dev:
        print(f"[!] '{NAME}' 못 찾음 (BLE 광고/범위 확인)"); return None
    print(f"[+] 연결: {dev.name} [{dev.address}]")
    return dev


async def send_lines(lines):
    dev = await connect()
    if not dev:
        return
    async with BleakClient(dev) as c:
        for ln in lines:
            await c.write_gatt_char(NUS_RX, ln.encode("utf-8"), response=False)
            print("[>]", ln[:60])
            await asyncio.sleep(0.3)
    print("[v] 완료")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "demo"
    if mode == "status":
        asyncio.run(send_lines(["S " + sys.argv[2]]))
    elif mode == "event":
        asyncio.run(send_lines(["E " + sys.argv[2]]))
    else:  # demo
        s = json.dumps({"session": "ble1", "host": HOST, "label": "claude-hud",
                        "cost_usd": 0.25, "context_used_pct": 19,
                        "rl5h_used_pct": 20, "rl5h_reset_in": 3600,
                        "rl7d_used_pct": 35, "rl7d_reset_in": 172800}, ensure_ascii=False)
        e1 = json.dumps({"session": "ble1", "host": HOST, "label": "claude-hud",
                         "type": "tool", "msg": "BLE 무선 전송 테스트"}, ensure_ascii=False)
        e2 = json.dumps({"session": "ble2", "host": HOST, "label": "docs",
                         "type": "tool", "msg": "문서 편집 중"}, ensure_ascii=False)
        asyncio.run(send_lines(["S " + s, "E " + e1, "E " + e2]))


if __name__ == "__main__":
    main()
