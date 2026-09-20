$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:WINDIR 'System32\GenericCardLogon.CredentialProvider.dll'
if (Test-Path $dest) { & "$env:WINDIR\System32\regsvr32.exe" /u /s $dest }
Remove-Item $dest -Force -ErrorAction SilentlyContinue
Write-Host 'Credential Provider removed.'
