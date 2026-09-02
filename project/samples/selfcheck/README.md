# selfcheck — 배포 후 셀프체크 / 스모크 테스트

배포 직후 **자동으로** "펌웨어가 잘 부팅했고 디스플레이 렌더 파이프라인이 정상인지"를
확인하는 샘플. 사람이 화면을 보지 않아도 PASS/FAIL 판정이 나온다.

## 원리
- **디바이스(`selfcheck.ino`)**: 화면에 3색 띠 + `SELF/CHECK` 라벨을 그리고, 자기 상태를
  Serial(CH343/COM)로 `[SELFCHECK]` 한 줄에 1.5초마다 내보낸다.
  - 보고 항목: `begin`(gfx 초기화), `psram`, `heap`, `screen`(그린 내용 라벨), `crc`(내용 체크섬), `uptime`
- **PC(`selfcheck.ps1`)**: 그 줄을 읽어 `begin=OK` / `psram>0` / `heap>0` 을 검사하고
  **PASS(종료코드 0) / FAIL(1)** 을 낸다. 의존성 없음(PowerShell 내장 SerialPort).

## 한계 (정직하게)
GC9A01은 사실상 쓰기 전용이라 **화면 픽셀을 되읽어 검증할 수는 없다.** 이 셀프체크는
"코드가 정상 부팅·렌더했고 무엇을 그렸는지"까지 확인한다(스모크 테스트로 충분). 백라이트/
물리 표시의 최종 확인은 여전히 육안. → 그래서 `bl` 핀 상태와 `screen` 라벨을 함께 보고한다.

## 사용 (배포 + 셀프체크 한 번에)
```powershell
# 1) 빌드 + 업로드 (CDCOnBoot 없음 => Serial 이 COM 으로 나감)
arduino-cli compile --upload -p COM6 --fqbn "esp32:esp32:esp32s3:PSRAM=enabled,FlashSize=16M" project/samples/selfcheck

# 2) 셀프체크 판정 (COM 을 잡으므로 IDE Serial Monitor 는 닫아둘 것)
powershell -File project/samples/selfcheck/selfcheck.ps1 -Port COM6
#  -> PASS: ... (exit 0)  /  FAIL: ... (exit 1)
```

## 확장 아이디어
- 각 펌웨어에 동일한 `[SELFCHECK]` 라인을 심어 배포 파이프라인의 공통 스모크 게이트로 사용
- BLE 채널로도 같은 자기보고를 내보내 무선 셀프체크
- 패널 ID 레지스터(GC9A01 RDDID) SPI 리드백 시도로 하드웨어 링크까지 확인(모듈에 따라 불안정)
