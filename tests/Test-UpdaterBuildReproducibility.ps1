[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Builder = Join-Path $Root 'tools\Build-CobbleMusicUpdater.ps1'
$GlobalJson = Join-Path $Root 'global.json'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-updater-repro-' + [Guid]::NewGuid().ToString('N'))
$Archive = Join-Path $TempRoot 'committed-updater-source.zip'
$SourceA = Join-Path $TempRoot 'source-a'
$SourceB = Join-Path $TempRoot 'substantially-longer-distinct-source-root-b'
$FirstBuildCopy = Join-Path $TempRoot 'root-a-first-build.exe'

function Invoke-ReproBuild([string]$SourceRoot) {
    $builderPath = Join-Path $SourceRoot 'tools\Build-CobbleMusicUpdater.ps1'
    $outputPath = Join-Path $SourceRoot 'repro-output'
    $output = @(& pwsh -NoProfile -File $builderPath -Runtime win-x64 -OutputDirectory $outputPath 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host ([string]$_) }
    if ($exitCode -ne 0) { throw "Reproducibility build failed under $SourceRoot with exit code $exitCode." }
    $exe = Join-Path $outputPath 'CobbleMusicUpdater.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Reproducibility build did not produce $exe" }
    return $exe
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

foreach ($path in @($Builder, $GlobalJson)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Reproducibility input is missing: $path" }
}
foreach ($command in @('git', 'dotnet', 'pwsh')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$command is required for updater reproducibility testing." }
}

$dirtyUpdaterSource = @(& git -C $Root status --porcelain=v1 -- updater/CobbleMusicUpdater)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect committed updater source state.' }
if ($dirtyUpdaterSource.Count -ne 0) {
    throw "Updater reproducibility must use a committed clean source tree:`n$($dirtyUpdaterSource -join [Environment]::NewLine)"
}

try {
    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    & git -C $Root archive --format=zip --output=$Archive HEAD -- updater/CobbleMusicUpdater
    if ($LASTEXITCODE -ne 0) { throw "git archive failed with exit code $LASTEXITCODE." }

    foreach ($sourceRoot in @($SourceA, $SourceB)) {
        New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
        Expand-Archive -LiteralPath $Archive -DestinationPath $sourceRoot
        New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'tools') -Force | Out-Null
        Copy-Item -LiteralPath $Builder -Destination (Join-Path $sourceRoot 'tools\Build-CobbleMusicUpdater.ps1')
        Copy-Item -LiteralPath $GlobalJson -Destination (Join-Path $sourceRoot 'global.json')
    }

    Write-Host 'Building updater in the first clean source root (cold build)...'
    $firstA = Invoke-ReproBuild $SourceA
    Copy-Item -LiteralPath $firstA -Destination $FirstBuildCopy
    $firstHash = (Get-FileHash -LiteralPath $FirstBuildCopy -Algorithm SHA256).Hash

    Write-Host 'Rebuilding updater in the same source root (warm/repeated build)...'
    $secondA = Invoke-ReproBuild $SourceA
    $secondHash = (Get-FileHash -LiteralPath $secondA -Algorithm SHA256).Hash
    Assert-ByteIdentical $FirstBuildCopy $secondA 'Repeated updater build'

    Write-Host 'Building updater in a distinct clean source root...'
    $firstB = Invoke-ReproBuild $SourceB
    $thirdHash = (Get-FileHash -LiteralPath $firstB -Algorithm SHA256).Hash
    Assert-ByteIdentical $FirstBuildCopy $firstB 'Cross-root updater build'

    if ($firstHash -cne $secondHash -or $firstHash -cne $thirdHash) {
        throw "Updater hashes differ despite byte comparison: $firstHash / $secondHash / $thirdHash"
    }
    Write-Host "Updater build reproducibility passed: two distinct clean roots and a repeated build are byte-identical ($firstHash)."
}
finally {
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force }
}
