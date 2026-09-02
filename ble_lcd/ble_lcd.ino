// Waveshare ESP32-S3-LCD-1.28 (GC9A01 round LCD)
// BLE로 폰에서 보낸 글자를 화면에 표시 (Nordic UART Service / NUS)
//
// VERIFIED pins: DC=8, CS=9, SCK=10, MOSI=11, RST=14, BL=2
//
// !! 중요 !! 이 스케치는 BLE + 그래픽이라 용량이 큽니다.
//   Arduino IDE -> Tools -> Partition Scheme -> "Huge APP (3MB No OTA/1MB SPIFFS)" 선택
//   (안 하면 "Sketch too big" 컴파일 에러)

#include <U8g2lib.h>            // 한글 유니폰트 (u8g2_font_unifont_t_korean1)
#include <Arduino_GFX_Library.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

// ---------- LCD ----------
#define LCD_DC   8
#define LCD_CS   9
#define LCD_SCK  10
#define LCD_MOSI 11
#define LCD_RST  14
#define LCD_BL   2

Arduino_DataBus *bus = new Arduino_ESP32SPI(LCD_DC, LCD_CS, LCD_SCK, LCD_MOSI, GFX_NOT_DEFINED);
Arduino_GFX *gfx = new Arduino_GC9A01(bus, LCD_RST, 0 /* rotation */, true /* IPS */);

// ---------- BLE (Nordic UART Service) ----------
#define SERVICE_UUID "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define CHAR_RX_UUID "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // phone -> device (write)
#define CHAR_TX_UUID "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // device -> phone (notify)

#define DEVICE_NAME  "ESP32-S3-LCD"

BLECharacteristic *txChar = nullptr;
volatile bool bleConnected = false;
String  incomingMsg = "";
volatile bool hasNew = false;

void showText(const String &msg, uint16_t color);

class ServerCB : public BLEServerCallbacks {
  void onConnect(BLEServer *s) override {
    bleConnected = true;
    showText("connected", 0x07E0);            // green
  }
  void onDisconnect(BLEServer *s) override {
    bleConnected = false;
    showText("disconnected\n\nadvertising...", 0xFBE0);  // orange
    s->getAdvertising()->start();             // 재광고
  }
};

class RxCB : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic *c) override {
    String v = c->getValue().c_str();         // 폰이 보낸 문자열
    v.trim();
    if (v.length() > 0) { incomingMsg = v; hasNew = true; }
  }
};

void setup() {
  Serial.begin(115200);

  pinMode(LCD_BL, OUTPUT);
  digitalWrite(LCD_BL, HIGH);                 // backlight ON (GPIO2)
  gfx->begin();
  gfx->fillScreen(0x0000);

  showText("BLE ready\n\nconnect to:\n" DEVICE_NAME, 0x07FF);  // cyan

  // BLE 서버 + NUS
  BLEDevice::init(DEVICE_NAME);
  BLEServer *server = BLEDevice::createServer();
  server->setCallbacks(new ServerCB());

  BLEService *svc = server->createService(SERVICE_UUID);

  BLECharacteristic *rxChar = svc->createCharacteristic(
      CHAR_RX_UUID,
      BLECharacteristic::PROPERTY_WRITE | BLECharacteristic::PROPERTY_WRITE_NR);
  rxChar->setCallbacks(new RxCB());

  txChar = svc->createCharacteristic(CHAR_TX_UUID, BLECharacteristic::PROPERTY_NOTIFY);
  txChar->addDescriptor(new BLE2902());

  svc->start();

  BLEAdvertising *adv = BLEDevice::getAdvertising();
  adv->addServiceUUID(SERVICE_UUID);
  adv->setScanResponse(true);
  BLEDevice::startAdvertising();

  Serial.println("BLE advertising as " DEVICE_NAME);
}

void loop() {
  if (hasNew) {
    hasNew = false;
    Serial.print("RX: "); Serial.println(incomingMsg);
    showText(incomingMsg, 0xFFFF);            // 받은 글자 화면에 표시
    if (bleConnected && txChar) {             // 폰으로 확인 응답
      String ack = "OK: " + incomingMsg;
      txChar->setValue(ack.c_str());
      txChar->notify();
    }
  }
  delay(20);
}

// 원형 240x240 화면에 한글/영문을 U8g2 유니폰트로 표시 (자동 중앙 정렬)
void showText(const String &msg, uint16_t color) {
  gfx->fillScreen(0x0000);
  gfx->setUTF8Print(true);                     // UTF-8 디코딩 켜기 (한글)
  gfx->setFont(u8g2_font_unifont_t_korean1);   // 한글+영문 유니폰트(16px)
  gfx->setTextColor(color);
  gfx->setTextWrap(true);

  // 글자 수에 따라 배율 (유니폰트 16px 기준)
  uint8_t size = 2;
  int len = msg.length();          // 바이트 수(한글은 3바이트) 기준 대략치
  if (len > 12) size = 1;
  gfx->setTextSize(size);

  // 화면 중앙 정렬
  int16_t x1, y1; uint16_t w, h;
  gfx->getTextBounds(msg, 0, 0, &x1, &y1, &w, &h);
  int16_t x = (240 - (int16_t)w) / 2 - x1;
  int16_t y = (240 - (int16_t)h) / 2 - y1;
  if (x < 2) x = 2;
  gfx->setCursor(x, y);
  gfx->print(msg);
}
