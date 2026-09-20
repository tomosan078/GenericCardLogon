$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'bin\x64\Release\GenericCardLogon.CredentialProvider.dll'
if (!(Test-Path $dll)) { throw "Release DLL not found: $dll" }
$dest = Join-Path $env:WINDIR 'System32\GenericCardLogon.CredentialProvider.dll'
Copy-Item $dll $dest -Force
& "$env:WINDIR\System32\regsvr32.exe" /s $dest
if ($LASTEXITCODE -ne 0) { throw "regsvr32 failed: $LASTEXITCODE" }
Write-Host 'Credential Provider installed.'
