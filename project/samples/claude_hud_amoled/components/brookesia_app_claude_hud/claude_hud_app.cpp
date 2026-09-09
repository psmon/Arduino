#include <cstdio>
#include <cstring>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:ClaudeHud"
#include "esp_lib_utils.h"
#include "bsp/esp-bsp.h"
#include "claude_hud_app.hpp"
#include "hud_state.hpp"
#include "hud_transport.hpp"

#define APP_NAME "Claude HUD"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using claude_hud::State;
using claude_hud::Session;
using claude_hud::Limits;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_claude_hud_112_112);

namespace esp_brookesia::apps {

// palette
static const uint32_t C_BG     = 0x000000;
static const uint32_t C_WHITE  = 0xFFFFFF;
static const uint32_t C_GRAY   = 0x8A8F98;
static const uint32_t C_DIM    = 0x3A4052;
static const uint32_t C_GREEN  = 0x3DD68C;   // active
static const uint32_t C_BLUE   = 0x4C8DFF;   // done
static const uint32_t C_AMBER  = 0xF5A524;   // idle
static const uint32_t C_YELLOW = 0xFFE066;
static const uint32_t C_ORANGE = 0xFF8C42;
static const uint32_t C_RED    = 0xFF5C5C;
static const uint32_t C_CYAN   = 0x5CE1E6;

static uint32_t stateColor(const char *st)
{
    if (!strcmp(st, "tool") || !strcmp(st, "thinking") || !strcmp(st, "prompt_start") || !strcmp(st, "subagent"))
        return C_GREEN;
    if (!strcmp(st, "done") || !strcmp(st, "tool_end")) return C_BLUE;
    return C_AMBER;
}

static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color,
                         lv_align_t align, int x, int y)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    lv_obj_align(l, align, x, y);
    return l;
}

static lv_obj_t *mkBar(lv_obj_t *parent, int y, uint32_t color)
{
    lv_obj_t *b = lv_bar_create(parent);
    lv_obj_set_size(b, 280, 14);
    lv_obj_align(b, LV_ALIGN_TOP_MID, 0, y);
    lv_bar_set_range(b, 0, 100);
    lv_bar_set_value(b, 0, LV_ANIM_OFF);
    lv_obj_set_style_bg_color(b, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), LV_PART_INDICATOR);
    lv_obj_set_style_radius(b, 7, LV_PART_MAIN);
    lv_obj_set_style_radius(b, 7, LV_PART_INDICATOR);
    return b;
}

static lv_obj_t *mkTile(lv_obj_t *tv, int col, lv_dir_t dir)
{
    lv_obj_t *t = lv_tileview_add_tile(tv, col, 0, dir);
    lv_obj_set_style_bg_color(t, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(t, LV_OPA_COVER, 0);
    lv_obj_remove_flag(t, LV_OBJ_FLAG_SCROLLABLE);
    return t;
}

ClaudeHud *ClaudeHud::_instance = nullptr;

ClaudeHud *ClaudeHud::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) _instance = new ClaudeHud(use_status_bar, use_navigation_bar);
    return _instance;
}

ClaudeHud::ClaudeHud(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_claude_hud_112_112, true, use_status_bar, use_navigation_bar)
{
}

ClaudeHud::~ClaudeHud() {}

bool ClaudeHud::init(void)
{
    ESP_UTILS_LOGI("Init: starting BLE (the only transport)");
    claude_hud::startBle();
    return true;
}

bool ClaudeHud::run(void)
{
    ESP_UTILS_LOGD("Run");
    buildUi(lv_scr_act());
    _seenVersion = 0;
    refresh(true);
    // Recorded by the core (enable_recycle_resource) -> deleted automatically on close.
    _timer = lv_timer_create(timerCb, 500, this);
    return true;
}

bool ClaudeHud::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

bool ClaudeHud::close(void)
{
    _timer = nullptr;   // freed by the core together with the screen
    return true;
}

void ClaudeHud::timerCb(lv_timer_t *t)
{
    auto *self = (ClaudeHud *)lv_timer_get_user_data(t);
    if (self) self->refresh(false);
}

void ClaudeHud::brightnessCb(lv_event_t *e)
{
    lv_obj_t *slider = (lv_obj_t *)lv_event_get_target(e);
    bsp_display_brightness_set((int)lv_slider_get_value(slider));
}

void ClaudeHud::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    lv_obj_t *tv = lv_tileview_create(scr);
    lv_obj_set_size(tv, LV_PCT(100), LV_PCT(100));
    lv_obj_set_style_bg_color(tv, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(tv, LV_OPA_COVER, 0);
    lv_obj_set_scrollbar_mode(tv, LV_SCROLLBAR_MODE_OFF);

    lv_obj_t *t0 = mkTile(tv, 0, LV_DIR_RIGHT);
    lv_obj_t *t1 = mkTile(tv, 1, LV_DIR_HOR);
    lv_obj_t *t2 = mkTile(tv, 2, LV_DIR_LEFT);

    // ---- tile 0: SESSIONS (round display: keep content inside ~x 60..406) ----
    _ring = lv_arc_create(t0);
    lv_obj_set_size(_ring, 456, 456);
    lv_obj_center(_ring);
    lv_arc_set_bg_angles(_ring, 0, 360);
    lv_arc_set_range(_ring, 0, 360);
    lv_arc_set_value(_ring, 360);
    lv_obj_remove_style(_ring, nullptr, LV_PART_KNOB);
    lv_obj_remove_flag(_ring, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_arc_width(_ring, 6, LV_PART_MAIN);
    lv_obj_set_style_arc_width(_ring, 6, LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(C_AMBER), LV_PART_INDICATOR);

    mkLabel(t0, "SESSIONS", &lv_font_montserrat_28, C_WHITE, LV_ALIGN_TOP_MID, 0, 62);
    _lblActive = mkLabel(t0, "0 active", &lv_font_montserrat_20, C_GRAY, LV_ALIGN_TOP_MID, 0, 104);
    for (int i = 0; i < 4; i++) {
        _rows[i] = mkLabel(t0, "", &lv_font_montserrat_20, C_GRAY, LV_ALIGN_TOP_LEFT, 64, 160 + i * 46);
        lv_obj_set_width(_rows[i], 338);
        lv_label_set_long_mode(_rows[i], LV_LABEL_LONG_DOT);
    }
    mkLabel(t0, "swipe > usage", &lv_font_montserrat_16, C_DIM, LV_ALIGN_BOTTOM_MID, 0, -70);

    // ---- tile 1: USAGE ----
    mkLabel(t1, "USAGE", &lv_font_montserrat_28, C_CYAN, LV_ALIGN_TOP_MID, 0, 62);
    _lblCost = mkLabel(t1, "$0.000", &lv_font_montserrat_44, C_YELLOW, LV_ALIGN_TOP_MID, 0, 108);
    _lblSess = mkLabel(t1, "0 sess", &lv_font_montserrat_20, C_GRAY, LV_ALIGN_TOP_MID, 0, 164);
    _lblCtx = mkLabel(t1, "ctx 0%", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 206);
    _barCtx = mkBar(t1, 234, C_GREEN);
    _lblRl5 = mkLabel(t1, "5h  n/a", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 262);
    _barRl5 = mkBar(t1, 290, C_ORANGE);
    _lblRl7 = mkLabel(t1, "7d  n/a", &lv_font_montserrat_20, C_WHITE, LV_ALIGN_TOP_LEFT, 92, 318);
    _barRl7 = mkBar(t1, 346, C_RED);

    // ---- tile 2: INFO + brightness ----
    mkLabel(t2, "INFO", &lv_font_montserrat_28, C_GRAY, LV_ALIGN_TOP_MID, 0, 62);
    _lblNet = mkLabel(t2, "", &lv_font_montserrat_18, C_WHITE, LV_ALIGN_TOP_LEFT, 84, 110);
    lv_obj_set_width(_lblNet, 300);
    lv_label_set_long_mode(_lblNet, LV_LABEL_LONG_WRAP);
    mkLabel(t2, "brightness", &lv_font_montserrat_16, C_YELLOW, LV_ALIGN_TOP_LEFT, 92, 318);
    lv_obj_t *sl = lv_slider_create(t2);
    lv_obj_set_size(sl, 260, 16);
    lv_obj_align(sl, LV_ALIGN_TOP_MID, 0, 346);
    lv_slider_set_range(sl, 10, 100);
    lv_slider_set_value(sl, 80, LV_ANIM_OFF);
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_YELLOW), LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(sl, lv_color_hex(C_WHITE), LV_PART_KNOB);
    lv_obj_add_event_cb(sl, brightnessCb, LV_EVENT_VALUE_CHANGED, nullptr);
}

void ClaudeHud::refresh(bool force)
{
    State &st = State::instance();
    uint32_t now = claude_hud::nowMs();
    uint32_t ver = st.version();
    if (!force && ver == _seenVersion && now - _lastRefreshMs < 1500) return;
    _seenVersion = ver;
    _lastRefreshMs = now;

    Session ss[claude_hud::MAX_SESSIONS];
    Limits lim;
    st.snapshot(ss, lim);

    // ---- sessions ----
    int active = 0, shown = 0;
    bool anyActive = false;
    float totalCost = 0, maxCtx = 0;
    for (auto &s : ss) {
        if (!s.used) continue;
        bool idle = now - s.lastSeenMs > claude_hud::SESSION_IDLE_MS;
        if (!idle) active++;
        if (!idle && strcmp(s.state, "done") && strcmp(s.state, "idle")) anyActive = true;
        totalCost += s.costUsd;
        if (s.ctxUsedPct > maxCtx) maxCtx = s.ctxUsedPct;
        if (shown < 4) {
            char line[120];
            if (s.host[0]) snprintf(line, sizeof(line), "%s:%s  %s", s.host, s.label, s.activity);
            else           snprintf(line, sizeof(line), "%s  %s", s.label, s.activity);
            lv_label_set_text(_rows[shown], line);
            lv_obj_set_style_text_color(_rows[shown], lv_color_hex(idle ? C_GRAY : stateColor(s.state)), 0);
            shown++;
        }
    }
    for (int i = shown; i < 4; i++) lv_label_set_text(_rows[i], i == 0 ? "no sessions yet" : "");
    char buf[64];
    snprintf(buf, sizeof(buf), "%d active", active);
    lv_label_set_text(_lblActive, buf);
    lv_obj_set_style_arc_color(_ring, lv_color_hex(anyActive ? C_GREEN : C_AMBER), LV_PART_INDICATOR);

    // ---- usage ----
    snprintf(buf, sizeof(buf), "$%.3f", totalCost);
    lv_label_set_text(_lblCost, buf);
    snprintf(buf, sizeof(buf), "%d sess", active);
    lv_label_set_text(_lblSess, buf);
    snprintf(buf, sizeof(buf), "ctx %d%%", (int)maxCtx);
    lv_label_set_text(_lblCtx, buf);
    lv_bar_set_value(_barCtx, (int)maxCtx, LV_ANIM_OFF);
    if (lim.has) {
        snprintf(buf, sizeof(buf), "5h  %d%%", (int)lim.rl5hPct);
        lv_label_set_text(_lblRl5, buf);
        lv_bar_set_value(_barRl5, (int)lim.rl5hPct, LV_ANIM_OFF);
        snprintf(buf, sizeof(buf), "7d  %d%%", (int)lim.rl7dPct);
        lv_label_set_text(_lblRl7, buf);
        lv_bar_set_value(_barRl7, (int)lim.rl7dPct, LV_ANIM_OFF);
    } else {
        lv_label_set_text(_lblRl5, "5h  n/a");
        lv_label_set_text(_lblRl7, "7d  n/a");
    }

    // ---- info ----
    auto &net = st.net;
    char info[300];
    const char *bleState = net.bleConn ? "connected" : (net.bleAdv ? "advertising" : (net.bleReady ? "idle" : "starting"));
    if (net.bleErr[0]) bleState = net.bleErr;
    if (net.rxBle) {
        snprintf(info, sizeof(info), "BLE  %s\nname  claude-hud\nrx %lu  last %lus ago\nup %lus",
                 bleState, (unsigned long)net.rxBle, (unsigned long)((now - net.lastRxMs) / 1000),
                 (unsigned long)(now / 1000));
    } else {
        snprintf(info, sizeof(info), "BLE  %s\nname  claude-hud\nrx none yet\nup %lus",
                 bleState, (unsigned long)(now / 1000));
    }
    lv_label_set_text(_lblNet, info);
}

ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, ClaudeHud, APP_NAME, []()
{
    return std::shared_ptr<ClaudeHud>(ClaudeHud::requestInstance(), [](ClaudeHud * p) {});
})

} // namespace esp_brookesia::apps
