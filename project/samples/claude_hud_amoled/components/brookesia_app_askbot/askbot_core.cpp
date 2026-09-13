#include "askbot_core.hpp"

#include <cstdio>
#include <cstring>
#include <string>

#include "cJSON.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "akka/remote_client.h"
#include "akka/transport.h"

static const char *TAG = "askbot";

namespace askbot {
namespace {

constexpr int TX_QUEUE_LEN = 8;
constexpr size_t TX_MAX = 640;   // one JSON message; the frame budget is far larger

constexpr const char *CHAT_ACTOR = "/user/chat";   // on the host
constexpr const char *LOCAL_ACTOR = "chat";        // ours: /user/chat on this node

uint32_t nowMs() { return (uint32_t)(esp_timer_get_time() / 1000); }

// ---------------------------------------------------------------- wifi
EventGroupHandle_t s_wifiEvents = nullptr;
constexpr int WIFI_OK = BIT0;
constexpr int WIFI_FAIL = BIT1;
constexpr int WIFI_MAX_RETRY = 10;
int s_retries = 0;

void onWifi(void *, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        if (s_retries < WIFI_MAX_RETRY) {
            s_retries++;
            ESP_LOGW(TAG, "wifi retry %d/%d", s_retries, WIFI_MAX_RETRY);
            esp_wifi_connect();
        } else {
            xEventGroupSetBits(s_wifiEvents, WIFI_FAIL);
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        s_retries = 0;
        xEventGroupSetBits(s_wifiEvents, WIFI_OK);
    }
}

// Brings up station mode and waits for a lease. Returns the assigned IP, or an
// empty string. Tolerates a netif/event loop another component already created -
// BLE is up in this firmware long before AskBot is opened.
std::string wifiUp()
{
    static bool started = false;
    static std::string ip;
    if (started) return ip;

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    if (err != ESP_OK && err != ESP_ERR_NVS_NO_FREE_PAGES) ESP_LOGW(TAG, "nvs init: %s", esp_err_to_name(err));

    s_wifiEvents = xEventGroupCreate();
    if (esp_netif_init() != ESP_OK) ESP_LOGW(TAG, "netif already initialised");
    err = esp_event_loop_create_default();
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) ESP_LOGW(TAG, "event loop: %s", esp_err_to_name(err));

    esp_netif_t *netif = esp_netif_create_default_wifi_sta();
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    if (esp_wifi_init(&init) != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_init failed");
        return {};
    }

    esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &onWifi, nullptr, nullptr);
    esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &onWifi, nullptr, nullptr);

    wifi_config_t cfg = {};
    snprintf((char *)cfg.sta.ssid, sizeof(cfg.sta.ssid), "%s", CONFIG_ASKBOT_WIFI_SSID);
    snprintf((char *)cfg.sta.password, sizeof(cfg.sta.password), "%s", CONFIG_ASKBOT_WIFI_PASSWORD);
    esp_wifi_set_mode(WIFI_MODE_STA);
    esp_wifi_set_config(WIFI_IF_STA, &cfg);
    if (esp_wifi_start() != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_start failed");
        return {};
    }
    started = true;

    const EventBits_t bits = xEventGroupWaitBits(s_wifiEvents, WIFI_OK | WIFI_FAIL, pdFALSE, pdFALSE,
                                                 pdMS_TO_TICKS(30000));
    if ((bits & WIFI_OK) == 0) {
        ESP_LOGE(TAG, "wifi did not connect to '%s'", CONFIG_ASKBOT_WIFI_SSID);
        return {};
    }

    esp_netif_ip_info_t info{};
    esp_netif_get_ip_info(netif, &info);
    char buf[16];
    snprintf(buf, sizeof(buf), IPSTR, IP2STR(&info.ip));
    ip.assign(buf);
    ESP_LOGI(TAG, "wifi up, ip %s", ip.c_str());
    return ip;
}

void taskTrampoline(void *arg) { ((Core *)arg)->linkTask(); }

}  // namespace

Core &Core::instance()
{
    static Core core;
    return core;
}

void Core::start()
{
    if (task_) return;
    queue_ = xQueueCreate(TX_QUEUE_LEN, sizeof(char *));
    if (!queue_) {
        ESP_LOGE(TAG, "tx queue alloc failed");
        return;
    }
    TaskHandle_t handle = nullptr;
    // 8 KB: std::string/std::vector in the protocol layer plus lwIP. 4 KB overflows.
    if (xTaskCreate(taskTrampoline, "askbot", 8192, this, 5, &handle) != pdPASS) {
        ESP_LOGE(TAG, "link task create failed");
        return;
    }
    task_ = handle;
}

// ---------------------------------------------------------------- user actions
bool Core::queueJson(const char *json)
{
    if (!queue_ || !json) return false;
    const size_t n = strnlen(json, TX_MAX - 1) + 1;
    char *copy = (char *)malloc(n);
    if (!copy) return false;
    memcpy(copy, json, n - 1);
    copy[n - 1] = 0;
    if (xQueueSend((QueueHandle_t)queue_, &copy, 0) != pdPASS) {
        free(copy);
        std::lock_guard<std::mutex> lock(m_);
        s_.dropped++;
        return false;
    }
    return true;
}

bool Core::sendText(const char *text)
{
    if (!text || !*text) return false;
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        if (s_.link != Link::Up) return false;
        id = nextId_++;
        s_.reqId = id;
        snprintf(s_.question, sizeof(s_.question), "%s", text);
        s_.reply[0] = 0;
        s_.replyDone = false;
        s_.error[0] = 0;
        s_.chunks = 0;
        s_.stage = Stage::Sending;
        askStartMs_ = nowMs();
        bump();
    }

    cJSON *js = cJSON_CreateObject();
    cJSON_AddStringToObject(js, "t", "text");
    cJSON_AddNumberToObject(js, "id", id);
    cJSON_AddStringToObject(js, "text", text);
    cJSON_AddBoolToObject(js, "tts", false);   // spoken answers are not wired up yet
    char *out = cJSON_PrintUnformatted(js);
    const bool ok = out && queueJson(out);
    cJSON_free(out);
    cJSON_Delete(js);
    if (!ok) setStage(Stage::Error, "send failed");
    return ok;
}

void Core::cancel()
{
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        id = s_.reqId;
    }
    char js[64];
    snprintf(js, sizeof(js), "{\"t\":\"cancel\",\"id\":%d}", id);
    queueJson(js);
}

void Core::newChat()
{
    int id;
    {
        std::lock_guard<std::mutex> lock(m_);
        id = s_.reqId;
        s_.question[0] = 0;
        s_.reply[0] = 0;
        s_.replyDone = false;
        s_.error[0] = 0;
        s_.stage = Stage::Idle;
        bump();
    }
    char js[64];
    snprintf(js, sizeof(js), "{\"t\":\"newsession\",\"id\":%d}", id);
    queueJson(js);
}

void Core::clear()
{
    std::lock_guard<std::mutex> lock(m_);
    s_.question[0] = 0;
    s_.reply[0] = 0;
    s_.replyDone = false;
    s_.error[0] = 0;
    s_.stage = Stage::Idle;
    bump();
}

void Core::snapshot(Snapshot &out)
{
    std::lock_guard<std::mutex> lock(m_);
    out = s_;
}

uint32_t Core::version()
{
    std::lock_guard<std::mutex> lock(m_);
    return ver_;
}

void Core::setStage(Stage stage, const char *error)
{
    std::lock_guard<std::mutex> lock(m_);
    s_.stage = stage;
    if (error) snprintf(s_.error, sizeof(s_.error), "%s", error);
    else if (stage != Stage::Error) s_.error[0] = 0;
    bump();
}

// ---------------------------------------------------------------- inbound
void Core::onMessage(const char *json)
{
    cJSON *js = cJSON_Parse(json);
    if (!js) {
        ESP_LOGW(TAG, "bad json from host: %.60s", json);
        return;
    }

    const cJSON *t = cJSON_GetObjectItem(js, "t");
    const char *type = cJSON_IsString(t) ? t->valuestring : "";

    if (strcmp(type, "hostinfo") == 0) {
        std::lock_guard<std::mutex> lock(m_);
        const cJSON *h = cJSON_GetObjectItem(js, "host");
        const cJSON *p = cJSON_GetObjectItem(js, "provider");
        const cJSON *c = cJSON_GetObjectItem(js, "chat");
        snprintf(s_.host, sizeof(s_.host), "%s", cJSON_IsString(h) ? h->valuestring : "?");
        snprintf(s_.provider, sizeof(s_.provider), "%s", cJSON_IsString(p) ? p->valuestring : "?");
        if (cJSON_IsNumber(c)) s_.chatNo = c->valueint;
        s_.hostOnline = true;
        bump();
        cJSON_Delete(js);
        return;
    }

    if (strcmp(type, "answer") != 0) {
        ESP_LOGW(TAG, "unknown message type '%s'", type);
        cJSON_Delete(js);
        return;
    }

    const cJSON *st = cJSON_GetObjectItem(js, "st");
    const cJSON *tx = cJSON_GetObjectItem(js, "text");
    const char *stage = cJSON_IsString(st) ? st->valuestring : "";
    const char *text = cJSON_IsString(tx) ? tx->valuestring : nullptr;

    std::lock_guard<std::mutex> lock(m_);
    if (strcmp(stage, "think") == 0) {
        s_.stage = Stage::Think;
    } else if (strcmp(stage, "reply") == 0) {
        const cJSON *seq = cJSON_GetObjectItem(js, "seq");
        // seq 0 starts a fresh answer; a resent or reordered first chunk must not
        // append to the previous one.
        if (cJSON_IsNumber(seq) && seq->valueint == 0) {
            s_.reply[0] = 0;
            s_.chunks = 0;
        }
        if (text) {
            const size_t have = strlen(s_.reply);
            snprintf(s_.reply + have, sizeof(s_.reply) - have, "%s", text);
        }
        s_.chunks++;
        s_.replyDone = cJSON_IsTrue(cJSON_GetObjectItem(js, "done"));
        s_.stage = Stage::Reply;
        if (s_.replyDone) s_.askMs = nowMs() - askStartMs_;
    } else if (strcmp(stage, "session") == 0) {
        const cJSON *n = cJSON_GetObjectItem(js, "n");
        if (cJSON_IsNumber(n)) s_.chatNo = n->valueint;
        s_.stage = Stage::Idle;
    } else if (strcmp(stage, "idle") == 0) {
        s_.stage = Stage::Idle;
    } else if (strcmp(stage, "err") == 0) {
        snprintf(s_.error, sizeof(s_.error), "%s", text ? text : "host error");
        s_.stage = Stage::Error;
    }
    bump();
    cJSON_Delete(js);
}

// ---------------------------------------------------------------- link task
void Core::linkTask()
{
    {
        std::lock_guard<std::mutex> lock(m_);
        s_.link = Link::WifiConnecting;
        bump();
    }

    const std::string ip = wifiUp();
    if (ip.empty()) {
        std::lock_guard<std::mutex> lock(m_);
        s_.link = Link::WifiFailed;
        snprintf(s_.error, sizeof(s_.error), "wifi: %s", CONFIG_ASKBOT_WIFI_SSID);
        bump();
        vTaskDelete(nullptr);
        return;
    }

    akka::ClientConfig config;
    config.remote.system = CONFIG_ASKBOT_HOST_SYSTEM;
    config.remote.host = CONFIG_ASKBOT_HOST_IP;
    config.remote.port = CONFIG_ASKBOT_HOST_PORT;
    config.local.system = CONFIG_ASKBOT_LOCAL_SYSTEM;
    config.local.host = ip;
    config.local.port = CONFIG_ASKBOT_LOCAL_PORT;

    {
        std::lock_guard<std::mutex> lock(m_);
        snprintf(s_.ip, sizeof(s_.ip), "%s", ip.c_str());
        bump();
    }

    while (true) {
        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Associating;
            s_.hostOnline = false;
            bump();
        }

        akka::RemoteClient client(config, akka::MakeTcpStream());
        client.set_logger([](const char *level, const std::string &message) {
            if (strcmp(level, "error") == 0)      ESP_LOGE(TAG, "%s", message.c_str());
            else if (strcmp(level, "warn") == 0)  ESP_LOGW(TAG, "%s", message.c_str());
            else                                  ESP_LOGI(TAG, "%s", message.c_str());
        });
        // The client actor. Runs on this task, so it may touch the snapshot directly.
        client.Register(LOCAL_ACTOR, [this](const akka::Message &message) {
            if (!message.text.empty()) onMessage(message.text.c_str());
        });

        if (!client.Connect()) {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Down;
            snprintf(s_.error, sizeof(s_.error), "no host at %s:%d", CONFIG_ASKBOT_HOST_IP,
                     CONFIG_ASKBOT_HOST_PORT);
            bump();
            vTaskDelay(pdMS_TO_TICKS(5000));
            continue;
        }

        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Up;
            snprintf(s_.peer, sizeof(s_.peer), "%s", client.peer().ToString().c_str());
            s_.error[0] = 0;
            bump();
        }

        client.TellAs(LOCAL_ACTOR, CHAT_ACTOR,
                      "{\"t\":\"hello\",\"name\":\"askbot\",\"fw\":\"akka-1\"}");

        while (client.associated()) {
            if (!client.Poll(100)) break;

            char *json = nullptr;
            while (xQueueReceive((QueueHandle_t)queue_, &json, 0) == pdPASS) {
                const bool ok = client.TellAs(LOCAL_ACTOR, CHAT_ACTOR, json);
                free(json);
                std::lock_guard<std::mutex> lock(m_);
                if (ok) s_.sent++;
                else    s_.dropped++;
            }
        }

        ESP_LOGW(TAG, "association lost, reconnecting");
        {
            std::lock_guard<std::mutex> lock(m_);
            s_.link = Link::Down;
            s_.hostOnline = false;
            bump();
        }
        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}

} // namespace askbot
