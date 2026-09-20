$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\x64\Release\GenericCardLogon.Service.exe'
if (!(Test-Path $exe)) { throw "Release service EXE not found: $exe" }
$name = 'GenericCardLogon'
$display = 'GenericCardLogon Service'
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue; sc.exe delete $name | Out-Null; Start-Sleep -Milliseconds 500 }
$binPath = '"' + $exe + '"'
sc.exe create $name binPath= $binPath start= auto obj= LocalSystem DisplayName= $display | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed: $LASTEXITCODE" }
sc.exe description $name 'GenericCardLogon RC-S380 FeliCa authentication service.' | Out-Null
Start-Service -Name $name
Write-Host 'Service installed and started.'
