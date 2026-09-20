$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "GenericCardLogon-RC-S380.sln"
$installerSolution = Join-Path $root "GCL-Installer.sln"

$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (Test-Path $vswhere) {
    $msbuild = & $vswhere `
        -latest `
        -products * `
        -requires Microsoft.Component.MSBuild `
        -find MSBuild\**\Bin\MSBuild.exe |
        Select-Object -First 1
}

if (-not $msbuild) {
    $msbuild = "msbuild.exe"
}

Write-Host "1/2 Building GenericCardLogon core projects Release x64..."
Write-Host "MSBuild: $msbuild"

# IMPORTANT:
# Restore must happen before Rebuild because the SDK-style .NET Framework
# projects require project.assets.json.
#
# /restore makes MSBuild perform NuGet restore first, using the same
# solution/configuration/platform as the actual build.
& $msbuild $solution `
    /restore `
    /m `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=x64

if ($LASTEXITCODE -ne 0) {
    throw "Main solution build failed."
}

$payloads = @(
    (Join-Path $root "bin\AnyCPU\Release\net48\GenericCardLogon.Core.dll"),
    (Join-Path $root "bin\AnyCPU\Release\net48\GenericCardLogon.Service.exe"),
    (Join-Path $root "bin\AnyCPU\Release\net48\GenericCardLogon.Service.exe.config"),
    (Join-Path $root "bin\AnyCPU\Release\net48\PCSC.dll"),
    (Join-Path $root "bin\AnyCPU\Release\net48\PCSC.Iso7816.dll"),
    (Join-Path $root "bin\AnyCPU\Release\net48\GenericCardLogon.Manager.exe"),
    (Join-Path $root "bin\AnyCPU\Release\net48\GenericCardLogon.Manager.exe.config"),
    (Join-Path $root "bin\x64\Release\GenericCardLogon.CredentialProvider.dll")
)

foreach ($payload in $payloads) {
    if (-not (Test-Path $payload)) {
        throw "Required payload is missing: $payload"
    }
}

Write-Host "2/2 Building GCL-Installer Release x64..."

# Installer solution also restores its dependencies.
& $msbuild $installerSolution `
    /restore `
    /m `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=x64

if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed."
}

$out = Join-Path `
    $root `
    "src\GenericCardLogon.Installer\bin\x64\Release\net48\GCL-Installer.exe"

if (-not (Test-Path $out)) {
    throw "Installer EXE was not produced: $out"
}

Write-Host ""
Write-Host "GCL-Installer build complete."
Write-Host "Output: $out"
