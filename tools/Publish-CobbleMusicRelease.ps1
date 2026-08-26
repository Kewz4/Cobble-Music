<#
Builds a signed, chunked GitHub Release payload from a canonical Minecraft
client folder. Full baselines use schema v1. Releases based on a verified,
signed prior manifest use schema v2 and carry only changed/new files plus exact
deletion metadata. Publishing always uses a persistent draft and validates the
remote asset inventory before making the release public.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [string]$SourceMinecraftDir = "C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon - Client 1.0.1\minecraft",
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
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

    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$BaseVersion,
    [string]$BaseManifestPath,
    [string]$BaseSignaturePath,
    [switch]$FullBaseline,

    [int]$ChunkSizeMiB = 256,
    [switch]$Publish,
    [switch]$ResumePublish,
    [switch]$ConfirmDistributionRights
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$OutputRoot = Join-Path $Root "release-output\$Version"
$ReleaseOutputRoot = Join-Path $Root 'release-output'
$CoreModule = Join-Path $PSScriptRoot 'CobbleMusicRelease.Core.psm1'
$UpdaterProject = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$UpdaterDll = Join-Path $Root 'updater\CobbleMusicUpdater\bin\Release\net10.0-windows\win-x64\CobbleMusicUpdater.dll'
$AllowedRoots = @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')

Import-Module $CoreModule -Force

function Assert-Under([string]$Path, [string]$Base) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullBase = [IO.Path]::GetFullPath($Base).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $fullBase + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to access outside the expected directory: $fullPath"
    }
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-RelativeSlashPath([string]$FullPath, [string]$Base) {
    $relative = [IO.Path]::GetRelativePath($Base, $FullPath).Replace('\', '/')
    Assert-CobbleManagedPath -Path $relative -Context 'source file' | Out-Null
    return $relative
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-NativeText {
    param([Parameter(Mandatory)][string]$Command, [Parameter(Mandatory)][string[]]$Arguments)

    $lines = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $result = ($lines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw "$Command failed with exit code $exitCode.`n$result"
    }
    return $result
}

function Invoke-NativeHost {
    param([Parameter(Mandatory)][string]$Command, [Parameter(Mandatory)][string[]]$Arguments)

    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $json = Invoke-NativeText -Command 'gh' -Arguments $Arguments
    try { return $json | ConvertFrom-Json }
    catch { throw "GitHub CLI returned invalid JSON for: gh $($Arguments -join ' ')" }
}

function Build-UpdaterSigningTool {
    & dotnet restore $UpdaterProject --locked-mode | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater restore failed with exit code $LASTEXITCODE" }
    & dotnet build $UpdaterProject --configuration Release --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater build failed with exit code $LASTEXITCODE" }
}

function Test-SignedManifest([string]$ManifestPath, [string]$SignaturePath) {
    & dotnet $UpdaterDll --verify-manifest $ManifestPath --signature-file $SignaturePath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Manifest signature verification failed: $ManifestPath" }
}

function Read-JsonFile([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Description was not found: $Path" }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "$Description is not valid JSON: $Path" }
}

function Read-LegacyCleanupManifest([string]$Path, [string[]]$ForbiddenPaths) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    $entries = @(Read-JsonFile -Path $Path -Description 'Legacy cleanup manifest')
    $set = ConvertTo-CobbleFileRecordSet -Entries $entries -Context 'legacy cleanup manifest' -AllowEmpty
    $forbidden = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $ForbiddenPaths) { [void]$forbidden.Add($item.Normalize([Text.NormalizationForm]::FormC)) }
    foreach ($entry in $set.Entries) {
        if ($forbidden.Contains($entry.path.Normalize([Text.NormalizationForm]::FormC))) {
            throw "Legacy cleanup overlaps a current or signed-base managed file: $($entry.path)"
        }
    }
    return @($set.Entries | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
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
                while ($written -lt $ChunkBytes) {
                    $wanted = [int][Math]::Min($buffer.Length, $ChunkBytes - $written)
                    $read = $input.Read($buffer, 0, $wanted)
                    if ($read -le 0) { break }
                    $output.Write($buffer, 0, $read)
                    $written += $read
                }
            }
            finally { $output.Dispose() }
            $parts.Add([ordered]@{ name = $partName; size = (Get-Item -LiteralPath $partPath).Length; sha256 = (Get-Sha256 $partPath) })
        }
    }
    finally { $input.Dispose() }
    return @($parts)
}

function New-PayloadParts([object[]]$PayloadFiles) {
    if ($PayloadFiles.Count -eq 0) {
        return [pscustomobject]@{ Payload = $null; Parts = @(); Size = 0 }
    }

    $fileList = Join-Path $OutputRoot 'payload-files.txt'
    Write-Utf8 $fileList (($PayloadFiles.path | ForEach-Object { $_.Replace('/', '\') }) -join [Environment]::NewLine)
    $payloadPath = Join-Path $OutputRoot 'cobble-music-payload.zip'
    Assert-Under $payloadPath $OutputRoot
    $sevenZip = (Get-Command 7z -ErrorAction Stop).Source
    Push-Location $SourceMinecraftDir
    try {
        & $sevenZip a -tzip $payloadPath "@$fileList" '-mx=0' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    $payloadSize = (Get-Item -LiteralPath $payloadPath).Length
    $payloadHash = Get-Sha256 $payloadPath
    try {
        $parts = Split-ReleaseFile $payloadPath $OutputRoot ([int64]$ChunkSizeMiB * 1MB)
    }
    finally {
        # Only the independently hashed parts are retained. This saves one full
        # archive worth of disk space and prevents it from becoming an asset.
        if (Test-Path -LiteralPath $payloadPath -PathType Leaf) {
            Assert-Under $payloadPath $OutputRoot
            Remove-Item -LiteralPath $payloadPath -Force
        }
    }
    if ($parts.Count -eq 0) { throw 'A non-empty payload produced no release parts.' }

    return [pscustomobject]@{
        Payload = [ordered]@{
            archiveName = 'cobble-music-payload.zip'
            size = $payloadSize
            sha256 = $payloadHash
            parts = @($parts)
        }
        Parts = @($parts)
        Size = $payloadSize
    }
}

function Get-GitHubReleaseByTag([string]$Tag) {
    $matches = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $result = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/releases?per_page=100&page=$page"))
        foreach ($release in $result) {
            if ([string]$release.tag_name -ceq $Tag) { $matches.Add($release) }
        }
        if ($result.Count -lt 100) { break }
    }
    if ($matches.Count -gt 1) { throw "GitHub contains multiple releases for reserved tag $Tag." }
    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Get-GitHubReleaseAssets([int64]$ReleaseId) {
    $assets = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $result = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/releases/$ReleaseId/assets?per_page=100&page=$page"))
        foreach ($asset in $result) { $assets.Add($asset) }
        if ($result.Count -lt 100) { break }
    }
    return @($assets)
}

function Resolve-BaseArtifacts([string]$RequestedVersion, [string]$ManifestPath, [string]$SignaturePath) {
    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) { throw 'A delta requires -BaseVersion.' }
    $hasManifest = -not [string]::IsNullOrWhiteSpace($ManifestPath)
    $hasSignature = -not [string]::IsNullOrWhiteSpace($SignaturePath)
    if ($hasManifest -xor $hasSignature) { throw 'Specify both -BaseManifestPath and -BaseSignaturePath, or neither.' }

    if ($hasManifest) {
        if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Base manifest was not found: $ManifestPath" }
        if (-not (Test-Path -LiteralPath $SignaturePath -PathType Leaf)) { throw "Base signature was not found: $SignaturePath" }
        return [pscustomobject]@{ ManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path; SignaturePath = (Resolve-Path -LiteralPath $SignaturePath).Path; TempRoot = $null }
    }

    $tag = "modpack-v$RequestedVersion"
    $release = Get-GitHubReleaseByTag $tag
    if ($null -eq $release -or [bool]$release.draft -or [bool]$release.prerelease) {
        throw "Published stable signed base release was not found: $tag"
    }
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-music-base-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    try {
        Invoke-NativeText -Command 'gh' -Arguments @(
            'release', 'download', $tag, '--repo', $Repository,
            '--pattern', 'cobble-music-update.json', '--pattern', 'cobble-music-update.sig',
            '--dir', $tempRoot
        ) | Out-Null
        $downloadedManifest = Join-Path $tempRoot 'cobble-music-update.json'
        $downloadedSignature = Join-Path $tempRoot 'cobble-music-update.sig'
        if (-not (Test-Path -LiteralPath $downloadedManifest -PathType Leaf) -or -not (Test-Path -LiteralPath $downloadedSignature -PathType Leaf)) {
            throw "Base release $tag is missing the signed manifest assets."
        }
        return [pscustomobject]@{ ManifestPath = $downloadedManifest; SignaturePath = $downloadedSignature; TempRoot = $tempRoot }
    }
    catch {
        Assert-Under $tempRoot ([IO.Path]::GetTempPath())
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
        throw
    }
}

function Remove-BaseTemp([object]$BaseArtifacts) {
    if ($null -ne $BaseArtifacts -and -not [string]::IsNullOrWhiteSpace([string]$BaseArtifacts.TempRoot) -and (Test-Path -LiteralPath $BaseArtifacts.TempRoot)) {
        Assert-Under $BaseArtifacts.TempRoot ([IO.Path]::GetTempPath())
        Remove-Item -LiteralPath $BaseArtifacts.TempRoot -Recurse -Force
    }
}

function Get-ExpectedStagedAssets([object]$Manifest, [string]$ManifestPath, [string]$SignaturePath) {
    $expected = [Collections.Generic.List[object]]::new()
    foreach ($path in @($ManifestPath, $SignaturePath)) {
        $item = Get-Item -LiteralPath $path
        $expected.Add([pscustomobject]@{ name = $item.Name; path = $item.FullName; size = $item.Length; sha256 = (Get-Sha256 $item.FullName) })
    }

    $parts = if ($null -eq $Manifest.payload) { @() } else { @($Manifest.payload.parts) }
    $partNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [int64]$totalSize = 0
    foreach ($part in $parts) {
        $name = [string]$part.name
        if ($name -cnotmatch '^cobble-music-payload\.part\d{3,}$' -or -not $partNames.Add($name)) {
            throw "Manifest payload part is unsafe or duplicate: $name"
        }
        $partPath = Join-Path $OutputRoot $name
        Assert-Under $partPath $OutputRoot
        if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) { throw "Staged payload part is missing: $name" }
        $item = Get-Item -LiteralPath $partPath
        $hash = Get-Sha256 $partPath
        if ($item.Length -ne [int64]$part.size -or $hash -cne [string]$part.sha256) {
            throw "Staged payload part does not match its signed manifest: $name"
        }
        $totalSize += $item.Length
        $expected.Add([pscustomobject]@{ name = $name; path = $item.FullName; size = $item.Length; sha256 = $hash })
    }

    if ($null -ne $Manifest.payload) {
        if ($parts.Count -eq 0 -or $totalSize -ne [int64]$Manifest.payload.size -or [string]$Manifest.payload.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Signed payload metadata and staged part sizes are inconsistent.'
        }
    }
    return @($expected)
}

function Publish-StagedRelease([object]$Manifest, [string]$ManifestPath, [string]$SignaturePath, [string]$NotesPath) {
    $tag = "modpack-v$Version"
    $expected = @(Get-ExpectedStagedAssets $Manifest $ManifestPath $SignaturePath)
    $release = Get-GitHubReleaseByTag $tag
    if ($null -eq $release) {
        Write-Host "Creating persistent draft $tag (no assets uploaded yet)."
        Invoke-NativeText -Command 'gh' -Arguments @(
            'release', 'create', $tag, '--repo', $Repository,
            '--title', "Cobble Music $Version", '--notes-file', $NotesPath, '--draft'
        ) | Out-Host
        $release = Get-GitHubReleaseByTag $tag
        if ($null -eq $release -or -not [bool]$release.draft) { throw "GitHub did not create the expected draft release: $tag" }
    }

    if ([bool]$release.prerelease) { throw "Reserved release $tag is unexpectedly marked prerelease." }
    $assets = @(Get-GitHubReleaseAssets ([int64]$release.id))
    if (-not [bool]$release.draft) {
        Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets $assets -RequireComplete | Out-Null
        Write-Host "Release $tag is already public and exactly matches signed staging."
        return
    }

    $missing = @(Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets $assets)
    if ($missing.Count -gt 0) {
        $expectedByName = @{}
        foreach ($asset in $expected) { $expectedByName[$asset.name] = $asset }
        $uploadPaths = @($missing | ForEach-Object { $expectedByName[$_].path })
        Write-Host "Uploading $($uploadPaths.Count) missing asset(s) to persistent draft $tag. Completed matching assets will be reused on -ResumePublish."
        try { Invoke-NativeHost -Command 'gh' -Arguments (@('release', 'upload', $tag, '--repo', $Repository) + $uploadPaths) }
        catch { throw "$($_.Exception.Message)`nThe draft was preserved. After any active GitHub upload settles, rerun this version with -ResumePublish -ConfirmDistributionRights." }
    }

    $assets = @(Get-GitHubReleaseAssets ([int64]$release.id))
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets $assets -RequireComplete | Out-Null
    Write-Host 'Remote asset names, states, sizes, and GitHub SHA-256 digests exactly match signed staging.'

    Invoke-GhJson -Arguments @('api', '--method', 'PATCH', "repos/$Repository/releases/$($release.id)", '-F', 'draft=false') | Out-Null
    $published = Get-GitHubReleaseByTag $tag
    if ($null -eq $published -or [bool]$published.draft) { throw "GitHub did not publish expected release $tag." }
    $publishedAssets = @(Get-GitHubReleaseAssets ([int64]$published.id))
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets $publishedAssets -RequireComplete | Out-Null
    Write-Host "Published signed GitHub Release $tag to $Repository"
}

function Read-And-ValidateStagedManifest {
    $manifestPath = Join-Path $OutputRoot 'cobble-music-update.json'
    $signaturePath = Join-Path $OutputRoot 'cobble-music-update.sig'
    $notesPath = Join-Path $OutputRoot 'RELEASE_NOTES.md'
    foreach ($path in @($manifestPath, $signaturePath, $notesPath)) {
        Assert-Under $path $OutputRoot
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Resume staging file is missing: $path" }
    }
    Test-SignedManifest $manifestPath $signaturePath
    $manifest = Read-JsonFile $manifestPath 'Staged signed manifest'
    if ([string]$manifest.version -cne $Version -or [string]$manifest.releaseTag -cne "modpack-v$Version") {
        throw 'Staged signed manifest does not match the requested release version.'
    }
    ConvertTo-CobbleFileRecordSet -Entries @($manifest.files) -Context 'staged authoritative files' | Out-Null

    $baseArtifacts = $null
    try {
        if ([int]$manifest.schemaVersion -eq 2) {
            $baseArtifacts = Resolve-BaseArtifacts -RequestedVersion ([string]$manifest.base.version) -ManifestPath $BaseManifestPath -SignaturePath $BaseSignaturePath
            Test-SignedManifest $baseArtifacts.ManifestPath $baseArtifacts.SignaturePath
            $baseHash = Get-Sha256 $baseArtifacts.ManifestPath
            $baseManifest = Read-JsonFile $baseArtifacts.ManifestPath 'Signed base manifest'
            Assert-CobbleBaseManifest -Manifest $baseManifest -ExpectedVersion ([string]$manifest.base.version) -TargetVersion $Version | Out-Null
            Assert-CobbleDeltaManifest -Manifest $manifest -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash | Out-Null
        }
        elseif ([int]$manifest.schemaVersion -eq 1) {
            Assert-CobbleV1Manifest -Manifest $manifest | Out-Null
        }
        else {
            throw "Unsupported staged manifest schema: $($manifest.schemaVersion)"
        }
    }
    finally { Remove-BaseTemp $baseArtifacts }

    Get-ExpectedStagedAssets $manifest $manifestPath $signaturePath | Out-Null
    return [pscustomobject]@{ Manifest = $manifest; ManifestPath = $manifestPath; SignaturePath = $signaturePath; NotesPath = $notesPath }
}

if ($ChunkSizeMiB -lt 1 -or $ChunkSizeMiB -gt 1536) {
    throw 'ChunkSizeMiB must be between 1 and 1536, safely under the GitHub Releases 2 GiB per-asset limit.'
}
if ($Publish -and $ResumePublish) { throw 'Use either -Publish for a fresh staging build or -ResumePublish for existing signed staging, not both.' }
if (($Publish -or $ResumePublish) -and -not $ConfirmDistributionRights) {
    throw 'Publishing is blocked until you explicitly pass -ConfirmDistributionRights. Confirm that every payload file may be distributed through this public GitHub Release.'
}

if ($ResumePublish) {
    if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) { throw "Release staging output does not exist: $OutputRoot" }
    Assert-Under $OutputRoot $ReleaseOutputRoot
    Build-UpdaterSigningTool
    $staged = Read-And-ValidateStagedManifest
    Publish-StagedRelease $staged.Manifest $staged.ManifestPath $staged.SignaturePath $staged.NotesPath
    exit 0
}

if ($FullBaseline -and -not [string]::IsNullOrWhiteSpace($BaseVersion)) { throw 'Choose either -FullBaseline (schema v1) or -BaseVersion (schema v2), not both.' }
if (-not $FullBaseline -and [string]::IsNullOrWhiteSpace($BaseVersion)) {
    throw 'Choose an explicit release mode: use -BaseVersion <published version> for a small signed delta, or -FullBaseline for a complete schema-v1 baseline.'
}
if ([string]::IsNullOrWhiteSpace($BaseVersion) -and (-not [string]::IsNullOrWhiteSpace($BaseManifestPath) -or -not [string]::IsNullOrWhiteSpace($BaseSignaturePath))) {
    throw '-BaseManifestPath/-BaseSignaturePath require -BaseVersion.'
}
if (-not (Test-Path -LiteralPath $SourceMinecraftDir -PathType Container)) { throw "Minecraft source directory not found: $SourceMinecraftDir" }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "Private signing key not found: $PrivateKeyPath" }
if (Test-Path -LiteralPath $OutputRoot) { throw "Refusing to overwrite existing release staging output: $OutputRoot" }

Build-UpdaterSigningTool

$baseArtifacts = $null
try {
    $baseManifest = $null
    $baseHash = $null
    $baseFiles = @()
    if (-not $FullBaseline) {
        $baseArtifacts = Resolve-BaseArtifacts -RequestedVersion $BaseVersion -ManifestPath $BaseManifestPath -SignaturePath $BaseSignaturePath
        Test-SignedManifest $baseArtifacts.ManifestPath $baseArtifacts.SignaturePath
        $baseHash = Get-Sha256 $baseArtifacts.ManifestPath
        $baseManifest = Read-JsonFile $baseArtifacts.ManifestPath 'Signed base manifest'
        $baseSet = Assert-CobbleBaseManifest -Manifest $baseManifest -ExpectedVersion $BaseVersion -TargetVersion $Version
        $baseFiles = @($baseSet.Entries)
        Write-Host "Verified signed base $BaseVersion ($baseHash)."
    }

    Write-Host "Building authoritative file manifest from $SourceMinecraftDir"
    $sourceFiles = [Collections.Generic.List[object]]::new()
    foreach ($rootName in $IncludeRoots | Sort-Object -Unique) {
        if ($AllowedRoots -cnotcontains $rootName) { throw "Include root is outside the updater allowlist: $rootName" }
        $rootPath = Join-Path $SourceMinecraftDir $rootName
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
        $rootItem = Get-Item -LiteralPath $rootPath
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Include root may not be a reparse point: $rootPath" }
        foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Recurse) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Managed source may not be a reparse point: $($file.FullName)" }
            $relative = Get-RelativeSlashPath $file.FullName $SourceMinecraftDir
            $sourceFiles.Add([pscustomobject]@{ full = $file.FullName; path = $relative; size = $file.Length; sha256 = (Get-Sha256 $file.FullName) })
        }
    }
    foreach ($relativeInput in $IncludeFiles | Sort-Object -Unique) {
        $relative = $relativeInput.Replace('\', '/')
        Assert-CobbleManagedPath -Path $relative -Context 'included file' | Out-Null
        $full = Join-Path $SourceMinecraftDir $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        if (@($sourceFiles | ForEach-Object { $_.path }) -ccontains $relative) { continue }
        $item = Get-Item -LiteralPath $full
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Managed source may not be a reparse point: $($item.FullName)" }
        $sourceFiles.Add([pscustomobject]@{ full = $item.FullName; path = $relative; size = $item.Length; sha256 = (Get-Sha256 $item.FullName) })
    }

    $currentSet = ConvertTo-CobbleFileRecordSet -Entries @($sourceFiles) -Context 'current authoritative files'
    $sourcePathByKey = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $sourceFiles) { $sourcePathByKey.Add($source.path.Normalize([Text.NormalizationForm]::FormC), $source.full) }

    if ($FullBaseline) {
        $authoritativeFiles = @($currentSet.Entries)
        $payloadFiles = @($authoritativeFiles)
        $deletedFiles = @()
    }
    else {
        $deltaPlan = New-CobbleDeltaPlan -CurrentFiles @($currentSet.Entries) -BaseFiles $baseFiles
        $authoritativeFiles = @($deltaPlan.Files)
        $payloadFiles = @($deltaPlan.PayloadFiles)
        $deletedFiles = @($deltaPlan.DeletedFiles)
        Write-Host "Delta plan: $($payloadFiles.Count) changed/new, $($deletedFiles.Count) deleted, $($deltaPlan.UnchangedFiles.Count) unchanged."
    }

    foreach ($payloadFile in $payloadFiles) {
        $key = $payloadFile.path.Normalize([Text.NormalizationForm]::FormC)
        if (-not $sourcePathByKey.ContainsKey($key)) { throw "Payload file is not backed by a hashed canonical source: $($payloadFile.path)" }
    }

    $forbiddenCleanupPaths = @($authoritativeFiles | ForEach-Object { $_.path }) + @($baseFiles | ForEach-Object { $_.path })
    $legacyCleanup = Read-LegacyCleanupManifest $LegacyCleanupManifest $forbiddenCleanupPaths
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    Assert-Under $OutputRoot $ReleaseOutputRoot
    $payloadResult = New-PayloadParts $payloadFiles

    $manifest = if ($FullBaseline) {
        [ordered]@{
            schemaVersion = 1
            modpackId = 'cobble-music'
            channel = 'stable'
            version = $Version
            releaseTag = "modpack-v$Version"
            minimumUpdaterVersion = '1.0.0'
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            payload = $payloadResult.Payload
            files = @($authoritativeFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            deletePaths = @()
            legacyCleanup = @($legacyCleanup)
        }
    }
    else {
        [ordered]@{
            schemaVersion = 2
            modpackId = 'cobble-music'
            channel = 'stable'
            version = $Version
            releaseTag = "modpack-v$Version"
            minimumUpdaterVersion = '1.2.0'
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            base = [ordered]@{ version = $BaseVersion; manifestSha256 = $baseHash }
            payload = $payloadResult.Payload
            files = @($authoritativeFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            payloadFiles = @($payloadFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            deletedFiles = @($deletedFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            legacyCleanup = @($legacyCleanup)
        }
    }

    if ($FullBaseline) {
        Assert-CobbleV1Manifest -Manifest ([pscustomobject]$manifest) | Out-Null
    }
    else {
        Assert-CobbleDeltaManifest -Manifest ([pscustomobject]$manifest) -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash | Out-Null
    }

    $manifestPath = Join-Path $OutputRoot 'cobble-music-update.json'
    Write-Utf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)
    $signaturePath = Join-Path $OutputRoot 'cobble-music-update.sig'
    & dotnet $UpdaterDll --sign-manifest $manifestPath --private-key-file $PrivateKeyPath --signature-output $signaturePath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Manifest signing failed with exit code $LASTEXITCODE" }
    Test-SignedManifest $manifestPath $signaturePath

    $notesPath = Join-Path $OutputRoot 'RELEASE_NOTES.md'
    $modeDescription = if ($FullBaseline) { 'Full schema-v1 baseline' } else { "Schema-v2 delta from signed $BaseVersion" }
    Write-Utf8 $notesPath @"
# Cobble Music $Version

This release is a signed update payload for the Kewz's Cobblemon Prism updater.

- Mode: $modeDescription
- Authoritative files after install: $($authoritativeFiles.Count)
- Changed/new payload files: $($payloadFiles.Count)
- Exact signed-base deletions: $($deletedFiles.Count)
- Payload size: $($payloadResult.Size) bytes
- Parts: $($payloadResult.Parts.Count), each at most $ChunkSizeMiB MiB
- Source: canonical live client directory supplied to this command
"@

    Get-ExpectedStagedAssets ([pscustomobject]$manifest) $manifestPath $signaturePath | Out-Null
    Write-Host "Staged signed release at $OutputRoot"
    Write-Host "Authoritative files: $($authoritativeFiles.Count); payload files: $($payloadFiles.Count); chunks: $($payloadResult.Parts.Count)"
    if (-not $Publish) {
        Write-Host 'No GitHub mutation was made. Review the staged manifest, then rerun with -ResumePublish -ConfirmDistributionRights to upload this exact signed staging through a persistent draft.'
        exit 0
    }

    Publish-StagedRelease ([pscustomobject]$manifest) $manifestPath $signaturePath $notesPath
}
finally {
    Remove-BaseTemp $baseArtifacts
}
