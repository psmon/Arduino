// The only physical button this board exposes to software.
//
// BSP_CAPS_BUTTONS is 0 on the ESP32-S3-Touch-AMOLED-1.75C: the two keys on the case are BOOT (GPIO0)
// and RESET, and RESET is wired to the chip's reset line, so it can never be read from firmware. That
// leaves one button, which is why volume up and volume down share it: a short press steps up, a long
// press steps down. Everything else lives on the touch screen in the Settings app.
#include "boot_button.hpp"

#include "driver/gpio.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "chat_core.hpp"

static const char *TAG = "boot_btn";

namespace settings_app {

static constexpr gpio_num_t PIN = GPIO_NUM_0;   // BOOT, active low, pulled up on the board
static constexpr int POLL_MS = 20;
static constexpr int DEBOUNCE_MS = 40;
static constexpr int LONG_MS = 600;
static constexpr int REPEAT_MS = 350;           // holding past LONG_MS keeps stepping down
static constexpr int STEP = 5;

static bool s_started = false;

static inline uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

static void task(void *)
{
    bool wasDown = false;
    uint32_t downAt = 0, lastRepeat = 0;
    bool longFired = false;

    for (;;) {
        bool down = gpio_get_level(PIN) == 0;
        uint32_t t = nowMs();

        if (down && !wasDown) {
            wasDown = true;
            downAt = t;
            longFired = false;
        } else if (down && wasDown) {
            if (!longFired && t - downAt >= LONG_MS) {
                longFired = true;
                lastRepeat = t;
                auto &c = voice_chat::Core::instance();
                c.setVolume(c.volume() - STEP);
                c.playTestTone();
                ESP_LOGI(TAG, "long press: volume %d", c.volume());
            } else if (longFired && t - lastRepeat >= REPEAT_MS) {
                lastRepeat = t;
                auto &c = voice_chat::Core::instance();
                c.setVolume(c.volume() - STEP);
            }
        } else if (!down && wasDown) {
            wasDown = false;
            if (!longFired && t - downAt >= DEBOUNCE_MS) {
                auto &c = voice_chat::Core::instance();
                c.setVolume(c.volume() + STEP);
                c.playTestTone();
                ESP_LOGI(TAG, "short press: volume %d", c.volume());
            }
        }
        vTaskDelay(pdMS_TO_TICKS(POLL_MS));
    }
}

void startBootButton()
{
    if (s_started) return;
    s_started = true;

    gpio_config_t cfg = {};
    cfg.pin_bit_mask = 1ULL << PIN;
    cfg.mode = GPIO_MODE_INPUT;
    cfg.pull_up_en = GPIO_PULLUP_ENABLE;
    cfg.pull_down_en = GPIO_PULLDOWN_DISABLE;
    cfg.intr_type = GPIO_INTR_DISABLE;
    if (gpio_config(&cfg) != ESP_OK) { ESP_LOGW(TAG, "gpio_config failed"); return; }

    // Polled rather than interrupt driven: 20 ms is plenty for a volume key and it keeps the ISR-safety
    // question away from the codec calls this makes.
    xTaskCreatePinnedToCore(task, "boot_btn", 3072, nullptr, 3, nullptr, 1);
    ESP_LOGI(TAG, "BOOT button: short press = volume up, long press = volume down");
}

} // namespace settings_app
