// Claude Code HUD — Waveshare ESP32-S3-(Touch-)LCD-1.28
// 이 PC의 여러 Claude Code 세션이 statusline/hooks 로 보내는 진행상황+사용량을 LAN HTTP 로
// 받아 원형 LCD 에 종합 표시. (Phase A: WiFi + HTTP + 세션테이블 + 2화면 자동순환)
//
// LCD pins(검증): DC=8 CS=9 SCK=10 MOSI=11 RST=14 BL=2
// FQBN: esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M,PartitionScheme=huge_app
// 엔드포인트: POST /status, POST /event, GET /health, GET /

#include <WiFi.h>
#include <WiFiMulti.h>   // 여러 WiFi 자동 접속 (집/사무실)
#include <WebServer.h>
#include <ESPmDNS.h>
#include <ArduinoJson.h>
#include <Wire.h>
#include "CST816S.h"   // 터치(제스처) 컨트롤러
#include <U8g2lib.h>   // 한글 유니폰트 (u8g2_font_unifont_t_korean1)
#include <Arduino_GFX_Library.h>
#include "secrets.h"   // WIFI_SSID, WIFI_PASS  (gitignore)

#define LCD_DC 8
#define LCD_CS 9
#define LCD_SCK 10
#define LCD_MOSI 11
#define LCD_RST 14
#define LCD_BL 2

// CST816S 터치 (I2C 6/7 는 IMU 와 공유), TP_RST=13, TP_INT=5
#define TP_SDA 6
#define TP_SCL 7
#define TP_RST 13
#define TP_INT 5

#define HTTP_PORT 8080
#define MDNS_NAME "claude-hud"
#define FW_NAME "claude_hud"
#define FW_VER "1.0"

#define SCREEN_SESSIONS 0
#define SCREEN_USAGE    1
#define SCREEN_SETTINGS 2

#define MAX_SESSIONS 6
#define SESSION_TTL_MS 180000UL   // 3분 동안 소식 없으면 슬롯 회수
#define SESSION_IDLE_MS 45000UL   // 45초 지나면 idle 취급

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *panel = new Arduino_GC9A01(bus, LCD_RST, 0, true);
// 더블버퍼: PSRAM 프레임버퍼에 그린 뒤 flush() 로 한 번에 push -> 깜박임 없음
Arduino_Canvas *gfx = new Arduino_Canvas(240, 240, panel);
WebServer server(HTTP_PORT);
WiFiMulti wifiMulti;
CST816S touch(TP_SDA, TP_SCL, TP_RST, TP_INT);

int  brightness = 255;              // 백라이트 PWM (0-255)
bool touchActive = false;           // 첫 터치 후 자동순환 중지
bool touchOk = false;

// ---- 세션별 상태 ----
struct Session {
  bool used = false;
  char id[40]   = "";
  char host[14] = "";       // 발신 머신 이름 (여러 대 구분)
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

void setBrightness(int v) {
  if (v < 20) v = 20; if (v > 255) v = 255;
  brightness = v; ledcWrite(LCD_BL, v);
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

// ---------- 상태 갱신 (HTTP/USB 공용) ----------
void applyStatus(JsonDocument &doc) {
  Session *s = getSession(doc["session"] | "?");
  if (doc["host"].is<const char*>())   copyStr(s->host, sizeof(s->host), doc["host"]);
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
  dirty = true;
}

void applyEvent(JsonDocument &doc) {
  Session *s = getSession(doc["session"] | "?");
  if (doc["host"].is<const char*>())  copyStr(s->host, sizeof(s->host), doc["host"]);
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
  dirty = true;
}

// ---------- HTTP 핸들러 (WiFi/리모트 모드) ----------
void handleStatus() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }
  applyStatus(doc);
  server.send(200, "text/plain", "ok");
}

void handleEvent() {
  String body = server.hasArg("plain") ? server.arg("plain") : "";
  JsonDocument doc;
  if (deserializeJson(doc, body)) { server.send(400, "text/plain", "bad json"); return; }
  applyEvent(doc);
  server.send(200, "text/plain", "ok");
}

// ---------- USB 시리얼 입력 (유선 모드) ----------
// 한 줄 프로토콜:  "S {json}" = status,  "E {json}" = event
void handleSerialLine(String line) {
  line.trim();
  if (line.length() < 3) return;
  char kind = line[0];
  int sp = line.indexOf(' ');
  if (sp < 1) return;
  String js = line.substring(sp + 1);
  JsonDocument doc;
  if (deserializeJson(doc, js)) return;
  if (kind == 'S') applyStatus(doc);
  else if (kind == 'E') applyEvent(doc);
}

void pollSerial() {
  static String sbuf;
  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\n') { handleSerialLine(sbuf); sbuf = ""; }
    else if (c != '\r' && sbuf.length() < 400) sbuf += c;
  }
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
// U8g2 유니폰트(한글+ASCII). 커서는 baseline 기준
void setFontSmall() { gfx->setFont(u8g2_font_unifont_t_korean1); gfx->setTextSize(1); }
void setFontBig()   { gfx->setFont(u8g2_font_unifont_t_korean1); gfx->setTextSize(2); }

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
  gfx->setCursor(120 - (int)strlen(title) * 4, 40); gfx->print(title);
}

void drawScreenSessions() {
  gfx->fillScreen(0x0000);
  drawHeader("SESSIONS", anyActive() ? 0x07E0 : 0xFBE0);
  uint32_t now = millis();
  gfx->setTextWrap(false);
  setFontSmall(); gfx->setTextColor(0xC618);
  gfx->setCursor(76, 62); gfx->printf("%d active", activeCount());

  int y = 88, shown = 0;
  for (int i = 0; i < MAX_SESSIONS && shown < 4; i++) {
    Session &s = sessions[i];
    if (!s.used) continue;
    bool idle = now - s.lastSeenMs > SESSION_IDLE_MS;
    uint16_t col = idle ? 0x8410 : stateColor(s.state);
    char line[56];   // host 있으면 "host:label activity", 없으면 "label activity"
    if (s.host[0]) snprintf(line, sizeof(line), "%s:%s %s", s.host, s.label, s.activity);
    else           snprintf(line, sizeof(line), "%s %s", s.label, s.activity);
    gfx->setTextColor(col);
    gfx->setCursor(18, y); gfx->print(line);
    y += 24; shown++;
  }
  if (shown == 0) {
    setFontSmall(); gfx->setTextColor(0x8410);
    gfx->setCursor(45, 120); gfx->print("no sessions yet");
  }
}

void drawBar(int base, const char *label, float pct, uint16_t col) {
  int x = 40, w = 160, by = base + 5;
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(x, base); gfx->printf("%s %d%%", label, (int)pct);
  gfx->drawRect(x, by, w, 8, 0x8410);
  int fill = (int)((w - 2) * (pct / 100.0f));
  if (fill < 0) fill = 0; if (fill > w - 2) fill = w - 2;
  gfx->fillRect(x + 1, by + 1, fill, 6, col);
}

void drawScreenUsage() {
  gfx->fillScreen(0x0000);
  drawHeader("USAGE", 0x07FF);
  gfx->setTextWrap(false);
  float totalCost = 0, maxCtx = 0; uint32_t now = millis(); int n = 0;
  for (int i = 0; i < MAX_SESSIONS; i++) if (sessions[i].used) {
    totalCost += sessions[i].costUsd;
    if (sessions[i].ctxUsedPct > maxCtx) maxCtx = sessions[i].ctxUsedPct;
    if (now - sessions[i].lastSeenMs < SESSION_IDLE_MS) n++;
  }
  setFontBig(); gfx->setTextColor(0xFFE0);
  gfx->setCursor(48, 82); gfx->printf("$%.3f", totalCost);
  setFontSmall(); gfx->setTextColor(0xC618);
  gfx->setCursor(90, 104); gfx->printf("%d sess", n);
  drawBar(128, "ctx", maxCtx, 0x07E0);
  if (lim.has) {
    drawBar(154, "5h", lim.rl5hPct, 0xFD20);
    drawBar(180, "7d", lim.rl7dPct, 0xF800);
  } else {
    setFontSmall(); gfx->setTextColor(0x8410);
    gfx->setCursor(45, 156); gfx->print("limits: n/a");
  }
}

void drawScreenSettings() {
  gfx->fillScreen(0x0000);
  drawHeader("SETTINGS", 0xC618);
  gfx->setTextWrap(false);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  int y = 66;
  gfx->setCursor(22, y); gfx->printf("IP %s", WiFi.localIP().toString().c_str()); y += 20;
  gfx->setCursor(22, y); gfx->printf("ver %s  %ddBm", FW_VER, WiFi.RSSI()); y += 20;
  gfx->setCursor(22, y); gfx->printf("sess %d  up %lus", activeCount(), (unsigned long)(millis() / 1000)); y += 22;
  gfx->setTextColor(0xFFE0);
  gfx->setCursor(22, y); gfx->printf("bright %d%%", (brightness * 100) / 255);
  int bx = 22, bw = 150, by = y + 6;
  gfx->drawRect(bx, by, bw, 8, 0x8410);
  gfx->fillRect(bx + 1, by + 1, (bw - 2) * brightness / 255, 6, 0xFFE0);
  y = by + 22;
  gfx->setTextColor(0x07FF);
  gfx->setCursor(20, y); gfx->print("tap=bright  down=back"); y += 20;
  gfx->setTextColor(0x8410);
  gfx->setCursor(14, y); gfx->printf("http://%s", WiFi.localIP().toString().c_str());
}

void render() {
  if (currentScreen == SCREEN_SESSIONS) drawScreenSessions();
  else if (currentScreen == SCREEN_USAGE) drawScreenUsage();
  else drawScreenSettings();
  gfx->flush();   // 버퍼 -> 화면 (한 번에, 깜박임 없음)
}

// ---------- setup / loop ----------
void connectWiFi() {
  gfx->fillScreen(0x0000);
  setFontSmall(); gfx->setTextColor(0xFFFF);
  gfx->setCursor(30, 110); gfx->print("WiFi connecting...");
  gfx->flush();
  WiFi.mode(WIFI_STA);
  // 등록된 WiFi 를 모두 추가 -> 켜져있는 곳(신호 좋은 곳)에 자동 접속
#ifdef WIFI_SSID1
  wifiMulti.addAP(WIFI_SSID1, WIFI_PASS1);
#endif
#ifdef WIFI_SSID2
  wifiMulti.addAP(WIFI_SSID2, WIFI_PASS2);
#endif
#ifdef WIFI_SSID3
  wifiMulti.addAP(WIFI_SSID3, WIFI_PASS3);
#endif
  uint32_t t0 = millis();
  while (wifiMulti.run() != WL_CONNECTED && millis() - t0 < 20000) { delay(300); Serial.print("."); }
  wifiOk = (WiFi.status() == WL_CONNECTED);
  gfx->fillScreen(0x0000);
  setFontSmall(); gfx->setTextColor(wifiOk ? 0x07E0 : 0xF800);
  if (wifiOk) {
    Serial.printf("\nWiFi OK  SSID=%s  IP=%s\n", WiFi.SSID().c_str(), WiFi.localIP().toString().c_str());
    gfx->setCursor(30, 100); gfx->print("WiFi:" + WiFi.SSID());
    gfx->setCursor(20, 130); gfx->print(WiFi.localIP().toString());
    gfx->setCursor(20, 150); gfx->print(String(MDNS_NAME) + ".local");
  } else {
    Serial.println("\nWiFi FAILED");
    gfx->setCursor(40, 120); gfx->print("WiFi FAILED");
    gfx->setCursor(20, 145); gfx->print("check secrets.h");
  }
  gfx->flush();
  delay(2500);
}

void setup() {
  Serial.begin(115200);
  ledcAttach(LCD_BL, 5000, 8);          // 백라이트 PWM (pin, 5kHz, 8-bit)
  setBrightness(brightness);
  if (!gfx->begin()) Serial.println("[gfx] canvas begin FAILED (PSRAM?)");
  gfx->setUTF8Print(true);   // 한글 UTF-8 렌더
  gfx->fillScreen(0x0000);
  connectWiFi();

  // 터치(CST816S) 초기화 + I2C 스캔(0x15 확인용)
  Wire.begin(TP_SDA, TP_SCL);
  Serial.println("[i2c] scan:");
  for (uint8_t a = 1; a < 127; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) Serial.printf("  found 0x%02X\n", a);
  }
  touch.begin();
  touchOk = true;
  Serial.println("[touch] CST816S begin (swipe L/R=screen, up=settings)");

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
  pollSerial();                 // USB(유선) 입력도 상시 수신
  uint32_t now = millis();
  expireSessions();

  // 터치 제스처 (CST816S) — 한 스와이프가 이벤트를 여러 번 내므로 디바운스로
  // "한 스와이프 = 한 번 반응". 실제 제스처(NONE 아님)일 때만 처리.
  static uint32_t lastGestureMs = 0;
  if (touchOk && touch.available()) {
    uint8_t g = touch.data.gestureID;
    if (g != NONE && now - lastGestureMs > 400) {
      lastGestureMs = now;
      touchActive = true;
      if (currentScreen == SCREEN_SETTINGS) {
        if (g == SWIPE_DOWN || g == SWIPE_UP) currentScreen = SCREEN_SESSIONS;
        else if (g == SINGLE_CLICK) setBrightness(touch.data.x < 120 ? brightness - 40 : brightness + 40);
      } else {
        if (g == SWIPE_LEFT || g == SWIPE_RIGHT) currentScreen ^= 1;   // 0<->1
        else if (g == SWIPE_UP) currentScreen = SCREEN_SETTINGS;
      }
      dirty = true;
    }
  }

  // 자동순환: 터치 조작 전에만 (터치하면 수동 모드로)
  if (!touchActive && now - lastAutoSwitch > 6000) { currentScreen ^= 1; lastAutoSwitch = now; dirty = true; }

  if (dirty || now - lastRender > 1500) { render(); lastRender = now; dirty = false; }
}
