// Claude Code HUD — Waveshare ESP32-S3-(Touch-)LCD-1.28
// 이 PC의 Claude Code가 statusline/hooks 로 보내는 진행상황+사용량을 LAN HTTP 로 받아
// 원형 LCD 에 표시. (Phase A: WiFi + HTTP 서버 + 2화면 자동순환, 터치는 Phase B)
//
// LCD pins(검증): DC=8 CS=9 SCK=10 MOSI=11 RST=14 BL=2
// FQBN: esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app
// 엔드포인트: POST /status, POST /event, GET /health, GET /

#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <ArduinoJson.h>
#include <Arduino_GFX_Library.h>
#include "secrets.h"   // WIFI_SSID, WIFI_PASS  (gitignore)

#define LCD_DC 8
#define LCD_CS 9
#define LCD_SCK 10
#define LCD_MOSI 11
#define LCD_RST 14
#define LCD_BL 2

#define HTTP_PORT 8080
#define MDNS_NAME "claude-hud"
#define FW_NAME "claude_hud"
#define FW_VER "A1"

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *gfx = new Arduino_GC9A01(bus, LCD_RST, 0, true);
WebServer server(HTTP_PORT);

// ---- 표시 상태 ----
struct Progress {
  char state[16]   = "idle";     // idle / prompt / thinking / tool / done
  char activity[72] = "waiting for Claude";
  char model[24]   = "-";
  char branch[24]  = "-";
  uint32_t turnStartMs = 0;
  uint32_t lastEventMs = 0;
} prog;

struct Usage {
  bool  hasLimits = false;
  float costUsd = 0;
  float ctxUsedPct = 0;
  float rl5hPct = 0;  long rl5hResetIn = 0;
  float rl7dPct = 0;  long rl7dResetIn = 0;
  uint32_t lastMs = 0;
} use;

int  currentScreen = 0;          // 0=progress 1=usage (Phase A: 자동순환)
bool wifiOk = false;
uint32_t lastAutoSwitch = 0;
uint32_t lastRender = 0;
bool dirty = true;

// ---------- 유틸 ----------
static void copyStr(char *dst, size_t n, const char *src) {
  if (!src) { dst[0] = 0; return; }
  strncpy(dst, src, n - 1); dst[n - 1] = 0;
}

// ---------- HTTP 핸들러 ----------
void handleStatus() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  Serial.print("[/status] "); Serial.println(body);   // 실제 필드 스키마 검증용 로그
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }

  if (doc["model"].is<const char*>())  copyStr(prog.model, sizeof(prog.model), doc["model"]);
  use.costUsd    = doc["cost_usd"]        | use.costUsd;
  use.ctxUsedPct = doc["context_used_pct"]| use.ctxUsedPct;
  if (doc["rl5h_used_pct"].is<float>() || doc["rl5h_used_pct"].is<int>()) {
    use.hasLimits = true;
    use.rl5hPct = doc["rl5h_used_pct"] | 0.0f;  use.rl5hResetIn = doc["rl5h_reset_in"] | 0;
    use.rl7dPct = doc["rl7d_used_pct"] | 0.0f;  use.rl7dResetIn = doc["rl7d_reset_in"] | 0;
  }
  if (doc["branch"].is<const char*>()) copyStr(prog.branch, sizeof(prog.branch), doc["branch"]);
  use.lastMs = millis();
  dirty = true;
  server.send(200, "text/plain", "ok");
}

void handleEvent() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  Serial.print("[/event] "); Serial.println(body);
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }

  const char *type = doc["type"] | "";
  copyStr(prog.state, sizeof(prog.state), type);
  if (doc["msg"].is<const char*>()) {
    copyStr(prog.activity, sizeof(prog.activity), doc["msg"]);
  } else {
    const char *tool = doc["tool"] | "";
    const char *tgt  = doc["target"] | "";
    snprintf(prog.activity, sizeof(prog.activity), "%s %s", tool, tgt);
  }
  uint32_t now = millis();
  if (!strcmp(type, "prompt_start")) prog.turnStartMs = now;
  prog.lastEventMs = now;
  dirty = true;
  server.send(200, "text/plain", "ok");
}

void handleHealth() {
  JsonDocument doc;
  doc["fw"] = FW_NAME; doc["ver"] = FW_VER;
  doc["ip"] = WiFi.localIP().toString();
  doc["rssi"] = WiFi.RSSI();
  doc["heap"] = ESP.getFreeHeap();
  doc["psram"] = ESP.getPsramSize();
  doc["uptime"] = millis() / 1000;
  doc["screen"] = currentScreen;
  String out; serializeJson(doc, out);
  server.send(200, "application/json", out);
}

void handleRoot() {
  String s = String("Claude HUD ") + FW_VER + " — POST /status, POST /event, GET /health";
  server.send(200, "text/plain", s);
}

// ---------- 렌더 ----------
// 내장(고전) 폰트 사용 — 커서는 top-left 기준, size1=6x8, size2=12x16
void setFontSmall() { gfx->setFont((const GFXfont *)nullptr); gfx->setTextSize(1); }
void setFontBig()   { gfx->setFont((const GFXfont *)nullptr); gfx->setTextSize(2); }

uint16_t stateColor(const char *st) {
  if (!strcmp(st, "tool") || !strcmp(st, "thinking") || !strcmp(st, "prompt")) return 0x07E0; // green
  if (!strcmp(st, "done")) return 0x001F;   // blue
  return 0xFBE0;                              // amber (idle)
}

void drawHeader(const char *title, uint16_t col) {
  gfx->drawCircle(120, 120, 118, col);              // 얇은 상태 링(2px)
  gfx->drawCircle(120, 120, 117, col);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(120 - (strlen(title) * 4), 34); gfx->print(title);
}

void drawScreenProgress() {
  gfx->fillScreen(0x0000);
  drawHeader("PROGRESS", stateColor(prog.state));
  setFontSmall(); gfx->setTextColor(0xC618);
  gfx->setCursor(40, 70); gfx->print(prog.model);
  // 상태
  setFontBig(); gfx->setTextColor(stateColor(prog.state));
  gfx->setCursor(40, 110); gfx->print(prog.state);
  // 활동(길면 잘림)
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setTextWrap(true); gfx->setCursor(24, 135); gfx->print(prog.activity);
  gfx->setTextWrap(false);
  // 경과 + branch
  setFontSmall(); gfx->setTextColor(0x07FF);
  uint32_t el = prog.turnStartMs ? (millis() - prog.turnStartMs) / 1000 : 0;
  gfx->setCursor(60, 180); gfx->printf("%lus  @%s", (unsigned long)el, prog.branch);
}

void drawBar(int y, const char *label, float pct, uint16_t col) {
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(40, y - 4); gfx->printf("%s %d%%", label, (int)pct);
  int x = 55, w = 130;
  gfx->drawRect(x, y, w, 12, 0x8410);
  gfx->fillRect(x + 1, y + 1, (int)((w - 2) * (pct / 100.0f)), 10, col);
}

void drawScreenUsage() {
  gfx->fillScreen(0x0000);
  drawHeader("USAGE", 0x07FF);
  setFontBig(); gfx->setTextColor(0xFFE0);
  gfx->setCursor(60, 78); gfx->printf("$%.3f", use.costUsd);
  drawBar(100, "ctx", use.ctxUsedPct, 0x07E0);
  if (use.hasLimits) {
    drawBar(130, "5h", use.rl5hPct, 0xFD20);
    drawBar(158, "7d", use.rl7dPct, 0xF800);
  } else {
    setFontSmall(); gfx->setTextColor(0x8410);
    gfx->setCursor(40, 150); gfx->print("limits: n/a (cost+ctx)");
  }
}

void render() {
  if (currentScreen == 0) drawScreenProgress();
  else drawScreenUsage();
}

// ---------- setup / loop ----------
void connectWiFi() {
  gfx->fillScreen(0x0000);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(30, 110); gfx->print("WiFi connecting...");
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  uint32_t t0 = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - t0 < 20000) { delay(300); Serial.print("."); }
  wifiOk = (WiFi.status() == WL_CONNECTED);
  gfx->fillScreen(0x0000);
  setFontSmall(); gfx->setTextColor(wifiOk ? 0x07E0 : 0xF800);
  if (wifiOk) {
    Serial.print("\nWiFi OK  IP="); Serial.println(WiFi.localIP());
    gfx->setCursor(50, 100); gfx->print("WiFi OK");
    gfx->setCursor(20, 130); gfx->print(WiFi.localIP().toString());
    gfx->setCursor(20, 150); gfx->print(String(MDNS_NAME) + ".local");
  } else {
    Serial.println("\nWiFi FAILED");
    gfx->setCursor(40, 120); gfx->print("WiFi FAILED");
    gfx->setCursor(20, 145); gfx->print("check secrets.h");
  }
  delay(2500);
}

void setup() {
  Serial.begin(115200);
  pinMode(LCD_BL, OUTPUT); digitalWrite(LCD_BL, HIGH);
  gfx->begin();
  gfx->fillScreen(0x0000);

  connectWiFi();

  if (wifiOk) {
    if (MDNS.begin(MDNS_NAME)) MDNS.addService("http", "tcp", HTTP_PORT);
    server.on("/status", HTTP_POST, handleStatus);
    server.on("/event",  HTTP_POST, handleEvent);
    server.on("/health", HTTP_GET,  handleHealth);
    server.on("/",       HTTP_GET,  handleRoot);
    server.begin();
    Serial.printf("[claude_hud] HTTP server on :%d\n", HTTP_PORT);
  }
  dirty = true;
}

void loop() {
  if (wifiOk) server.handleClient();

  uint32_t now = millis();
  // Phase A: 터치 없으니 6초마다 화면 자동 순환 (Phase B에서 터치로 교체)
  if (now - lastAutoSwitch > 6000) {
    currentScreen ^= 1; lastAutoSwitch = now; dirty = true;
  }
  // 진행 화면은 경과시간이 흐르니 주기 갱신
  if (dirty || (currentScreen == 0 && now - lastRender > 1000)) {
    render(); lastRender = now; dirty = false;
  }
}
