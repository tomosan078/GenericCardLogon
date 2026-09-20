$ErrorActionPreference = 'Continue'
$name = 'GenericCardLogon'
Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
sc.exe delete $name | Out-Null
Write-Host 'GenericCardLogon Service removed.'
