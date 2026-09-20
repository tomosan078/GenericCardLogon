# Run PowerShell as Administrator.
$ErrorActionPreference = 'Stop'
$clsid = '{4FE441C9-78D1-4CA3-8217-B78CE7B73EEE}'
$cp = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
$inproc = "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
$dll = Join-Path $env:WINDIR 'System32\GenericCardLogon.CredentialProvider.dll'
Write-Host '=== GenericCardLogon Credential Provider ==='
Write-Host "DLL: $dll"
if (Test-Path $dll) {
    Get-Item $dll | Select-Object FullName,Length,LastWriteTime | Format-List
} else {
    Write-Warning 'Credential Provider DLL is not installed in System32.'
}
Write-Host '=== Provider registration ==='
if (Test-Path $cp) { Get-ItemProperty $cp | Format-List * } else { Write-Warning "Provider registration not found: $cp" }
Write-Host '=== COM registration ==='
if (Test-Path $inproc) { Get-ItemProperty $inproc | Format-List * } else { Write-Warning "COM registration not found: $inproc" }
Write-Host '=== Important ==='
Write-Host 'Build and install Release|x64, then sign out. Keep Windows password/PIN available for recovery.'
