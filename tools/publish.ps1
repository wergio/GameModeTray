# Builds a single-file GameModeTray.exe into dist\.
#   -Version 1.0.0   version stamped into the exe (default: the one in the csproj)
#   -Install         copies the exe to %LOCALAPPDATA%\Programs\GameModeTray (closing a running
#                    instance first)
# Requires the .NET 10 Desktop Runtime on the target PC.
param(
    [string]$Version,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$publishArgs = @(
    (Join-Path $root 'GameModeTray.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-p:DebugType=none',
    '-o', $dist
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\GameModeTray'
    $running = Get-Process GameModeTray -ErrorAction SilentlyContinue
    if ($running) {
        # Ask the running instance to quit cleanly, then make sure it is gone.
        & (Join-Path $dist 'GameModeTray.exe') --exit
        foreach ($p in $running) {
            if (-not $p.WaitForExit(8000)) { $p.Kill() }
        }
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    # The executable can stay locked for a moment after the process exits.
    for ($attempt = 1; ; $attempt++) {
        try {
            Copy-Item (Join-Path $dist 'GameModeTray.exe') $target -Force -ErrorAction Stop
            break
        }
        catch [System.IO.IOException] {
            if ($attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Output "Installed to $target"
}
