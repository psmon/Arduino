// Claude HUD as an ESP-Brookesia phone app. Three swipeable tiles: SESSIONS / USAGE / INFO.
#pragma once

#include "lvgl.h"
#include "systems/phone/esp_brookesia_phone_app.hpp"

namespace esp_brookesia::apps {

class ClaudeHud : public systems::phone::App {
public:
    static ClaudeHud *requestInstance(bool use_status_bar = false, bool use_navigation_bar = false);
    ~ClaudeHud();

protected:
    ClaudeHud(bool use_status_bar, bool use_navigation_bar);

    bool init(void) override;    // install time: start USB / BLE / WiFi transports once
    bool run(void) override;     // build the UI on the default screen
    bool back(void) override;
    bool close(void) override;

private:
    void buildUi(lv_obj_t *scr);
    void refresh(bool force);
    static void timerCb(lv_timer_t *t);
    static void brightnessCb(lv_event_t *e);

    lv_timer_t *_timer = nullptr;
    uint32_t    _seenVersion = 0;
    uint32_t    _lastRefreshMs = 0;

    // tile 0: sessions
    lv_obj_t *_ring = nullptr;
    lv_obj_t *_lblActive = nullptr;
    lv_obj_t *_rows[4] = {};
    // tile 1: usage
    lv_obj_t *_lblCost = nullptr;
    lv_obj_t *_lblSess = nullptr;
    lv_obj_t *_barCtx = nullptr, *_barRl5 = nullptr, *_barRl7 = nullptr;
    lv_obj_t *_lblCtx = nullptr, *_lblRl5 = nullptr, *_lblRl7 = nullptr;
    // tile 2: info
    lv_obj_t *_lblNet = nullptr;

    static ClaudeHud *_instance;
};

} // namespace esp_brookesia::apps
