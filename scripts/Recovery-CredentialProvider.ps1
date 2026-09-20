$ErrorActionPreference = 'Stop'
$clsid='{4FE441C9-78D1-4CA3-8217-B78CE7B73EEE}'
$cp="HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
$com="HKLM:\SOFTWARE\Classes\CLSID\$clsid"
Remove-Item $cp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $com -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Credential Provider registration removed. Standard Windows password/PIN providers were not modified.'
