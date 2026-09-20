$ErrorActionPreference = 'Continue'
Write-Host '=== GenericCardLogon v9.1 ==='
$service = Get-Service -Name GenericCardLogon -ErrorAction SilentlyContinue
if ($service) { Write-Host "Service: $($service.Status) / $($service.StartType)" } else { Write-Host 'Service: NOT INSTALLED' }
$dll = Join-Path $env:WINDIR 'System32\GenericCardLogon.CredentialProvider.dll'
Write-Host "Credential Provider DLL: $dll"
Write-Host "Exists: $(Test-Path $dll)"
$clsid='{4FE441C9-78D1-4CA3-8217-B78CE7B73EEE}'
$cp="HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
$com="HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
Write-Host "Provider registration: $(Test-Path $cp)"
Write-Host "COM registration: $(Test-Path $com)"
Write-Host 'LSA Authentication Packages are intentionally not modified by v9.1.'
