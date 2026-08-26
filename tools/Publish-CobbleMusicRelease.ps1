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
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
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

    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$BaseVersion,
    [string]$BaseManifestPath,
    [string]$BaseSignaturePath,
    [switch]$FullBaseline,

    [int]$ChunkSizeMiB = 256,
    [switch]$Publish,
    [switch]$ResumePublish,
    [switch]$RepairStaleUploads,
    [switch]$ConfirmDistributionRights
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$OutputRoot = Join-Path $Root "release-output\$Version"
$ReleaseOutputRoot = Join-Path $Root 'release-output'
$CoreModule = Join-Path $PSScriptRoot 'CobbleMusicRelease.Core.psm1'
$UpdaterBootstrap = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
$PinnedUpdaterExe = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe'
$RequiredPinnedUpdaterVersion = '1.2.0'
$AllowedRoots = @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
$MaximumManifestSnapshotBytes = 8MB
$MaximumSignatureSnapshotBytes = 64KB

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

function Get-SingleQuotedBootstrapAssignment([string]$Text, [string]$Name) {
    $pattern = '(?m)^\s*\${0}\s*=\s*''(?<value>[^''\r\n]+)''\s*$' -f [regex]::Escape($Name)
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Bootstrap must contain exactly one canonical `$${Name} assignment." }
    return $matches[0].Groups['value'].Value
}

function Get-PinnedUpdaterPin {
    if (-not (Test-Path -LiteralPath $UpdaterBootstrap -PathType Leaf)) { throw "Pinned updater bootstrap was not found: $UpdaterBootstrap" }
    $text = [IO.File]::ReadAllText($UpdaterBootstrap)
    $version = Get-SingleQuotedBootstrapAssignment $text 'UpdaterVersion'
    $sha256 = Get-SingleQuotedBootstrapAssignment $text 'ExpectedUpdaterSha256'
    if ($version -cne $RequiredPinnedUpdaterVersion -or
        $version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Bootstrap must pin the supported distributed updater version $RequiredPinnedUpdaterVersion."
    }
    if ($sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Bootstrap updater SHA-256 must be canonical uppercase hexadecimal.' }
    return [pscustomobject]@{ Version = $version; Sha256 = $sha256.ToLowerInvariant() }
}

function Assert-PinnedUpdaterStream([IO.FileStream]$Stream) {
    $pin = Get-PinnedUpdaterPin
    $Stream.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [Convert]::ToHexString($hasher.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($actualHash -cne $pin.Sha256) {
        throw "Pinned updater artifact does not match bootstrap SHA-256: $PinnedUpdaterExe"
    }
    $productVersion = (Get-Item -LiteralPath $PinnedUpdaterExe).VersionInfo.ProductVersion
    if ([string]$productVersion -cne $pin.Version) {
        throw "Pinned updater artifact ProductVersion does not match bootstrap version: $productVersion / $($pin.Version)"
    }
    return [pscustomobject]@{
        version = $pin.Version
        name = 'CobbleMusicUpdater.exe'
        path = $PinnedUpdaterExe
        size = [int64]$Stream.Length
        sha256 = $actualHash
    }
}

function Get-PinnedUpdaterIdentity {
    if (-not (Test-Path -LiteralPath $PinnedUpdaterExe -PathType Leaf)) {
        throw "Checksum-pinned distributed updater artifact was not found: $PinnedUpdaterExe"
    }
    $stream = [IO.File]::Open($PinnedUpdaterExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return Assert-PinnedUpdaterStream $stream }
    finally { $stream.Dispose() }
}

function Invoke-PinnedUpdater([string[]]$Arguments) {
    if (-not (Test-Path -LiteralPath $PinnedUpdaterExe -PathType Leaf)) {
        throw "Checksum-pinned distributed updater artifact was not found: $PinnedUpdaterExe"
    }
    # The read-only, read-shared handle stays open through process exit, so the
    # artifact cannot be replaced or modified after its checksum is verified.
    $stream = [IO.File]::Open($PinnedUpdaterExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-PinnedUpdaterStream $stream | Out-Null
        & $PinnedUpdaterExe @Arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Pinned updater failed with exit code $LASTEXITCODE." }
    }
    finally { $stream.Dispose() }
}

function Test-SignedManifest([string]$ManifestPath, [string]$SignaturePath) {
    try { Invoke-PinnedUpdater -Arguments @('--verify-manifest', $ManifestPath, '--signature-file', $SignaturePath) }
    catch { throw "Manifest signature verification failed: $ManifestPath`n$($_.Exception.Message)" }
}

function Open-ManifestSignatureIdentity([string]$ManifestPath, [string]$SignaturePath) {
    $manifestSnapshot = $null
    $signatureSnapshot = $null
    try {
        $manifestSnapshot = Open-CobbleLockedFileSnapshot -Path $ManifestPath -MaximumBytes $MaximumManifestSnapshotBytes
        $signatureSnapshot = Open-CobbleLockedFileSnapshot -Path $SignaturePath -MaximumBytes $MaximumSignatureSnapshotBytes
        return [pscustomobject]@{
            Manifest = $manifestSnapshot
            Signature = $signatureSnapshot
            Assets = @(
                [pscustomobject]@{ name = 'cobble-music-update.json'; path = $manifestSnapshot.path; size = $manifestSnapshot.size; sha256 = $manifestSnapshot.sha256 },
                [pscustomobject]@{ name = 'cobble-music-update.sig'; path = $signatureSnapshot.path; size = $signatureSnapshot.size; sha256 = $signatureSnapshot.sha256 }
            )
        }
    }
    catch {
        Close-CobbleLockedFileSnapshot $signatureSnapshot
        Close-CobbleLockedFileSnapshot $manifestSnapshot
        throw
    }
}

function Close-ManifestSignatureIdentity([AllowNull()]$Identity) {
    if ($null -eq $Identity) { return }
    Close-CobbleLockedFileSnapshot $Identity.Signature
    Close-CobbleLockedFileSnapshot $Identity.Manifest
}

function Assert-ManifestSignatureIdentity([object]$Identity) {
    Assert-CobbleLockedFileSnapshot $Identity.Manifest | Out-Null
    Assert-CobbleLockedFileSnapshot $Identity.Signature | Out-Null
    Test-SignedManifest $Identity.Manifest.path $Identity.Signature.path
    return $true
}

function Read-JsonSnapshot([object]$Snapshot, [string]$Description) {
    try {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        return $encoding.GetString([byte[]]$Snapshot.bytes) | ConvertFrom-Json
    }
    catch { throw "$Description locked bytes are not valid UTF-8 JSON: $($Snapshot.path)" }
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
        Assert-CobbleReleaseAssetCount -PayloadPartCount 0 | Out-Null
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

    try { Assert-CobblePayloadZipInventory -ZipPath $payloadPath -ExpectedFiles $PayloadFiles | Out-Null }
    catch {
        if (Test-Path -LiteralPath $payloadPath -PathType Leaf) {
            Assert-Under $payloadPath $OutputRoot
            Remove-Item -LiteralPath $payloadPath -Force
        }
        throw
    }

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
    Assert-CobbleReleaseAssetCount -PayloadPartCount $parts.Count | Out-Null

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

function Get-GitHubReleaseIndex {
    $releases = [Collections.Generic.List[object]]::new()
    for ($page = 1; ; $page++) {
        $result = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/releases?per_page=100&page=$page"))
        foreach ($release in $result) { $releases.Add($release) }
        if (-not (Test-CobblePaginationHasNextPage -Page $page -ResultCount $result.Count -MaximumPages 100 -Context 'GitHub release index')) { break }
    }
    return @($releases)
}

function Get-GitHubReleaseByTag([string]$Tag) {
    $matches = @(Get-GitHubReleaseIndex | Where-Object { [string]$_.tag_name -ceq $Tag })
    if ($matches.Count -gt 1) { throw "GitHub contains multiple releases for reserved tag $Tag." }
    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Get-GitHubReleaseById([int64]$ReleaseId) {
    return Invoke-GhJson -Arguments @('api', "repos/$Repository/releases/$ReleaseId")
}

function Get-GitHubReleaseAssets([int64]$ReleaseId) {
    $assets = [Collections.Generic.List[object]]::new()
    for ($page = 1; ; $page++) {
        $result = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/releases/$ReleaseId/assets?per_page=100&page=$page"))
        foreach ($asset in $result) { $assets.Add($asset) }
        if (-not (Test-CobblePaginationHasNextPage -Page $page -ResultCount $result.Count -MaximumPages 100 -Context "GitHub assets for release $ReleaseId")) { break }
    }
    return @($assets)
}

function Get-ExactReleaseSnapshotById([int64]$ReleaseId, [string]$Tag, [ValidateSet('draft', 'public')][string]$State) {
    $release = Get-GitHubReleaseById $ReleaseId
    Assert-CobbleReleaseIdentityState -Release $release -ExpectedId $ReleaseId -ExpectedTag $Tag -ExpectedState $State | Out-Null
    return [pscustomobject]@{
        Release = $release
        Assets = @(Get-GitHubReleaseAssets $ReleaseId)
    }
}

function Get-PublishedStableReleaseSnapshot([string]$Tag, [string]$Description) {
    $release = Get-GitHubReleaseByTag $Tag
    $publishedAt = if ($null -eq $release) { $null } else { $release.PSObject.Properties['published_at'] }
    if ($null -eq $release -or [bool]$release.draft -or [bool]$release.prerelease -or
        $null -eq $publishedAt -or [string]::IsNullOrWhiteSpace([string]$publishedAt.Value)) {
        throw "$Description is not a currently published stable release: $Tag"
    }
    return [pscustomobject]@{
        Release = $release
        Assets = @(Get-GitHubReleaseAssets ([int64]$release.id))
    }
}

function Assert-PublishedPinnedUpdater {
    $identity = Get-PinnedUpdaterIdentity
    $snapshot = Get-PublishedStableReleaseSnapshot -Tag "updater-v$($identity.version)" -Description 'Pinned distributed updater'
    Assert-CobblePublishedUpdaterAsset -LocalAsset $identity -RemoteAssets @($snapshot.Assets) | Out-Null
    return $identity
}

function Assert-PublishedBaseReleaseIdentity([string]$BaseVersion, [object[]]$ExpectedAssets) {
    $snapshot = Get-PublishedStableReleaseSnapshot -Tag "modpack-v$BaseVersion" -Description 'Signed base'
    Assert-CobblePublishedBaseAssets -LocalAssets $ExpectedAssets -RemoteAssets @($snapshot.Assets) | Out-Null
    return $true
}

function Resolve-BaseArtifacts([string]$RequestedVersion, [string]$ManifestPath, [string]$SignaturePath) {
    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) { throw 'A delta requires -BaseVersion.' }
    $hasManifest = -not [string]::IsNullOrWhiteSpace($ManifestPath)
    $hasSignature = -not [string]::IsNullOrWhiteSpace($SignaturePath)
    if ($hasManifest -xor $hasSignature) { throw 'Specify both -BaseManifestPath and -BaseSignaturePath, or neither.' }

    $tag = "modpack-v$RequestedVersion"
    $snapshot = Get-PublishedStableReleaseSnapshot -Tag $tag -Description 'Signed base'
    $publishedAssets = @($snapshot.Assets)

    if ($hasManifest) {
        if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Base manifest was not found: $ManifestPath" }
        if (-not (Test-Path -LiteralPath $SignaturePath -PathType Leaf)) { throw "Base signature was not found: $SignaturePath" }
        $resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
        $resolvedSignature = (Resolve-Path -LiteralPath $SignaturePath).Path
        $identity = $null
        try {
            $identity = Open-ManifestSignatureIdentity $resolvedManifest $resolvedSignature
            Assert-CobblePublishedBaseAssets -LocalAssets @($identity.Assets) -RemoteAssets $publishedAssets | Out-Null
            return [pscustomobject]@{
                ManifestPath = $resolvedManifest
                SignaturePath = $resolvedSignature
                TempRoot = $null
                Identity = $identity
                IdentityAssets = @($identity.Assets)
            }
        }
        catch {
            Close-ManifestSignatureIdentity $identity
            throw
        }
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-music-base-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $identity = $null
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
        $identity = Open-ManifestSignatureIdentity $downloadedManifest $downloadedSignature
        Assert-CobblePublishedBaseAssets -LocalAssets @($identity.Assets) -RemoteAssets $publishedAssets | Out-Null
        return [pscustomobject]@{
            ManifestPath = $downloadedManifest
            SignaturePath = $downloadedSignature
            TempRoot = $tempRoot
            Identity = $identity
            IdentityAssets = @($identity.Assets)
        }
    }
    catch {
        if ($null -ne $identity) { Close-ManifestSignatureIdentity $identity }
        Assert-Under $tempRoot ([IO.Path]::GetTempPath())
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
        throw
    }
}

function Remove-BaseTemp([object]$BaseArtifacts) {
    if ($null -ne $BaseArtifacts) { Close-ManifestSignatureIdentity $BaseArtifacts.Identity }
    if ($null -ne $BaseArtifacts -and -not [string]::IsNullOrWhiteSpace([string]$BaseArtifacts.TempRoot) -and (Test-Path -LiteralPath $BaseArtifacts.TempRoot)) {
        Assert-Under $BaseArtifacts.TempRoot ([IO.Path]::GetTempPath())
        Remove-Item -LiteralPath $BaseArtifacts.TempRoot -Recurse -Force
    }
}

function Get-ExpectedStagedAssets([object]$Manifest, [object]$StagedIdentity) {
    $expected = [Collections.Generic.List[object]]::new()
    Assert-CobbleLockedFileSnapshot $StagedIdentity.Manifest | Out-Null
    Assert-CobbleLockedFileSnapshot $StagedIdentity.Signature | Out-Null
    foreach ($asset in @($StagedIdentity.Assets)) {
        $expected.Add([pscustomobject]@{ name = $asset.name; path = $asset.path; size = $asset.size; sha256 = $asset.sha256 })
    }

    $payload = Get-CobbleOptionalPropertyValue -Object $Manifest -Name 'payload'
    $partIdentities = @(Assert-CobbleStagedPayloadParts -Payload $payload -StagingRoot $OutputRoot)
    Assert-CobbleReleaseAssetCount -PayloadPartCount $partIdentities.Count | Out-Null
    foreach ($partIdentity in $partIdentities) {
        $expected.Add($partIdentity)
    }
    return @($expected)
}

function Publish-StagedRelease(
    [object]$Manifest,
    [object]$StagedIdentity,
    [string]$NotesPath,
    [AllowEmptyCollection()][object[]]$BaseIdentityAssets = @()
) {
    Assert-PublishedPinnedUpdater | Out-Null
    $tag = "modpack-v$Version"
    Assert-ManifestSignatureIdentity $StagedIdentity | Out-Null
    $expected = @(Get-ExpectedStagedAssets $Manifest $StagedIdentity)
    $release = Get-GitHubReleaseByTag $tag
    if ($null -eq $release) {
        Write-Host "Creating persistent draft $tag (no assets uploaded yet)."
        Assert-ManifestSignatureIdentity $StagedIdentity | Out-Null
        Invoke-NativeText -Command 'gh' -Arguments @(
            'release', 'create', $tag, '--repo', $Repository,
            '--title', "Cobble Music $Version", '--notes-file', $NotesPath, '--draft'
        ) | Out-Host
        $release = Get-GitHubReleaseByTag $tag
        if ($null -eq $release) { throw "GitHub did not create the expected draft release: $tag" }
    }

    [int64]$releaseId = [int64]$release.id
    $initialState = if ([bool]$release.draft) { 'draft' } else { 'public' }
    $snapshot = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State $initialState
    if ($initialState -ceq 'public') {
        Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($snapshot.Assets) -RequireComplete | Out-Null
        Write-Host "Release $tag is already public and exactly matches signed staging."
        return
    }

    $assets = @($snapshot.Assets)
    if ($RepairStaleUploads) {
        $staleAssets = @(Get-CobbleRepairableStarterAssets -ExpectedAssets $expected -RemoteAssets $assets)
        if ($staleAssets.Count -gt 0) {
            Write-Host "Removing $($staleAssets.Count) explicitly approved stale starter asset(s) from draft $tag."
            foreach ($stale in $staleAssets) {
                # Reverify local signed bytes, then refetch the exact draft and
                # its complete assets immediately before each destructive call.
                Assert-ManifestSignatureIdentity $StagedIdentity | Out-Null
                $current = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
                $stillStale = Get-CobbleStarterAssetForDeletion -Candidate $stale -ExpectedAssets $expected -RemoteAssets @($current.Assets)
                if ($null -eq $stillStale) {
                    Write-Host "Skipped stale repair because asset state changed safely: $($stale.name)"
                    continue
                }
                Invoke-NativeText -Command 'gh' -Arguments @('api', '--method', 'DELETE', "repos/$Repository/releases/assets/$($stillStale.id)") | Out-Null
                Write-Host "Removed revalidated stale starter asset: $($stillStale.name)"
            }
        }
    }

    $current = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
    $missing = @(Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($current.Assets))
    if ($missing.Count -gt 0) {
        $expectedByName = @{}
        foreach ($asset in $expected) { $expectedByName[$asset.name] = $asset }
        Assert-ManifestSignatureIdentity $StagedIdentity | Out-Null
        $current = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
        $missing = @(Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($current.Assets))
        if ($missing.Count -gt 0) {
            $uploadPaths = @($missing | ForEach-Object { $expectedByName[$_].path })
            Write-Host "Uploading $($uploadPaths.Count) missing asset(s) to persistent draft $tag. Completed matching assets will be reused on -ResumePublish."
            try { Invoke-NativeHost -Command 'gh' -Arguments (@('release', 'upload', $tag, '--repo', $Repository) + $uploadPaths) }
            catch { throw "$($_.Exception.Message)`nThe draft was preserved. After any active GitHub upload settles, rerun this version with -ResumePublish -ConfirmDistributionRights." }
        }
    }

    $current = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($current.Assets) -RequireComplete | Out-Null
    Write-Host 'Remote asset names, states, sizes, and GitHub SHA-256 digests exactly match signed staging.'

    Assert-PublishedPinnedUpdater | Out-Null
    if ([int]$Manifest.schemaVersion -eq 2) {
        if ($BaseIdentityAssets.Count -ne 2) { throw 'Delta publication is missing its verified signed-base identity snapshot.' }
        Assert-PublishedBaseReleaseIdentity -BaseVersion ([string]$Manifest.base.version) -ExpectedAssets $BaseIdentityAssets | Out-Null
    }

    # The final local signature check happens before the final exact-by-ID
    # draft/asset snapshot; no stale tag lookup is used for publication.
    Assert-ManifestSignatureIdentity $StagedIdentity | Out-Null
    # Updater 1.2 reads at most five 100-item release-index pages and rejects a
    # full fifth page as ambiguous truncation. Perform the prospective count
    # before the last exact draft fetch, leaving at most 499 non-draft releases;
    # public prereleases consume slots too. The exact ID/state/assets re-fetch
    # then remains the final remote validation immediately before PATCH.
    Assert-CobblePublicReleaseCapacity -Releases @(Get-GitHubReleaseIndex) -AdditionalPublicReleases 1 | Out-Null
    $finalDraft = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($finalDraft.Assets) -RequireComplete | Out-Null
    Invoke-GhJson -Arguments @('api', '--method', 'PATCH', "repos/$Repository/releases/$releaseId", '-F', 'draft=false') | Out-Null

    $published = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'public'
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($published.Assets) -RequireComplete | Out-Null
    Assert-CobblePublicReleaseCapacity -Releases @(Get-GitHubReleaseIndex) -AdditionalPublicReleases 0 | Out-Null
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
    $stagedIdentity = $null
    try {
        $stagedIdentity = Open-ManifestSignatureIdentity $manifestPath $signaturePath
        Assert-ManifestSignatureIdentity $stagedIdentity | Out-Null
        $manifest = Read-JsonSnapshot $stagedIdentity.Manifest 'Staged signed manifest'
        if ([string]$manifest.version -cne $Version -or [string]$manifest.releaseTag -cne "modpack-v$Version") {
            throw 'Staged signed manifest does not match the requested release version.'
        }
        ConvertTo-CobbleFileRecordSet -Entries @($manifest.files) -Context 'staged authoritative files' | Out-Null

        $baseArtifacts = $null
        $baseIdentityAssets = @()
        try {
            if ([int]$manifest.schemaVersion -eq 2) {
                $baseArtifacts = Resolve-BaseArtifacts -RequestedVersion ([string]$manifest.base.version) -ManifestPath $BaseManifestPath -SignaturePath $BaseSignaturePath
                $baseIdentityAssets = @($baseArtifacts.IdentityAssets)
                Assert-ManifestSignatureIdentity $baseArtifacts.Identity | Out-Null
                $baseHash = [string]$baseArtifacts.Identity.Manifest.sha256
                $baseManifest = Read-JsonSnapshot $baseArtifacts.Identity.Manifest 'Signed base manifest'
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

        Get-ExpectedStagedAssets $manifest $stagedIdentity | Out-Null
        return [pscustomobject]@{
            Manifest = $manifest
            StagedIdentity = $stagedIdentity
            NotesPath = $notesPath
            BaseIdentityAssets = $baseIdentityAssets
        }
    }
    catch {
        Close-ManifestSignatureIdentity $stagedIdentity
        throw
    }
}

if ($ChunkSizeMiB -lt 1 -or $ChunkSizeMiB -gt 1536) {
    throw 'ChunkSizeMiB must be between 1 and 1536, safely under the GitHub Releases 2 GiB per-asset limit.'
}
if ($Publish -and $ResumePublish) { throw 'Use either -Publish for a fresh staging build or -ResumePublish for existing signed staging, not both.' }
if ($RepairStaleUploads -and -not $ResumePublish) { throw '-RepairStaleUploads is allowed only with -ResumePublish for an existing persistent draft.' }
if (($Publish -or $ResumePublish) -and -not $ConfirmDistributionRights) {
    throw 'Publishing is blocked until you explicitly pass -ConfirmDistributionRights. Confirm that every payload file may be distributed through this public GitHub Release.'
}

if ($ResumePublish) {
    if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) { throw "Release staging output does not exist: $OutputRoot" }
    Assert-Under $OutputRoot $ReleaseOutputRoot
    Get-PinnedUpdaterIdentity | Out-Null
    $staged = Read-And-ValidateStagedManifest
    try { Publish-StagedRelease $staged.Manifest $staged.StagedIdentity $staged.NotesPath $staged.BaseIdentityAssets }
    finally { Close-ManifestSignatureIdentity $staged.StagedIdentity }
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

$SourceMinecraftDir = (Resolve-Path -LiteralPath $SourceMinecraftDir).Path
$PrivateKeyPath = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
$managedRootPaths = [Collections.Generic.List[string]]::new()
foreach ($rootName in $IncludeRoots | Sort-Object -Unique) {
    if ($AllowedRoots -cnotcontains $rootName) { throw "Include root is outside the updater allowlist: $rootName" }
    $managedRootPaths.Add((Join-Path $SourceMinecraftDir $rootName))
}
Assert-CobblePrivateKeyIsolation -PrivateKeyPath $PrivateKeyPath -SourceMinecraftDir $SourceMinecraftDir `
    -ReleaseOutputRoot $ReleaseOutputRoot -ManagedRoots @($managedRootPaths) | Out-Null

Get-PinnedUpdaterIdentity | Out-Null

$baseArtifacts = $null
$stagedIdentity = $null
try {
    $baseManifest = $null
    $baseHash = $null
    $baseFiles = @()
    if (-not $FullBaseline) {
        $baseArtifacts = Resolve-BaseArtifacts -RequestedVersion $BaseVersion -ManifestPath $BaseManifestPath -SignaturePath $BaseSignaturePath
        Assert-ManifestSignatureIdentity $baseArtifacts.Identity | Out-Null
        $baseHash = [string]$baseArtifacts.Identity.Manifest.sha256
        $baseManifest = Read-JsonSnapshot $baseArtifacts.Identity.Manifest 'Signed base manifest'
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
    try { Invoke-PinnedUpdater -Arguments @('--sign-manifest', $manifestPath, '--private-key-file', $PrivateKeyPath, '--signature-output', $signaturePath) }
    catch { throw "Manifest signing failed.`n$($_.Exception.Message)" }
    $stagedIdentity = Open-ManifestSignatureIdentity $manifestPath $signaturePath
    Assert-ManifestSignatureIdentity $stagedIdentity | Out-Null
    $signedManifest = Read-JsonSnapshot $stagedIdentity.Manifest 'Newly signed manifest'
    if ($FullBaseline) {
        Assert-CobbleV1Manifest -Manifest $signedManifest | Out-Null
    }
    else {
        Assert-CobbleDeltaManifest -Manifest $signedManifest -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash | Out-Null
    }

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

    Get-ExpectedStagedAssets $signedManifest $stagedIdentity | Out-Null
    Write-Host "Staged signed release at $OutputRoot"
    Write-Host "Authoritative files: $($authoritativeFiles.Count); payload files: $($payloadFiles.Count); chunks: $($payloadResult.Parts.Count)"
    if (-not $Publish) {
        Write-Host 'No GitHub mutation was made. Review the staged manifest, then rerun with -ResumePublish -ConfirmDistributionRights to upload this exact signed staging through a persistent draft.'
        exit 0
    }

    $baseIdentityAssets = if ($null -eq $baseArtifacts) { @() } else { @($baseArtifacts.IdentityAssets) }
    Publish-StagedRelease $signedManifest $stagedIdentity $notesPath $baseIdentityAssets
}
finally {
    Close-ManifestSignatureIdentity $stagedIdentity
    Remove-BaseTemp $baseArtifacts
}
