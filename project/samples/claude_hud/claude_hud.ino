// Claude Code HUD — Waveshare ESP32-S3-(Touch-)LCD-1.28
// 이 PC의 여러 Claude Code 세션이 statusline/hooks 로 보내는 진행상황+사용량을 LAN HTTP 로
// 받아 원형 LCD 에 종합 표시. (Phase A: WiFi + HTTP + 세션테이블 + 2화면 자동순환)
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
#define FW_VER "A2"

#define MAX_SESSIONS 6
#define SESSION_TTL_MS 180000UL   // 3분 동안 소식 없으면 슬롯 회수
#define SESSION_IDLE_MS 45000UL   // 45초 지나면 idle 취급

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *gfx = new Arduino_GC9A01(bus, LCD_RST, 0, true);
WebServer server(HTTP_PORT);

// ---- 세션별 상태 ----
struct Session {
  bool used = false;
  char id[40]   = "";
  char label[14] = "";      // 프로젝트/폴더 이름 (친근한 라벨)
  char state[12] = "idle";  // idle/prompt/thinking/tool/tool_end/done/subagent
  char activity[48] = "";
  char model[16] = "-";
  float costUsd = 0;
  float ctxUsedPct = 0;
  uint32_t turnStartMs = 0;
  uint32_t lastSeenMs = 0;
} sessions[MAX_SESSIONS];

// 계정 공용 rate limit (세션 무관, 최신값)
struct Limits {
  bool  has = false;
  float rl5hPct = 0;  long rl5hResetIn = 0;
  float rl7dPct = 0;  long rl7dResetIn = 0;
} lim;

int  currentScreen = 0;      // 0=sessions 1=usage
bool wifiOk = false;
uint32_t lastAutoSwitch = 0, lastRender = 0;
bool dirty = true;

// ---------- 유틸 ----------
static void copyStr(char *dst, size_t n, const char *src) {
  if (!src) { dst[0] = 0; return; }
  strncpy(dst, src, n - 1); dst[n - 1] = 0;
}

// session_id 로 슬롯 찾기(없으면 새로/가장 오래된 것 회수)
Session *getSession(const char *id) {
  if (!id || !*id) id = "?";
  for (int i = 0; i < MAX_SESSIONS; i++)
    if (sessions[i].used && !strcmp(sessions[i].id, id)) return &sessions[i];
  // 빈 슬롯
  for (int i = 0; i < MAX_SESSIONS; i++)
    if (!sessions[i].used) {
      sessions[i] = Session();
      sessions[i].used = true; copyStr(sessions[i].id, sizeof(sessions[i].id), id);
      // 라벨 기본값: id 뒤 4글자
      size_t L = strlen(id); const char *sfx = (L > 4) ? id + L - 4 : id;
      snprintf(sessions[i].label, sizeof(sessions[i].label), "#%s", sfx);
      return &sessions[i];
    }
  // 꽉 참 -> 가장 오래된 것 회수
  int oldest = 0;
  for (int i = 1; i < MAX_SESSIONS; i++)
    if (sessions[i].lastSeenMs < sessions[oldest].lastSeenMs) oldest = i;
  sessions[oldest] = Session();
  sessions[oldest].used = true; copyStr(sessions[oldest].id, sizeof(sessions[oldest].id), id);
  return &sessions[oldest];
}

void expireSessions() {
  uint32_t now = millis();
  for (int i = 0; i < MAX_SESSIONS; i++)
    if (sessions[i].used && now - sessions[i].lastSeenMs > SESSION_TTL_MS)
      sessions[i].used = false;
}

int activeCount() {
  uint32_t now = millis(); int n = 0;
  for (int i = 0; i < MAX_SESSIONS; i++)
    if (sessions[i].used && now - sessions[i].lastSeenMs < SESSION_IDLE_MS) n++;
  return n;
}

// ---------- HTTP 핸들러 ----------
void handleStatus() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  Serial.print("[/status] "); Serial.println(body);
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }
  Session *s = getSession(doc["session"] | "?");
  if (doc["label"].is<const char*>())  copyStr(s->label, sizeof(s->label), doc["label"]);
  if (doc["model"].is<const char*>())  copyStr(s->model, sizeof(s->model), doc["model"]);
  s->costUsd    = doc["cost_usd"]         | s->costUsd;
  s->ctxUsedPct = doc["context_used_pct"] | s->ctxUsedPct;
  if (doc["rl5h_used_pct"].is<float>() || doc["rl5h_used_pct"].is<int>()) {
    lim.has = true;
    lim.rl5hPct = doc["rl5h_used_pct"] | 0.0f;  lim.rl5hResetIn = doc["rl5h_reset_in"] | 0;
    lim.rl7dPct = doc["rl7d_used_pct"] | 0.0f;  lim.rl7dResetIn = doc["rl7d_reset_in"] | 0;
  }
  s->lastSeenMs = millis();
  dirty = true; server.send(200, "text/plain", "ok");
}

void handleEvent() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  Serial.print("[/event] "); Serial.println(body);
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }
  Session *s = getSession(doc["session"] | "?");
  if (doc["label"].is<const char*>()) copyStr(s->label, sizeof(s->label), doc["label"]);
  const char *type = doc["type"] | "";
  copyStr(s->state, sizeof(s->state), type);
  if (doc["msg"].is<const char*>()) copyStr(s->activity, sizeof(s->activity), doc["msg"]);
  else {
    const char *tool = doc["tool"] | ""; const char *tgt = doc["target"] | "";
    snprintf(s->activity, sizeof(s->activity), "%s %s", tool, tgt);
  }
  if (!strcmp(type, "prompt_start")) s->turnStartMs = millis();
  s->lastSeenMs = millis();
  dirty = true; server.send(200, "text/plain", "ok");
}

void handleHealth() {
  JsonDocument doc;
  doc["fw"] = FW_NAME; doc["ver"] = FW_VER;
  doc["ip"] = WiFi.localIP().toString();
  doc["rssi"] = WiFi.RSSI();
  doc["heap"] = ESP.getFreeHeap();
  doc["sessions"] = activeCount();
  doc["uptime"] = millis() / 1000;
  String out; serializeJson(doc, out);
  server.send(200, "application/json", out);
}

void handleRoot() {
  server.send(200, "text/plain", String("Claude HUD ") + FW_VER +
              " — POST /status, POST /event, GET /health");
}

// ---------- 렌더 ----------
void setFontSmall() { gfx->setFont((const GFXfont *)nullptr); gfx->setTextSize(1); }
void setFontBig()   { gfx->setFont((const GFXfont *)nullptr); gfx->setTextSize(2); }

uint16_t stateColor(const char *st) {
  if (!strcmp(st, "tool") || !strcmp(st, "thinking") || !strcmp(st, "prompt_start")
      || !strcmp(st, "subagent")) return 0x07E0;       // green (active)
  if (!strcmp(st, "done") || !strcmp(st, "tool_end")) return 0x001F; // blue (done)
  return 0xFBE0;                                        // amber (idle)
}

bool anyActive() {
  uint32_t now = millis();
  for (int i = 0; i < MAX_SESSIONS; i++)
    if (sessions[i].used && now - sessions[i].lastSeenMs < SESSION_IDLE_MS
        && strcmp(sessions[i].state, "done") && strcmp(sessions[i].state, "idle")) return true;
  return false;
}

void drawHeader(const char *title, uint16_t col) {
  gfx->drawCircle(120, 120, 118, col);
  gfx->drawCircle(120, 120, 117, col);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(120 - (int)strlen(title) * 3, 30); gfx->print(title);
}

void drawScreenSessions() {
  gfx->fillScreen(0x0000);
  drawHeader("SESSIONS", anyActive() ? 0x07E0 : 0xFBE0);
  uint32_t now = millis();
  setFontSmall(); gfx->setTextColor(0xC618);
  gfx->setCursor(88, 44); gfx->printf("%d active", activeCount());

  int y = 62, shown = 0;
  for (int i = 0; i < MAX_SESSIONS && shown < 5; i++) {
    Session &s = sessions[i];
    if (!s.used) continue;
    bool idle = now - s.lastSeenMs > SESSION_IDLE_MS;
    uint16_t col = idle ? 0x8410 : stateColor(s.state);
    // 라벨(굵게 색) + 활동(흰색)
    gfx->setTextColor(col);
    gfx->setCursor(24, y); gfx->print(s.label);
    gfx->setTextColor(0xFFFF);
    char act[26]; snprintf(act, sizeof(act), "%s", s.activity);
    gfx->setCursor(24, y + 10); gfx->print(act);
    y += 28; shown++;
  }
  if (shown == 0) {
    setFontSmall(); gfx->setTextColor(0x8410);
    gfx->setCursor(50, 115); gfx->print("no sessions yet");
  }
}

void drawBar(int top, const char *label, float pct, uint16_t col) {
  int x = 45, w = 150, by = top + 11;
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(x, top); gfx->printf("%s %d%%", label, (int)pct);
  gfx->drawRect(x, by, w, 9, 0x8410);
  int fill = (int)((w - 2) * (pct / 100.0f));
  if (fill < 0) fill = 0; if (fill > w - 2) fill = w - 2;
  gfx->fillRect(x + 1, by + 1, fill, 7, col);
}

void drawScreenUsage() {
  gfx->fillScreen(0x0000);
  drawHeader("USAGE", 0x07FF);
  float totalCost = 0, maxCtx = 0; uint32_t now = millis(); int n = 0;
  for (int i = 0; i < MAX_SESSIONS; i++) if (sessions[i].used) {
    totalCost += sessions[i].costUsd;
    if (sessions[i].ctxUsedPct > maxCtx) maxCtx = sessions[i].ctxUsedPct;
    if (now - sessions[i].lastSeenMs < SESSION_IDLE_MS) n++;
  }
  setFontBig(); gfx->setTextColor(0xFFE0);
  gfx->setCursor(50, 58); gfx->printf("$%.3f", totalCost);
  setFontSmall(); gfx->setTextColor(0xC618);
  gfx->setCursor(92, 80); gfx->printf("%d sess", n);
  drawBar(96, "ctx", maxCtx, 0x07E0);
  if (lim.has) {
    drawBar(128, "5h", lim.rl5hPct, 0xFD20);
    drawBar(160, "7d", lim.rl7dPct, 0xF800);
  } else {
    setFontSmall(); gfx->setTextColor(0x8410);
    gfx->setCursor(45, 132); gfx->print("limits: n/a");
  }
}

void render() {
  if (currentScreen == 0) drawScreenSessions();
  else drawScreenUsage();
}

// ---------- setup / loop ----------
void connectWiFi() {
  gfx->fillScreen(0x0000);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(30, 110); gfx->print("WiFi connecting...");
  WiFi.mode(WIFI_STA); WiFi.begin(WIFI_SSID, WIFI_PASS);
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
  gfx->begin(); gfx->fillScreen(0x0000);
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
  expireSessions();
  if (now - lastAutoSwitch > 6000) { currentScreen ^= 1; lastAutoSwitch = now; dirty = true; }
  if (dirty || now - lastRender > 1500) { render(); lastRender = now; dirty = false; }
}
