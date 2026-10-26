param([switch]$Offline, [string]$OutputDirectory = '', [switch]$Archive)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Split-Path -Parent $projectRoot } else { [IO.Path]::GetFullPath($OutputDirectory) }
$stageRoot = Join-Path $projectRoot 'artifacts/publish'
if (Test-Path -LiteralPath $stageRoot) {
    $resolvedStage = (Resolve-Path -LiteralPath $stageRoot).Path
    if (-not $resolvedStage.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish staging directory is outside development.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
$restoreOptions = if ($Offline) { @('--source', $env:NUGET_PACKAGES, '--ignore-failed-sources') } else { @('--configfile', (Join-Path $projectRoot 'NuGet.Config')) }
$singleFileOptions = @('-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableSingleFileAnalyzer=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
foreach ($project in @('Desktop', 'Cli')) {
    $source = "src/ResearchTracker.$project/ResearchTracker.$project.csproj"
    dotnet restore $source -r win-x64 @restoreOptions -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "$project runtime restore failed." }
    dotnet publish $source -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $stageRoot $project) @singleFileOptions
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$desktopTarget = Join-Path $outputRoot 'WorkTracker.Desktop.exe'
$cliTarget = Join-Path $outputRoot 'work-tracker.exe'
foreach ($process in @(Get-Process WorkTracker.Desktop, work-tracker -ErrorAction SilentlyContinue)) {
    if ($process.Path -in @($desktopTarget, $cliTarget)) { throw 'Close Work Tracker and its CLI/MCP process before replacing the executables. Completed builds remain in development/artifacts/publish.' }
}
Copy-Item -LiteralPath (Join-Path $stageRoot 'Desktop/WorkTracker.Desktop.exe') -Destination $desktopTarget -Force
Copy-Item -LiteralPath (Join-Path $stageRoot 'Cli/work-tracker.exe') -Destination $cliTarget -Force
if ($Archive) { Compress-Archive -LiteralPath $desktopTarget, $cliTarget -DestinationPath (Join-Path $projectRoot 'artifacts/WorkTracker-windows-x64.zip') -Force }
Write-Host "Ready: $desktopTarget and $cliTarget"