# Downloads the exact Microsoft libraries the recorder uses from nuget.org (Microsoft's official package
# source) into lib\, and refuses to continue unless every DLL matches its pinned SHA-256 fingerprint.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root 'lib'
New-Item -ItemType Directory -Force -Path $lib | Out-Null
$packages = @(
    @{ Id = 'Microsoft.Diagnostics.Tracing.TraceEvent'; Version = '4.0.0'; Files = @(
        @('lib/netstandard2.0/Microsoft.Diagnostics.Tracing.TraceEvent.dll', '9E20D3797881C237E891DD97B5137F0D738FD4ECBBDA7E64568A6A21171FF4B7'),
        @('lib/netstandard2.0/Microsoft.Diagnostics.FastSerialization.dll', 'AD9C4E4001B2AED5E7337188747D0791C2A8D6058FD9A7EB5F12832982F76934'),
        @('lib/netstandard2.0/Dia2Lib.dll', '6C4CAC68010FE032218EFE5E9FCF46EEF9F77BFAA5F3BD33F03C5FF77D5A8FAC'),
        @('lib/netstandard2.0/TraceReloggerLib.dll', '6830C0F1D3F3C17719181E73DD5CFA8CCBB3F3C575329D2120576545103467B5')) },
    @{ Id = 'Microsoft.Win32.Registry'; Version = '5.0.0'; Files = @(,@('lib/net461/Microsoft.Win32.Registry.dll', 'E9A9D281C1A708AAAE366F82FD6A1742F65DA2918CC4FA5EAAAADA0BE24277D9')) },
    @{ Id = 'System.Security.AccessControl'; Version = '5.0.0'; Files = @(,@('lib/net461/System.Security.AccessControl.dll', '1FDA491EEBDB19EA0A83CF6C16AB5DD004A1BFDFC845EDE017EBE0945BEB927F')) },
    @{ Id = 'System.Security.Principal.Windows'; Version = '5.0.0'; Files = @(,@('lib/net461/System.Security.Principal.Windows.dll', 'B4D8E15ADC235D0E858E39B5133E5D00A4BAA8C94F4F39E3B5E791B0F9C0C806')) },
    @{ Id = 'System.Collections.Immutable'; Version = '10.0.7'; Files = @(,@('lib/net462/System.Collections.Immutable.dll', '76027956C9A116DAC74749B1BBE901A31BFB91BF6FA3BE139DAC20FAEDBD7A62')) },
    @{ Id = 'System.Reflection.Metadata'; Version = '9.0.8'; Files = @(,@('lib/net462/System.Reflection.Metadata.dll', '8055D24D3B17010F18D59BEA962884922DEA516632A5D8A02C57EA7534F2A214')) },
    @{ Id = 'System.Runtime.CompilerServices.Unsafe'; Version = '6.1.2'; Files = @(,@('lib/net462/System.Runtime.CompilerServices.Unsafe.dll', '08CBD7278B66F1E68425A82D4B97181A4130D93E3DD91831407ABA7212CCDACF')) },
    @{ Id = 'System.Memory'; Version = '4.6.3'; Files = @(,@('lib/net462/System.Memory.dll', 'D5E8E4866F9CFA66F7765660F84B210198893E55335487AFE5EBDA342C0E913D')) },
    @{ Id = 'System.Buffers'; Version = '4.6.1'; Files = @(,@('lib/net462/System.Buffers.dll', '2D78D770C9CB997199154AE8C018B9F1D1EFBC86729F7264DDE6DBAD2A12CAC3')) }
)
function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }
foreach ($p in $packages) {
    $missing = @($p.Files | Where-Object { $t = Join-Path $lib ([IO.Path]::GetFileName($_[0])); -not ((Test-Path -LiteralPath $t) -and ((Get-Sha256 $t) -eq $_[1])) })
    if ($missing.Count -eq 0) { Write-Host ('OK      ' + $p.Id + ' ' + $p.Version); continue }
    $url = 'https://www.nuget.org/api/v2/package/' + $p.Id + '/' + $p.Version
    $pkg = Join-Path ([IO.Path]::GetTempPath()) ($p.Id + '.' + $p.Version + '.nupkg')
    Write-Host ('GET     ' + $url)
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $pkg
    $zip = [IO.Compression.ZipFile]::OpenRead($pkg)
    try {
        foreach ($f in $p.Files) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq $f[0] } | Select-Object -First 1
            if (-not $entry) { throw ('Missing ' + $f[0] + ' in ' + $p.Id + ' ' + $p.Version) }
            $target = Join-Path $lib ([IO.Path]::GetFileName($f[0]))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $hash = Get-Sha256 $target
            if ($hash -ne $f[1]) { Remove-Item -LiteralPath $target -Force; throw ('Fingerprint mismatch for ' + $target + ': got ' + $hash) }
            Write-Host ('VERIFY  ' + [IO.Path]::GetFileName($f[0]) + '  ' + $hash)
        }
    } finally { $zip.Dispose(); Remove-Item -LiteralPath $pkg -Force -ErrorAction SilentlyContinue }
}
Write-Host 'All libraries present and verified.'
