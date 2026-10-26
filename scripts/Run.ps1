param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$restoreOptions = if ($Offline) { @('--source', $env:NUGET_PACKAGES, '--ignore-failed-sources') } else { @('--configfile', 'NuGet.Config') }
dotnet restore src/ResearchTracker.Desktop/ResearchTracker.Desktop.csproj @restoreOptions
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
dotnet run --project src/ResearchTracker.Desktop -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Application could not start.' }
