[CmdletBinding()]
param(
    [string]$SourceRepositoryRoot,

    [ValidatePattern('^[0-9a-fA-F]{40,64}$')]
    [string]$SourceCommit,

    # When the publisher invokes this test, this is the actual staged release
    # EXE. It must be identical to all independently exported commit builds.
    [string]$ExpectedExePath
)

$ErrorActionPreference = 'Stop'
$ScriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$SourceRepositoryRoot = if ([string]::IsNullOrWhiteSpace($SourceRepositoryRoot)) {
    $ScriptRoot
} else {
    [IO.Path]::GetFullPath($SourceRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-updater-repro-' + [Guid]::NewGuid().ToString('N'))
$SourceA = Join-Path $TempRoot 'source-a'
$SourceB = Join-Path $TempRoot 'substantially-longer-distinct-source-root-b'
$FirstBuildCopy = Join-Path $TempRoot 'root-a-first-build.exe'
$ForbiddenGlobalPackages = Join-Path $TempRoot 'forbidden-shared-global-packages'

function Invoke-SourceGit([string[]]$Arguments) {
    $output = @(& git -C $SourceRepositoryRoot @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git -C $SourceRepositoryRoot $($Arguments -join ' ') failed with exit code ${exitCode}: $([string]::Join([Environment]::NewLine, $output))"
    }
    return @($output)
}

function Assert-NoArchiveTransformAttributes {
    $treePaths = @(Invoke-SourceGit @('ls-tree', '-r', '--name-only', $SourceCommit, '--'))
    $attributePaths = @($treePaths | Where-Object { [IO.Path]::GetFileName([string]$_) -ceq '.gitattributes' })
    foreach ($attributePath in $attributePaths) {
        $attributeLines = @(Invoke-SourceGit @('show', "${SourceCommit}:$attributePath"))
        foreach ($line in $attributeLines) {
            $trimmed = ([string]$line).Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#', [StringComparison]::Ordinal)) { continue }
            if ($trimmed -match '(?i)(^|\s)export-(ignore|subst)(\s|$)') {
                throw "Exact source export forbids export-ignore/export-subst attributes: $attributePath"
            }
        }
    }
}

function Export-SourceCommit([string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Reproducibility source root already exists: $Destination" }
    Assert-NoArchiveTransformAttributes
    $archivePath = Join-Path $TempRoot ('exact-source-' + [Guid]::NewGuid().ToString('N') + '.zip')
    Invoke-SourceGit @('archive', '--format=zip', "--output=$archivePath", $SourceCommit) | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Git did not create a reproducibility source archive.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $Destination
    Remove-Item -LiteralPath $archivePath -Force

    foreach ($relative in @(
        'global.json',
        'NuGet.Config',
        'tools\Build-CobbleMusicUpdater.ps1',
        'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj',
        'updater\CobbleMusicUpdater\packages.lock.json'
    )) {
        $required = Join-Path $Destination $relative
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Exact commit export is missing reproducibility input: $required"
        }
    }
    $committedPackages = Join-Path $Destination 'updater\packages'
    if (Test-Path -LiteralPath $committedPackages) {
        throw "Exact commit contains a generated package cache instead of starting cold: $committedPackages"
    }
}

function Invoke-ReproBuild([string]$SourceRoot) {
    $builderPath = Join-Path $SourceRoot 'tools\Build-CobbleMusicUpdater.ps1'
    $outputPath = Join-Path $SourceRoot 'repro-output'
    $output = @(& pwsh -NoProfile -File $builderPath -Runtime win-x64 -OutputDirectory $outputPath 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host ([string]$_) }
    if ($exitCode -ne 0) { throw "Reproducibility build failed under $SourceRoot with exit code $exitCode." }
    $exe = Join-Path $outputPath 'CobbleMusicUpdater.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Reproducibility build did not produce $exe" }
    $packagesRoot = Join-Path $SourceRoot 'updater\packages'
    if (-not (Test-Path -LiteralPath $packagesRoot -PathType Container)) { throw "Build did not create its isolated package root: $packagesRoot" }
    $packageHashFiles = @(Get-ChildItem -LiteralPath $packagesRoot -Filter '*.nupkg.sha512' -File -Recurse)
    if ($packageHashFiles.Count -eq 0) { throw "Isolated package root contains no NuGet package hash proofs: $packagesRoot" }
    $assetsPath = Join-Path $SourceRoot 'updater\CobbleMusicUpdater\obj\project.assets.json'
    $assets = [IO.File]::ReadAllText($assetsPath) | ConvertFrom-Json -Depth 100
    $assetPackageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $expectedPackageRoot = [IO.Path]::GetFullPath($packagesRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($assetPackageRoots.Count -ne 1) { throw "Build assets reference $($assetPackageRoots.Count) package roots instead of one isolated root." }
    $actualPackageRoot = [IO.Path]::GetFullPath($assetPackageRoots[0]).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $actualPackageRoot.Equals($expectedPackageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build assets escaped the per-source package root: $actualPackageRoot"
    }
    return [pscustomobject]@{ Exe = $exe; PackagesRoot = $expectedPackageRoot }
}

function Assert-ByteIdentical([string]$Expected, [string]$Actual, [string]$Context) {
    $expectedItem = Get-Item -LiteralPath $Expected
    $actualItem = Get-Item -LiteralPath $Actual
    if ($expectedItem.Length -ne $actualItem.Length) {
        throw "$Context differs in size: $($expectedItem.Length) versus $($actualItem.Length)."
    }
    $left = [IO.File]::OpenRead($expectedItem.FullName)
    $right = [IO.File]::OpenRead($actualItem.FullName)
    try {
        $leftBuffer = New-Object byte[] 1MB
        $rightBuffer = New-Object byte[] 1MB
        [int64]$offset = 0
        while (($leftRead = $left.Read($leftBuffer, 0, $leftBuffer.Length)) -gt 0) {
            $rightRead = $right.Read($rightBuffer, 0, $rightBuffer.Length)
            if ($rightRead -ne $leftRead) { throw "$Context differs near byte offset $offset." }
            for ($index = 0; $index -lt $leftRead; $index++) {
                if ($leftBuffer[$index] -ne $rightBuffer[$index]) {
                    throw "$Context differs at byte offset $($offset + $index)."
                }
            }
            $offset += $leftRead
        }
        if ($right.ReadByte() -ne -1) { throw "$Context contains trailing bytes." }
    }
    finally {
        $left.Dispose()
        $right.Dispose()
    }
}

foreach ($command in @('git', 'dotnet', 'pwsh')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$command is required for updater reproducibility testing." }
}
$topLevel = @((Invoke-SourceGit @('rev-parse', '--show-toplevel')))
if ($topLevel.Count -ne 1) { throw 'Git did not return exactly one reproducibility repository root.' }
$resolvedTopLevel = [IO.Path]::GetFullPath(([string]$topLevel[0]).Trim()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $resolvedTopLevel.Equals($SourceRepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Reproducibility source root $SourceRepositoryRoot does not match Git root $resolvedTopLevel."
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    $commitOutput = @((Invoke-SourceGit @('rev-parse', '--verify', 'HEAD^{commit}')))
    if ($commitOutput.Count -ne 1) { throw 'Git did not return exactly one reproducibility source commit.' }
    $SourceCommit = ([string]$commitOutput[0]).Trim().ToLowerInvariant()
}
if ($SourceCommit -notmatch '^[0-9a-f]{40,64}$') { throw "Invalid reproducibility source commit: $SourceCommit" }
Invoke-SourceGit @('cat-file', '-e', "$SourceCommit^{commit}") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($ExpectedExePath)) {
    $ExpectedExePath = [IO.Path]::GetFullPath($ExpectedExePath)
    if (-not (Test-Path -LiteralPath $ExpectedExePath -PathType Leaf)) { throw "Expected release EXE is missing: $ExpectedExePath" }
}
$hadGlobalPackages = Test-Path Env:NUGET_PACKAGES
$savedGlobalPackages = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $ForbiddenGlobalPackages -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $ForbiddenGlobalPackages 'sentinel.txt'), 'must remain the only file', [Text.UTF8Encoding]::new($false))
    $env:NUGET_PACKAGES = $ForbiddenGlobalPackages
    Export-SourceCommit $SourceA
    Export-SourceCommit $SourceB

    Write-Host "Building updater commit $SourceCommit in the first clean source root (cold build)..."
    $firstA = Invoke-ReproBuild $SourceA
    Copy-Item -LiteralPath $firstA.Exe -Destination $FirstBuildCopy
    $firstHash = (Get-FileHash -LiteralPath $FirstBuildCopy -Algorithm SHA256).Hash
    if (-not [string]::IsNullOrWhiteSpace($ExpectedExePath)) {
        Assert-ByteIdentical $ExpectedExePath $FirstBuildCopy 'Exact staged release artifact versus clean commit build'
    }

    Write-Host 'Rebuilding updater in the same source root (warm/repeated build)...'
    $secondA = Invoke-ReproBuild $SourceA
    $secondHash = (Get-FileHash -LiteralPath $secondA.Exe -Algorithm SHA256).Hash
    Assert-ByteIdentical $FirstBuildCopy $secondA.Exe 'Repeated updater build'
    if ($firstA.PackagesRoot -cne $secondA.PackagesRoot) { throw 'Repeated build escaped its source-root-isolated package cache.' }

    Write-Host 'Building updater in a distinct clean source root...'
    $firstB = Invoke-ReproBuild $SourceB
    $thirdHash = (Get-FileHash -LiteralPath $firstB.Exe -Algorithm SHA256).Hash
    Assert-ByteIdentical $FirstBuildCopy $firstB.Exe 'Cross-root updater build'
    if ($firstA.PackagesRoot.Equals($firstB.PackagesRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Distinct source roots shared one mutable extracted-package cache.'
    }

    $globalPackageFiles = @(Get-ChildItem -LiteralPath $ForbiddenGlobalPackages -File -Recurse)
    if ($globalPackageFiles.Count -ne 1 -or $globalPackageFiles[0].Name -cne 'sentinel.txt') {
        throw "A build wrote to the forbidden shared NUGET_PACKAGES cache: $($globalPackageFiles.FullName -join ', ')"
    }

    if ($firstHash -cne $secondHash -or $firstHash -cne $thirdHash) {
        throw "Updater hashes differ despite byte comparison: $firstHash / $secondHash / $thirdHash"
    }
    $releaseProof = if ([string]::IsNullOrWhiteSpace($ExpectedExePath)) { '' } else { ', including the exact staged release artifact' }
    Write-Host "Updater build reproducibility passed: cold, warm, and two distinct exact-commit roots$releaseProof are byte-identical ($firstHash)."
}
finally {
    if ($hadGlobalPackages) { $env:NUGET_PACKAGES = $savedGlobalPackages }
    else { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force }
}
