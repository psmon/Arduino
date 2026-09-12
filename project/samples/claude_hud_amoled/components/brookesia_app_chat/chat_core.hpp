// Voice-chat core: protocol state (device<->host, see amoled_chat_host/PROTOCOL.md), microphone capture task
// and IMA ADPCM streaming over the shared NUS transport. UI-agnostic; the app polls snapshot()/version().
#pragma once
#include <cstdint>
#include <cstddef>
#include <mutex>

namespace voice_chat {

enum class Stage : uint8_t { Idle, Recording, Sending, Stt, Think, Reply, Error, Busy };

struct Snapshot {
    Stage    stage = Stage::Idle;
    int      reqId = 0;
    char     transcript[256] = "";     // last STT result from the host
    char     reply[1200] = "";         // answer (chunks concatenated)
    bool     replyDone = false;
    char     error[96] = "";
    char     host[40] = "";            // host machine name from the H line
    char     provider[24] = "";        // chat CLI the host uses
    bool     hostOnline = false;       // H line received on this connection
    bool     bleConnected = false;
    bool     micOk = false;
    float    level = 0;                // 0..1 input level while recording
    uint32_t recMs = 0;                // recording length so far
    uint32_t framesSent = 0, framesDropped = 0;
};

class Core {
public:
    static Core &instance();

    void init();                       // install-time: register the BLE line hook, start the capture task
    // --- user actions (any task)
    bool startVoice(uint32_t maxMs = 30000);   // begin capture; false if BLE/host not ready or busy
    void stopVoice();                  // end capture -> host runs STT + chat
    bool sendText(const char *text);   // typed/preset prompt
    void clear();                      // clear transcript/reply

    void     snapshot(Snapshot &out);
    uint32_t version();                // bumps on every visible change

    // internal (public for the task/hook trampolines)
    bool onLine(const char *line, size_t len);
    void captureTask();
    void txTask();

private:
    Core() = default;
    bool micInit();
    bool sendLine(const char *json);   // "R " + json, blocking - never call from the NimBLE host task
    bool queueLine(const char *json);  // hand "R " + json to the tx task (safe from any task)
    void setStage(Stage st, const char *err = nullptr);
    void bump() { ver_++; }

    std::mutex m_;
    Snapshot   s_;
    uint32_t   ver_ = 1;
    uint8_t    nextId_ = 1;
    volatile bool recording_ = false;  // set by startVoice, cleared by stopVoice / limits
    uint32_t   maxMs_ = 30000;
    void      *mic_ = nullptr;         // esp_codec_dev_handle_t
    void      *task_ = nullptr;        // TaskHandle_t (capture)
    void      *txTask_ = nullptr;      // TaskHandle_t (outbound lines)
    void      *txQueue_ = nullptr;     // QueueHandle_t of TxMsg
};

} // namespace voice_chat
