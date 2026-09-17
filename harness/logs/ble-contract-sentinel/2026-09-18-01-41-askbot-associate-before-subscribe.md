---
date: 2026-09-18T01:41:00+09:00
agent: ble-contract-sentinel
type: review
mode: execution
trigger: "형제 프로젝트(AgentZeroLite) 크로스 협업 — 기기 로그가 드러낸 ASSOCIATE 실패 이관"
target: components/brookesia_app_askbot/askbot_ble_stream.cpp
---

# AskBot 이 구독 전에 ASSOCIATE 를 보낸다 — 매 접속마다 실패

## 어디서 왔나

형제 프로젝트 [AgentZeroLite](https://github.com/psmon/AgentZeroLite) 의 `device-smith`
에이전트가 호스트 로그와 이 기기의 시리얼 로그를 **동시에** 잡으면서 드러났다. 호스트
로그만으로는 "터널이 접속 4초 뒤에 열린다"까지만 보이고 이유는 알 수 없었다.

## 증상 (COM7, 수정 전)

```
I ( 8656) hud_ble:    central connected (handle 1)
I ( 8810) askbot_ble: tunnel open, 508 bytes per notification
W ( 9010) askbot_ble: notify blocked for 200 ms, dropping the tunnel
E ( 9010) askbot:     sending ASSOCIATE failed
I (10100) hud_ble:    TX notify subscribed          ← 1.3 s 늦게 도착
I (14010) askbot_ble: tunnel open   (5 s 뒤 재시도)
I (14230) askbot:     associated
```

매 접속마다 재현된다. 기기 리부트 직후에도 동일.

## 원인

`askbot_ble_stream.cpp` 의 `BleStream::Connect` 는 `claude_hud::bleConnected()` 만 기다렸다.
**바로 위 주석은 이미 올바른 조건을 말하고 있었다** — *"wait for the PC's bridge to be
connected **and subscribed**"* — 코드가 그 절반만 구현한 상태였다.

센트럴은 링크가 붙는 즉시 connected 지만, TX notify 구독은 서비스 탐색 + CCCD write 이후에야
켠다. Windows 에서 그 간격이 1.3 초다. 그 사이에 터널을 열면 첫 ASSOCIATE 가 허공에 notify
되고, `WriteAll` 이 200 ms 백프레셔 재시도를 소진한 뒤 링크를 버린다.

## 수정

| 파일 | 변경 |
|---|---|
| `brookesia_app_claude_hud/hud_transport.hpp` | `bleSubscribed()` 공개 + 왜 `bleConnected()` 로는 부족한지 명시 |
| `brookesia_app_claude_hud/hud_ble.cpp` | 이미 있던 `s_txSubscribed` 를 노출 (1줄) |
| `brookesia_app_askbot/askbot_ble_stream.cpp` | `Connect` 가 `bleSubscribed()` 를 기다림. `IsOpen()` 도 같은 기준. 폴링 200 ms → 50 ms. 타임아웃 시 경고 |

`bleNotify`/`bleSendLine` 은 이미 `s_txSubscribed` 를 확인하고 있었다 — 게이트가 없던 곳은
터널을 여는 쪽뿐이었다.

## 실측 검증 (동일 보드, 플래시 후)

```
I (23702) hud_ble:    central connected (handle 1)
I (25152) hud_ble:    TX notify subscribed
I (25153) askbot_ble: tunnel open, 508 bytes per notification
I (25154) askbot:     sent ASSOCIATE as akka.tcp://askbot-device@askbot-ble:2553
I (25249) askbot:     associated with akka.tcp://AskBot@127.0.0.1:2552
```

| 지표 | 전 | 후 |
|---|---|---|
| central connected → associated | 5574 ms | **1547 ms** |
| 실패한 ASSOCIATE | 접속당 1건 | **0건** |
| 호스트측 connected → tunnel open | 3993 ms | **5 ms** |

## 평가

- **워크플로우 개선도: A** — 원인·수정·실측 검증이 한 사이클에 끝났고, 회귀 지표가 수치로 남았다.
- **하네스 성숙도 기여**: 이 결함은 **한쪽 로그만으로는 보이지 않았다.** 양쪽 로그를 동시에
  잡는 절차가 형제 프로젝트에 생기면서 처음 드러났다. 이 정원의 점검 절차에도 같은 항목을
  넣을 가치가 있다.

## 다음 단계 제안

1. `PROTOCOL.md` 에 **"센트럴은 CCCD write 전까지 수신하지 않는다"** 를 계약으로 명시.
   지금은 암묵적이라 같은 실수가 다른 송신 경로에서 반복될 수 있다
2. Chat 경로(`chat_core`)도 같은 창에서 송신을 시도하는지 점검 — 이번엔 증상이 없었으나
   `bleSendLine` 이 조용히 false 를 반환하고 있을 가능성
