# 배포 후 셀프체크 판정기 — 보드가 Serial로 내보내는 [SELFCHECK] 줄을 읽어 PASS/FAIL.
# 사용:  powershell -File selfcheck.ps1 -Port COM6
# 종료코드: 0=PASS, 1=FAIL  (CI/스크립트에서 활용)
param(
  [string]$Port = "COM6",
  [int]$BaudRate = 115200,
  [int]$TimeoutSec = 8
)

$sp = New-Object System.IO.Ports.SerialPort($Port, $BaudRate, 'None', 8, 'One')
$sp.ReadTimeout = 400
try {
  $sp.Open()
  # 보드 리셋(EN 펄스) -> 부팅부터 캡처
  $sp.RtsEnable = $true; Start-Sleep -Milliseconds 120; $sp.RtsEnable = $false

  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  $line = $null
  while ((Get-Date) -lt $deadline) {
    try {
      $l = $sp.ReadLine()
      if ($l -match '\[SELFCHECK\].*begin=') { $line = $l.Trim(); break }
    } catch { }
  }
} finally {
  if ($sp.IsOpen) { $sp.Close() }
}

if (-not $line) {
  Write-Output "FAIL: $Port 에서 [SELFCHECK] 줄을 못 받음 (펌웨어 미부팅 / 포트 점유 / 잘못된 포트)"
  exit 1
}

Write-Output "수신: $line"
# 필드 파싱
$fields = @{}
foreach ($m in [regex]::Matches($line, '(\w+)=("[^"]*"|\S+)')) {
  $fields[$m.Groups[1].Value] = $m.Groups[2].Value.Trim('"')
}

$fail = @()
if ($fields['begin'] -ne 'OK')            { $fail += "begin=$($fields['begin'])" }
if ([int]($fields['psram']) -le 0)        { $fail += "psram=$($fields['psram'])" }
if ([int]($fields['heap'])  -le 0)        { $fail += "heap=$($fields['heap'])" }

Write-Output ("- fw     : {0} v{1}" -f $fields['fw'], $fields['ver'])
Write-Output ("- begin  : {0}" -f $fields['begin'])
Write-Output ("- screen : {0}  (crc={1})" -f $fields['screen'], $fields['crc'])
Write-Output ("- psram  : {0} bytes" -f $fields['psram'])
Write-Output ("- heap   : {0} bytes" -f $fields['heap'])
Write-Output ("- uptime : {0}s" -f $fields['uptime'])

if ($fail.Count -eq 0) {
  Write-Output "PASS: 셀프체크 통과 (부팅+렌더 정상)"
  exit 0
} else {
  Write-Output ("FAIL: " + ($fail -join ", "))
  exit 1
}
