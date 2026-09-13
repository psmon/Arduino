# Akka.NET classic remoting, as spoken by a C++ peer

> **Transport note.** The device carries these PDUs over **BLE**, not TCP: chunks tagged
> `0xAB` on the Nordic UART Service, relayed to the host's remoting port by AkkaHost's
> `BleTunnel` (or `pc/ble_akka_bridge.py`). Everything below is unchanged by that - Akka
> needs an ordered, reliable byte stream and BLE is one. The framing, handshake and
> envelope layout are identical whether the bytes arrive over TCP or BLE, which is why the
> device only swapped `akka::IByteStream` implementations.

Everything here was read out of `akkadotnet/akka.net@dev` (the 1.6 line) and then
confirmed against a running `AskBot.Host` with the C++ client in `cpp/`.

Sources: `src/protobuf/WireFormats.proto`, `src/protobuf/ContainerFormats.proto`,
`src/core/Akka.Remote/Transport/AkkaPduCodec.cs`,
`src/core/Akka.Remote/Transport/AkkaProtocolTransport.cs`,
`src/core/Akka.Remote/Transport/DotNetty/DotNettyTransport.cs`,
`src/core/Akka.Remote/EndpointManager.cs`,
`src/core/Akka.Remote/Configuration/Remote.conf`.

## Which transport

Classic remoting (`akka.tcp://`), i.e. the DotNetty TCP transport. The 1.6 branch
also carries an **Artery** transport (`akka://`, Pekko-style, `canonical.port = 25520`)
but it ships `enabled = off` and its own config comment states the two are *not*
wire-compatible. Nothing here applies to Artery.

## Framing

```
[ uint32 length, LITTLE-ENDIAN ][ AkkaProtocolMessage protobuf ]
```

* Length excludes the 4-byte header itself.
* Little-endian because `akka.remote.dot-netty.tcp.byte-order = "little-endian"`
  is the default (`LengthFieldPrepender(Settings.ByteOrder, 4, 0, false)`).
* `maximum-frame-size = 128000b`; a longer length means frame desync, not a big
  message.

## Connection direction: one socket is enough

`akka.remote.use-passive-connections = on` is the default, and
`EndpointManager.AcceptPendingReader` marks an accepted inbound handle as
*writable* when no outbound endpoint to that address exists yet. So the .NET node
replies over the connection the client opened, and **the device needs no
listening socket** — which is the whole reason this is viable on an MCU behind
NAT.

The advertised address still has to be well-formed (`host` and `port` non-empty,
`AkkaPduProtobuffCodec.SerializeAddress` throws otherwise), it just never has to
accept a connection.

## Handshake

```
client --> ASSOCIATE (AkkaHandshakeInfo: origin address + uid)
client <-- ASSOCIATE (the peer's own origin + uid)
both   --> HEARTBEAT every 4s
```

`AkkaProtocolTransport`, `WaitHandshake` state: the inbound side answers an
association attempt with its own `ASSOCIATE`. No cookie, no auth — `cookie = 3`
is marked "not used" in the .proto. `refuseUid` only matters when the peer is
re-associating after a quarantine.

**The trap that costs an afternoon:** the handshake rides the *wrapped*
transport, so `AddressData.protocol` on the wire is `"tcp"`, not `"akka.tcp"` —
`AkkaProtocolTransport` prepends `akka.` on receipt. Send `"akka.tcp"` and the
peer registers you as `akka.akka.tcp://you@host:port`; your `akka.tcp://` sender
paths then match no endpoint, the peer tries to dial your advertised port
instead, that fails, the address gets gated for 5s and every reply goes to dead
letters. Observed exactly once, in this log line:

```
reliableEndpointWriter-akka.akka.tcp%3A%2F%2Faskbot-dev%40127.0.0.1%3A2553-1
```

Actor **paths** are the opposite: those use `akka.tcp://system@host:port/user/x`.

## Failure detector

`akka.remote.transport-failure-detector`: `heartbeat-interval = 4s`,
`acceptable-heartbeat-pause = 120s`. Send a `HEARTBEAT` control PDU every ~4s and
the association survives; go quiet for two minutes and the peer drops it.

## Messages

Every user message is a `payload` PDU whose bytes are an `AckAndEnvelopeContainer`:

```
AkkaProtocolMessage.payload(1) = AckAndEnvelopeContainer {
  ack(1)      = AcknowledgementInfo { cumulativeAck(1) fixed64, nacks(2) repeated fixed64 }
  envelope(2) = RemoteEnvelope {
    recipient(1) = ActorRefData { path(1) string }
    message(2)   = Payload { message(1) bytes, serializerId(2) int32, messageManifest(3) bytes }
    sender(4)    = ActorRefData { path(1) string }
    seq(5)       = fixed64
  }
}
```

`seq` **must always be written** as `ulong.MaxValue` (`kSeqUndefined`). proto3
omits zero defaults, and a missing `seq` reads as system message #0, dragging the
message into the reliable-delivery/resend machinery.

### Serializers worth knowing

From `Akka.Remote/Configuration/Remote.conf`:

| id | serializer | used for |
|---|---|---|
| 1 | `NewtonSoftJsonSerializer` | `System.Object` fallback — payload is UTF-8 JSON text |
| 2 | `ProtobufSerializer` | `Google.Protobuf.IMessage` |
| 16 | `MiscMessageSerializer` | `IActorRef`, `PoisonPill`, `Status.Failure`, ... |
| 17 | `PrimitiveSerializers` | `string` / `int` / `long` |
| 22 | `SystemMessageSerializer` | DeathWatch and friends |

A `string` is therefore: `serializerId = 17`, `messageManifest = "S"`, `message =`
the UTF-8 bytes, nothing else. That is why the C++ side needs no .NET serializer —
and it is verified: `"한글 UTF-8 왕복 테스트"` comes back as 15 chars counted by
.NET. Two legacy manifests (`System.String, System.Private.CoreLib` and
`..., mscorlib`) mean the same thing and are accepted on decode.

### ask, without an ActorSystem

`Ask` sets `sender` to `akka.tcp://<local system>@<local host>:<port>/temp/$N`.
The .NET actor's `Sender.Tell(reply)` then routes back over the passive
connection addressed to that path, and the client matches the `/temp/` suffix to
its pending request. No temp-actor registry, no `ActorSystem` on the C++ side.

The local address used in those sender paths **must** be byte-identical to the
origin sent in the handshake, because that address is the key the peer's endpoint
registry uses.

### Inbound system messages

Anything arriving with `seq != kSeqUndefined` is on the reliable-delivery path.
The client does not implement that path; it replies with a pure-ack container
(`cumulativeAck = seq`) so the peer stops resending, and nothing else. Avoid
`Context.Watch` on the device ref and this stays theoretical.

## What is deliberately not implemented

Cluster membership, DeathWatch, `Akka.Persistence`, custom serializers,
`ActorSelection`, Hyperion/protobuf payloads, TLS. A device that needs any of
those is a device that should be running a real `ActorSystem`.

---

# Application layer: the AskBot chat protocol

The transport above carries one more layer, which is what the device app and the
host actor actually speak. Payloads are **JSON strings** (serializer 17), the same
field names the BLE protocol of `amoled_chat_host` uses, so the device code is a
transport swap rather than a rewrite.

## The client actor

The device registers a local actor and sends *as* it:

```
akka.tcp://askbot-device@<device-ip>:2553/user/chat
```

The host's `ChatActor` therefore sees a `Sender` it can push to at any time -
stages, reply chunks, session changes are plain `Tell`s, not responses to a
request. `RemoteClient::Register("chat", handler)` on the device dispatches
inbound envelopes whose recipient path ends in `/user/chat` to that handler, with
`sender_path` intact.

This is the part that answers "can a small device take part in the actor model":
there is no `ActorSystem` on the ESP32, no mailbox scheduler, no supervision - but
on the wire it is a peer with an address, and the host talks to it as one.

## Device to host (`/user/chat` on the host)

| `t` | JSON | meaning |
|---|---|---|
| `hello` | `{"t":"hello","name":"askbot","fw":"akka-1"}` | sent once per association |
| `text` | `{"t":"text","id":N,"text":"...","tts":false}` | a question |
| `cancel` | `{"t":"cancel","id":N}` | abandon the running answer |
| `newsession` | `{"t":"newsession","id":N}` | move to a fresh conversation |
| `ping` | `{"t":"ping"}` | liveness check |

## Host to device (`/user/chat` on the device)

| `t` / `st` | JSON | meaning |
|---|---|---|
| `hostinfo` | `{"t":"hostinfo","host":"PC","provider":"netclaw","tts":false,"chat":1,"v":1}` | answer to `hello` |
| `answer` `think` | `{"t":"answer","st":"think","id":N}` | prompt handed to the chat CLI |
| `answer` `reply` | `{"t":"answer","st":"reply","id":N,"seq":i,"n":k,"text":"...","done":true}` | answer chunk; `seq` 0 starts a fresh answer, concatenate in order |
| `answer` `session` | `{"t":"answer","st":"session","id":N,"n":k}` | conversation number changed |
| `answer` `idle` | `{"t":"answer","st":"idle","id":N}` | nothing running (answer to `cancel`) |
| `answer` `err` | `{"t":"answer","st":"err","id":N,"text":"..."}` | request failed |
| `answer` `pong` | `{"t":"answer","st":"pong","id":N}` | answer to `ping` |

Chunking is UTF-8 safe and defaults to 400 bytes (`Chat.ChunkBytes`), well under
the frame budget; the limit exists because the device appends into a fixed buffer
and redraws per chunk.

Newest-question-wins: a new `text` cancels the request in flight instead of being
refused, matching what the BLE host settled on.

## Spoken answers

When a `text` request carries `"tts":true` and the host has a voice, the answer is
also synthesized and streamed:

| direction | message | meaning |
|---|---|---|
| host -> device | `{"t":"answer","st":"speak","id":N,"fmt":"adpcm","rate":16000,"ch":1,"frames":K,"ms":M}` | an utterance of K frames follows |
| host -> device | .NET `byte[]` (serializer **4**, no manifest) | `0xA6 \| id(1) \| seq(2 LE) \| ADPCM block` |
| host -> device | `{"t":"answer","st":"speak_end","id":N}` | all frames sent; `text` present only on failure |

The device decodes each block into a PSRAM buffer as it arrives and plays the whole
utterance when `speak_end` lands. Two buffers, because the next answer's frames start
arriving while the current one is still playing.

`hostinfo` reports `"tts":true` and the voice id only when the model is actually
installed, so the device hides its voice toggle otherwise - the same degradation the
BLE app applies.

### Why byte[] and not base64 in JSON

Base64 costs a third more for nothing, and `ByteArraySerializer` (id 4) is a plain
`Serializer` rather than a `SerializerWithStringManifest`, so the manifest field stays
empty and the payload is the frame itself. One ADPCM block per message: 960 samples
(60 ms) in 484 bytes, which keeps the device-side decoder identical to the Chat app's.

### The synthesizer

SuperTonic-3, four ONNX graphs run through `Microsoft.ML.OnnxRuntime`: no Python, no
espeak-ng, no COM. 44.1 kHz float out, resampled on the host to 16 kHz PCM16 with a
windowed-sinc kernel (not NAudio - its resamplers go through Media Foundation, which
would put COM back into a binary that has to survive Native AOT). The model is the one
AgentZeroLite already installed under
`%LOCALAPPDATA%\AgentZeroLite\models\supertonic`; nothing is downloaded.

Measured: ~4 s of CPU for ~20 s of Korean audio at 8 denoising steps, plus ~0.9 s of
one-time model load. It runs inside the Native AOT binary too, which Windows
`System.Speech` cannot.

## Microphone, when it arrives

Speech *input* is still the BLE Chat app's job. The shape is already fixed by the
frames above: microphone audio travels the same way in the other direction, magic
`0xA5`, and `ChatActor` already recognises inbound `byte[]`.
