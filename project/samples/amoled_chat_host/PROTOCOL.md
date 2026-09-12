# AMOLED ↔ Host chat protocol (BLE NUS)

Transport: the existing Nordic UART Service on the AMOLED firmware (`hud_ble.cpp`), unchanged UUIDs.

| Direction | GATT | UUID |
|---|---|---|
| host → device | RX (write / write-no-rsp) | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` |
| device → host | TX (notify) | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

Preferred MTU 512 (`CONFIG_BT_NIMBLE_ATT_PREFERRED_MTU=512`), so one write / one notification carries up to 509 bytes.

## Two kinds of packets

1. **Text line** — `<TAG> <json>\n`. Tag is one ASCII letter. Lines may be split across several BLE
   packets; the receiver buffers until `\n`. The host never emits a line longer than `Ble:MaxLineBytes` (480).
   JSON is written as **raw UTF-8**, never `\uXXXX` — escaping doubles a Korean payload and splits a line
   that would otherwise fit one write. A receiver may also accept a line that arrives without the trailing
   newline, but only after checking the braces actually balance: "ends with `}`" alone accepts a line
   truncated mid-value whenever a long line is split across writes.
2. **Binary audio frame** — `<magic> | id(1) | seq(2, little-endian) | payload`, one frame per packet.
   `0xA5` is the microphone stream (device → host), `0xA6` the spoken answer (host → device). Neither can
   collide with a text line because every text tag is ASCII.

## Host → device

| Tag | Meaning | JSON |
|---|---|---|
| `S` | HUD status (unchanged, from Claude Code statusLine) | as before |
| `E` | HUD event (unchanged, from Claude Code hooks) | as before |
| `H` | host hello, sent once when the link comes up | `{"host":"PCNAME","provider":"netclaw","stt":"whisper-small","sttReady":true,"tts":true,"chat":1,"v":3}` |
| `A` | answer / progress for request `id` | see stages below |
| `C` | remote control of the on-screen app (test aid) | `{"cmd":"talk","ms":5000}` · `{"cmd":"text","text":"..."}` · `{"cmd":"mode","voice":true}` · `{"cmd":"newchat"}` · `{"cmd":"clear"}` |

The greeting is one round trip and one direction only: host sends `H` on connect, the device answers `R hello`,
and the host does **not** answer that with another `H`. Replying to the reply ping-pongs forever.

`A` stages (`st`):

| `st` | when | extra fields |
|---|---|---|
| `rec` | host accepted a `voice` begin and is collecting frames | – |
| `stt` | (no `text`) transcription started · (with `text`) transcription result | `text` |
| `think` | prompt handed to the chat CLI | – |
| `reply` | answer chunk; UTF-8 safe split, concatenate in `seq` order | `seq`, `n`, `text`, `done:true` on the last chunk |
| `speak` | spoken answer follows as `0xA6` frames | `fmt`, `rate`, `ch`, `frames`, `ms` |
| `speak_end` | all speech frames sent; the device plays what it buffered | `text` only on failure |
| `idle` | nothing is running any more (answer to `cancel`) | – |
| `session` | the conversation changed; the device clears its bubbles | `n` (1-based conversation number) |
| `err` | request failed / no speech / audio decode error | `text` |
| `busy` | legacy; the host preempts instead of refusing, so this should not appear | – |
| `pong` | reply to `ping` | – |

`id` = the request id the device chose. Answers pushed from the host web UI use `id: 0`.

## Device → host

All device lines use tag `R`:

| `t` | JSON | notes |
|---|---|---|
| `hello` | `{"t":"hello","name":"claude-hud","fw":"chat-1","fmt":"adpcm"}` | sent in reply to `H`; the host records it and stays quiet |
| `ping` | `{"t":"ping","id":n}` | host answers `A {"id":n,"st":"pong"}` |
| `text` | `{"t":"text","id":n,"text":"...","lang":"ko","tts":false}` | typed / preset prompt; `lang` optional |
| `voice` | `{"t":"voice","id":n,"fmt":"adpcm","rate":16000,"ch":1,"lang":"ko","tts":false}` | begin an utterance; then send audio frames with this `id` |
| `end` | `{"t":"end","id":n}` | utterance finished → host runs STT → chat → `A` stages |
| `cancel` | `{"t":"cancel","id":n}` | drop the capture, the host request and any answer audio |
| `newsession` | `{"t":"newsession","id":n}` | leave this conversation and start a fresh one |

`tts` is the device's answer-mode setting: `false` = text only, `true` = text plus a spoken answer. The host
advertises whether it can speak at all in its `H` line (`"tts":true`); the device hides the setting when it
cannot.

`fmt`:
- `pcm16` — raw 16-bit little-endian PCM in every frame payload.
- `adpcm` — IMA ADPCM, each frame payload is a **self-contained block**:
  `[predictor int16 LE][step index u8][0][nibbles… low nibble first]`. Losing one notification costs one block only.
  4:1 compression; the host self-test (`GET /api/selftest/adpcm`) shows ~33 dB SNR on a 440 Hz tone.
  Reference encoder: `ChatHost/Stt/AudioConvert.cs` → `ImaAdpcm.EncodeBlock`.

Recommended capture on the device: 16 kHz mono from `bsp_audio_codec_microphone_init()`, ADPCM blocks of
480 samples (30 ms → 244-byte payload → fits one notification at MTU 247+; at MTU 512 use 960 samples/484 B).

## One request at a time, newest wins

A chat CLI keeps per-session state — netclaw holds an exclusive lock on `~/.netclaw/logs/<session>.log` —
so two prompts cannot run on one session at once. Rather than refuse the second one, the host **preempts**:
the older request is abandoned, its answer discarded, and only the newer one reaches the device. The
abandoned CLI child is *not* killed (killing it wedges the session server-side); it is left to finish while
the provider's slot stays held, and only killed if it overruns a 12 s grace. If a session does end up
unusable, the host rotates to a fresh session id and the person just asks again.

The practical consequence for the device: a second question always wins, and `busy` should never show.

## Bandwidth reality

| format | bytes / s | 5 s utterance | at ~40 kB/s NUS notify throughput |
|---|---|---|---|
| pcm16 16 kHz | 32 000 | 160 kB | ~4 s |
| adpcm 16 kHz | 8 000 | 40 kB | ~1 s |

Stream while recording (do not buffer the whole utterance first) so the transfer overlaps the speech.

## Sequence

```
device                      host
  R voice{id:7} ───────────▶  start capture        ◀── A {id:7,st:"rec"}
  0xA5 07 0000 …  ─────────▶
  0xA5 07 0001 …  ─────────▶  (frames)
  R end{id:7}   ───────────▶  decode → STT         ◀── A {id:7,st:"stt"}
                                                   ◀── A {id:7,st:"stt",text:"오늘 날씨 어때"}
                              chat CLI (netclaw)   ◀── A {id:7,st:"think"}
                                                   ◀── A {id:7,st:"reply",seq:0,n:2,text:"…"}
                                                   ◀── A {id:7,st:"reply",seq:1,n:2,text:"…",done:true}
   (answer mode = text+voice only)
                              SAPI synthesis        ◀── A {id:7,st:"speak",frames:113,ms:6770}
                                                   ◀── 0xA6 07 0000 …
                                                   ◀── 0xA6 07 0001 …
                              buffered in PSRAM     ◀── A {id:7,st:"speak_end"}
   plays 6.8 s on the speaker
```
