#!/usr/bin/env python
# BLE bridge for claude_hud_amoled (Bluetooth-only device).
#
# Keeps ONE BLE connection to the "claude-hud" device open and exposes a tiny local HTTP server so the
# Claude Code hook/statusline scripts (send_event.ps1 / hud_statusline.ps1) just POST JSON to
# http://127.0.0.1:8765 and never touch Bluetooth themselves (a per-event BLE connect would cost seconds).
#
#   POST /status  -> "S {json}\n"  to NUS RX
#   POST /event   -> "E {json}\n"  to NUS RX
#   GET  /health  -> {"ble": true|false, "sent": n, "dropped": n, "device": "..."}
#
# No fallback transport: if BLE is down the message is dropped and a WARNING is logged (stderr and
# ~/.claude/hud/ble_bridge.log). Requires: pip install bleak   (see requirements.txt)
import asyncio, json, logging, os, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread

try:
    sys.stdout.reconfigure(encoding="utf-8"); sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass
from bleak import BleakClient, BleakScanner

NAME   = os.environ.get("CLAUDE_HUD_BLE_NAME", "claude-hud")
NUS_RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"
PORT   = int(os.environ.get("CLAUDE_HUD_BRIDGE_PORT", "8765"))
LOGDIR = os.path.join(os.path.expanduser("~"), ".claude", "hud")

os.makedirs(LOGDIR, exist_ok=True)
logging.basicConfig(
    level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s",
    handlers=[logging.StreamHandler(sys.stderr),
              logging.FileHandler(os.path.join(LOGDIR, "ble_bridge.log"), encoding="utf-8")])
log = logging.getLogger("ble_bridge")


class Bridge:
    def __init__(self):
        self.queue: asyncio.Queue = None
        self.loop: asyncio.AbstractEventLoop = None
        self.client: BleakClient = None
        self.connected = False
        self.device = ""
        self.sent = 0
        self.dropped = 0
        self.last_warn = 0.0

    # called from HTTP threads
    def submit(self, line: str) -> bool:
        if not self.connected:
            self.dropped += 1
            now = time.time()
            if now - self.last_warn > 10:          # rate-limit the warning
                log.warning("BLE not connected: dropping message (%d dropped so far)", self.dropped)
                self.last_warn = now
            return False
        self.loop.call_soon_threadsafe(self.queue.put_nowait, line)
        return True

    async def run(self):
        self.loop = asyncio.get_running_loop()
        self.queue = asyncio.Queue()
        while True:
            try:
                await self.session()
            except Exception as e:               # scan/connect failure -> warn, retry
                log.warning("BLE session ended: %s", e)
            self.connected = False
            await asyncio.sleep(3)

    async def session(self):
        log.info("scanning for '%s' ...", NAME)
        dev = await BleakScanner.find_device_by_name(NAME, timeout=8.0)
        if not dev:
            log.warning("device '%s' not found (powered? in range? BLE advertising?)", NAME)
            return
        async with BleakClient(dev, timeout=15.0) as c:
            self.client = c
            self.device = f"{dev.name} [{dev.address}]"
            self.connected = True
            mtu = getattr(c, "mtu_size", 23)
            log.info("connected to %s (MTU %s)", self.device, mtu)
            chunk = max(20, mtu - 3)
            while c.is_connected:
                try:
                    line = await asyncio.wait_for(self.queue.get(), timeout=1.0)
                except asyncio.TimeoutError:
                    continue
                data = line.encode("utf-8")
                try:
                    for i in range(0, len(data), chunk):
                        await c.write_gatt_char(NUS_RX, data[i:i + chunk], response=False)
                    self.sent += 1
                except Exception as e:
                    self.dropped += 1
                    log.warning("BLE write failed, message dropped: %s", e)
                    break
            self.connected = False
            log.warning("disconnected from %s", self.device)


bridge = Bridge()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):   # quiet
        pass

    def _reply(self, code, body):
        b = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json" if body.startswith("{") else "text/plain")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        if self.path == "/health":
            self._reply(200, json.dumps({"ble": bridge.connected, "device": bridge.device,
                                         "sent": bridge.sent, "dropped": bridge.dropped}))
        else:
            self._reply(200, "claude_hud BLE bridge - POST /status, POST /event, GET /health")

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace").strip()
        if self.path == "/status":
            prefix = "S"
        elif self.path == "/event":
            prefix = "E"
        else:
            self._reply(404, "unknown endpoint"); return
        try:
            json.loads(body)
        except Exception:
            self._reply(400, "bad json"); return
        ok = bridge.submit(f"{prefix} {body}\n")
        self._reply(200 if ok else 503, "ok" if ok else "ble down (dropped)")


def main():
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    Thread(target=srv.serve_forever, daemon=True).start()
    log.info("local endpoint http://127.0.0.1:%d  (hooks post here; bridge forwards over BLE)", PORT)
    try:
        asyncio.run(bridge.run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
