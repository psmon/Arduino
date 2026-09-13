// AskBot device client: joins WiFi, associates with the .NET ActorSystem as an
// Akka remoting peer, and asks the actor a question every few seconds.
//
// No display work here on purpose - this is the transport proving ground. The
// Brookesia UI app is the next layer up and reuses this client as-is.
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>

#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "akka/remote_client.h"
#include "akka/transport.h"

namespace {

const char* kTag = "askbot";

constexpr int kWifiConnectedBit = BIT0;
constexpr int kWifiFailedBit = BIT1;
constexpr int kWifiMaxRetry = 8;

EventGroupHandle_t s_wifi_events = nullptr;
int s_retries = 0;

void OnWifiEvent(void* /*arg*/, esp_event_base_t base, int32_t id, void* data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
        return;
    }
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        if (s_retries < kWifiMaxRetry) {
            s_retries++;
            ESP_LOGW(kTag, "wifi disconnected, retry %d/%d", s_retries, kWifiMaxRetry);
            esp_wifi_connect();
        } else {
            xEventGroupSetBits(s_wifi_events, kWifiFailedBit);
        }
        return;
    }
    if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        auto* event = static_cast<ip_event_got_ip_t*>(data);
        ESP_LOGI(kTag, "got ip " IPSTR, IP2STR(&event->ip_info.ip));
        s_retries = 0;
        xEventGroupSetBits(s_wifi_events, kWifiConnectedBit);
    }
}

// Returns the station's own IP as a string, which is what the handshake has to
// advertise: the host keys its endpoint registry on that address.
bool WifiStart(std::string* own_ip)
{
    s_wifi_events = xEventGroupCreate();

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_t* netif = esp_netif_create_default_wifi_sta();

    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init));

    ESP_ERROR_CHECK(esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &OnWifiEvent,
                                                       nullptr, nullptr));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &OnWifiEvent,
                                                        nullptr, nullptr));

    wifi_config_t config = {};
    std::snprintf(reinterpret_cast<char*>(config.sta.ssid), sizeof(config.sta.ssid), "%s",
                  CONFIG_ASKBOT_WIFI_SSID);
    std::snprintf(reinterpret_cast<char*>(config.sta.password), sizeof(config.sta.password), "%s",
                  CONFIG_ASKBOT_WIFI_PASSWORD);

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &config));
    ESP_ERROR_CHECK(esp_wifi_start());

    const EventBits_t bits = xEventGroupWaitBits(s_wifi_events, kWifiConnectedBit | kWifiFailedBit,
                                                 pdFALSE, pdFALSE, portMAX_DELAY);
    if ((bits & kWifiConnectedBit) == 0) {
        ESP_LOGE(kTag, "wifi connect failed for ssid %s", CONFIG_ASKBOT_WIFI_SSID);
        return false;
    }

    esp_netif_ip_info_t ip{};
    ESP_ERROR_CHECK(esp_netif_get_ip_info(netif, &ip));
    char buf[16];
    std::snprintf(buf, sizeof(buf), IPSTR, IP2STR(&ip.ip));
    own_ip->assign(buf);
    return true;
}

void AskbotTask(void*)
{
    std::string own_ip;
    if (!WifiStart(&own_ip)) {
        vTaskDelete(nullptr);
        return;
    }

    akka::ClientConfig config;
    config.remote.system = CONFIG_ASKBOT_HOST_SYSTEM;
    config.remote.host = CONFIG_ASKBOT_HOST_IP;
    config.remote.port = CONFIG_ASKBOT_HOST_PORT;
    config.local.system = CONFIG_ASKBOT_LOCAL_SYSTEM;
    config.local.host = own_ip;
    config.local.port = CONFIG_ASKBOT_LOCAL_PORT;

    const std::string actor = CONFIG_ASKBOT_ACTOR_PATH;
    int question = 0;

    while (true) {
        akka::RemoteClient client(config, akka::MakeTcpStream());
        client.set_logger([](const char* level, const std::string& message) {
            if (std::strcmp(level, "error") == 0) {
                ESP_LOGE(kTag, "%s", message.c_str());
            } else if (std::strcmp(level, "warn") == 0) {
                ESP_LOGW(kTag, "%s", message.c_str());
            } else {
                ESP_LOGI(kTag, "%s", message.c_str());
            }
        });
        client.set_reply_handler([](uint64_t correlation, const std::string& text) {
            ESP_LOGI(kTag, "reply #%llu: %s", static_cast<unsigned long long>(correlation),
                     text.c_str());
        });

        if (!client.Connect()) {
            ESP_LOGW(kTag, "association failed, retrying in 5s");
            vTaskDelay(pdMS_TO_TICKS(5000));
            continue;
        }
        ESP_LOGI(kTag, "associated with %s", client.peer().ToString().c_str());

        int64_t next_question_ms = 0;
        while (client.associated()) {
            if (!client.Poll(200)) break;

            const int64_t now = akka::NowMs();
            if (now >= next_question_ms) {
                next_question_ms = now + 5000;
                const std::string text = "안녕 액터, 질문 " + std::to_string(++question);
                if (client.Ask(actor, text) == 0) break;
                ESP_LOGI(kTag, "asked: %s", text.c_str());
            }
        }

        ESP_LOGW(kTag, "link lost, reconnecting");
        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}

}  // namespace

extern "C" void app_main()
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    // std::string / std::vector in the protocol layer plus lwIP buffers: 8 KB of
    // stack is comfortable, 4 KB is not.
    xTaskCreate(AskbotTask, "askbot", 8192, nullptr, 5, nullptr);
}
