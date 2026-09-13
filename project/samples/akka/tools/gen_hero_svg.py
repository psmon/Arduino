"""Draw the README hero: what the three watch apps actually look like, and how one host feeds them.

The screens are traced from the real layouts rather than imagined - the y offsets below are the
LVGL constants from askbot_app.cpp, chat_app.cpp, claude_hud_app.cpp and settings_app.cpp, scaled
from the panel's 466x466 down to the drawing. Regenerate after moving anything on screen:

    python tools/gen_hero_svg.py docs/ui-hero.svg
"""
import sys

W, H = 1440, 772
PANEL = 466                      # the real display, square, round bezel
DIA = 240                        # how big each watch is drawn
S = DIA / PANEL                  # device px -> svg px

BG = "#0b0e14"
CARD = "#12161f"
EDGE = "#232733"
WHITE = "#ffffff"
GRAY = "#8a8f98"
DIM = "#3a4052"
CYAN = "#5ce1e6"
GREEN = "#3dd68c"
AMBER = "#f5a524"
RED = "#ff5c5c"
BLUE = "#4c8dff"
PURPLE = "#a78bfa"
YELLOW = "#ffe066"

out = []


def add(s):
    out.append(s)


def esc(t):
    return (t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def text(x, y, t, size=13, fill=WHITE, anchor="start", weight="400", family=None, opacity=None):
    fam = family or "'Segoe UI','Malgun Gothic','Noto Sans KR',system-ui,sans-serif"
    op = f' opacity="{opacity}"' if opacity else ""
    add(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{fam}" font-size="{size}" fill="{fill}" '
        f'text-anchor="{anchor}" font-weight="{weight}"{op}>{esc(t)}</text>')


def rect(x, y, w, h, fill, r=0, stroke=None, sw=1, opacity=None):
    st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    op = f' opacity="{opacity}"' if opacity else ""
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{r}" fill="{fill}"{st}{op}/>')


def circle(cx, cy, r, fill, stroke=None, sw=1, opacity=None):
    st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    op = f' opacity="{opacity}"' if opacity else ""
    add(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{r:.1f}" fill="{fill}"{st}{op}/>')


def line(x1, y1, x2, y2, stroke=DIM, sw=1, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    add(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" '
        f'stroke-width="{sw}"{d}/>')


class Watch:
    """One round screen. Device coordinates in, svg coordinates out."""

    def __init__(self, cx, cy, title, subtitle):
        self.cx, self.cy = cx, cy
        self.r = DIA / 2
        self.top = cy - self.r
        self.left = cx - self.r
        # bezel + panel
        circle(cx, cy, self.r + 7, "#05070b", stroke="#2b3040", sw=2)
        circle(cx, cy, self.r, "#000000")
        text(cx, cy + self.r + 30, title, 15, WHITE, "middle", "600")
        text(cx, cy + self.r + 50, subtitle, 12, GRAY, "middle")

    def x(self, dx):          # device x (0..466, 233 = centre) -> svg
        return self.left + dx * S

    def y(self, dy):          # device y from the top of the panel -> svg
        return self.top + dy * S

    def cxo(self, dx=0):      # centred horizontally, device offset
        return self.cx + dx * S

    def label(self, dy, t, size=10.5, fill=WHITE, dx=0, anchor="middle", weight="400"):
        text(self.cxo(dx), self.y(dy), t, size, fill, anchor, weight)

    def pill(self, dx, dy, w, h, fill, t, tcolor=GRAY, size=9.5):
        rect(self.cxo(dx) - w * S / 2, self.y(dy), w * S, h * S, fill, r=h * S / 2)
        text(self.cxo(dx), self.y(dy) + h * S / 2 + 3.2, t, size, tcolor, "middle")

    def box(self, dy, w, h, fill=CARD):
        rect(self.cxo() - w * S / 2, self.y(dy), w * S, h * S, fill, r=9)

    def bar(self, dy, pct, color=GREEN, w=200):
        x = self.cxo() - w * S / 2
        rect(x, self.y(dy), w * S, 3.4, DIM, r=1.7)
        rect(x, self.y(dy), w * S * pct, 3.4, color, r=1.7)

    def button(self, dx, dy, d, fill, glyph=None, gcolor=WHITE, gsize=13):
        cx, cy = self.cxo(dx), self.y(dy) + d * S / 2
        circle(cx, cy, d * S / 2, fill)
        if glyph:
            text(cx, cy + gsize * 0.35, glyph, gsize, gcolor, "middle", "600")
        return cx, cy


def callouts(x, y, items):
    """Numbered legend under a watch. Fixed columns: a long label cannot collide with its text."""
    for i, (label, desc) in enumerate(items):
        yy = y + i * 19
        circle(x + 6, yy - 3.5, 6.5, "#1b2130", stroke=EDGE)
        text(x + 6, yy, str(i + 1), 8.5, CYAN, "middle", "700")
        text(x + 18, yy, label, 10.5, WHITE, "start", "600")
        text(x + 96, yy, desc, 10.5, GRAY)


def marker(w, n, dy, side="right", length=30):
    """A number tag outside the bezel, on the row of the thing it points at."""
    y0 = w.y(dy)
    dyc = y0 - w.cy
    span = max(0.0, w.r * w.r - dyc * dyc) ** 0.5      # half-width of the circle at this row
    x0 = w.cx + (span if side == "right" else -span)
    x1 = x0 + (length if side == "right" else -length)
    line(x0, y0, x1, y0, DIM, 1)
    circle(x1 + (7 if side == "right" else -7), y0, 7, "#1b2130", stroke=EDGE)
    text(x1 + (7 if side == "right" else -7), y0 + 3, str(n), 8.5, CYAN, "middle", "700")


# ---------------------------------------------------------------- canvas
add(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
rect(0, 0, W, H, BG)

text(40, 52, "AskBot", 30, WHITE, "start", "700")
text(150, 52, "an actor system you can talk to, on a 466x466 round AMOLED", 17, GRAY)
text(40, 78, "One PC host, one BLE link, three apps. Actors on the PC; the watch is a peer, not a terminal.",
     13, GRAY)

# ---------------------------------------------------------------- the link strip
strip_y = 104
rect(40, strip_y, W - 80, 96, CARD, r=12, stroke=EDGE)

text(66, strip_y + 26, "PC", 11, GRAY, "start", "700")
rect(60, strip_y + 36, 250, 44, "#161b26", r=8, stroke=EDGE)
text(185, strip_y + 55, "AkkaHost  (.NET 10 + Akka 1.6)", 11.5, WHITE, "middle", "600")
text(185, strip_y + 71, "netclaw / claude  ·  SuperTonic TTS  ·  whisper STT", 9.5, GRAY, "middle")

text(60, strip_y + 92, "Claude Code hooks -> 127.0.0.1:8765", 9, DIM)

# three channels
chan = [
    ("0xAB  Akka PDUs", "AskBot  —  a real remoting peer", PURPLE),
    ("R/A lines + 0xA6", "Chat  —  audio both ways", CYAN),
    ("S/E lines", "Claude HUD  —  session telemetry", GREEN),
]
for i, (wire, who, col) in enumerate(chan):
    yy = strip_y + 30 + i * 22
    line(320, yy, 470, yy, col, 1.6, dash="4 3")
    text(478, yy + 3.5, wire, 10, col, "start", "600")
    text(478 + 112, yy + 3.5, who, 10, GRAY)

text(1000, strip_y + 26, "BLE (one central)", 10, GRAY, "middle")
rect(935, strip_y + 36, 130, 44, "#161b26", r=8, stroke=EDGE)
text(1000, strip_y + 62, "ESP32-S3", 12, WHITE, "middle", "600")
line(880, strip_y + 58, 931, strip_y + 58, GRAY, 1.4)

text(1105, strip_y + 44, "no WiFi: the PDUs ride", 10, GRAY)
text(1105, strip_y + 60, "the BLE link the HUD", 10, GRAY)
text(1105, strip_y + 76, "already keeps up", 10, GRAY)

# ---------------------------------------------------------------- 1. AskBot
row_y = 356
w1 = Watch(180, row_y, "AskBot", "hold to talk, the actor answers")
w1.label(42, "ASUS-AI · netclaw", 10, GRAY)
w1.pill(-46, 56, 146, 28, CARD, "actor?", GRAY)
w1.pill(77, 56, 84, 28, CARD, "New chat", GRAY)
w1.box(92, 344, 182)
w1.label(116, "지금 액터가 동작하고 있어?", 10, CYAN, dx=-150, anchor="start")
w1.label(146, "네, 시계가 액터 시스템의", 10, WHITE, dx=-150, anchor="start")
w1.label(168, "피어로 붙어 있습니다.", 10, WHITE, dx=-150, anchor="start")
w1.label(292, "done · 1 chunk · 1266 ms", 9.5, GREEN)
w1.bar(304, 0.0, GREEN)
w1.button(0, 318, 88, BLUE, "◉", WHITE, 16)
w1.button(-96, 336, 52, CARD, "T", WHITE, 12)
w1.button(96, 336, 52, CARD, "■", WHITE, 11)
w1.button(-144, 272, 52, CARD, "♪", PURPLE, 12)
marker(w1, 1, 42, "left")
marker(w1, 2, 70, "left")
marker(w1, 4, 130, "right")
marker(w1, 5, 292, "right")
marker(w1, 6, 362, "right")
marker(w1, 3, 298, "left")
callouts(24, row_y + 196, [
    ("host", "which PC and which chat CLI answered"),
    ("preset", "tap to cycle the canned questions"),
    ("voice", "speak the answer, or text only"),
    ("heard", "the transcript whisper produced"),
    ("stage", "think / receiving / done, with timing"),
    ("mic", "hold to talk; T sends the preset, ■ cancels"),
])

# ---------------------------------------------------------------- 2. Chat
w2 = Watch(540, row_y, "Chat", "the older app, same actors now")
w2.label(42, "ASUS-AI · netclaw · chat 2", 10, GRAY)
w2.pill(-46, 56, 146, 28, "#2a2440", "♪ Text + voice", PURPLE)
w2.pill(77, 56, 84, 28, CARD, "New chat", GRAY)
w2.box(92, 344, 182)
w2.label(116, "오늘의 날씨는", 10, CYAN, dx=-150, anchor="start")
w2.label(146, "오늘 서울은 맑고 선선해.", 10, WHITE, dx=-150, anchor="start")
w2.label(292, "listening 2.4s", 9.5, RED)
w2.bar(308, 0.62, GREEN)
w2.button(0, 324, 88, RED, "◉", WHITE, 16)
w2.button(-96, 342, 52, CARD, "T", WHITE, 12)
w2.button(96, 342, 52, CARD, "■", WHITE, 11)
marker(w2, 1, 70, "left")
marker(w2, 2, 292, "right")
marker(w2, 3, 308, "right")
callouts(384, row_y + 196, [
    ("answer mode", "shown only when the host can speak"),
    ("stage", "recording, transcribing, then the answer"),
    ("level", "input level while the mic is held"),
    ("same engine", "one ChatActor serves this and AskBot"),
    ("unchanged", "its firmware never had to be touched"),
])

# ---------------------------------------------------------------- 3. Claude HUD
w3 = Watch(900, row_y, "Claude HUD", "swipe: crew / sessions / usage / info")
w3.label(80, "USAGE", 15, CYAN, weight="700")
w3.label(132, "$1.23", 30, YELLOW, weight="700")
w3.label(176, "3 sess", 11, GRAY)
w3.label(216, "ctx 42%", 10, WHITE, dx=-110, anchor="start")
w3.bar(228, 0.42, GREEN, 190)
w3.label(272, "5h  12%", 10, WHITE, dx=-110, anchor="start")
w3.bar(284, 0.12, GREEN, 190)
w3.label(328, "7d  30%", 10, WHITE, dx=-110, anchor="start")
w3.bar(340, 0.30, AMBER, 190)
w3.label(392, "< sessions      info >", 9, DIM)
marker(w3, 1, 132, "right")
marker(w3, 2, 228, "right")
marker(w3, 3, 284, "right")
marker(w3, 4, 392, "right")
callouts(744, row_y + 196, [
    ("cost", "this session's spend, from the statusLine"),
    ("context", "how full the window is"),
    ("limits", "5-hour and 7-day rate limits"),
    ("tiles", "crew, sessions, usage, info - swipe across"),
    ("no change", "hooks still post to :8765, now to AkkaHost"),
])

# ---------------------------------------------------------------- 4. Settings
w4 = Watch(1260, row_y, "Settings", "device-wide, shared by both apps")
w4.label(48, "Settings", 12, WHITE, weight="600")
rows = [("Speaker volume", 0.70), ("Mic gain", 0.50), ("Brightness", 0.80)]
for i, (name, val) in enumerate(rows):
    yy = 78 + i * 52
    rect(w4.cxo() - 135 * S, w4.y(yy), 270 * S, 40 * S, CARD, r=7)
    text(w4.cxo(-125), w4.y(yy) + 15, name, 9.5, WHITE)
    w4.bar(yy + 30, val, GREEN, 220)
for i, (label, value, col) in enumerate([
        ("Answer", "text + voice", PURPLE),
        ("Listen", "한국어", WHITE),
        ("Speak", "English", WHITE),
        ("Voice", "M2", CYAN)]):
    yy = 238 + i * 40
    rect(w4.cxo() - 135 * S, w4.y(yy), 270 * S, 32 * S, CARD, r=7)
    text(w4.cxo(-125), w4.y(yy) + 13, label, 9.5, GRAY)
    text(w4.cxo(125), w4.y(yy) + 13, value, 9.5, col, "end")
w4.label(410, "WiFi: off (no SSID)", 9, DIM)
marker(w4, 1, 96, "left")
marker(w4, 2, 252, "left")
marker(w4, 3, 332, "left")
callouts(1104, row_y + 196, [
    ("audio", "volume and mic gain, one codec for both apps"),
    ("listen", "language hint for whisper - beats auto-detect"),
    ("speak", "language and voice of the answer, set apart"),
])

# ---------------------------------------------------------------- footer
foot = H - 84
rect(40, foot, W - 80, 62, CARD, r=12, stroke=EDGE)
facts = [
    ("Akka 1.6 under Native AOT", "works, once HOCON's types are rooted"),
    ("10 voices x 31 languages", "read from the model, verified by round trip"),
    ("whisper 4.1 s in 0.24 s", "31 threads, language pinned, silence gated"),
    ("0 draw failures", "after WiFi stopped eating the LCD's DMA heap"),
]
for i, (head, tail) in enumerate(facts):
    x = 66 + i * 340
    text(x, foot + 26, head, 11.5, WHITE, "start", "700")
    text(x, foot + 44, tail, 10, GRAY)

add("</svg>")

path = sys.argv[1] if len(sys.argv) > 1 else "docs/ui-hero.svg"
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
print(f"wrote {path} ({len('\n'.join(out))} bytes)")
