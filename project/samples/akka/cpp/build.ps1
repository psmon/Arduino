# Builds the C++ Akka remoting module and its dev tools with MSVC + Ninja.
#
# The compiler only needs to exist for the PC-side harness; the same sources are
# compiled by the Xtensa toolchain when they move into the ESP-IDF component.
param(
    [string]$BuildDir = 'build',
    [switch]$Clean,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere not found. Install Visual Studio Build Tools with the C++ workload."
}

$vsRoot = & $vswhere -products * -latest -property installationPath
if (-not $vsRoot) { throw 'No Visual Studio installation found.' }

$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsRoot" }

if ($Clean -and (Test-Path $BuildDir)) { Remove-Item $BuildDir -Recurse -Force }

$steps = @(
    "cmake -S . -B $BuildDir -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo",
    "cmake --build $BuildDir"
)
if ($Test) { $steps += "ctest --test-dir $BuildDir --output-on-failure" }

# vcvars64.bat shells out to vswhere itself, so put the Installer dir on PATH.
$installerDir = Split-Path $vswhere -Parent
$script = 'set "PATH=' + $installerDir + ';%PATH%" && "' + $vcvars + '" >nul && ' + ($steps -join ' && ')
& cmd /c $script
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

Write-Host ''
Write-Host "binaries: $BuildDir\askbot_cli.exe, $BuildDir\akka_wire_test.exe"
