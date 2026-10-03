param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'App\Demo.csproj'
$logDir = Join-Path $PSScriptRoot ('evidence\console-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
Start-Transcript -Path (Join-Path $logDir 'console.txt') -Force | Out-Null
try {
    Push-Location (Join-Path $PSScriptRoot 'App')
    Write-Host '=== DNC local verification ==='
    & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Required .NET 11 RC1 SDK not found. See README-AR.md.' }
    & dotnet restore $project --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed; preserve this log.' }
    & dotnet build $project --configuration Release --no-restore --disable-build-servers --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; preserve this log.' }
    & dotnet run --project $project --configuration Release --no-build -- --verify
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed. Send receipt.json and this console log.' }
    Write-Host 'PASS: verification completed. Receipt is under evidence.'
} finally {
    Pop-Location
    Stop-Transcript | Out-Null
}
