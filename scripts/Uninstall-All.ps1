$ErrorActionPreference = 'Continue'
& (Join-Path $PSScriptRoot 'Uninstall-CredentialProvider.ps1')
& (Join-Path $PSScriptRoot 'Uninstall-Service.ps1')
Write-Host 'GenericCardLogon uninstalled.'
