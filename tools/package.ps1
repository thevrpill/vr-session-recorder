# Creates dist\<name>.zip containing only what a player needs, plus SHA256SUMS.txt.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = 'VR-Session-Recorder-' + $Version
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'lib') | Out-Null
foreach ($f in @('VR Session Recorder.exe', 'VR Session Recorder.exe.config', 'report-template.html', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'Test VD toggle.cmd')) {
    Copy-Item -LiteralPath (Join-Path $root $f) -Destination $stage
}
Copy-Item -Path (Join-Path $root 'lib\*.dll') -Destination (Join-Path $stage 'lib')
$zip = Join-Path $dist ($name + '.zip')
Compress-Archive -Path $stage -DestinationPath $zip
$lines = @()
$lines += ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  ' + ($name + '.zip'))
$lines += ((Get-FileHash -LiteralPath (Join-Path $stage 'VR Session Recorder.exe') -Algorithm SHA256).Hash + '  ' + ($name + '/VR Session Recorder.exe'))
Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value $lines -Encoding ASCII
Get-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt')
