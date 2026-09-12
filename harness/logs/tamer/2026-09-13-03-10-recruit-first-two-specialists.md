---
date: 2026-09-13T03:10:00+09:00
agent: tamer
type: recruit
mode: recruit
command: "/harness-creator 하네스 전문가 업데이트"
---

# 첫 전문가 2명 영입 — device-resource-warden, ble-contract-sentinel

## 실행 요약

v1.0.0 에서 정원지기 혼자였던 정원에 specialist 2명을 심었다. 원천은 **프로젝트 자체**
(recruit-workflow §2-3): 이번 세션에서 사람이 놓쳐 하드웨어가 대신 찾아낸 결함들을 두 갈래로 묶었다.

1. 리소스 계열 — 압축 폰트 + 드로우 유닛 2개로 한글 깨짐, NimBLE 태스크 스택 4 KB 초과로 리부트,
   답변 오디오 단일 버퍼로 16.8초→3.6초 절단, `LV_COORD_MAX` 스크롤 오버플로우, `LV_EVENT_ALL`
   등록으로 버튼 콜백 폭주
2. 계약 계열 — 이스케이프된 JSON 으로 한 줄이 BLE 한 번 쓰기 초과, `}` 로 끝나면 완성으로 간주해
   잘린 줄 수용, MTU 캐시 리셋으로 20바이트 페이로드 강등, hello 무한 왕복

두 계열 모두 **증상이 원인과 전혀 닮지 않는다**. 그래서 지식 문서를 증상 우선으로 썼다.

5-파일 패턴대로 agents 2 + knowledge 2 + engine 1 + config + docs 를 만들었다.

## 결과

| 파일 | 상태 |
|------|------|
| `agents/device-resource-warden.md` | 신규 (specialist) |
| `agents/ble-contract-sentinel.md` | 신규 (specialist) |
| `knowledge/device-resource-traps.md` | 신규 |
| `knowledge/ble-wire-contract.md` | 신규 |
| `engine/full-review.md` | 신규 — `전체 점검해` / `하네스 수행해` |
| `harness.config.json` | agents 3, engine 1, 1.0.0 → 1.1.0 |
| `docs/v1.1.0.md` | 신규 |

정전(canon)은 `project/samples/amoled_chat_host/PROTOCOL.md` 로 두고, knowledge 는 그것을 *어떻게 볼지*만
적었다. 툴과 지식을 분리하라는 원칙에 따라 에이전트 정의에는 판단 기준만, 대상 파일 경로는 knowledge 에 뒀다.

엔진은 순차 실행이고 합성 등급을 금지했다. "펌웨어는 멀쩡한데 프로토콜이 어긋났다"가 쓸모 있는 문장인데
평균 낸 등급은 그 문장을 지운다.

## 평가

정원지기 3축 (references/evaluation.md):

| 축 | 판정 | 근거 |
|----|------|------|
| 워크플로우 개선도 | **B** | 반복된 결함 유형이 점검 대상으로 문서화됐다. 다만 아직 한 번도 실행해 보지 않아 실효는 미검증 |
| Claude 스킬 활용도 | **2/5** | 두 전문가 모두 외부 스킬을 호출하지 않는다. 읽기·대조 위주라 현재는 불필요 |
| 하네스 성숙도 | **L2** | 3층이 모두 채워졌지만 각 층에 항목이 2개 이하이고 로그 축적이 없다 |

## 다음 단계 제안

- `전체 점검해` 를 실제로 한 번 돌려 두 전문가의 절차가 이 저장소에서 작동하는지 확인한다.
  절차가 헛도는 단계가 있으면 그때 고친다.
- LCD-1.28(Arduino) 쪽은 아직 담당이 없다. `arduino-cli` FQBN 옵션과 파티션 스킴이 문서와 어긋나는
  사고가 있었으므로 `build-doctor` 영입을 검토한다. 단 지금 주력이 AMOLED 이므로 서두르지 않는다.
- PC 호스트(C#)의 취소·세션 수명 같은 동시성 결함은 이번에 세 번 고쳤다. 재발하면 별도 specialist 로
  승격할 후보다.
