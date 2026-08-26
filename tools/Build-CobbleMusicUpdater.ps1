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
$LockFile = Join-Path $Root 'updater\CobbleMusicUpdater\packages.lock.json'
$GlobalJson = Join-Path $Root 'global.json'
$NuGetConfig = Join-Path $Root 'NuGet.Config'
$Packages = Join-Path $Root 'updater\packages'
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
if (-not (Test-Path -LiteralPath $LockFile -PathType Leaf)) { throw "Updater dependency lock is missing: $LockFile" }
if (-not (Test-Path -LiteralPath $GlobalJson -PathType Leaf)) { throw "Pinned SDK configuration is missing: $GlobalJson" }
if (-not (Test-Path -LiteralPath $NuGetConfig -PathType Leaf)) { throw "Pinned NuGet configuration is missing: $NuGetConfig" }
if ($Root.IndexOfAny([char[]]@(',', '=')) -ge 0) {
    throw "The repository path contains a character that cannot be represented safely in MSBuild PathMap: $Root"
}
$PathMap = "$Root=$VirtualSourceRoot"

[xml]$nugetConfiguration = [IO.File]::ReadAllText($NuGetConfig)
$packageSources = @($nugetConfiguration.SelectNodes('/configuration/packageSources/*'))
if ($packageSources.Count -ne 2 -or $packageSources[0].Name -cne 'clear' `
    -or $packageSources[1].Name -cne 'add' -or [string]$packageSources[1].key -cne 'nuget.org' `
    -or [string]$packageSources[1].value -cne 'https://api.nuget.org/v3/index.json' `
    -or [string]$packageSources[1].protocolVersion -cne '3') {
    throw 'NuGet.Config must clear inherited feeds and declare only the reviewed nuget.org v3 package source.'
}
$dependencyLock = [IO.File]::ReadAllText($LockFile) | ConvertFrom-Json -Depth 100
if ([int]$dependencyLock.version -lt 1) { throw 'Updater dependency lock has an invalid format version.' }
$lockedPackages = @($dependencyLock.dependencies.PSObject.Properties | ForEach-Object { $_.Value.PSObject.Properties } | ForEach-Object { $_.Value })
if ($lockedPackages.Count -eq 0) { throw 'Updater dependency lock contains no packages.' }
foreach ($lockedPackage in $lockedPackages) {
    if ([string]::IsNullOrWhiteSpace([string]$lockedPackage.resolved) -or [string]::IsNullOrWhiteSpace([string]$lockedPackage.contentHash)) {
        throw 'Every updater dependency must have an exact resolved version and content hash in packages.lock.json.'
    }
}

# These files are discovered implicitly by MSBuild/NuGet while walking parent
# directories. They are forbidden rather than silently omitted from the
# signed source identity. Package feeds are supplied only by NuGet.Config.
$implicitNames = @(
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'Directory.Build.rsp',
    'MSBuild.rsp',
    ([IO.Path]::GetFileName($Project) + '.user')
)
$searchDirectory = Split-Path -Parent $Project
while (-not [string]::IsNullOrWhiteSpace($searchDirectory)) {
    foreach ($name in $implicitNames) {
        $candidate = Join-Path $searchDirectory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            throw "Implicit build input is forbidden; move reviewed settings into the committed project or explicit build command: $candidate"
        }
    }
    $parent = Split-Path -Parent $searchDirectory
    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $searchDirectory) { break }
    $searchDirectory = $parent
}

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
        & dotnet restore $Project --locked-mode --runtime $Runtime --configfile $NuGetConfig `
            --packages $Packages `
            --no-http-cache `
            -p:RestoreLockedMode=true `
            -p:RestorePackagesWithLockFile=true `
            -p:ImportDirectoryBuildProps=false `
            -p:ImportDirectoryBuildTargets=false `
            -p:ManagePackageVersionsCentrally=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
        $assetsPath = Join-Path (Split-Path -Parent $Project) 'obj\project.assets.json'
        if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw "NuGet restore did not create $assetsPath" }
        $assets = [IO.File]::ReadAllText($assetsPath) | ConvertFrom-Json -Depth 100
        $actualPackageFolders = @($assets.packageFolders.PSObject.Properties.Name)
        $expectedPackageFolder = [IO.Path]::GetFullPath($Packages).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        if ($actualPackageFolders.Count -ne 1) { throw "NuGet restore used $($actualPackageFolders.Count) extracted package roots instead of exactly one isolated root." }
        $actualPackageFolder = [IO.Path]::GetFullPath($actualPackageFolders[0]).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        if (-not $actualPackageFolder.Equals($expectedPackageFolder, [StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet restore escaped the isolated package root: expected $expectedPackageFolder, found $actualPackageFolder"
        }
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
            "-p:RestorePackagesPath=$Packages" `
            -p:RestoreLockedMode=true `
            -p:RestorePackagesWithLockFile=true `
            -p:ImportDirectoryBuildProps=false `
            -p:ImportDirectoryBuildTargets=false `
            -p:ManagePackageVersionsCentrally=false `
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
