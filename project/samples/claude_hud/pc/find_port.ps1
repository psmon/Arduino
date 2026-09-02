# HUD 디바이스의 COM 포트 자동 검출 (Windows).
# 보드의 USB-Serial 은 WCH CH343 = VID_1A86 (PID_55D3). 그 COM 포트를 찾아 출력한다.
# 사용:  pwsh -File find_port.ps1        -> 검출된 COMx 한 줄 출력 (성공 시 stdout=포트만)
# 종료코드 0=찾음, 1=못찾음. 여러 개면 첫 번째.
$ErrorActionPreference = 'SilentlyContinue'

$devs = Get-CimInstance Win32_PnPEntity | Where-Object {
  $_.PNPDeviceID -match 'VID_1A86' -and $_.Name -match 'COM\d+'
}
$ports = @()
foreach ($d in $devs) { if ($d.Name -match '(COM\d+)') { $ports += $matches[1] } }
$ports = @($ports | Select-Object -Unique)

if ($ports.Count -eq 0) {
  Write-Host "[!] CH343(WCH, VID_1A86) 장치를 못 찾음." -ForegroundColor Yellow
  Write-Host "    - USB 케이블 연결 확인 (데이터용 케이블)" -ForegroundColor Yellow
  Write-Host "    - CH343 드라이버: https://www.wch.cn/downloads/CH343SER_EXE.html" -ForegroundColor Yellow
  Write-Host "    - 장치관리자에서 'USB-Enhanced-SERIAL CH343 (COMx)' 확인" -ForegroundColor Yellow
  exit 1
}
if ($ports.Count -gt 1) {
  Write-Host "[i] 여러 개 발견: $($ports -join ', '). 첫 번째($($ports[0])) 사용." -ForegroundColor Cyan
}
# 성공 시 stdout 에는 포트만 (다른 스크립트가 캡처하기 쉽게)
Write-Output $ports[0]
exit 0
