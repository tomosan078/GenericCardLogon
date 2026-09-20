$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin\x64\Release'
$cp = Join-Path $bin 'GenericCardLogon.CredentialProvider.dll'
if (!(Test-Path $cp)) { throw "Credential Provider DLL not found. Build Release|x64 first: $cp" }
& (Join-Path $PSScriptRoot 'Install-Service.ps1')
& (Join-Path $PSScriptRoot 'Install-CredentialProvider.ps1')
Write-Host 'GenericCardLogon installation completed.'
Write-Host 'No LSA Authentication Package is installed or modified.'
