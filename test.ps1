# -CI runs only the checks that work without a GPU, headset or admin rights (used by the GitHub build).
param([switch]$Trace, [switch]$CI)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'references.ps1')
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler /nologo /target:exe /platform:x64 /optimize+ /main:RecorderTests ('/out:' + (Join-Path $PSScriptRoot 'RecorderTests.exe')) $references $sourceFiles (Join-Path $PSScriptRoot 'tests\RecorderTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VR Session Recorder.exe.config') -Destination (Join-Path $PSScriptRoot 'RecorderTests.exe.config') -Force
$testArgs = @(); if ($Trace) { $testArgs += '--trace' }; if ($CI) { $testArgs += '--ci' }
& (Join-Path $PSScriptRoot 'RecorderTests.exe') $testArgs
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
