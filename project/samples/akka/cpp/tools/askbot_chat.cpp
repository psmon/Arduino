// Device simulator: runs the AskBot chat protocol from the PC, using the very
// client-actor code the firmware uses.
//
// The point is to get the whole conversation flow - hello, text, streamed reply
// chunks, cancel, newsession - working before any of it is flashed, and to keep a
// regression check that needs no board.
//
//   askbot_chat                                 interactive
//   askbot_chat --say "안녕" --say "/new"        scripted; exit code 0 if every
//                                               question produced a complete reply
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "akka/remote_client.h"
#include "akka/transport.h"
#include "askbot/ima_adpcm.h"

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

const char* Arg(int argc, char** argv, const char* name, const char* fallback)
{
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::strcmp(argv[i], name) == 0) return argv[i + 1];
    }
    return fallback;
}

bool HasFlag(int argc, char** argv, const char* name)
{
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], name) == 0) return true;
    }
    return false;
}

std::vector<std::string> Repeated(int argc, char** argv, const char* name)
{
    std::vector<std::string> out;
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::strcmp(argv[i], name) == 0) out.emplace_back(argv[i + 1]);
    }
    return out;
}

// Minimal field readers. The firmware has cJSON; here a couple of substring
// lookups keep the harness free of a JSON dependency.
std::string JsonString(const std::string& json, const char* key)
{
    const std::string needle = std::string("\"") + key + "\":\"";
    const size_t at = json.find(needle);
    if (at == std::string::npos) return {};

    std::string out;
    for (size_t i = at + needle.size(); i < json.size(); ++i) {
        const char c = json[i];
        if (c == '\\' && i + 1 < json.size()) {
            const char next = json[++i];
            out.push_back(next == 'n' ? '\n' : next);
            continue;
        }
        if (c == '"') break;
        out.push_back(c);
    }
    return out;
}

long JsonNumber(const std::string& json, const char* key, long fallback)
{
    const std::string needle = std::string("\"") + key + "\":";
    const size_t at = json.find(needle);
    if (at == std::string::npos) return fallback;
    return std::strtol(json.c_str() + at + needle.size(), nullptr, 10);
}

bool JsonTrue(const std::string& json, const char* key)
{
    const std::string needle = std::string("\"") + key + "\":true";
    return json.find(needle) != std::string::npos;
}

std::string Escape(const std::string& text)
{
    std::string out;
    for (char c : text) {
        if (c == '"' || c == '\\') {
            out.push_back('\\');
            out.push_back(c);
        } else if (c == '\n') {
            out += "\\n";
        } else {
            out.push_back(c);  // UTF-8 stays raw, exactly as the host writes it
        }
    }
    return out;
}

// Mirrors what askbot_core keeps on the device.
struct ChatState {
    // speech, as the device would buffer it
    bool  speaking = false;
    int   speakWant = 0;
    int   speakGot = 0;
    int   speakMs = 0;
    uint32_t speakRate = 16000;
    std::vector<int16_t> pcm;
    bool  speakDone = false;

    std::string host;
    std::string provider;
    int conversation = 1;
    bool host_online = false;

    int request = 0;
    std::string stage = "idle";
    std::string reply;
    bool reply_done = false;
    std::string error;
};

}  // namespace

int main(int argc, char** argv)
{
#if defined(_WIN32)
    SetConsoleOutputCP(CP_UTF8);
    std::vector<std::string> utf8_args;
    utf8_args.reserve(static_cast<size_t>(argc));
    for (int i = 0; i < argc; ++i) {
        const int wide = MultiByteToWideChar(CP_ACP, 0, argv[i], -1, nullptr, 0);
        std::wstring w(static_cast<size_t>(wide), L'\0');
        MultiByteToWideChar(CP_ACP, 0, argv[i], -1, w.data(), wide);
        const int utf8 = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string s(static_cast<size_t>(utf8), '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, s.data(), utf8, nullptr, nullptr);
        s.resize(static_cast<size_t>(utf8) - 1);
        utf8_args.push_back(s);
        argv[i] = const_cast<char*>(utf8_args.back().c_str());
    }
#endif

    akka::ClientConfig config;
    config.remote.system = Arg(argc, argv, "--system", "AskBot");
    config.remote.host = Arg(argc, argv, "--host", "127.0.0.1");
    config.remote.port = static_cast<uint32_t>(std::atoi(Arg(argc, argv, "--port", "2552")));
    config.local.system = Arg(argc, argv, "--local-system", "askbot-device");
    config.local.host = Arg(argc, argv, "--local-host", "127.0.0.1");
    config.local.port = static_cast<uint32_t>(std::atoi(Arg(argc, argv, "--local-port", "2553")));

    const std::string chat_actor = Arg(argc, argv, "--actor", "/user/chat");
    const std::vector<std::string> scripted = Repeated(argc, argv, "--say");
    // Ask for a spoken answer too, and save what arrives so it can be listened to.
    const bool want_tts = HasFlag(argc, argv, "--tts");
    const std::string wav_path = Arg(argc, argv, "--wav", "askbot_speech.wav");

    ChatState state;
    akka::RemoteClient client(config, akka::MakeTcpStream());
    client.set_logger([](const char* level, const std::string& message) {
        if (std::strcmp(level, "info") != 0) std::printf("[%s] %s\n", level, message.c_str());
    });

    // The client actor. Everything the host pushes - stages, reply chunks, session
    // changes - arrives here, addressed to /user/chat on this node.
    client.Register("chat", [&](const akka::Message& message) {
        // Speech frames arrive as .NET byte[] (serializer 4), not text.
        askbot::SpeechFrame frame;
        if (message.bytes != nullptr && !message.bytes->empty() &&
            askbot::ParseSpeechFrame(message.bytes->data(), message.bytes->size(), &frame)) {
            askbot::DecodeAdpcmBlock(frame.block, frame.block_len, &state.pcm);
            state.speakGot++;
            return;
        }

        const std::string& json = message.text;
        const std::string type = JsonString(json, "t");

        if (type == "hostinfo") {
            state.host = JsonString(json, "host");
            state.provider = JsonString(json, "provider");
            state.conversation = static_cast<int>(JsonNumber(json, "chat", 1));
            state.host_online = true;
            std::printf("[host] %s via %s, conversation %d\n", state.host.c_str(), state.provider.c_str(),
                        state.conversation);
            return;
        }
        if (type != "answer") {
            std::printf("[?] %s\n", json.c_str());
            return;
        }

        const std::string stage = JsonString(json, "st");
        state.stage = stage;

        if (stage == "reply") {
            if (JsonNumber(json, "seq", 0) == 0) state.reply.clear();
            state.reply += JsonString(json, "text");
            state.reply_done = JsonTrue(json, "done");
            std::printf("  <- [%s %ld/%ld] %s\n", stage.c_str(), JsonNumber(json, "seq", 0) + 1,
                        JsonNumber(json, "n", 1), JsonString(json, "text").c_str());
        } else if (stage == "err") {
            state.error = JsonString(json, "text");
            std::printf("  <- [err] %s\n", state.error.c_str());
        } else if (stage == "speak") {
            state.speaking = true;
            state.speakDone = false;
            state.speakGot = 0;
            state.pcm.clear();
            state.speakWant = static_cast<int>(JsonNumber(json, "frames", 0));
            state.speakMs = static_cast<int>(JsonNumber(json, "ms", 0));
            state.speakRate = static_cast<uint32_t>(JsonNumber(json, "rate", 16000));
            std::printf("  <- [speak] %d frames, %d ms @ %u Hz\n", state.speakWant, state.speakMs,
                        state.speakRate);
        } else if (stage == "speak_end") {
            state.speaking = false;
            state.speakDone = true;
            const std::string err = JsonString(json, "text");
            if (!err.empty()) {
                std::printf("  <- [speak_end] %s\n", err.c_str());
            } else {
                std::printf("  <- [speak_end] got %d/%d frames, %zu samples (%.2f s)\n", state.speakGot,
                            state.speakWant, state.pcm.size(),
                            state.pcm.size() / static_cast<double>(state.speakRate));
            }
        } else if (stage == "session") {
            state.conversation = static_cast<int>(JsonNumber(json, "n", 1));
            std::printf("  <- [session] now on conversation %d\n", state.conversation);
        } else {
            std::printf("  <- [%s]\n", stage.c_str());
        }
    });

    if (!client.Connect()) {
        std::printf("connect failed - is AskBot.Host running on %s:%u?\n", config.remote.host.c_str(),
                    config.remote.port);
        return 1;
    }
    std::printf("associated with %s\n", client.peer().ToString().c_str());
    std::printf("client actor: %s\n", client.LocalPath("chat").c_str());

    const auto pump = [&](int timeout_ms) {
        const int64_t deadline = akka::NowMs() + timeout_ms;
        while (akka::NowMs() < deadline) {
            if (!client.Poll(100)) return false;
        }
        return true;
    };
    const auto pump_until_done = [&](int timeout_ms) {
        const int64_t deadline = akka::NowMs() + timeout_ms;
        while (akka::NowMs() < deadline) {
            if (!client.Poll(100)) return false;
            if (!state.error.empty()) return true;
            // With voice on, the answer is not finished until the audio has landed.
            if (state.reply_done && (!want_tts || state.speakDone)) return true;
        }
        return true;
    };

    const auto save_speech = [&]() {
        if (state.pcm.empty()) return;
        const std::vector<uint8_t> wav = askbot::PcmToWav(state.pcm, state.speakRate);
        FILE* f = std::fopen(wav_path.c_str(), "wb");
        if (!f) {
            std::printf("  (could not write %s)\n", wav_path.c_str());
            return;
        }
        std::fwrite(wav.data(), 1, wav.size(), f);
        std::fclose(f);
        std::printf("  == wrote %s (%zu bytes)\n", wav_path.c_str(), wav.size());
    };

    client.TellAs("chat", chat_actor, "{\"t\":\"hello\",\"name\":\"askbot-sim\",\"fw\":\"akka-1\"}");
    pump(1500);
    if (!state.host_online) {
        std::printf("host never answered the hello\n");
        client.Disconnect();
        return 1;
    }

    int next_id = 1;
    const auto send_text = [&](const std::string& text) {
        state.reply.clear();
        state.reply_done = false;
        state.error.clear();
        state.request = next_id++;
        char json[1024];
        std::snprintf(json, sizeof(json), "{\"t\":\"text\",\"id\":%d,\"text\":\"%s\",\"tts\":%s}",
                      state.request, Escape(text).c_str(), want_tts ? "true" : "false");
        return client.TellAs("chat", chat_actor, json);
    };
    const auto send_control = [&](const char* type) {
        char json[128];
        std::snprintf(json, sizeof(json), "{\"t\":\"%s\",\"id\":%d}", type, state.request);
        return client.TellAs("chat", chat_actor, json);
    };

    if (!scripted.empty()) {
        int failures = 0;
        for (const std::string& text : scripted) {
            if (text == "/new") {
                std::printf("  -> newsession\n");
                send_control("newsession");
                pump(1500);
                continue;
            }
            if (text == "/cancel") {
                std::printf("  -> cancel\n");
                send_control("cancel");
                pump(1500);
                continue;
            }
            std::printf("  -> %s\n", text.c_str());
            send_text(text);
            pump_until_done(60000);
            if (!state.reply_done) {
                std::printf("  <- (incomplete: stage %s)\n", state.stage.c_str());
                failures++;
            } else {
                std::printf("  == full answer: %s\n", state.reply.c_str());
                if (want_tts) save_speech();
            }
        }
        client.Disconnect();
        std::printf("\n%s\n", failures == 0 ? "OK" : "FAILED");
        return failures == 0 ? 0 : 1;
    }

    std::printf("\nType a question. /new = new conversation, /cancel = abandon, empty line = quit.\n");
    char line[512];
    while (true) {
        std::printf("askbot> ");
        std::fflush(stdout);
        if (std::fgets(line, sizeof(line), stdin) == nullptr) break;

        std::string text(line);
        while (!text.empty() && (text.back() == '\n' || text.back() == '\r')) text.pop_back();
        if (text.empty()) break;

        if (text == "/new") {
            send_control("newsession");
            pump(1500);
            continue;
        }
        if (text == "/cancel") {
            send_control("cancel");
            pump(1500);
            continue;
        }
        if (!send_text(text)) break;
        if (!pump_until_done(120000)) {
            std::printf("link lost\n");
            break;
        }
        if (state.reply_done) {
            std::printf("  == %s\n", state.reply.c_str());
            if (want_tts) save_speech();
        }
    }

    client.Disconnect();
    return 0;
}
