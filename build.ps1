# Builds "VR Session Recorder.exe" from src\ with Windows' built-in .NET Framework compiler.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'lib\Microsoft.Diagnostics.Tracing.TraceEvent.dll'))) { & (Join-Path $PSScriptRoot 'tools\restore-libs.ps1') }
. (Join-Path $PSScriptRoot 'references.ps1')
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:' + (Join-Path $PSScriptRoot 'VR Session Recorder.exe')) $references $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host 'Built VR Session Recorder.exe'
