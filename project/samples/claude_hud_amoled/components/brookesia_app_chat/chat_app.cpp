#include <cstdio>
#include <cstring>
#include "lvgl.h"
#include "esp_brookesia.hpp"
#ifdef ESP_UTILS_LOG_TAG
#   undef ESP_UTILS_LOG_TAG
#endif
#define ESP_UTILS_LOG_TAG "BS:Chat"
#include "esp_lib_utils.h"
#include "chat_app.hpp"
#include "hud_transport.hpp"

#define APP_NAME "Chat"

using namespace esp_brookesia::gui;
using namespace esp_brookesia::systems;
using voice_chat::Core;
using voice_chat::Snapshot;
using voice_chat::Stage;

LV_IMG_DECLARE(esp_brookesia_app_icon_launcher_chat_112_112);
LV_FONT_DECLARE(font_nanum_18);          // shared with brookesia_app_claude_hud (Latin + Hangul)

namespace esp_brookesia::apps {

static const uint32_t C_BG    = 0x000000;
static const uint32_t C_WHITE = 0xFFFFFF;
static const uint32_t C_GRAY  = 0x8A8F98;
static const uint32_t C_DIM   = 0x232733;
static const uint32_t C_GREEN = 0x3DD68C;
static const uint32_t C_BLUE  = 0x4C8DFF;
static const uint32_t C_AMBER = 0xF5A524;
static const uint32_t C_RED   = 0xFF5C5C;
static const uint32_t C_CYAN  = 0x5CE1E6;

static const char *PRESET_TEXT = "안녕! 지금 몇 시인지 한 문장으로 알려줘.";

VoiceChat *VoiceChat::_instance = nullptr;

VoiceChat *VoiceChat::requestInstance(bool use_status_bar, bool use_navigation_bar)
{
    if (_instance == nullptr) {
        _instance = new (std::nothrow) VoiceChat(use_status_bar, use_navigation_bar);
        ESP_UTILS_CHECK_NULL_RETURN(_instance, nullptr, "Create instance failed");
    }
    return _instance;
}

VoiceChat::VoiceChat(bool use_status_bar, bool use_navigation_bar):
    App(APP_NAME, &esp_brookesia_app_icon_launcher_chat_112_112, true, use_status_bar, use_navigation_bar)
{
}

VoiceChat::~VoiceChat() {}

bool VoiceChat::init(void)
{
    ESP_UTILS_LOGI("Init: BLE transport + chat core");
    claude_hud::startBle();          // idempotent; whichever app installs first starts the stack
    Core::instance().init();
    return true;
}

bool VoiceChat::run(void)
{
    buildUi(lv_scr_act());
    _seen = 0;
    refresh();
    _timer = lv_timer_create(timerCb, 100, this);   // recycled by the core on close
    return true;
}

bool VoiceChat::back(void)
{
    ESP_UTILS_CHECK_FALSE_RETURN(notifyCoreClosed(), false, "Notify core closed failed");
    return true;
}

bool VoiceChat::close(void)
{
    Core::instance().stopVoice();
    _timer = nullptr;
    return true;
}

void VoiceChat::timerCb(lv_timer_t *t)
{
    auto *self = (VoiceChat *)lv_timer_get_user_data(t);
    if (self) self->refresh();
}

// ---------------------------------------------------------------- events
void VoiceChat::micEvent(lv_event_t *e)
{
    auto code = lv_event_get_code(e);
    if (code == LV_EVENT_PRESSED) {
        Core::instance().startVoice(30000);
    } else if (code == LV_EVENT_RELEASED || code == LV_EVENT_PRESS_LOST) {
        Core::instance().stopVoice();
    }
}

void VoiceChat::textEvent(lv_event_t *)  { Core::instance().sendText(PRESET_TEXT); }
void VoiceChat::clearEvent(lv_event_t *) { Core::instance().clear(); }

// ---------------------------------------------------------------- UI
static lv_obj_t *mkLabel(lv_obj_t *parent, const char *txt, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_label_set_text(l, txt);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    return l;
}

// `code` must be a specific event (LV_EVENT_CLICKED, ...). Registering LV_EVENT_ALL here would fire the
// callback on every draw/refresh event too - that once turned the T button into a prompt flood.
static lv_obj_t *mkRoundBtn(lv_obj_t *parent, int size, uint32_t color, const char *txt, const lv_font_t *font,
                            lv_event_cb_t cb, lv_event_code_t code)
{
    lv_obj_t *b = lv_button_create(parent);
    lv_obj_set_size(b, size, size);
    lv_obj_set_style_radius(b, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), 0);
    lv_obj_set_style_bg_color(b, lv_color_hex(color), LV_STATE_PRESSED);
    lv_obj_set_style_shadow_width(b, 0, 0);
    lv_obj_add_event_cb(b, cb, code, nullptr);
    lv_obj_t *l = mkLabel(b, txt, font, C_WHITE);
    lv_obj_center(l);
    return b;
}

void VoiceChat::buildUi(lv_obj_t *scr)
{
    lv_obj_set_style_bg_color(scr, lv_color_hex(C_BG), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);

    // top: host line
    _lblHost = mkLabel(scr, "BLE 대기 중", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblHost, 300);
    lv_obj_set_style_text_align(_lblHost, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblHost, LV_ALIGN_TOP_MID, 0, 34);

    // middle: conversation box (scrollable)
    _box = lv_obj_create(scr);
    lv_obj_set_size(_box, 360, 210);
    lv_obj_align(_box, LV_ALIGN_TOP_MID, 0, 70);
    lv_obj_set_style_bg_color(_box, lv_color_hex(C_DIM), 0);
    lv_obj_set_style_bg_opa(_box, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(_box, 0, 0);
    lv_obj_set_style_radius(_box, 18, 0);
    lv_obj_set_style_pad_all(_box, 14, 0);
    lv_obj_set_flex_flow(_box, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(_box, 8, 0);
    lv_obj_set_scroll_dir(_box, LV_DIR_VER);

    _lblUser = mkLabel(_box, "", &font_nanum_18, C_CYAN);
    lv_obj_set_width(_lblUser, 330);
    lv_label_set_long_mode(_lblUser, LV_LABEL_LONG_WRAP);
    _lblAi = mkLabel(_box, "마이크 버튼을 누른 채 말하세요.", &font_nanum_18, C_WHITE);
    lv_obj_set_width(_lblAi, 330);
    lv_label_set_long_mode(_lblAi, LV_LABEL_LONG_WRAP);

    // stage line + level bar
    _lblStage = mkLabel(scr, "", &font_nanum_18, C_GRAY);
    lv_obj_set_width(_lblStage, 300);
    lv_obj_set_style_text_align(_lblStage, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(_lblStage, LV_ALIGN_TOP_MID, 0, 290);

    _bar = lv_bar_create(scr);
    lv_obj_set_size(_bar, 200, 6);
    lv_obj_align(_bar, LV_ALIGN_TOP_MID, 0, 318);
    lv_bar_set_range(_bar, 0, 100);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_DIM), LV_PART_MAIN);
    lv_obj_set_style_bg_color(_bar, lv_color_hex(C_GREEN), LV_PART_INDICATOR);
    lv_obj_add_flag(_bar, LV_OBJ_FLAG_HIDDEN);

    // bottom: [TEXT]  (MIC)  [CLR]
    // hold-to-talk: three separate registrations so no draw/refresh event can reach the handler
    _btnMic = mkRoundBtn(scr, 96, C_BLUE, LV_SYMBOL_AUDIO, &lv_font_montserrat_28, micEvent, LV_EVENT_PRESSED);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_RELEASED, nullptr);
    lv_obj_add_event_cb(_btnMic, micEvent, LV_EVENT_PRESS_LOST, nullptr);
    lv_obj_align(_btnMic, LV_ALIGN_TOP_MID, 0, 334);
    _lblMic = lv_obj_get_child(_btnMic, 0);

    _btnText = mkRoundBtn(scr, 56, C_DIM, "T", &lv_font_montserrat_22, textEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnText, LV_ALIGN_TOP_MID, -96, 354);
    _btnClear = mkRoundBtn(scr, 56, C_DIM, LV_SYMBOL_TRASH, &lv_font_montserrat_20, clearEvent, LV_EVENT_CLICKED);
    lv_obj_align(_btnClear, LV_ALIGN_TOP_MID, 96, 354);
}

void VoiceChat::refresh()
{
    Core &core = Core::instance();
    uint32_t v = core.version();
    // level bar/timer need updating even without a version bump while recording
    Snapshot s;
    core.snapshot(s);
    bool recording = s.stage == Stage::Recording;
    if (v == _seen && !recording) return;
    _seen = v;

    char buf[160];
    if (!s.bleConnected)      snprintf(buf, sizeof(buf), "BLE 연결 대기 중 (claude-hud)");
    else if (!s.hostOnline)   snprintf(buf, sizeof(buf), "BLE 연결됨 · 호스트 응답 대기");
    else                      snprintf(buf, sizeof(buf), "%s · %s", s.host, s.provider);
    lv_label_set_text(_lblHost, buf);

    lv_label_set_text(_lblUser, s.transcript[0] ? s.transcript : "");
    if (s.reply[0])             lv_label_set_text(_lblAi, s.reply);
    else if (s.stage == Stage::Idle && !s.transcript[0]) lv_label_set_text(_lblAi, "마이크 버튼을 누른 채 말하세요.");
    else                        lv_label_set_text(_lblAi, "");

    const char *stage = "";
    uint32_t micColor = C_BLUE;
    switch (s.stage) {
    case Stage::Idle:      stage = s.micOk ? "누르고 말하기" : "누르고 말하기 (마이크 초기화 대기)"; break;
    case Stage::Recording: snprintf(buf, sizeof(buf), "듣는 중… %lu.%lus", (unsigned long)(s.recMs / 1000), (unsigned long)((s.recMs / 100) % 10));
                           stage = buf; micColor = C_RED; break;
    case Stage::Sending:   stage = "전송 완료, 호스트 대기…"; micColor = C_AMBER; break;
    case Stage::Stt:       stage = "음성 인식 중…"; micColor = C_AMBER; break;
    case Stage::Think:     stage = "생각 중…"; micColor = C_AMBER; break;
    case Stage::Reply:     stage = s.replyDone ? "완료" : "답변 수신 중…"; micColor = s.replyDone ? C_GREEN : C_AMBER; break;
    case Stage::Busy:      stage = "호스트가 바쁩니다, 잠시 후 다시"; micColor = C_AMBER; break;
    case Stage::Error:     snprintf(buf, sizeof(buf), "오류: %s", s.error); stage = buf; micColor = C_RED; break;
    }
    lv_label_set_text(_lblStage, stage);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(micColor), 0);
    lv_obj_set_style_bg_color(_btnMic, lv_color_hex(micColor), LV_STATE_PRESSED);

    if (recording) {
        lv_obj_remove_flag(_bar, LV_OBJ_FLAG_HIDDEN);
        lv_bar_set_value(_bar, (int)(s.level * 100), LV_ANIM_OFF);
    } else {
        lv_obj_add_flag(_bar, LV_OBJ_FLAG_HIDDEN);
    }
    // Scroll to the answer only when it actually grew. Never pass LV_COORD_MAX to lv_obj_scroll_to_y:
    // it is ~5.4e8, and the resulting scroll arithmetic overflows and scrambles every child of _box
    // (both bubbles render as garbage). lv_obj_scroll_to_view does the clamped, correct thing.
    size_t replyLen = strlen(s.reply);
    if (replyLen != _lastReplyLen) {
        _lastReplyLen = replyLen;
        if (replyLen) lv_obj_scroll_to_view(_lblAi, LV_ANIM_OFF);
    }
}

ESP_UTILS_REGISTER_PLUGIN_WITH_CONSTRUCTOR(systems::base::App, VoiceChat, APP_NAME, []()
{
    return std::shared_ptr<VoiceChat>(VoiceChat::requestInstance(), [](VoiceChat * p) {});
})

} // namespace esp_brookesia::apps
