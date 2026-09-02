#!/usr/bin/env python
# PC(Windows)에서 BLE로 ESP32-S3-LCD에 글자 전송 (Nordic UART Service)
# 사용법:  venv\Scripts\python send_ble.py "보낼 글자"
#          인자 없으면 대화식으로 계속 입력해서 보냄 (quit 입력시 종료)

import asyncio
import sys
try:
    sys.stdout.reconfigure(encoding="utf-8")  # 한글 깨짐 방지
except Exception:
    pass
from bleak import BleakScanner, BleakClient

DEVICE_NAME = "ESP32-S3-LCD"
RX_UUID = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"  # phone/PC -> device (write)
TX_UUID = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"  # device -> phone/PC (notify)


async def find_device():
    print(f"[*] '{DEVICE_NAME}' 스캔 중 (최대 10초)...")
    devices = await BleakScanner.discover(timeout=10.0)
    for d in devices:
        if d.name and DEVICE_NAME.lower() in d.name.lower():
            print(f"[+] 발견: {d.name} [{d.address}]")
            return d
    print(f"[!] '{DEVICE_NAME}' 못 찾음. 보드가 광고 중인지 확인하세요 "
          f"(ble_lcd.ino 업로드 후 화면에 'BLE ready' 표시).")
    print("[i] 주변에 보인 BLE 기기:")
    for d in devices:
        print(f"      {d.name!r}  {d.address}")
    return None


def on_notify(_char, data: bytearray):
    print(f"    <- 보드 응답: {data.decode('utf-8', errors='replace')}")


async def send_once(text: str):
    device = await find_device()
    if not device:
        return
    print("[*] 연결 중...")
    async with BleakClient(device) as client:
        try:
            await client.start_notify(TX_UUID, on_notify)
        except Exception:
            pass
        await client.write_gatt_char(RX_UUID, text.encode("utf-8"), response=False)
        print(f"[>] 전송: {text!r}")
        await asyncio.sleep(1.0)
    print("[v] 완료")


async def send_interactive():
    device = await find_device()
    if not device:
        return
    print("[*] 연결 중...")
    async with BleakClient(device) as client:
        try:
            await client.start_notify(TX_UUID, on_notify)
        except Exception:
            pass
        print("[i] 연결됨. 보낼 글자를 입력하고 Enter (종료: quit)")
        loop = asyncio.get_event_loop()
        while True:
            text = await loop.run_in_executor(None, input, "text> ")
            if text.strip().lower() in ("quit", "exit", "q"):
                break
            if not text:
                continue
            await client.write_gatt_char(RX_UUID, text.encode("utf-8"), response=False)
            print(f"[>] 전송: {text!r}")
            await asyncio.sleep(0.3)
    print("[v] 종료")


def main():
    if len(sys.argv) > 1:
        text = " ".join(sys.argv[1:])
        asyncio.run(send_once(text))
    else:
        asyncio.run(send_interactive())


if __name__ == "__main__":
    main()
