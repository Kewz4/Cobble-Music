<#
Builds a signed, chunked GitHub Release payload from a canonical Minecraft
client folder. It never reads Claude scratchpads or old .mrpack artifacts.
By default it only stages files locally; -Publish additionally creates the
GitHub Release after the maintainer confirms distribution rights.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [string]$SourceMinecraftDir = "C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon - Client 1.0.1\minecraft",
    [string]$Repository = 'Kewz4/Cobble-Music',
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE '.cobble-music\keys\cobble-music-release-private.key'),
    [string[]]$IncludeRoots = @('mods', 'resourcepacks', 'defaultconfigs', 'kubejs', 'scripts'),
    [string[]]$IncludeFiles = @(
        'config/cobble-music-bridge.json',
        'config/logbegone.json',
        'config/MCBrowser/tabs.json',
        'config/ReactiveMusic.json5',
        'config/musicnotification.json',
        'config/resourcepackoverrides.json'
    ),
    [string]$LegacyCleanupManifest,
    [int]$ChunkSizeMiB = 512,
    [switch]$Publish,
    [switch]$ConfirmDistributionRights
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$OutputRoot = Join-Path $Root "release-output\$Version"
$UpdaterProject = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$UpdaterDll = Join-Path $Root 'updater\CobbleMusicUpdater\bin\Release\net10.0-windows\win-x64\CobbleMusicUpdater.dll'

function Assert-Under([string]$Path, [string]$Base) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullBase = [IO.Path]::GetFullPath($Base).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $fullBase + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the release-output directory: $fullPath"
    }
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-RelativeSlashPath([string]$FullPath, [string]$Base) {
    $relative = [IO.Path]::GetRelativePath($Base, $FullPath).Replace('\', '/')
    if ($relative.StartsWith('../') -or $relative.Contains('/../')) { throw "Unsafe relative path: $relative" }
    return $relative
}

function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Read-LegacyCleanupManifest([string]$Path, [string[]]$CurrentPaths) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Legacy cleanup manifest was not found: $Path" }
    try { $entries = @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    catch { throw "Legacy cleanup manifest is not a JSON array: $Path" }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        $relative = [string]$entry.path
        $size = [int64]$entry.size
        $hash = ([string]$entry.sha256).ToLowerInvariant()
        if ($relative -notmatch '^(mods|resourcepacks|config|defaultconfigs|kubejs|scripts)/.+' -or $relative.Contains('..') -or $relative.Contains(':') -or $size -lt 0 -or $hash -notmatch '^[0-9a-f]{64}$') {
            throw "Legacy cleanup entry is unsafe or incomplete: $($entry | ConvertTo-Json -Compress)"
        }
        if (-not $seen.Add($relative) -or $CurrentPaths -contains $relative) {
            throw "Legacy cleanup entry is duplicate or overlaps a current managed file: $relative"
        }
        $result.Add([ordered]@{ path=$relative; size=$size; sha256=$hash })
    }
    return @($result)
}

function Split-ReleaseFile([string]$Source, [string]$DestinationRoot, [int64]$ChunkBytes) {
    $parts = [Collections.Generic.List[object]]::new()
    $buffer = New-Object byte[] (4MB)
    $input = [IO.File]::OpenRead($Source)
    try {
        $number = 0
        while ($input.Position -lt $input.Length) {
            $number++
            $partName = "cobble-music-payload.part$($number.ToString('000'))"
            $partPath = Join-Path $DestinationRoot $partName
            $output = [IO.File]::Create($partPath)
            try {
                [int64]$written = 0
                while ($written -lt $ChunkBytes -and ($read = $input.Read($buffer, 0, [int][Math]::Min($buffer.Length, $ChunkBytes - $written))) -gt 0) {
                    $output.Write($buffer, 0, $read)
                    $written += $read
                }
            }
            finally { $output.Dispose() }
            $parts.Add([ordered]@{ name=$partName; size=(Get-Item -LiteralPath $partPath).Length; sha256=(Get-Sha256 $partPath) })
        }
    }
    finally { $input.Dispose() }
    return @($parts)
}

if (-not (Test-Path -LiteralPath $SourceMinecraftDir -PathType Container)) { throw "Minecraft source directory not found: $SourceMinecraftDir" }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "Private signing key not found: $PrivateKeyPath" }
if ($ChunkSizeMiB -lt 1 -or $ChunkSizeMiB -gt 1536) { throw 'ChunkSizeMiB must be between 1 and 1536, safely under the GitHub Releases 2 GiB per-asset limit.' }
if (Test-Path -LiteralPath $OutputRoot) { throw "Refusing to overwrite existing release staging output: $OutputRoot" }
if ($Publish -and -not $ConfirmDistributionRights) {
    throw 'Publishing is blocked until you explicitly pass -ConfirmDistributionRights. Confirm that every payload file may be distributed through this public GitHub Release.'
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Assert-Under $OutputRoot (Join-Path $Root 'release-output')

Write-Host "Building authoritative file manifest from $SourceMinecraftDir"
$sourceFiles = [Collections.Generic.List[object]]::new()
foreach ($rootName in $IncludeRoots | Sort-Object -Unique) {
    if ($rootName -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid include root: $rootName" }
    $rootPath = Join-Path $SourceMinecraftDir $rootName
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Recurse) {
        $relative = Get-RelativeSlashPath $file.FullName $SourceMinecraftDir
        $sourceFiles.Add([pscustomobject]@{ full=$file.FullName; relative=$relative; size=$file.Length; sha256=(Get-Sha256 $file.FullName) })
    }
}
foreach ($relative in $IncludeFiles | Sort-Object -Unique) {
    $relative = $relative.Replace('\', '/')
    if ($relative -notmatch '^(mods|resourcepacks|config|defaultconfigs|kubejs|scripts)/.+') { throw "File is outside the updater allowlist: $relative" }
    $full = Join-Path $SourceMinecraftDir $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    if ($sourceFiles.relative -contains $relative) { continue }
    $item = Get-Item -LiteralPath $full
    $sourceFiles.Add([pscustomobject]@{ full=$item.FullName; relative=$relative; size=$item.Length; sha256=(Get-Sha256 $item.FullName) })
}
$sourceFiles = @($sourceFiles | Sort-Object relative -Unique)
if ($sourceFiles.Count -eq 0) { throw 'No approved modpack files were found to package.' }
$legacyCleanup = Read-LegacyCleanupManifest $LegacyCleanupManifest @($sourceFiles.relative)

$fileList = Join-Path $OutputRoot 'payload-files.txt'
Write-Utf8 $fileList (($sourceFiles.relative | ForEach-Object { $_.Replace('/', '\') }) -join [Environment]::NewLine)
$payloadPath = Join-Path $OutputRoot 'cobble-music-payload.zip'
$sevenZip = (Get-Command 7z -ErrorAction Stop).Source
Push-Location $SourceMinecraftDir
try {
    & $sevenZip a -tzip $payloadPath "@$fileList" '-mx=0' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$parts = Split-ReleaseFile $payloadPath $OutputRoot ([int64]$ChunkSizeMiB * 1MB)
$manifest = [ordered]@{
    schemaVersion = 1
    modpackId = 'cobble-music'
    channel = 'stable'
    version = $Version
    releaseTag = "modpack-v$Version"
    minimumUpdaterVersion = '1.0.0'
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    payload = [ordered]@{
        archiveName = [IO.Path]::GetFileName($payloadPath)
        size = (Get-Item -LiteralPath $payloadPath).Length
        sha256 = (Get-Sha256 $payloadPath)
        parts = @($parts)
    }
    files = @($sourceFiles | ForEach-Object { [ordered]@{ path=$_.relative; size=$_.size; sha256=$_.sha256 } })
    deletePaths = @()
    legacyCleanup = @($legacyCleanup)
}
$manifestPath = Join-Path $OutputRoot 'cobble-music-update.json'
Write-Utf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)

& dotnet restore $UpdaterProject --locked-mode | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Updater restore failed with exit code $LASTEXITCODE" }
& dotnet build $UpdaterProject --configuration Release --no-restore | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Updater build failed with exit code $LASTEXITCODE" }
$signaturePath = Join-Path $OutputRoot 'cobble-music-update.sig'
& dotnet $UpdaterDll --sign-manifest $manifestPath --private-key-file $PrivateKeyPath --signature-output $signaturePath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Manifest signing failed with exit code $LASTEXITCODE" }
& dotnet $UpdaterDll --verify-manifest $manifestPath --signature-file $signaturePath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Manifest self-verification failed with exit code $LASTEXITCODE" }

$notesPath = Join-Path $OutputRoot 'RELEASE_NOTES.md'
Write-Utf8 $notesPath @"
# Cobble Music $Version

This release is a signed update payload for the Cobble Music Prism updater.

- Files: $($sourceFiles.Count)
- Payload size: $((Get-Item -LiteralPath $payloadPath).Length) bytes
- Parts: $($parts.Count), each at most $ChunkSizeMiB MiB
- Source: canonical live client directory supplied to this command
"@

Write-Host "Staged signed release at $OutputRoot"
Write-Host "Payload files: $($sourceFiles.Count); chunks: $($parts.Count)"
if (-not $Publish) {
    Write-Host 'No GitHub mutation was made. Review the staged manifest, then rerun with -Publish -ConfirmDistributionRights when you authorize distribution.'
    exit 0
}

$releaseAssets = @($manifestPath, $signaturePath) + @($parts | ForEach-Object { Join-Path $OutputRoot $_.name })
& gh release create "modpack-v$Version" --repo $Repository --title "Cobble Music $Version" --notes-file $notesPath @releaseAssets | Out-Host
if ($LASTEXITCODE -ne 0) { throw "GitHub release creation failed with exit code $LASTEXITCODE" }
Write-Host "Published signed GitHub Release modpack-v$Version to $Repository"
