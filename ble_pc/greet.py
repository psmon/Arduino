#!/usr/bin/env python
# 7개국 인사말을 한 연결로 차례차례 BLE 전송 (3초 간격)
import asyncio, sys
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass
from bleak import BleakScanner, BleakClient

NAME = "ESP32-S3-LCD"
RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"
TX = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"

GREETINGS = [
    ("English",  "Hello"),
    ("Korean",   "안녕하세요"),
    ("French",   "Bonjour"),
    ("Spanish",  "Hola"),
    ("Russian",  "Привет"),
    ("Japanese", "こんにちは"),
    ("Chinese",  "你好"),
]


def on_notify(_c, data: bytearray):
    print(f"    <- 보드: {data.decode('utf-8', errors='replace')}")


async def main():
    print(f"[*] '{NAME}' 스캔...")
    devices = await BleakScanner.discover(timeout=10.0)
    dev = next((d for d in devices if d.name and NAME.lower() in d.name.lower()), None)
    if not dev:
        print(f"[!] '{NAME}' 못 찾음 (보드가 광고 중인지 확인)")
        return
    print(f"[+] 연결: {dev.name} [{dev.address}]")
    async with BleakClient(dev) as c:
        try:
            await c.start_notify(TX, on_notify)
        except Exception:
            pass
        for lang, txt in GREETINGS:
            await c.write_gatt_char(RX, txt.encode("utf-8"), response=False)
            print(f"[>] {lang:9s}: {txt}")
            await asyncio.sleep(3.0)
    print("[v] 7개국 인사 전송 완료")


asyncio.run(main())
