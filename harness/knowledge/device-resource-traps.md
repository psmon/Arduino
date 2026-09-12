# Device resource traps — ESP32-S3 + LVGL + NimBLE on this board

> Owner: **`device-resource-warden`**.
> Every entry below cost a debugging session on the ESP32-S3-Touch-AMOLED-1.75C. They are recorded as
> symptoms first, because the symptom is what you actually see.

The board is an ESP32-S3R8: 32 MB flash, 8 MB octal PSRAM, a 466×466 CO5300 AMOLED, ES8311 out and ES7210
in on one I2S bus, NimBLE, and LVGL 9 rendering with two software draw units. Nothing here is exotic; the
traps come from those pieces meeting each other.

---

## 1. Symptom: most glyphs render as garbage, a few look fine

**Cause.** LVGL keeps the RLE reader for *compressed* font glyphs in one global,
`LV_GLOBAL_DEFAULT()->font_fmt_rle`. With `CONFIG_LV_DRAW_SW_DRAW_UNIT_CNT=2` two draw units decompress
glyphs concurrently and clobber each other's reader state. The more text on screen, the worse it looks.

**Tell.** `.bitmap_format = 1` in the generated font `.c`. Built-in fonts (Montserrat, unscii) are
uncompressed, so an app that only uses those never shows the problem.

**Fix.** Generate the font with `--no-compress` (`tools/gen_font.py`), or drop to one draw unit.

## 2. Symptom: the board reboots mid-operation, `stack overflow in task nimble_host`

**Cause.** Callbacks registered with the BLE layer run on the NimBLE host task, whose default stack is
4 KB — already carrying a 600-byte reassembly buffer, cJSON parsing and `ESP_LOG` formatting.

**Fix.** `CONFIG_BT_NIMBLE_HOST_TASK_STACK_SIZE=8192`, *and* keep large temporaries off that task. The
outbound queue carries a heap pointer rather than a 400-byte struct for this reason.

**Rule.** Anything reached from a GATT callback is on someone else's stack. Count what you put there.

## 3. Symptom: audio plays back much shorter than it should

**Cause.** One buffer served both "being filled from BLE" and "being played". BLE delivers roughly four
times faster than the speaker consumes, so the next utterance overwrote the one in flight: 16.8 s of speech
came out as 3.6 s.

**Fix.** Two buffers, fill one while the other plays. Generally: any producer/consumer pair with a speed
ratio like this needs either two buffers or back-pressure, and BLE gives you no back-pressure.

## 4. Symptom: a deadlock or a lockup when replying to a message

**Cause.** Calling back into the BLE stack from inside its own receive callback, while the host lock is
held.

**Fix.** Queue the reply to a separate task. The chat app has a dedicated tx task for exactly this.

## 5. Symptom: text in a container renders scrambled after a value updates

**Cause.** `lv_obj_scroll_to_y(obj, LV_COORD_MAX, ...)`. `LV_COORD_MAX` is about 5.4e8 and the scroll
arithmetic overflows, moving every child to a nonsense position.

**Fix.** `lv_obj_scroll_to_view(child, ...)`, which clamps.

## 6. Symptom: a button's handler fires hundreds of times without a touch

**Cause.** `lv_obj_add_event_cb(btn, cb, LV_EVENT_ALL, ...)`. `LV_EVENT_ALL` includes draw and refresh
events, so the handler runs on every redraw.

**Fix.** Register the specific code (`LV_EVENT_CLICKED`, `LV_EVENT_PRESSED`, ...). A handler that must see
several codes gets several registrations, or checks `lv_event_get_code` and is registered narrowly anyway.

## 7. Symptom: an icon or asset does not appear

**Cause.** The component's CMake is missing `WHOLE_ARCHIVE`, so the statically registered plugin or the
image descriptor is dropped by the linker.

## 8. Budgets worth checking before adding anything

| Resource | Size | Note |
|---|---|---|
| app partition | 8 MB | image is ~3.1 MB; `check_sizes.py` prints the headroom every build |
| PSRAM | 8 MB | audio buffers, LVGL task stacks (`stack_in_psram`), large assets |
| internal RAM | scarce | DMA buffers and task stacks that touch I2S must be internal |
| NimBLE host stack | 8 KB | see §2 |
| notification payload | MTU − 3 | 509 at MTU 512; ask the stack, do not cache (see the BLE contract doc) |

## 9. Boundaries

- **Does**: memory, stacks, buffers, driver and LVGL configuration hazards on this board.
- **Does not**: protocol agreement between device and host (that is `ble-contract-sentinel`), nor product
  behaviour or wording.
