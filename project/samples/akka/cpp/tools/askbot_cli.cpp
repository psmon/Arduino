// Dev harness for the C++ Akka remoting module.
//
// Same sources that will go into the ESP-IDF component, driven from a console so
// the protocol can be debugged without flashing anything.
//
//   askbot_cli                                  interactive, talks to 127.0.0.1:2552
//   askbot_cli --ask "ping" --ask "time"        one-shot, useful for scripted checks
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "akka/remote_client.h"
#include "akka/transport.h"

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

#if defined(_WIN32)
// The console here runs code page 949: argv arrives as CP949 bytes and stdout
// mangles UTF-8 unless told otherwise. The device will hand the library real
// UTF-8, so the harness has to as well or every Korean test is a lie.
std::string AnsiToUtf8(const char* s)
{
    const int wide_len = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
    if (wide_len <= 0) return std::string(s);
    std::wstring wide(static_cast<size_t>(wide_len), L'\0');
    MultiByteToWideChar(CP_ACP, 0, s, -1, wide.data(), wide_len);

    const int utf8_len = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (utf8_len <= 0) return std::string(s);
    std::string utf8(static_cast<size_t>(utf8_len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, utf8.data(), utf8_len, nullptr, nullptr);
    utf8.resize(static_cast<size_t>(utf8_len) - 1);  // drop the NUL
    return utf8;
}

void UseUtf8Console(std::vector<std::string>* storage, int argc, char** argv)
{
    SetConsoleOutputCP(CP_UTF8);
    storage->reserve(static_cast<size_t>(argc));
    for (int i = 0; i < argc; ++i) {
        storage->push_back(AnsiToUtf8(argv[i]));
        argv[i] = const_cast<char*>((*storage)[static_cast<size_t>(i)].c_str());
    }
}
#endif

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

}  // namespace

int main(int argc, char** argv)
{
#if defined(_WIN32)
    std::vector<std::string> utf8_args;
    UseUtf8Console(&utf8_args, argc, argv);
#endif

    akka::ClientConfig config;
    config.remote.system = Arg(argc, argv, "--system", "AskBot");
    config.remote.host = Arg(argc, argv, "--host", "127.0.0.1");
    config.remote.port = static_cast<uint32_t>(std::atoi(Arg(argc, argv, "--port", "2552")));

    config.local.system = Arg(argc, argv, "--local-system", "askbot-dev");
    config.local.host = Arg(argc, argv, "--local-host", "127.0.0.1");
    config.local.port = static_cast<uint32_t>(std::atoi(Arg(argc, argv, "--local-port", "2553")));

    const std::string actor = Arg(argc, argv, "--actor", "/user/ask");
    const bool verbose = HasFlag(argc, argv, "--verbose");
    const std::vector<std::string> one_shot = Repeated(argc, argv, "--ask");

    akka::RemoteClient client(config, akka::MakeTcpStream());
    if (verbose) {
        client.set_logger([](const char* level, const std::string& message) {
            std::printf("[%s] %s\n", level, message.c_str());
        });
    } else {
        client.set_logger([](const char* level, const std::string& message) {
            if (std::strcmp(level, "info") != 0) std::printf("[%s] %s\n", level, message.c_str());
        });
    }

    bool got_reply = false;
    client.set_reply_handler([&](uint64_t correlation, const std::string& text) {
        std::printf("  <- [#%llu] %s\n", static_cast<unsigned long long>(correlation),
                    text.c_str());
        got_reply = true;
    });
    client.set_message_handler([](const akka::Envelope& envelope, const std::string& text) {
        std::printf("  <- (push to %s, serializer %d) %s\n", envelope.recipient_path.c_str(),
                    envelope.serializer_id, text.c_str());
    });

    if (!client.Connect()) {
        std::printf("connect failed - is AskBot.Host running on %s:%u?\n",
                    config.remote.host.c_str(), config.remote.port);
        return 1;
    }
    std::printf("associated with %s (uid %llu)\n", client.peer().ToString().c_str(),
                static_cast<unsigned long long>(client.peer_uid()));
    std::printf("target actor: %s\n\n", config.remote.PathOf(actor).c_str());

    // Drains replies for up to timeout_ms, or until every ask has been answered.
    const auto pump = [&](int timeout_ms) {
        const int64_t deadline = akka::NowMs() + timeout_ms;
        while (akka::NowMs() < deadline) {
            if (!client.Poll(100)) return false;
            if (client.pending_asks() == 0) return true;
        }
        return true;
    };

    if (!one_shot.empty()) {
        int failures = 0;
        for (const std::string& text : one_shot) {
            got_reply = false;
            std::printf("  -> %s\n", text.c_str());
            if (client.Ask(actor, text) == 0) {
                std::printf("send failed\n");
                failures++;
                continue;
            }
            if (!pump(5000) || !got_reply) {
                std::printf("  <- (no reply within 5s)\n");
                failures++;
            }
        }
        client.Disconnect();
        std::printf("\n%s\n", failures == 0 ? "OK" : "FAILED");
        return failures == 0 ? 0 : 1;
    }

    std::printf("Type a question, or an empty line to quit.\n");
    char line[512];
    while (true) {
        std::printf("ask> ");
        std::fflush(stdout);
        if (std::fgets(line, sizeof(line), stdin) == nullptr) break;

        std::string text(line);
        while (!text.empty() && (text.back() == '\n' || text.back() == '\r')) text.pop_back();
        if (text.empty()) break;

        if (client.Ask(actor, text) == 0) {
            std::printf("send failed\n");
            break;
        }
        if (!pump(5000)) {
            std::printf("link lost\n");
            break;
        }
        if (client.pending_asks() != 0) std::printf("  <- (no reply within 5s)\n");
    }

    client.Disconnect();
    return 0;
}
