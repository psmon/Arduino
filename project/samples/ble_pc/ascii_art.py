#!/usr/bin/env python
# BLE로 ESP32-S3-LCD에 자유 텍스트 / ASCII 아트 / 간단 애니메이션 전송.
# 주의: 디바이스가 앞뒤 공백을 trim 하므로 각 프레임의 첫/끝 글자는 공백이 아니어야 함.
import asyncio, sys
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass
from bleak import BleakScanner, BleakClient

NAME = "ESP32-S3-LCD"
RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"
TX = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"

last_echo = {"v": ""}
def on_notify(_c, data: bytearray):
    last_echo["v"] = data.decode("utf-8", errors="replace")


async def send(c, text, hold=1.0, label=""):
    await c.write_gatt_char(RX, text.encode("utf-8"), response=False)
    await asyncio.sleep(0.12)                    # echo 도착 대기
    ok = "OK" if last_echo["v"].replace("OK: ", "", 1) == text else "??"
    print(f"[>] {label:12s} bytes={len(text.encode()):3d} echo={ok}")
    await asyncio.sleep(hold)


async def main():
    print(f"[*] '{NAME}' 스캔...")
    devs = await BleakScanner.discover(timeout=10.0)
    dev = next((d for d in devs if d.name and NAME.lower() in d.name.lower()), None)
    if not dev:
        print(f"[!] '{NAME}' 못 찾음 (보드 전원/광고 확인)")
        return
    print(f"[+] 연결: {dev.name} [{dev.address}]")
    async with BleakClient(dev) as c:
        try:
            await c.start_notify(TX, on_notify)
        except Exception:
            pass

        # 1) 자유 텍스트
        await send(c, "FREE\nTEXT\nfrom PC", 1.6, "free-text")

        # 2) 정적 ASCII 아트 (첫/끝 글자 non-space)
        await send(c, "/\\_/\\\n(o.o)\n> ^ <", 2.2, "cat")
        await send(c, "+-----+\n| BLE |\n| LCD |\n+-----+", 2.2, "box")

        # 3) 표정 애니메이션 (짧은 문자열 -> 큰 글자)
        faces = ["^_^", "-_-", "^_^", "o_o", "^_^", ">_<", "^o^", "^_^"]
        for _ in range(2):
            for f in faces:
                await send(c, f, 0.35, f"face {f}")

        # 4) 스피너 (회전) 큰 글자
        spin = ["|", "/", "-", "\\"]
        for _ in range(8):
            for s in spin:
                await send(c, s, 0.12, "spin")

        # 5) 로딩 점 애니메이션
        for _ in range(3):
            for d in ["load", "load .", "load ..", "load ..."]:
                await send(c, d, 0.3, "loading")

        # 6) 바운스 (막대 위 공 이동) -- 첫 글자 '['로 non-space 고정
        for _ in range(3):
            for i in list(range(0, 9)) + list(range(7, 0, -1)):
                bar = "[" + "." * i + "o" + "." * (8 - i) + "]"
                await send(c, bar, 0.10, "bounce")

        # 마무리
        await send(c, "^o^\nDONE", 2.0, "done")

    print("[v] ASCII 아트 데모 완료")


asyncio.run(main())
