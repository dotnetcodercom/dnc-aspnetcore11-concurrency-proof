param([int]$Port = 5128)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'App\Demo.csproj'
Push-Location (Join-Path $PSScriptRoot 'App')
try {
    & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Required .NET 11 RC1 SDK not found. See README-AR.md.' }
    & dotnet restore $project --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Run Verify-Demo.ps1 and send its log.' }
    & dotnet build $project --configuration Release --no-restore --disable-build-servers --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Run Verify-Demo.ps1 and send its log.' }
    Write-Host "Open http://127.0.0.1:$Port in your browser when BROWSER appears."
    & dotnet run --project $project --configuration Release --no-build -- --port $Port
    if ($LASTEXITCODE -ne 0) { throw 'Server stopped with an error.' }
} finally { Pop-Location }
