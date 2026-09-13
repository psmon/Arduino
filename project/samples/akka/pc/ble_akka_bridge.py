"""Relay Akka remoting PDUs between the watch's BLE link and the .NET host's TCP port.

Akka classic remoting needs an ordered, reliable byte stream - not IP. BLE gives that,
so the device speaks the protocol over the Nordic UART Service it already has and this
bridge carries the bytes the last hop to the ActorSystem:

    [watch]  Akka PDUs --0xAB--> NUS TX (notify)  -->  this bridge  --> TCP 2552  [AskBot.Host]
             Akka PDUs <--0xAB-- NUS RX (write)   <--               <--

Why not WiFi: bringing the station up on this board starved the internal DMA heap, and
the LCD could no longer get a buffer to flush with - the screen tore. BLE was already
running for the HUD.

    python ble_akka_bridge.py                       # auto-connect to "claude-hud"
    python ble_akka_bridge.py --host 127.0.0.1 --port 2552 --verbose

Requires bleak (pip install bleak). Only one central can hold the device's BLE link, so
this cannot run at the same time as claude_hud_amoled's ble_bridge.py.
"""
import argparse
import asyncio
import sys

from bleak import BleakClient, BleakScanner

NUS_SERVICE = "6e400001-b5a3-f393-e0a9-e50e24dcca9e"
NUS_RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"  # host -> device (write)
NUS_TX = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"  # device -> host (notify)

TUNNEL_MAGIC = 0xAB
SPEECH_MAGIC = 0xA6  # the Chat app's audio frames, not ours


class Bridge:
    def __init__(self, args):
        self.args = args
        self.client: BleakClient | None = None
        self.reader: asyncio.StreamReader | None = None
        self.writer: asyncio.StreamWriter | None = None
        self.to_tcp: asyncio.Queue[bytes] = asyncio.Queue()
        self.chunk = 180  # conservative until the MTU is known
        self.tcp_bytes = 0
        self.ble_bytes = 0

    def log(self, *msg):
        print(*msg, flush=True)

    def debug(self, *msg):
        if self.args.verbose:
            print(*msg, flush=True)

    # ---------------------------------------------------------------- BLE side
    def on_notify(self, _sender, data: bytearray):
        if not data:
            return
        if data[0] != TUNNEL_MAGIC:
            # Text lines (ASCII tags) and 0xA5 microphone frames belong to the HUD /
            # Chat protocol; this bridge only carries the tunnel.
            self.debug(f"  (ignored {len(data)} B, tag {data[0]:#02x})")
            return
        self.to_tcp.put_nowait(bytes(data[1:]))

    async def pump_ble_to_tcp(self):
        while True:
            payload = await self.to_tcp.get()
            if self.writer is None:
                continue
            self.writer.write(payload)
            await self.writer.drain()
            self.ble_bytes += len(payload)
            self.debug(f"  ble->tcp {len(payload)} B (total {self.ble_bytes})")

    async def pump_tcp_to_ble(self):
        assert self.reader and self.client
        while True:
            data = await self.reader.read(4096)
            if not data:
                self.log("host closed the TCP connection")
                return
            self.tcp_bytes += len(data)
            for offset in range(0, len(data), self.chunk):
                piece = data[offset:offset + self.chunk]
                await self.client.write_gatt_char(NUS_RX, bytes([TUNNEL_MAGIC]) + piece, response=False)
            self.debug(f"  tcp->ble {len(data)} B (total {self.tcp_bytes})")

    # ---------------------------------------------------------------- wiring
    async def run(self):
        device = await self.find_device()
        if device is None:
            self.log(f"no device named '{self.args.name}' found")
            return 1

        self.log(f"connecting to {device.name} [{device.address}]")
        async with BleakClient(device, timeout=20.0) as client:
            self.client = client
            # bleak exposes the negotiated ATT MTU; keep 3 bytes for the ATT header and
            # 1 for the tunnel tag.
            mtu = getattr(client, "mtu_size", 0) or 23
            self.chunk = max(20, mtu - 4)
            self.log(f"BLE connected, ATT MTU {mtu} -> {self.chunk} bytes per write")

            await client.start_notify(NUS_TX, self.on_notify)

            self.log(f"opening TCP to {self.args.host}:{self.args.port}")
            self.reader, self.writer = await asyncio.open_connection(self.args.host, self.args.port)
            self.log("bridge up - the watch is now a peer in the ActorSystem")

            tasks = [
                asyncio.create_task(self.pump_ble_to_tcp()),
                asyncio.create_task(self.pump_tcp_to_ble()),
            ]
            try:
                done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                for task in pending:
                    task.cancel()
                for task in done:
                    exc = task.exception()
                    if exc:
                        raise exc
            finally:
                if self.writer:
                    self.writer.close()
                await client.stop_notify(NUS_TX)
        return 0

    async def find_device(self):
        if self.args.address:
            return await BleakScanner.find_device_by_address(self.args.address, timeout=self.args.scan)
        self.log(f"scanning {self.args.scan:.0f}s for '{self.args.name}' ...")
        return await BleakScanner.find_device_by_name(self.args.name, timeout=self.args.scan)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--name", default="claude-hud", help="advertised device name")
    parser.add_argument("--address", help="BLE address, skips the name scan")
    parser.add_argument("--host", default="127.0.0.1", help="AskBot.Host address")
    parser.add_argument("--port", type=int, default=2552)
    parser.add_argument("--scan", type=float, default=10.0, help="scan timeout, seconds")
    parser.add_argument("--retry", action="store_true", help="reconnect forever")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    async def once():
        return await Bridge(args).run()

    async def forever():
        while True:
            try:
                await once()
            except Exception as exc:  # noqa: BLE001 - a bridge should outlive any single failure
                print(f"bridge error: {exc}", flush=True)
            print("reconnecting in 3s ...", flush=True)
            await asyncio.sleep(3)

    try:
        return asyncio.run(forever() if args.retry else once())
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main() or 0)
