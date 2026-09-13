# End-to-end check: .NET 10 + Akka 1.6 remoting host <- C++ Akka peer.
#
#   pwsh -File run_test.ps1              framework-dependent host (fast build)
#   pwsh -File run_test.ps1 -Aot         Native AOT host (single exe, no runtime)
#
# Covers three layers: the raw protocol (askbot_cli against /user/ask), the chat
# flow the device app speaks (askbot_chat against /user/chat), and the spoken answer
# (SuperTonic -> ADPCM -> byte[] messages). Fails loudly if anything goes unanswered.
param(
    [int]$Port = 2552,
    [switch]$Aot,
    [switch]$SkipBuild,
    # Skip the spoken-answer leg (needs the SuperTonic bundle AgentZeroLite installs).
    [switch]$NoVoice
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$hostProj = 'host/AskBot.Host/AskBot.Host.csproj'
$hostExe = if ($Aot) {
    'host/AskBot.Host/bin/Release/net10.0/win-x64/publish/AskBot.Host.exe'
} else {
    'host/AskBot.Host/bin/Release/net10.0/AskBot.Host.exe'
}

Get-Process AskBot.Host -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $SkipBuild) {
    if ($Aot) {
        Write-Host '== publishing .NET host as Native AOT =='
        # ILC's link step shells out to vswhere; without it on PATH the link fails
        # with a misleading MSB3073.
        $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
        & dotnet publish $hostProj -c Release -r win-x64 -p:AskBotAot=true -p:PublishAot=true
    } else {
        Write-Host '== building .NET host (Akka 1.6 nightly) =='
        & dotnet build $hostProj -c Release --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw 'host build failed' }

    Write-Host '== building C++ akka remoting module =='
    & pwsh -NoProfile -File cpp/build.ps1 -Test
    if ($LASTEXITCODE -ne 0) { throw 'cpp build failed' }
}

Write-Host "== starting host on 127.0.0.1:$Port ($(if ($Aot) {'AOT'} else {'JIT'})) =="
$log = Join-Path ([System.IO.Path]::GetTempPath()) 'askbot_host.log'
# The host goes headless when stdin is redirected, so hand it an empty file rather
# than a console it would try to read a REPL from.
$emptyIn = Join-Path ([System.IO.Path]::GetTempPath()) 'askbot_stdin.txt'
Set-Content -Path $emptyIn -Value '' -NoNewline
$proc = Start-Process -FilePath $hostExe `
    -ArgumentList @('--host', '127.0.0.1', '--port', "$Port") `
    -RedirectStandardOutput $log -RedirectStandardInput $emptyIn `
    -PassThru -WindowStyle Hidden

$code = 1
try {
    $ready = $false
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 200
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Remoting started' -Quiet)) {
            $ready = $true
            break
        }
    }
    if (-not $ready) { Get-Content $log -ErrorAction SilentlyContinue; throw 'host did not come up' }

    Write-Host ''
    Write-Host '-- 1/3 raw protocol: associate + tell/ask against /user/ask'
    & cpp/build/askbot_cli.exe --port $Port --verbose --ask 'ping' --ask 'who' --ask '한글 왕복 테스트'
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        Write-Host ''
        Write-Host '-- 2/3 chat flow: client actor against /user/chat (offline echo provider)'
        & cpp/build/askbot_chat.exe --port $Port --say '한글 질문 테스트' --say '/new' --say 'second conversation'
        $code = $LASTEXITCODE
    }

    if ($code -eq 0 -and -not $NoVoice) {
        Write-Host ''
        Write-Host '-- 3/3 spoken answer: SuperTonic -> ADPCM -> byte[] messages -> decoded on the client'
        $wav = Join-Path ([System.IO.Path]::GetTempPath()) 'askbot_speech.wav'
        & cpp/build/askbot_chat.exe --port $Port --tts --wav $wav --say '짧게 한 문장으로 답해줘'
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            if (Test-Path $wav) {
                $kb = [math]::Round((Get-Item $wav).Length / 1KB)
                Write-Host "   received audio: $wav ($kb KB)"
            } else {
                Write-Host '   no audio arrived - is the SuperTonic model installed?'
                $code = 1
            }
        }
    }
} finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
}

if ($code -ne 0) {
    Write-Host ''
    Write-Host '--- host log ---'
    Get-Content $log -Tail 40
    throw "client run failed ($code)"
}

Write-Host ''
Write-Host "PASS - C++ peer associated with the Akka 1.6 node$(if ($Aot) {' (Native AOT binary)'}) and ran all three layers."
