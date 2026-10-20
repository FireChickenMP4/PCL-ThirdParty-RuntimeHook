$ErrorActionPreference = 'Stop'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$lib = Join-Path $root 'lib'
New-Item -ItemType Directory -Force $lib | Out-Null

$tmp = Join-Path $env:TEMP 'pclhook-deps'
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null

$packages = @(
    @{
        Name = 'dnlib'
        Url  = 'https://api.nuget.org/v3-flatcontainer/dnlib/4.5.0/dnlib.4.5.0.nupkg'
        Hash = '63BC2F9579568204CC8B30FA9F6700A231BCF868A8032E09118887AF3EAFEE58'
    },
    @{
        Name = 'harmony'
        Url  = 'https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.3/lib.harmony.2.4.3.nupkg'
        Hash = 'F07AF11A0D62447E7E1B6E3FCF1E28FC75D9F8D1ABF2D6EE79AABCDC4DB43E88'
    }
)

foreach ($package in $packages) {
    $zip = Join-Path $tmp ($package.Name + '.zip')
    Write-Host ('downloading ' + $package.Name)
    Invoke-WebRequest -UseBasicParsing $package.Url -OutFile $zip

    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $package.Hash) {
        throw ($package.Name + ' hash mismatch: ' + $actual)
    }

    Expand-Archive $zip -DestinationPath (Join-Path $tmp $package.Name) -Force
}

Copy-Item (Join-Path $tmp 'dnlib\lib\net45\dnlib.dll') $lib -Force
Copy-Item (Join-Path $tmp 'harmony\lib\net48\0Harmony.dll') $lib -Force

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'dependencies ready: lib\dnlib.dll, lib\0Harmony.dll'
