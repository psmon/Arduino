// The only transport: BLE Nordic UART Service (NUS) on NimBLE, advertising as "claude-hud".
// Same UUIDs as project/samples/claude_hud, so the same PC-side BLE sender protocol works:
//   service 6E400001-B5A3-F393-E0A9-E50E24DCCA9E, RX (write) ...0002, TX (notify) ...0003
// Any failure to start/advertise is logged as a warning and recorded in NetInfo::bleErr; there is
// no fallback transport by design (see README).
#include "hud_transport.hpp"
#include "hud_state.hpp"
#include <cstring>
#include <cstdio>
#include <string>
#include "esp_log.h"
#include "nvs_flash.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/util/util.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

#define BLE_NAME "claude-hud"

static const char *TAG = "hud_ble";

namespace claude_hud {

// 128-bit UUIDs are little-endian byte arrays in NimBLE.
static const ble_uuid128_t NUS_SVC_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x01, 0x00, 0x40, 0x6e);
static const ble_uuid128_t NUS_RX_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x02, 0x00, 0x40, 0x6e);
static const ble_uuid128_t NUS_TX_UUID = BLE_UUID128_INIT(
    0x9e, 0xca, 0xdc, 0x24, 0x0e, 0xe5, 0xa9, 0xe0, 0x93, 0xf3, 0xa3, 0xb5, 0x03, 0x00, 0x40, 0x6e);

static uint16_t    s_txHandle = 0;
static uint8_t     s_ownAddrType = 0;
static std::string s_rxBuf;

static void setErr(const char *msg, int rc)
{
    auto &net = State::instance().net;
    snprintf(net.bleErr, sizeof(net.bleErr), "%s (rc=%d)", msg, rc);
    ESP_LOGW(TAG, "%s", net.bleErr);
}

static void feed(const char *data, size_t len)
{
    auto &st = State::instance();
    s_rxBuf.append(data, len);
    // Split on newlines; a chunk that ends with '}' without a newline is also treated as complete.
    auto accept = [&](size_t n) {
        bool ok = st.handleLine(s_rxBuf.data(), n);
        if (ok) { st.net.rxBle++; st.net.lastRxMs = nowMs(); }
        ESP_LOGI(TAG, "rx %c %u bytes -> %s (total %lu)", s_rxBuf[0], (unsigned)n, ok ? "ok" : "rejected",
                 (unsigned long)st.net.rxBle);
    };
    for (;;) {
        size_t nl = s_rxBuf.find('\n');
        if (nl == std::string::npos) break;
        if (nl > 0) accept(nl);
        s_rxBuf.erase(0, nl + 1);
    }
    if (!s_rxBuf.empty() && s_rxBuf.back() == '}') {
        accept(s_rxBuf.size());
        s_rxBuf.clear();
    }
    if (s_rxBuf.size() > 8192) s_rxBuf.clear();
}

static int rxAccess(uint16_t, uint16_t, struct ble_gatt_access_ctxt *ctxt, void *)
{
    if (ctxt->op != BLE_GATT_ACCESS_OP_WRITE_CHR) return BLE_ATT_ERR_UNLIKELY;
    uint16_t len = OS_MBUF_PKTLEN(ctxt->om);
    char buf[600];
    if (len > sizeof(buf)) len = sizeof(buf);
    uint16_t out = 0;
    if (ble_hs_mbuf_to_flat(ctxt->om, buf, len, &out) != 0) return BLE_ATT_ERR_UNLIKELY;
    feed(buf, out);
    return 0;
}

static int txAccess(uint16_t, uint16_t, struct ble_gatt_access_ctxt *, void *)
{
    return 0;   // notify-only characteristic, nothing to read
}

static const struct ble_gatt_chr_def NUS_CHRS[] = {
    { .uuid = &NUS_RX_UUID.u, .access_cb = rxAccess,
      .flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP },
    { .uuid = &NUS_TX_UUID.u, .access_cb = txAccess,
      .flags = BLE_GATT_CHR_F_NOTIFY, .val_handle = &s_txHandle },
    { 0 },
};
static const struct ble_gatt_svc_def NUS_SVCS[] = {
    { .type = BLE_GATT_SVC_TYPE_PRIMARY, .uuid = &NUS_SVC_UUID.u, .characteristics = NUS_CHRS },
    { 0 },
};

static void advertise();

static int gapEvent(struct ble_gap_event *ev, void *)
{
    auto &net = State::instance().net;
    switch (ev->type) {
    case BLE_GAP_EVENT_CONNECT:
        if (ev->connect.status == 0) {
            net.bleConn = true; net.bleAdv = false;
            ESP_LOGI(TAG, "central connected");
        } else {
            advertise();
        }
        break;
    case BLE_GAP_EVENT_DISCONNECT:
        net.bleConn = false;
        ESP_LOGI(TAG, "central disconnected (reason %d); advertising again", ev->disconnect.reason);
        s_rxBuf.clear();
        advertise();
        break;
    case BLE_GAP_EVENT_ADV_COMPLETE:
        advertise();
        break;
    case BLE_GAP_EVENT_MTU:
        ESP_LOGI(TAG, "MTU %u", ev->mtu.value);
        break;
    default:
        break;
    }
    return 0;
}

static void advertise()
{
    struct ble_hs_adv_fields f = {};
    f.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    f.name = (uint8_t *)BLE_NAME;
    f.name_len = strlen(BLE_NAME);
    f.name_is_complete = 1;
    int rc = ble_gap_adv_set_fields(&f);
    if (rc) { setErr("adv fields failed", rc); return; }

    struct ble_hs_adv_fields rsp = {};
    rsp.uuids128 = &NUS_SVC_UUID;
    rsp.num_uuids128 = 1;
    rsp.uuids128_is_complete = 1;
    ble_gap_adv_rsp_set_fields(&rsp);

    struct ble_gap_adv_params p = {};
    p.conn_mode = BLE_GAP_CONN_MODE_UND;
    p.disc_mode = BLE_GAP_DISC_MODE_GEN;
    rc = ble_gap_adv_start(s_ownAddrType, nullptr, BLE_HS_FOREVER, &p, gapEvent, nullptr);
    if (rc) { setErr("adv start failed", rc); return; }
    State::instance().net.bleAdv = true;
    State::instance().net.bleErr[0] = 0;
    ESP_LOGI(TAG, "advertising as %s", BLE_NAME);
}

static void onSync()
{
    int rc = ble_hs_util_ensure_addr(0);
    if (rc) { setErr("no BLE address", rc); return; }
    rc = ble_hs_id_infer_auto(0, &s_ownAddrType);
    if (rc) { setErr("addr type failed", rc); return; }
    State::instance().net.bleReady = true;
    advertise();
}

static void onReset(int reason)
{
    State::instance().net.bleAdv = false;
    State::instance().net.bleConn = false;
    setErr("host reset", reason);
}

static void hostTask(void *)
{
    nimble_port_run();              // returns on nimble_port_stop()
    nimble_port_freertos_deinit();
}

void startBle()
{
    esp_err_t r = nvs_flash_init();  // NimBLE/PHY calibration data lives in NVS
    if (r == ESP_ERR_NVS_NO_FREE_PAGES || r == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        r = nvs_flash_init();
    }
    if (r != ESP_OK) { setErr("nvs init failed", (int)r); return; }

    r = nimble_port_init();
    if (r != ESP_OK) { setErr("nimble init failed", (int)r); return; }
    ble_hs_cfg.sync_cb = onSync;
    ble_hs_cfg.reset_cb = onReset;
    ble_svc_gap_init();
    ble_svc_gatt_init();
    int rc = ble_gatts_count_cfg(NUS_SVCS);
    if (rc == 0) rc = ble_gatts_add_svcs(NUS_SVCS);
    if (rc) { setErr("gatt svc failed", rc); return; }
    ble_svc_gap_device_name_set(BLE_NAME);
    nimble_port_freertos_init(hostTask);
    ESP_LOGI(TAG, "NimBLE started, waiting for sync");
}

} // namespace claude_hud
