#include "chat_core.hpp"
#include <cstring>
#include <cstdio>
#include <cmath>
#include <algorithm>
#include "esp_log.h"
#include "esp_timer.h"
#include "cJSON.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "bsp/esp-bsp.h"
#include "esp_codec_dev.h"
#include "hud_transport.hpp"

static const char *TAG = "chat_core";

namespace voice_chat {

using namespace claude_hud;

static constexpr int      SAMPLE_RATE = 16000;
static constexpr size_t   MAX_SAMPLES_PER_BLOCK = 960;      // 60 ms
static constexpr uint32_t HARD_MAX_MS = 60000;

static inline uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

// ---------------------------------------------------------------- IMA ADPCM (block = [pred i16][idx u8][0][nibbles])
static const int IDX_TAB[16] = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
static const int STEP_TAB[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
    118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876,
    963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
    5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385,
    24623, 27086, 29794, 32767 };

struct AdpcmState { int predictor = 0; int index = 0; };

// Encodes n samples (n even) into out; returns bytes written = 4 + n/2. Mirrors ChatHost ImaAdpcm.EncodeBlock.
static size_t adpcmEncodeBlock(const int16_t *s, size_t n, AdpcmState &st, uint8_t *out)
{
    out[0] = (uint8_t)st.predictor; out[1] = (uint8_t)(st.predictor >> 8); out[2] = (uint8_t)st.index; out[3] = 0;
    for (size_t i = 0; i < n; i++) {
        int step = STEP_TAB[st.index];
        int diff = s[i] - st.predictor;
        int nib = 0;
        if (diff < 0) { nib = 8; diff = -diff; }
        int vp = step >> 3;
        if (diff >= step)        { nib |= 4; diff -= step;      vp += step; }
        if (diff >= (step >> 1)) { nib |= 2; diff -= step >> 1; vp += step >> 1; }
        if (diff >= (step >> 2)) { nib |= 1;                    vp += step >> 2; }
        int p = (nib & 8) ? st.predictor - vp : st.predictor + vp;
        st.predictor = p < -32768 ? -32768 : (p > 32767 ? 32767 : p);
        int idx = st.index + IDX_TAB[nib];
        st.index = idx < 0 ? 0 : (idx > 88 ? 88 : idx);
        if ((i & 1) == 0) out[4 + i / 2] = (uint8_t)nib; else out[4 + i / 2] |= (uint8_t)(nib << 4);
    }
    return 4 + (n + 1) / 2;
}

// ---------------------------------------------------------------- singleton / hooks
Core &Core::instance() { static Core c; return c; }

static bool lineHookTrampoline(const char *line, size_t len) { return Core::instance().onLine(line, len); }
static void captureTaskTrampoline(void *) { Core::instance().captureTask(); }
static void txTaskTrampoline(void *) { Core::instance().txTask(); }

// Outbound line handed from the NimBLE host task (or the UI) to the tx task.
struct TxMsg { char json[400]; };

void Core::init()
{
    if (task_) return;
    setLineHook(lineHookTrampoline);
    // The tx task exists so a reply to an inbound line never calls into NimBLE from inside its own
    // GATT-write callback (the host lock is held there; notifying from it can deadlock).
    txQueue_ = xQueueCreate(4, sizeof(TxMsg));
    TaskHandle_t t = nullptr;
    xTaskCreatePinnedToCore(txTaskTrampoline, "chat_tx", 4096, nullptr, 4, &t, 0);
    txTask_ = t;
    // Capture task: internal-RAM stack (I2S DMA reads), core 1 keeps it away from the NimBLE host task on core 0.
    TaskHandle_t h = nullptr;
    xTaskCreatePinnedToCore(captureTaskTrampoline, "chat_mic", 6144, nullptr, 5, &h, 1);
    task_ = h;
    ESP_LOGI(TAG, "init: line hook set, capture + tx tasks started");
}

void Core::txTask()
{
    TxMsg *msg = (TxMsg *)heap_caps_malloc(sizeof(TxMsg), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!msg) { ESP_LOGE(TAG, "no memory for tx buffer"); vTaskDelete(nullptr); return; }
    for (;;) {
        if (xQueueReceive((QueueHandle_t)txQueue_, msg, portMAX_DELAY) == pdTRUE) {
            if (!sendLine(msg->json)) ESP_LOGW(TAG, "tx dropped: %.60s", msg->json);
        }
    }
}

bool Core::queueLine(const char *json)
{
    if (!txQueue_ || !json) return false;
    TxMsg msg;
    snprintf(msg.json, sizeof(msg.json), "%s", json);
    return xQueueSend((QueueHandle_t)txQueue_, &msg, 0) == pdTRUE;
}

bool Core::micInit()
{
    if (mic_) return true;
    esp_codec_dev_handle_t h = bsp_audio_codec_microphone_init();
    if (!h) { ESP_LOGW(TAG, "microphone init failed (bsp_audio_codec_microphone_init)"); return false; }
    esp_codec_dev_set_in_gain(h, 30.0f);
    esp_codec_dev_sample_info_t fs = {};
    fs.sample_rate = SAMPLE_RATE;
    fs.channel = 1;
    fs.bits_per_sample = 16;
    int rc = esp_codec_dev_open(h, &fs);
    if (rc != ESP_CODEC_DEV_OK) { ESP_LOGW(TAG, "codec open failed rc=%d", rc); return false; }
    mic_ = h;
    { std::lock_guard<std::mutex> g(m_); s_.micOk = true; bump(); }
    ESP_LOGI(TAG, "microphone ready: %d Hz mono 16-bit", SAMPLE_RATE);
    return true;
}

// ---------------------------------------------------------------- state helpers
void Core::setStage(Stage st, const char *err)
{
    std::lock_guard<std::mutex> g(m_);
    s_.stage = st;
    if (err) snprintf(s_.error, sizeof(s_.error), "%s", err);
    else if (st != Stage::Error) s_.error[0] = 0;
    bump();
}

void Core::snapshot(Snapshot &out)
{
    std::lock_guard<std::mutex> g(m_);
    s_.bleConnected = bleConnected();
    out = s_;
}

uint32_t Core::version() { std::lock_guard<std::mutex> g(m_); return ver_; }

void Core::clear()
{
    std::lock_guard<std::mutex> g(m_);
    s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
    s_.stage = Stage::Idle;
    bump();
}

bool Core::sendLine(const char *json)
{
    char buf[560];
    int n = snprintf(buf, sizeof(buf), "R %s", json);
    if (n <= 0 || n >= (int)sizeof(buf)) return false;
    return bleSendLine(buf, (size_t)n);
}

// ---------------------------------------------------------------- user actions
bool Core::startVoice(uint32_t maxMs)
{
    if (recording_) return false;
    if (!bleConnected()) { setStage(Stage::Error, "BLE not connected"); return false; }
    {
        std::lock_guard<std::mutex> g(m_);
        if (s_.stage == Stage::Stt || s_.stage == Stage::Think) { /* host still busy */ }
        s_.reqId = nextId_;
        nextId_ = (uint8_t)(nextId_ == 255 ? 1 : nextId_ + 1);
        s_.transcript[0] = 0; s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
        s_.recMs = 0; s_.level = 0; s_.framesSent = 0; s_.framesDropped = 0;
        s_.stage = Stage::Recording;
        bump();
    }
    maxMs_ = std::min(maxMs == 0 ? HARD_MAX_MS : maxMs, HARD_MAX_MS);
    recording_ = true;                 // the capture task picks this up (it sends the "voice" header itself)
    return true;
}

void Core::stopVoice()
{
    recording_ = false;                // capture task sends "end"
}

bool Core::sendText(const char *text)
{
    if (!text || !*text) return false;
    if (!bleConnected()) { setStage(Stage::Error, "BLE not connected"); return false; }
    int id;
    {
        std::lock_guard<std::mutex> g(m_);
        id = s_.reqId = nextId_;
        nextId_ = (uint8_t)(nextId_ == 255 ? 1 : nextId_ + 1);
        snprintf(s_.transcript, sizeof(s_.transcript), "%s", text);
        s_.reply[0] = 0; s_.replyDone = false; s_.error[0] = 0;
        s_.stage = Stage::Sending;
        bump();
    }
    cJSON *js = cJSON_CreateObject();
    cJSON_AddStringToObject(js, "t", "text");
    cJSON_AddNumberToObject(js, "id", id);
    cJSON_AddStringToObject(js, "text", text);
    char *out = cJSON_PrintUnformatted(js);
    bool ok = out && queueLine(out);
    cJSON_free(out); cJSON_Delete(js);
    if (!ok) setStage(Stage::Error, "send failed");
    return ok;
}

// ---------------------------------------------------------------- inbound (NimBLE host task)
bool Core::onLine(const char *line, size_t len)
{
    if (len < 3 || line[1] != ' ') return false;
    char tag = line[0];
    cJSON *js = cJSON_ParseWithLength(line + 2, len - 2);
    if (!js) { ESP_LOGW(TAG, "bad json on '%c' line", tag); return false; }
    bool ok = true;

    if (tag == 'H') {                                   // host hello
        const cJSON *h = cJSON_GetObjectItem(js, "host");
        const cJSON *p = cJSON_GetObjectItem(js, "provider");
        {
            std::lock_guard<std::mutex> g(m_);
            snprintf(s_.host, sizeof(s_.host), "%s", cJSON_IsString(h) ? h->valuestring : "?");
            snprintf(s_.provider, sizeof(s_.provider), "%s", cJSON_IsString(p) ? p->valuestring : "?");
            s_.hostOnline = true;
            bump();
        }
        ESP_LOGI(TAG, "host online: %s (%s)", s_.host, s_.provider);
        queueLine("{\"t\":\"hello\",\"name\":\"claude-hud\",\"fw\":\"chat-1\",\"fmt\":\"adpcm\"}");
    } else if (tag == 'A') {                            // answer / progress
        const cJSON *st = cJSON_GetObjectItem(js, "st");
        const cJSON *tx = cJSON_GetObjectItem(js, "text");
        const cJSON *id = cJSON_GetObjectItem(js, "id");
        const char *stage = cJSON_IsString(st) ? st->valuestring : "";
        const char *text = cJSON_IsString(tx) ? tx->valuestring : nullptr;
        int rid = cJSON_IsNumber(id) ? id->valueint : 0;
        std::lock_guard<std::mutex> g(m_);
        if (rid != 0 && rid != s_.reqId && strcmp(stage, "busy") != 0) {
            ESP_LOGD(TAG, "answer for old request %d ignored", rid);
        } else if (!strcmp(stage, "rec")) {
            // host accepted the capture — nothing to show
        } else if (!strcmp(stage, "stt")) {
            s_.stage = Stage::Stt;
            if (text) snprintf(s_.transcript, sizeof(s_.transcript), "%s", text);
        } else if (!strcmp(stage, "think")) {
            s_.stage = Stage::Think;
        } else if (!strcmp(stage, "reply")) {
            const cJSON *seq = cJSON_GetObjectItem(js, "seq");
            if (cJSON_IsNumber(seq) && seq->valueint == 0) s_.reply[0] = 0;
            if (text) {
                size_t cur = strlen(s_.reply);
                snprintf(s_.reply + cur, sizeof(s_.reply) - cur, "%s", text);
            }
            s_.stage = Stage::Reply;
            s_.replyDone = cJSON_IsTrue(cJSON_GetObjectItem(js, "done"));
            if (s_.replyDone) ESP_LOGI(TAG, "reply #%d: %s", rid, s_.reply);
        } else if (!strcmp(stage, "err")) {
            s_.stage = Stage::Error;
            snprintf(s_.error, sizeof(s_.error), "%s", text ? text : "error");
            recording_ = false;
        } else if (!strcmp(stage, "busy")) {
            s_.stage = Stage::Busy;
            recording_ = false;
        } else if (!strcmp(stage, "pong")) {
            // ignore
        } else ok = false;
        bump();
    } else if (tag == 'C') {                            // host debug command: {"cmd":"talk","ms":3000} | {"cmd":"text","text":".."}
        const cJSON *cmd = cJSON_GetObjectItem(js, "cmd");
        const char *c = cJSON_IsString(cmd) ? cmd->valuestring : "";
        if (!strcmp(c, "talk")) {
            const cJSON *ms = cJSON_GetObjectItem(js, "ms");
            uint32_t d = cJSON_IsNumber(ms) ? (uint32_t)ms->valueint : 3000;
            ESP_LOGI(TAG, "host asked for a %lu ms capture", (unsigned long)d);
            ok = startVoice(d);
        } else if (!strcmp(c, "text")) {
            const cJSON *tx = cJSON_GetObjectItem(js, "text");
            ok = cJSON_IsString(tx) && sendText(tx->valuestring);
        } else if (!strcmp(c, "clear")) {
            clear();
        } else ok = false;
    } else ok = false;

    cJSON_Delete(js);
    return ok;
}

// ---------------------------------------------------------------- capture task
void Core::captureTask()
{
    int16_t *pcm = (int16_t *)heap_caps_malloc(MAX_SAMPLES_PER_BLOCK * sizeof(int16_t), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    uint8_t *frame = (uint8_t *)heap_caps_malloc(4 + 4 + MAX_SAMPLES_PER_BLOCK / 2 + 8, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!pcm || !frame) { ESP_LOGE(TAG, "no memory for capture buffers"); vTaskDelete(nullptr); return; }

    for (;;) {
        if (!recording_) { vTaskDelay(pdMS_TO_TICKS(20)); continue; }

        if (!micInit()) {
            recording_ = false;
            setStage(Stage::Error, "microphone unavailable");
            continue;
        }

        int id;
        { std::lock_guard<std::mutex> g(m_); id = s_.reqId; }

        // header line first; then frames; stop when released, too long, disconnected or the host says busy/err
        char hdr[96];
        snprintf(hdr, sizeof(hdr), "{\"t\":\"voice\",\"id\":%d,\"fmt\":\"adpcm\",\"rate\":%d,\"ch\":1}", id, SAMPLE_RATE);
        if (!sendLine(hdr)) { recording_ = false; setStage(Stage::Error, "BLE send failed"); continue; }

        AdpcmState st;
        uint16_t seq = 0;
        uint32_t t0 = nowMs();
        uint32_t sent = 0, dropped = 0;
        size_t payloadMax = (size_t)bleMaxPayload();                       // frame incl. 4-byte header
        size_t samples = payloadMax > 12 ? (payloadMax - 8) * 2 : 16;      // 4 frame hdr + 4 block hdr
        samples = std::min(samples, MAX_SAMPLES_PER_BLOCK) & ~(size_t)1;
        ESP_LOGI(TAG, "capture #%d start: %u samples/frame, mtu payload %u", id, (unsigned)samples, (unsigned)payloadMax);

        while (recording_ && bleConnected()) {
            int want = (int)(samples * sizeof(int16_t));
            int rc = esp_codec_dev_read(mic_, pcm, want);
            if (rc != ESP_CODEC_DEV_OK) { ESP_LOGW(TAG, "codec read rc=%d", rc); vTaskDelay(pdMS_TO_TICKS(10)); continue; }

            double acc = 0;
            for (size_t i = 0; i < samples; i++) acc += (double)pcm[i] * pcm[i];
            float rms = (float)std::sqrt(acc / samples) / 32768.0f;

            frame[0] = 0xA5; frame[1] = (uint8_t)id; frame[2] = (uint8_t)seq; frame[3] = (uint8_t)(seq >> 8);
            size_t n = adpcmEncodeBlock(pcm, samples, st, frame + 4);
            bool ok = false;
            for (int retry = 0; retry < 8 && !ok; retry++) {
                ok = bleNotify(frame, 4 + n);
                if (!ok) vTaskDelay(pdMS_TO_TICKS(3));
            }
            if (ok) sent++; else dropped++;
            seq++;

            uint32_t el = nowMs() - t0;
            {
                std::lock_guard<std::mutex> g(m_);
                s_.level = std::min(1.0f, rms * 6.0f);
                s_.recMs = el;
                s_.framesSent = sent; s_.framesDropped = dropped;
                if ((seq & 3) == 0) bump();
            }
            if (el >= maxMs_) break;
        }
        recording_ = false;

        char end[48];
        snprintf(end, sizeof(end), "{\"t\":\"end\",\"id\":%d}", id);
        bool ok = sendLine(end);
        ESP_LOGI(TAG, "capture #%d end: %lu ms, %lu frames sent, %lu dropped, end %s",
                 id, (unsigned long)(nowMs() - t0), (unsigned long)sent, (unsigned long)dropped, ok ? "sent" : "FAILED");
        {
            std::lock_guard<std::mutex> g(m_);
            s_.level = 0;
            if (s_.stage == Stage::Recording) s_.stage = ok ? Stage::Sending : Stage::Error;
            if (!ok) snprintf(s_.error, sizeof(s_.error), "BLE send failed");
            bump();
        }
    }
}

} // namespace voice_chat
