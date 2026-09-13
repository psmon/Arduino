// AskBot core: the same conversation flow as brookesia_app_chat, carried over
// Akka.NET remoting instead of BLE.
//
// The device registers a client actor at
// akka.tcp://<local system>@<ip>:<port>/user/chat and sends from it, so the host
// actor's Sender.Tell(...) lands right back here - stages, reply chunks, session
// changes - as ordinary actor messages. Protocol details:
// project/samples/akka/PROTOCOL.md.
//
// Unlike the BLE apps in this firmware, this one needs IP: Akka classic remoting
// is TCP. WiFi is therefore started lazily, the first time the app is opened, so a
// user who never touches AskBot never has the radio brought up.
#pragma once
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace askbot {

enum class Link : uint8_t { Down, WifiConnecting, WifiFailed, Associating, Up };
enum class Stage : uint8_t { Idle, Sending, Think, Reply, Error };

struct Snapshot {
    Link link = Link::Down;
    char ip[16] = "";           // this device's address, as advertised to the host
    char peer[64] = "";         // akka.tcp://AskBot@host:port
    char host[40] = "";         // host machine name, from its hostinfo reply
    char provider[24] = "";     // chat CLI the host runs
    bool hostOnline = false;    // hostinfo received on this association
    int  chatNo = 1;            // conversation number the host has us on

    Stage stage = Stage::Idle;
    int   reqId = 0;
    char  question[256] = "";   // what we last asked
    char  reply[1200] = "";     // answer, chunks concatenated
    bool  replyDone = false;
    char  error[96] = "";

    uint32_t askMs = 0;         // round trip of the last completed answer
    uint32_t chunks = 0;        // chunks received for the current answer
    uint32_t sent = 0, dropped = 0;
};

class Core {
public:
    static Core &instance();

    // Idempotent. Brings up WiFi and the Akka association on a background task.
    void start();

    // --- user actions, safe from the LVGL task ---
    bool sendText(const char *text);
    void cancel();
    void newChat();
    void clear();

    void     snapshot(Snapshot &out);
    uint32_t version();

    // internal
    void linkTask();

private:
    Core() = default;
    bool queueJson(const char *json);
    void onMessage(const char *json);
    void setStage(Stage stage, const char *error = nullptr);
    void bump() { ver_++; }

    std::mutex m_;
    Snapshot   s_;
    uint32_t   ver_ = 1;
    int        nextId_ = 1;
    uint32_t   askStartMs_ = 0;

    void *task_ = nullptr;      // TaskHandle_t
    void *queue_ = nullptr;     // QueueHandle_t of char*
};

} // namespace askbot
