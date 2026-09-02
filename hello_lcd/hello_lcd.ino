// Waveshare ESP32-S3-LCD-1.28  ->  GC9A01A 240x240 round IPS LCD
// Library: "GFX Library for Arduino" (Arduino_GFX by moononournation)
//
// VERIFIED pin map for THIS board:
//   DC = GPIO8, CS = GPIO9, SCK = GPIO10, MOSI = GPIO11, RST = GPIO14, BL = GPIO2
//   (주의: 백라이트는 GPIO2, RST는 GPIO14 — 흔히 잘못 알려진 40/12가 아님)

#include <Arduino_GFX_Library.h>

#define LCD_DC   8
#define LCD_CS   9
#define LCD_SCK  10
#define LCD_MOSI 11
#define LCD_RST  14
#define LCD_BL   2

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *gfx = new Arduino_GC9A01(bus, LCD_RST, 0 /* rotation */, true /* IPS */);

void setup() {
  Serial.begin(115200);
  Serial.println("hello");

  pinMode(LCD_BL, OUTPUT);
  digitalWrite(LCD_BL, HIGH);   // backlight ON (GPIO2)

  gfx->begin();
  gfx->fillScreen(0x0000);      // black

  gfx->setTextColor(0xFFFF);    // white
  gfx->setTextSize(4);
  gfx->setCursor(60, 104);      // centered on 240x240
  gfx->println("HELLO");
}

void loop() {
}
