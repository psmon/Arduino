---
name: full-review
triggers:
  - "전체 점검해"
  - "하네스 수행해"
  - "full review"
description: Runs every resident specialist over the project, firmware first and then the device/host contract.
agents:
  - device-resource-warden
  - ble-contract-sentinel
---

# Full review

## Order

1. **device-resource-warden** — firmware changes: task stacks, buffers, allocation caps, LVGL font and
   event configuration, build envelope.
2. **ble-contract-sentinel** — the device/host wire contract against `amoled_chat_host/PROTOCOL.md`.

Resource findings come first on purpose. A board that reboots or renders garbage makes every protocol
observation unreliable, so the thing that keeps the device alive is checked before the thing that keeps the
two sides talking.

Execution is **sequential**. The two agents look at overlapping files but answer different questions, and
nothing in this harness depends on running them at the same time.

## Scope

Default scope is the whole repository. `변경 점검해` (targeted review) narrows it to the working-tree diff;
in that case each agent reviews only the files the diff touches, plus the canon documents they own.

## Reporting

Each agent reports its own findings with its own severity grades. **Do not merge them into one composite
grade** — "the firmware is fine but the protocol drifted" is the useful sentence, and an averaged letter
destroys it.

Report per agent:
- findings, most severe first, each with the symptom it would produce on hardware
- what to change, and on which side
- what was checked and found clean, briefly, so the reader knows the silence was deliberate

## Evaluation

The harness default (see `references/evaluation.md`): neither agent defines its own axes.

> PDSA: not adopted for this engine.
