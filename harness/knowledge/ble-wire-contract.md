# BLE wire contract — what the two sides must agree on

> Owner: **`ble-contract-sentinel`**.
> Canon: `project/samples/amoled_chat_host/PROTOCOL.md`. This document is the *review* knowledge:
> what breaks, how it breaks, and what to look at. When the two disagree, PROTOCOL.md wins and this
> file gets corrected.

The AMOLED device and the PC host are two codebases in two languages that only meet over one BLE
characteristic pair. Nothing at build time checks that they still agree. Every incident below was real.

---

## 1. The surfaces that must match

| Surface | Device | Host |
|---|---|---|
| Service / RX / TX UUIDs | `hud_ble.cpp` `NUS_*_UUID` | `BleLink.NusService/NusRx/NusTx` |
| Text tags | `hud_ble.cpp` `feed()` + `Core::onLine` | `ConversationService.OnLine`, `SendAsync` |
| Binary magics | `rxAccess` (`0xA6`), `Core::onFrame` (`0xA6`) | `NusFrames.AudioMagic` (`0xA5`), `SpeechMagic` (`0xA6`) |
| Audio format | `adpcmEncodeBlock` / `adpcmDecodeBlock`, 16 kHz mono | `ImaAdpcm.EncodeBlock` / `DecodeBlock` |
| Stage names | `Core::onLine` string compares | `SendAsync(id, "<stage>")` call sites |
| Greeting fields | `Core::onLine` `H` branch | `SendHelloAsync` |

A change on one side without the other is silent: the receiver logs "rejected" or ignores an unknown
stage, and the feature simply does nothing.

## 2. Failures seen on this link

**Escaped JSON doubled the payload.** `System.Text.Json` escapes non-ASCII as `\uXXXX` by default, so a
Korean reply line went out at 523 bytes instead of 313 and no longer fit one BLE write. Outbound lines use
`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`; the consumer is cJSON on a device, never a browser.

**"Ends with `}`" accepted half a line.** The device's reassembly treated any buffer ending in `}` as a
complete line, which is exactly what a long line looks like part-way through being split across writes. It
now requires the braces to balance outside of strings.

**A greeting that answered a greeting looped forever.** The host replied to the device's `hello` with
another `H`, the device answered that with `hello`, and so on at ~200 ms per round. The greeting is one
round trip in one direction: host sends `H` on connect, device answers `R hello`, host stays quiet.

**A cached MTU threw away the negotiated one.** The MTU-exchange event can arrive *before*
`BLE_GAP_EVENT_CONNECT`, so resetting the cached value on connect dropped 512 back to 23 and the device
streamed 20-byte payloads: 4000 notifications for six seconds of audio instead of 102. Ask the stack
(`ble_att_mtu`) instead of caching.

**Binary frames fed into the text buffer.** Speech frames written to RX were being reassembled as text
until `rxAccess` learned to route anything starting with `0xA6` straight to the frame hook.

## 3. Review procedure

1. Read `PROTOCOL.md` first. Treat it as the specification, not as documentation of the code.
2. For every tag, stage and field in the table above, find both sides and compare. A stage the host can
   send but the device never matches is a silent no-op, and vice versa.
3. Check the size budget: `Ble:MaxLineBytes` minus the JSON envelope must stay under one write
   (`MaxPdu - 3`). Remember that escaping, if it ever comes back, roughly doubles non-ASCII text.
4. Check both directions of every codec: the device's encoder and the host's decoder must produce the same
   bytes for the same samples. `GET /api/selftest/adpcm` is the reference round trip.
5. Check that a receiver ignores what it does not understand instead of misreading it — an unknown tag must
   be rejected, not fed to the wrong parser.

## 4. Boundaries

- **Does**: compares the two implementations against PROTOCOL.md and against each other; flags size,
  encoding and framing hazards.
- **Does not**: judge UI, audio quality, model choice or build configuration. Those belong elsewhere.
