$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler not found. This builds on Windows 10/11 with .NET Framework 4.8.' }
$netstandard = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard') -Filter netstandard.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$runtimeFacade = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Filter System.Runtime.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$references = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', ('/reference:' + $netstandard), ('/reference:' + $runtimeFacade), ('/reference:' + (Join-Path $PSScriptRoot 'lib\Microsoft.Diagnostics.Tracing.TraceEvent.dll')))
