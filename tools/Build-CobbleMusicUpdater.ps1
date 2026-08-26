[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # Used by the release publisher and reproducibility test. The default
    # remains the documented local dist directory.
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$Project = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$GlobalJson = Join-Path $Root 'global.json'
$Output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $Root "updater\dist\$Runtime"
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$StagingRoot = Join-Path $Root 'updater\publish-staging'
$Staging = Join-Path $StagingRoot ("$Runtime-" + [Guid]::NewGuid().ToString('N'))
$StagedExe = Join-Path $Staging 'CobbleMusicUpdater.exe'
$TargetExe = Join-Path $Output 'CobbleMusicUpdater.exe'
$TemporaryTarget = Join-Path $Output ('.CobbleMusicUpdater.exe.new-' + [Guid]::NewGuid().ToString('N'))
$VirtualSourceRoot = '/_/cobble-music'

if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Updater project is missing: $Project" }
if (-not (Test-Path -LiteralPath $GlobalJson -PathType Leaf)) { throw "Pinned SDK configuration is missing: $GlobalJson" }
if ($Root.IndexOfAny([char[]]@(',', '=')) -ge 0) {
    throw "The repository path contains a character that cannot be represented safely in MSBuild PathMap: $Root"
}
$PathMap = "$Root=$VirtualSourceRoot"
$sdkConfiguration = Get-Content -LiteralPath $GlobalJson -Raw | ConvertFrom-Json
$PinnedSdkVersion = [string]$sdkConfiguration.sdk.version
if ($PinnedSdkVersion -notmatch '^\d+\.\d+\.\d+$' `
    -or [string]$sdkConfiguration.sdk.rollForward -cne 'disable' `
    -or $sdkConfiguration.sdk.PSObject.Properties.Name -notcontains 'allowPrerelease' `
    -or [bool]$sdkConfiguration.sdk.allowPrerelease) {
    throw 'global.json must pin an exact three-part SDK version with rollForward=disable and allowPrerelease=false.'
}

try {
    New-Item -ItemType Directory -Path $Staging -Force | Out-Null
    Push-Location $Root
    try {
        $actualSdkVersion = (& dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE" }
        if ($actualSdkVersion -cne $PinnedSdkVersion) {
            throw "Pinned .NET SDK $PinnedSdkVersion is required, but dotnet selected $actualSdkVersion."
        }
        & dotnet restore $Project --locked-mode --runtime $Runtime
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
        & dotnet publish $Project `
            --configuration Release `
            --runtime $Runtime `
            --self-contained true `
            --no-restore `
            --output $Staging `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            -p:Deterministic=true `
            -p:DeterministicSourcePaths=true `
            -p:ContinuousIntegrationBuild=true `
            -p:IncludeSourceRevisionInInformationalVersion=false `
            -p:EnableSourceLink=false `
            -p:EnableSourceControlManagerQueries=false `
            -p:EmbedUntrackedSources=false `
            "-p:PathMap=$PathMap"
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
    if (-not (Test-Path -LiteralPath $StagedExe -PathType Leaf)) { throw "dotnet publish did not create $StagedExe" }

    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    # Never publish directly over an existing EXE: the single-file bundler can
    # reopen it, and a failed copy must leave the previous updater intact.
    [IO.File]::Copy($StagedExe, $TemporaryTarget, $true)
    [IO.File]::Move($TemporaryTarget, $TargetExe, $true)
    Write-Host "Built self-contained updater: $TargetExe"
}
finally {
    if (Test-Path -LiteralPath $TemporaryTarget) { Remove-Item -LiteralPath $TemporaryTarget -Force }
    if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
}
