// Round-trip checks for the hand-written PDU codec.
//
// These run on the dev box but are the same code the device executes, so a
// regression here is a regression on the board.
#include <cstdio>
#include <string>
#include <vector>

#include "akka/akka_wire.h"
#include "akka/pb.h"

namespace {

int failures = 0;

void Check(bool ok, const char* what)
{
    std::printf("%s %s\n", ok ? "  ok  " : "FAILED", what);
    if (!ok) failures++;
}

void CheckEq(const std::string& actual, const std::string& expected, const char* what)
{
    const bool ok = actual == expected;
    if (ok) {
        std::printf("  ok   %s\n", what);
    } else {
        std::printf("FAILED %s: got \"%s\", expected \"%s\"\n", what, actual.c_str(),
                    expected.c_str());
        failures++;
    }
}

void CheckEqU64(uint64_t actual, uint64_t expected, const char* what)
{
    const bool ok = actual == expected;
    if (ok) {
        std::printf("  ok   %s\n", what);
    } else {
        std::printf("FAILED %s: got %llu, expected %llu\n", what,
                    static_cast<unsigned long long>(actual),
                    static_cast<unsigned long long>(expected));
        failures++;
    }
}

void TestVarint()
{
    const uint64_t values[] = {0, 1, 127, 128, 300, 0x7FFFFFFF, UINT64_MAX};
    for (uint64_t v : values) {
        akka::pb::Writer w;
        w.Varint(v);
        akka::pb::Reader r(w.data(), w.size());
        uint64_t out = 0;
        const bool ok = r.Varint(&out) && out == v && r.done();
        Check(ok, "varint round trip");
    }
}

void TestAssociate()
{
    akka::Handshake info;
    info.origin.system = "askbot-dev";
    info.origin.host = "192.168.0.42";
    info.origin.port = 2553;
    info.uid = 0xDEADBEEFCAFEF00DULL;

    const std::vector<uint8_t> bytes = akka::EncodeAssociate(info);

    // The handshake rides the wrapped transport, so the scheme on the wire is
    // "tcp". Sending "akka.tcp" makes the peer register us as akka.akka.tcp://
    // and every reply is then routed to a port nothing is listening on.
    const std::string raw(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    Check(raw.find("akka.tcp") == std::string::npos, "handshake does not carry the akka. prefix");
    Check(raw.find("tcp") != std::string::npos, "handshake carries the tcp transport scheme");

    akka::Pdu pdu;
    Check(akka::DecodePdu(bytes.data(), bytes.size(), &pdu), "associate decodes");
    Check(pdu.is_control, "associate is a control PDU");
    Check(pdu.command == akka::ControlCommand::kAssociate, "command is ASSOCIATE");
    Check(pdu.has_handshake, "handshake present");
    CheckEq(pdu.handshake.origin.system, "askbot-dev", "origin system");
    CheckEq(pdu.handshake.origin.host, "192.168.0.42", "origin host");
    CheckEqU64(pdu.handshake.origin.port, 2553, "origin port");
    CheckEq(pdu.handshake.origin.protocol, "akka.tcp", "origin protocol");
    CheckEqU64(pdu.handshake.uid, info.uid, "handshake uid (fixed64)");
}

void TestHeartbeat()
{
    const std::vector<uint8_t> bytes = akka::EncodeHeartbeat();
    akka::Pdu pdu;
    Check(akka::DecodePdu(bytes.data(), bytes.size(), &pdu), "heartbeat decodes");
    Check(pdu.is_control && pdu.command == akka::ControlCommand::kHeartbeat, "command is HEARTBEAT");
    Check(!pdu.has_handshake, "heartbeat carries no handshake");
}

void TestMessage()
{
    akka::Envelope out;
    out.recipient_path = "akka.tcp://AskBot@127.0.0.1:2552/user/ask";
    out.sender_path = "akka.tcp://askbot-dev@127.0.0.1:2553/temp/$1";
    out.serializer_id = akka::kSerializerPrimitive;
    out.manifest = akka::kManifestString;
    const std::string text = "hello 안녕";  // UTF-8 goes on the wire unchanged
    out.message.assign(text.begin(), text.end());

    const std::vector<uint8_t> bytes = akka::EncodeMessage(out);

    akka::Pdu pdu;
    Check(akka::DecodePdu(bytes.data(), bytes.size(), &pdu), "message PDU decodes");
    Check(!pdu.is_control && !pdu.payload.empty(), "message PDU carries a payload");

    akka::AckAndEnvelope container;
    Check(akka::DecodeAckAndEnvelope(pdu.payload.data(), pdu.payload.size(), &container),
          "AckAndEnvelopeContainer decodes");
    Check(container.has_envelope, "envelope present");
    Check(!container.has_ack, "no ack piggybacked");

    const akka::Envelope& in = container.envelope;
    CheckEq(in.recipient_path, out.recipient_path, "recipient path");
    CheckEq(in.sender_path, out.sender_path, "sender path");
    CheckEqU64(static_cast<uint64_t>(in.serializer_id), akka::kSerializerPrimitive,
               "serializer id 17");
    CheckEq(in.manifest, "S", "string manifest");
    CheckEq(std::string(in.message.begin(), in.message.end()), text, "utf-8 payload");
    // The critical one: proto3 drops zero defaults, and a missing seq would be
    // read as system message #0 by Akka's reliable delivery path.
    CheckEqU64(in.seq, akka::kSeqUndefined, "seq is explicitly ulong.MaxValue");
    Check(akka::IsStringPayload(in.serializer_id, in.manifest), "recognised as a string payload");
}

void TestPureAck()
{
    const std::vector<uint8_t> bytes = akka::EncodePureAck(42);
    akka::Pdu pdu;
    Check(akka::DecodePdu(bytes.data(), bytes.size(), &pdu), "pure ack PDU decodes");
    akka::AckAndEnvelope container;
    Check(akka::DecodeAckAndEnvelope(pdu.payload.data(), pdu.payload.size(), &container),
          "pure ack container decodes");
    Check(container.has_ack && !container.has_envelope, "ack only, no envelope");
    CheckEqU64(container.cumulative_ack, 42, "cumulative ack");
}

void TestAddressFormatting()
{
    akka::Address a;
    a.system = "AskBot";
    a.host = "127.0.0.1";
    a.port = 2552;
    CheckEq(a.ToString(), "akka.tcp://AskBot@127.0.0.1:2552", "address formatting");
    CheckEq(a.PathOf("/user/ask"), "akka.tcp://AskBot@127.0.0.1:2552/user/ask", "absolute path");
    CheckEq(a.PathOf("user/ask"), "akka.tcp://AskBot@127.0.0.1:2552/user/ask", "relative path");
}

void TestTruncatedInputIsRejected()
{
    const std::vector<uint8_t> bytes = akka::EncodeAssociate({});
    akka::Pdu pdu;
    // An empty/complete-but-meaningless buffer must not look like a valid PDU.
    Check(!akka::DecodePdu(nullptr, 0, &pdu), "empty buffer rejected");
    if (bytes.size() > 3) {
        // Cutting a length-delimited field short must fail rather than read past the end.
        Check(!akka::DecodePdu(bytes.data(), bytes.size() - 2, &pdu), "truncated PDU rejected");
    }
}

}  // namespace

int main()
{
    TestVarint();
    TestAddressFormatting();
    TestAssociate();
    TestHeartbeat();
    TestMessage();
    TestPureAck();
    TestTruncatedInputIsRejected();

    std::printf("\n%s\n", failures == 0 ? "all wire tests passed" : "wire tests FAILED");
    return failures == 0 ? 0 : 1;
}
