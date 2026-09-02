// Waveshare ESP32-S3-LCD-1.28 — 배포 후 셀프체크(스모크 테스트) 샘플
//
// 화면에 알아보기 쉬운 패턴을 그리고, 자기 상태를 Serial(CH343/COM)로
// [SELFCHECK] 한 줄에 계속 내보낸다. PC의 selfcheck.ps1 이 이 줄을 읽어 PASS/FAIL 판정.
//
// pins: DC=8 CS=9 SCK=10 MOSI=11 RST=14 BL=2  (이 보드 검증값)
// FQBN(CLI): esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M   (CDCOnBoot 없음 -> Serial=UART0=COM)

#include <Arduino_GFX_Library.h>

#define LCD_DC 8
#define LCD_CS 9
#define LCD_SCK 10
#define LCD_MOSI 11
#define LCD_RST 14
#define LCD_BL 2

#define FW_NAME "selfcheck"
#define FW_VER  "1"

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *gfx = new Arduino_GC9A01(bus, LCD_RST, 0, true);

bool  gfxOk = false;
const char *screenLabel = "boot";
uint32_t drawCrc = 0;         // 그린 내용의 간단 체크섬(내용 식별용)

// 문자열 간단 CRC32 (내용 라벨 식별용)
uint32_t crc32(const char *s) {
  uint32_t c = 0xFFFFFFFF;
  for (; *s; s++) {
    c ^= (uint8_t)*s;
    for (int i = 0; i < 8; i++) c = (c >> 1) ^ (0xEDB88320 & (-(int32_t)(c & 1)));
  }
  return ~c;
}

void drawScreen(const char *label) {
  screenLabel = label;
  drawCrc = crc32(label);
  gfx->fillScreen(0x0000);
  // 알아보기 쉬운 3색 띠 + 라벨 (물리 표시 확인용)
  gfx->fillRect(0, 60, 240, 40, 0xF800);   // red
  gfx->fillRect(0, 100, 240, 40, 0x07E0);  // green
  gfx->fillRect(0, 140, 240, 40, 0x001F);  // blue
  gfx->setTextColor(0xFFFF);
  gfx->setTextSize(3);
  gfx->setCursor(30, 20);
  gfx->print("SELF");
  gfx->setCursor(30, 195);
  gfx->print("CHECK");
}

void setup() {
  Serial.begin(115200);
  delay(200);

  pinMode(LCD_BL, OUTPUT);
  digitalWrite(LCD_BL, HIGH);

  gfxOk = gfx->begin();
  drawScreen("selfcheck-main");

  Serial.println();
  Serial.println("[SELFCHECK] boot");
}

void loop() {
  static uint32_t n = 0;
  // 한 줄 자기보고 — PC가 이걸 파싱해 PASS/FAIL 판정
  Serial.printf(
    "[SELFCHECK] fw=%s ver=%s begin=%s bl=%d:HIGH psram=%u heap=%u screen=\"%s\" crc=%08X uptime=%lu seq=%lu\n",
    FW_NAME, FW_VER, gfxOk ? "OK" : "FAIL",
    LCD_BL, (unsigned)ESP.getPsramSize(), (unsigned)ESP.getFreeHeap(),
    screenLabel, (unsigned)drawCrc, (unsigned long)(millis() / 1000), (unsigned long)n);

  // 화면 하단에 살아있음 카운터(heartbeat) 표시
  gfx->fillRect(70, 185, 100, 20, 0x001F);
  gfx->setTextColor(0xFFFF);
  gfx->setTextSize(2);
  gfx->setCursor(150, 187);
  gfx->print((int)(millis() / 1000));

  n++;
  delay(1500);
}
