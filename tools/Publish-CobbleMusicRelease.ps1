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

    [string]$SourceMinecraftDir,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'Kewz4/Cobble-Music',
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE '.cobble-music\keys\cobble-music-release-private.key'),
    [string[]]$IncludeRoots = @('mods', 'resourcepacks', 'datapacks', 'shaderpacks', 'defaultconfigs', 'kubejs', 'scripts'),
    [string[]]$IncludeFiles = @(
        'config/cobble-music-bridge.json',
        'config/cobble-music-pack-version.json',
        'config/logbegone.json',
        'config/resourcepackoverrides.json',
        'resourcepacks/[Chilli´s] punchy! cobblemon (2).zip.rpo',
        'resourcepacks/Cobblemon Interface Modded.zip.rpo',
        'resourcepacks/Cobblemon Interface.zip.rpo',
        'resourcepacks/Icons Compats.zip.rpo',
        'resourcepacks/Icons v.1.13.2.zip.rpo',
        'resourcepacks/Punchy refined.zip.rpo',
        'resourcepacks/refined torches 2.1.zip.rpo'
    ),
    [string[]]$SeedFiles = @(
        'options.txt',
        'config/ReactiveMusic.json5',
        'config/musicnotification.json',
        'config/sodium-options.json',
        'config/sodium-extra-options.json',
        'config/voxy-config.json'
    ),
    [string[]]$SeedRoots = @('config'),
    [string[]]$ReofferSeedFiles = @(),
    [string]$SeedTextReplacementManifest,
    [string]$SeedTemplateDir,
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
    [ValidateRange(1, 4)]
    [int]$UploadProcessCount = 1,
    [switch]$ConfirmDistributionRights
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$SeedTemplateDir = if ([string]::IsNullOrWhiteSpace($SeedTemplateDir)) { Join-Path $Root 'release-defaults' } else { $SeedTemplateDir }
$OutputRoot = Join-Path $Root "release-output\$Version"
$ReleaseOutputRoot = Join-Path $Root 'release-output'
$CoreModule = Join-Path $PSScriptRoot 'CobbleMusicRelease.Core.psm1'
$UpdaterBootstrap = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
$PinnedUpdaterExe = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe'
$PinnedVerifierExe = Join-Path $Root 'updater\verifier\win-x64\CobbleMusicUpdater.exe'
$UpdaterChannelPath = Join-Path $Root 'updater\channel\stable.json'
$UpdaterChannelSignaturePath = Join-Path $Root 'updater\channel\stable.sig'
$RequiredVerifierVersion = '1.2.7'
$RequiredUpdaterVersion = '1.2.16'
$AllowedRoots = @('mods', 'resourcepacks', 'datapacks', 'shaderpacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
$OfficialPackProfilePaths = @(
    'config/packed_packs/profiles/resourcepacks/Default.profile.json',
    'config/packed_packs/profiles/resourcepacks/Realistic.profile.json'
)
# Only these two official profiles are managed. Custom profiles, selection,
# Packed Packs preferences and Iris settings retain their create-only policy.
$IncludeFiles = @(@($IncludeFiles) + $OfficialPackProfilePaths | Sort-Object -Unique)
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

function Get-OfficialPackProfileSources([string]$MinecraftDirectory, [string]$TemplateDirectory) {
    foreach ($relative in $OfficialPackProfilePaths) {
        $full = Join-Path $MinecraftDirectory $relative
        if (-not [string]::IsNullOrWhiteSpace($TemplateDirectory)) {
            $template = Join-Path $TemplateDirectory $relative
            Assert-Under $template $TemplateDirectory
            if (Test-Path -LiteralPath $template -PathType Leaf) { $full = $template }
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Required official pack profile is missing from template and canonical instance: $relative"
        }
        $item = Get-Item -LiteralPath $full
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Official pack profile may not be a reparse point: $full"
        }
        $profile = Read-JsonFile -Path $full -Description 'Official pack profile'
        $expectedName = [IO.Path]::GetFileName($relative).Replace('.profile.json', '')
        if ([string]$profile.name -cne $expectedName -or $profile.packIds -isnot [Collections.IList]) {
            throw "Official pack profile has an invalid name or packIds array: $relative"
        }
        [pscustomobject]@{ full = $item.FullName; path = $relative }
    }
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

function Invoke-GhReleaseUploadBatches(
    [string]$Tag,
    [string]$RepositoryName,
    [string[]]$UploadPaths,
    [ValidateRange(1, 4)][int]$ProcessCount
) {
    if ($UploadPaths.Count -eq 0) { return }
    $workerCount = [Math]::Min($ProcessCount, $UploadPaths.Count)
    if ($workerCount -eq 1) {
        Invoke-NativeHost -Command 'gh' -Arguments (@('release', 'upload', $Tag, '--repo', $RepositoryName) + $UploadPaths)
        return
    }

    $pathGroups = [object[]]::new($workerCount)
    for ($worker = 0; $worker -lt $workerCount; $worker++) {
        $pathGroups[$worker] = [Collections.Generic.List[string]]::new()
    }
    for ($index = 0; $index -lt $UploadPaths.Count; $index++) {
        $pathGroups[$index % $workerCount].Add($UploadPaths[$index])
    }
    $workItems = @(
        for ($worker = 0; $worker -lt $workerCount; $worker++) {
            [pscustomobject]@{
                Worker = $worker + 1
                Arguments = [string[]](@('release', 'upload', $Tag, '--repo', $RepositoryName) + $pathGroups[$worker].ToArray())
            }
        }
    )

    $results = @($workItems | ForEach-Object -Parallel {
        $arguments = [string[]]$_.Arguments
        $lines = @(& gh @arguments 2>&1)
        [pscustomobject]@{
            Worker = [int]$_.Worker
            ExitCode = [int]$LASTEXITCODE
            Output = (($lines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        }
    } -ThrottleLimit $workerCount)

    foreach ($result in @($results | Sort-Object Worker)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$result.Output)) {
            Write-Host "gh upload worker $($result.Worker):`n$($result.Output)"
        }
    }
    $failed = @($results | Where-Object ExitCode -NE 0)
    if ($failed.Count -gt 0) {
        throw "gh release upload worker(s) failed: $($failed.Worker -join ', ')"
    }
}

function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $isReadOnlyApiRequest = $Arguments.Count -ge 2 -and
        $Arguments[0] -ceq 'api' -and
        -not ($Arguments -contains '--method') -and
        -not ($Arguments -contains '-X')
    $maximumAttempts = if ($isReadOnlyApiRequest) { 6 } else { 1 }
    $json = $null
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        try {
            $json = Invoke-NativeText -Command 'gh' -Arguments $Arguments
            break
        }
        catch {
            $message = [string]$_.Exception.Message
            $isTransientTransportFailure = $message -match '(?i)(unexpected EOF|TLS handshake timeout|connection (?:timed out|reset)|i/o timeout|stream error|HTTP (?:502|503|504))'
            if (-not $isReadOnlyApiRequest -or -not $isTransientTransportFailure -or $attempt -eq $maximumAttempts) {
                throw
            }
            $delayMilliseconds = [Math]::Min(5000, 500 * [Math]::Pow(2, $attempt - 1))
            Write-Warning "Transient GitHub read failure (attempt $attempt/$maximumAttempts); retrying in $delayMilliseconds ms."
            Start-Sleep -Milliseconds $delayMilliseconds
        }
    }
    try { return $json | ConvertFrom-Json }
    catch { throw "GitHub CLI returned invalid JSON for: gh $($Arguments -join ' ')" }
}

function Get-SingleQuotedBootstrapAssignment([string]$Text, [string]$Name) {
    $pattern = '(?m)^\s*\${0}\s*=\s*''(?<value>[^''\r\n]+)''\s*$' -f [regex]::Escape($Name)
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Bootstrap must contain exactly one canonical `$${Name} assignment." }
    return $matches[0].Groups['value'].Value
}

function Get-BootstrapVerifierPin {
    if (-not (Test-Path -LiteralPath $UpdaterBootstrap -PathType Leaf)) { throw "Pinned updater bootstrap was not found: $UpdaterBootstrap" }
    $text = [IO.File]::ReadAllText($UpdaterBootstrap)
    $version = Get-SingleQuotedBootstrapAssignment $text 'VerifierVersion'
    $sha256 = Get-SingleQuotedBootstrapAssignment $text 'ExpectedVerifierSha256'
    if ($version -cne $RequiredVerifierVersion -or
        $version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Bootstrap must pin the supported immutable verifier version $RequiredVerifierVersion."
    }
    if ($sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Bootstrap verifier SHA-256 must be canonical uppercase hexadecimal.' }
    return [pscustomobject]@{ Version = $version; Sha256 = $sha256.ToLowerInvariant() }
}

function Invoke-PinnedVerifier([string[]]$Arguments) {
    if (-not (Test-Path -LiteralPath $PinnedVerifierExe -PathType Leaf)) {
        throw "Immutable verifier artifact was not found: $PinnedVerifierExe"
    }
    $pin = Get-BootstrapVerifierPin
    $stream = [IO.File]::Open($PinnedVerifierExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [Convert]::ToHexString($hasher.ComputeHash($stream)).ToLowerInvariant() }
        finally { $hasher.Dispose() }
        if ($actualHash -cne $pin.Sha256) { throw 'Immutable verifier artifact does not match the bootstrap SHA-256.' }
        $verifierProductVersion = [string](Get-Item -LiteralPath $PinnedVerifierExe).VersionInfo.ProductVersion
        if ($verifierProductVersion -cne $pin.Version) {
            throw 'Immutable verifier ProductVersion does not match the bootstrap pin.'
        }
        & $PinnedVerifierExe @Arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Pinned verifier failed with exit code $LASTEXITCODE." }
    }
    finally { $stream.Dispose() }
}

function Get-PinnedUpdaterPin {
    foreach ($path in @($UpdaterChannelPath, $UpdaterChannelSignaturePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Signed stable updater channel input is missing: $path" }
    }
    $channelBytes = [IO.File]::ReadAllBytes($UpdaterChannelPath)
    $signatureBytes = [IO.File]::ReadAllBytes($UpdaterChannelSignaturePath)
    if ($channelBytes.Length -gt 16KB -or $signatureBytes.Length -gt 64KB) {
        throw 'Signed stable updater channel input exceeds its safety limit.'
    }
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-music-channel-verify-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $channelSnapshot = Join-Path $temporaryRoot 'stable.json'
        $signatureSnapshot = Join-Path $temporaryRoot 'stable.sig'
        $verifiedOutput = Join-Path $temporaryRoot 'verified.json'
        [IO.File]::WriteAllBytes($channelSnapshot, $channelBytes)
        [IO.File]::WriteAllBytes($signatureSnapshot, $signatureBytes)
        Invoke-PinnedVerifier -Arguments @(
            '--verify-updater-channel', $channelSnapshot,
            '--signature-file', $signatureSnapshot,
            '--verified-output', $verifiedOutput
        )
        $channel = [IO.File]::ReadAllText($verifiedOutput) | ConvertFrom-Json
        return [pscustomobject]@{
            Version = [string]$channel.updaterVersion
            Sha256 = ([string]$channel.updater.sha256).ToLowerInvariant()
            Size = [int64]$channel.updater.size
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}

function Assert-PinnedUpdaterStream([IO.FileStream]$Stream) {
    $pin = Get-PinnedUpdaterPin
    $Stream.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [Convert]::ToHexString($hasher.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($actualHash -cne $pin.Sha256 -or $Stream.Length -ne $pin.Size) {
        throw "Distributed updater artifact does not match the signed stable channel: $PinnedUpdaterExe"
    }
    $productVersion = (Get-Item -LiteralPath $PinnedUpdaterExe).VersionInfo.ProductVersion
    if ([string]$productVersion -cne $pin.Version) {
        throw "Distributed updater artifact ProductVersion does not match signed channel version: $productVersion / $($pin.Version)"
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

function Assert-SourceStateBoundToBase(
    [string]$MinecraftDirectory,
    [string]$ExpectedVersion,
    [string]$ExpectedManifestSha256,
    [object[]]$ExpectedFiles
) {
    $statePath = Join-Path $MinecraftDirectory 'cobble-music-updater\state.json'
    $state = Read-JsonFile -Path $statePath -Description 'Canonical source updater state'
    if ([int]$state.schemaVersion -ne 1 -or
        [string]$state.version -cne $ExpectedVersion -or
        [string]$state.manifestSha256 -cne $ExpectedManifestSha256) {
        throw "Canonical source is not bound to the exact signed base $ExpectedVersion ($ExpectedManifestSha256): $statePath"
    }

    $stateSet = ConvertTo-CobbleFileRecordSet -Entries @($state.managedFiles) -Context 'canonical source updater state'
    $baseSet = ConvertTo-CobbleFileRecordSet -Entries $ExpectedFiles -Context 'signed base files'
    if ($stateSet.Entries.Count -ne $baseSet.Entries.Count) {
        throw "Canonical source updater state does not contain the signed base file inventory: $statePath"
    }
    for ($index = 0; $index -lt $baseSet.Entries.Count; $index++) {
        $actual = $stateSet.Entries[$index]
        $expected = $baseSet.Entries[$index]
        if ($actual.path -cne $expected.path -or $actual.size -ne $expected.size -or $actual.sha256 -cne $expected.sha256) {
            throw "Canonical source updater state differs from signed base at $($expected.path): $statePath"
        }
    }
    return $true
}

function Read-LegacyCleanupManifest([string]$Path, [string[]]$ForbiddenPaths) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    $entries = @(Read-JsonFile -Path $Path -Description 'Legacy cleanup manifest')
    $set = ConvertTo-CobbleLegacyCleanupSet -Entries $entries -Context 'legacy cleanup manifest'
    $forbidden = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $ForbiddenPaths) { [void]$forbidden.Add($item.Normalize([Text.NormalizationForm]::FormC)) }
    foreach ($entry in $set.Entries) {
        if ($forbidden.Contains($entry.path.Normalize([Text.NormalizationForm]::FormC))) {
            throw "Legacy cleanup overlaps a current or signed-base managed file: $($entry.path)"
        }
    }
    return @($set.Entries | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
}

function Read-SeedTextReplacementManifest([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    $entries = @(Read-JsonFile -Path $Path -Description 'Seed text replacement manifest')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $result = [Collections.Generic.List[object]]::new()
    $optionsReplacementCount = 0
    $hasIrisToggleReplacement = $false
    $hasFancyToastsReplacement = $false
    foreach ($entry in $entries) {
        $pathValue = [string]$entry.path
        Assert-CobbleSeedTextReplacementPathPolicy -Path $pathValue -Context 'seed text replacement' | Out-Null
        $key = Get-CobblePathKey $pathValue
        $oldText = [string]$entry.oldText
        $newText = [string]$entry.newText
        $requiredLines = @(if ($entry.PSObject.Properties.Name -contains 'requiredLines' -and $null -ne $entry.requiredLines) {
            @($entry.requiredLines | ForEach-Object { [string]$_ })
        } else { @() })
        $migrationId = if ($entry.PSObject.Properties.Name -contains 'migrationId') { [string]$entry.migrationId } else { '' }
        $identity = $key + [char]0 + $oldText
        $safeText = -not [string]::IsNullOrEmpty($oldText) -and -not [string]::IsNullOrEmpty($newText) -and
            $oldText -cne $newText -and $oldText.Length -le 4096 -and $newText.Length -le 4096 -and
            -not $oldText.Contains([char]0) -and -not $newText.Contains([char]0) -and
            -not $oldText.Contains("`n") -and -not $oldText.Contains("`r") -and
            -not $newText.Contains("`n") -and -not $newText.Contains("`r")
        $validIris = $pathValue -ieq 'config/iris.properties' -and $requiredLines.Count -eq 0 -and
            [string]::IsNullOrEmpty($migrationId) -and
            $oldText.StartsWith('shaderPack=', [StringComparison]::Ordinal) -and
            $newText.StartsWith('shaderPack=', [StringComparison]::Ordinal)
        $validOptions = $pathValue -ieq 'options.txt' -and $requiredLines.Count -eq 1 -and
            $migrationId -ceq 'options-contest-tracker-k-collision-v1' -and
            $requiredLines[0] -ceq 'key_key.companion_bonds.open_contest_tracker:key.keyboard.k' -and
            (($oldText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.k' -and
                $newText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.unknown') -or
             ($oldText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.k' -and
                $newText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.unknown'))
        if (-not $seen.Add($identity) -or -not $safeText -or -not ($validIris -or $validOptions)) {
            throw "Seed text replacement is unsafe or duplicated: $pathValue"
        }
        if ($validOptions) {
            $optionsReplacementCount++
            if ($oldText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.k') { $hasIrisToggleReplacement = $true }
            if ($oldText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.k') { $hasFancyToastsReplacement = $true }
        }
        $result.Add([ordered]@{
            path = $pathValue
            oldText = $oldText
            newText = $newText
            migrationId = $migrationId
            requiredLines = @($requiredLines)
        })
    }
    if ($optionsReplacementCount -ne 0 -and
        ($optionsReplacementCount -ne 2 -or -not $hasIrisToggleReplacement -or -not $hasFancyToastsReplacement)) {
        throw 'The options migration must contain the complete reviewed Iris and Fancy Toasts repair pair.'
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

function New-PayloadParts(
    [object[]]$PayloadFiles,
    [object[]]$PayloadSeedFiles = @()
) {
    if ($PayloadFiles.Count -eq 0) {
        Assert-CobbleReleaseAssetCount -PayloadPartCount 0 | Out-Null
        return [pscustomobject]@{ Payload = $null; Parts = @(); Size = 0 }
    }

    $payloadSourceRoot = Join-Path $OutputRoot 'payload-source'
    $payloadPath = Join-Path $OutputRoot 'cobble-music-payload.zip'
    Assert-Under $payloadSourceRoot $OutputRoot
    Assert-Under $payloadPath $OutputRoot
    if (Test-Path -LiteralPath $payloadSourceRoot) { throw "Payload staging root already exists: $payloadSourceRoot" }
    try {
        New-Item -ItemType Directory -Path $payloadSourceRoot | Out-Null
        foreach ($payloadFile in $PayloadFiles) {
            if ($null -eq $payloadFile.PSObject.Properties['full']) {
                throw "Payload entry is not bound to a canonical source file: $($payloadFile.path)"
            }
            $source = [IO.Path]::GetFullPath([string]$payloadFile.full)
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Payload source disappeared before archiving: $($payloadFile.path)"
            }
            $target = Join-Path $payloadSourceRoot ([string]$payloadFile.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
            Assert-Under $target $payloadSourceRoot
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            try { New-Item -ItemType HardLink -Path $target -Target $source -ErrorAction Stop | Out-Null }
            catch { Copy-Item -LiteralPath $source -Destination $target }
        }

        $fileList = Join-Path $OutputRoot 'payload-files.txt'
        Write-Utf8 $fileList (($PayloadFiles.path | ForEach-Object { $_.Replace('/', '\') }) -join [Environment]::NewLine)
        $sevenZip = (Get-Command 7z -ErrorAction Stop).Source
        Push-Location $payloadSourceRoot
        try {
            & $sevenZip a -tzip $payloadPath "@$fileList" '-mx=0' | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
        Assert-CobblePayloadZipInventory -ZipPath $payloadPath -ExpectedFiles $PayloadFiles `
            -ExpectedSeedFiles $PayloadSeedFiles | Out-Null
    }
    catch {
        if (Test-Path -LiteralPath $payloadPath -PathType Leaf) {
            Assert-Under $payloadPath $OutputRoot
            Remove-Item -LiteralPath $payloadPath -Force
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $payloadSourceRoot -PathType Container) {
            Assert-Under $payloadSourceRoot $OutputRoot
            Remove-Item -LiteralPath $payloadSourceRoot -Recurse -Force
        }
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

function Wait-GitHubReleaseByTag([string]$Tag, [int]$MaximumAttempts = 8) {
    if ($MaximumAttempts -lt 1) { throw 'Maximum release lookup attempts must be positive.' }
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $release = Get-GitHubReleaseByTag $Tag
        if ($null -ne $release) { return $release }
        if ($attempt -lt $MaximumAttempts) {
            # GitHub's paginated release index can lag immediately after
            # draft creation even though gh has already returned its URL.
            Start-Sleep -Milliseconds ([Math]::Min(2000, 250 * $attempt))
        }
    }
    return $null
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

function Wait-GitHubAssetInventoryFinalization(
    [int64]$ReleaseId,
    [string]$Tag,
    [object[]]$ExpectedAssets,
    [int]$MaximumAttempts = 60
) {
    if ($MaximumAttempts -lt 1) { throw 'MaximumAttempts must be positive.' }

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $snapshot = Get-ExactReleaseSnapshotById -ReleaseId $ReleaseId -Tag $Tag -State 'draft'
        $assets = @($snapshot.Assets)
        $starters = @(Get-CobbleRepairableStarterAssets -ExpectedAssets $ExpectedAssets -RemoteAssets $assets)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($asset in $assets) { $seen.Add([string]$asset.name) | Out-Null }
        $missing = @($ExpectedAssets | Where-Object { -not $seen.Contains([string]$_.name) })

        if ($starters.Count -eq 0 -and $missing.Count -eq 0) {
            Assert-CobbleRemoteAssetInventory -ExpectedAssets $ExpectedAssets -RemoteAssets $assets -RequireComplete | Out-Null
            return $snapshot
        }

        if ($attempt -eq $MaximumAttempts) {
            throw "GitHub did not finalize the exact asset inventory within the bounded wait ($($starters.Count) starter; $($missing.Count) missing)."
        }
        Start-Sleep -Milliseconds ([Math]::Min(2000, 250 * $attempt))
    }
    throw 'GitHub asset inventory finalization did not complete.'
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

function Get-PublishedMainUpdaterChannelPin([int]$MaximumAttempts = 6) {
    if ($MaximumAttempts -lt 1) { throw 'MaximumAttempts must be positive.' }
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-music-public-channel-' + [Guid]::NewGuid().ToString('N'))
    $lastFailure = $null
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $channelPath = Join-Path $temporaryRoot 'stable.json'
        $signaturePath = Join-Path $temporaryRoot 'stable.sig'
        $verifiedPath = Join-Path $temporaryRoot 'verified.json'
        for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
            try {
                $nonce = [Guid]::NewGuid().ToString('N')
                $headers = @{ 'Cache-Control' = 'no-cache'; Pragma = 'no-cache' }
                Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/$Repository/main/updater/channel/stable.json?verify=$nonce" `
                    -Headers $headers -OutFile $channelPath -TimeoutSec 30
                Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/$Repository/main/updater/channel/stable.sig?verify=$nonce" `
                    -Headers $headers -OutFile $signaturePath -TimeoutSec 30
                if (Test-Path -LiteralPath $verifiedPath) { Remove-Item -LiteralPath $verifiedPath -Force }
                Invoke-PinnedVerifier -Arguments @(
                    '--verify-updater-channel', $channelPath,
                    '--signature-file', $signaturePath,
                    '--verified-output', $verifiedPath
                )
                $channel = [IO.File]::ReadAllText($verifiedPath) | ConvertFrom-Json
                if ([string]$channel.repository -cne $Repository -or [string]$channel.channel -cne 'stable') {
                    throw 'Published main updater channel has the wrong repository or channel identity.'
                }
                return [pscustomobject]@{
                    Version = [string]$channel.updaterVersion
                    ReleaseTag = [string]$channel.releaseTag
                    Size = [int64]$channel.updater.size
                    Sha256 = ([string]$channel.updater.sha256).ToLowerInvariant()
                }
            }
            catch {
                $lastFailure = $_.Exception
                if ($attempt -lt $MaximumAttempts) {
                    Start-Sleep -Milliseconds ([Math]::Min(2000, 250 * $attempt))
                }
            }
        }
        throw "Published main updater channel did not become verifiable within the bounded wait: $($lastFailure.Message)"
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}

function Assert-PublishedPinnedUpdater([string]$MinimumVersion) {
    if ($MinimumVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Manifest minimum updater version is not canonical: $MinimumVersion"
    }
    $identity = Get-PinnedUpdaterIdentity
    if ([Version]$identity.version -lt [Version]$MinimumVersion) {
        throw "Pinned updater $($identity.version) does not satisfy manifest minimum $MinimumVersion."
    }
    $snapshot = Get-PublishedStableReleaseSnapshot -Tag "updater-v$($identity.version)" -Description 'Pinned distributed updater'
    Assert-CobblePublishedUpdaterAsset -LocalAsset $identity -RemoteAssets @($snapshot.Assets) | Out-Null
    $publicPin = Get-PublishedMainUpdaterChannelPin
    if ($publicPin.Version -cne $identity.version -or
        $publicPin.ReleaseTag -cne "updater-v$($identity.version)" -or
        $publicPin.Size -ne $identity.size -or
        $publicPin.Sha256 -cne $identity.sha256) {
        throw 'Published main updater channel does not advertise the exact pinned updater release.'
    }
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
    Assert-PublishedPinnedUpdater -MinimumVersion ([string]$Manifest.minimumUpdaterVersion) | Out-Null
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
        $release = Wait-GitHubReleaseByTag $tag
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
            Write-Host "Uploading $($uploadPaths.Count) missing asset(s) through $UploadProcessCount gh process(es) to persistent draft $tag. Completed matching assets will be reused on -ResumePublish."
            try {
                Invoke-GhReleaseUploadBatches -Tag $tag -RepositoryName $Repository -UploadPaths $uploadPaths -ProcessCount $UploadProcessCount
                Wait-GitHubAssetInventoryFinalization -ReleaseId $releaseId -Tag $tag -ExpectedAssets $expected | Out-Null
            }
            catch { throw "$($_.Exception.Message)`nThe draft was preserved. After any active GitHub upload settles, rerun this version with -ResumePublish -ConfirmDistributionRights." }
        }
    }

    $current = Get-ExactReleaseSnapshotById -ReleaseId $releaseId -Tag $tag -State 'draft'
    Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($current.Assets) -RequireComplete | Out-Null
    Write-Host 'Remote asset names, states, sizes, and GitHub SHA-256 digests exactly match signed staging.'

    Assert-PublishedPinnedUpdater -MinimumVersion ([string]$Manifest.minimumUpdaterVersion) | Out-Null
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
if ([string]::IsNullOrWhiteSpace($SourceMinecraftDir)) {
    throw '-SourceMinecraftDir is required when staging a release; choose the canonical instance explicitly.'
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

$packVersionPath = Join-Path $SourceMinecraftDir 'config\cobble-music-pack-version.json'
if (-not (Test-Path -LiteralPath $packVersionPath -PathType Leaf)) {
    throw "Canonical DEV pack-version marker is missing: $packVersionPath"
}
$packVersionMarker = Read-JsonFile -Path $packVersionPath -Description 'Canonical DEV pack-version marker'
if ([string]$packVersionMarker.pack -cne "Kewz's Cobblemon" -or
    [string]$packVersionMarker.channel -cne 'stable' -or
    [string]$packVersionMarker.version -cne $Version) {
    throw "Canonical DEV pack-version marker does not match requested release $Version."
}

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
        Assert-SourceStateBoundToBase -MinecraftDirectory $SourceMinecraftDir -ExpectedVersion $BaseVersion `
            -ExpectedManifestSha256 $baseHash -ExpectedFiles $baseFiles | Out-Null
        Write-Host "Verified signed base $BaseVersion ($baseHash)."
    }

    Write-Host "Building authoritative file manifest from $SourceMinecraftDir"
    $managedSourceCandidates = @(
        Get-CobbleManagedSourceFiles -SourceMinecraftDir $SourceMinecraftDir -IncludeRoots $IncludeRoots `
            -IncludeFiles $IncludeFiles -AllowedRoots $AllowedRoots |
            Where-Object { $OfficialPackProfilePaths -inotcontains $_.path }
        Get-OfficialPackProfileSources -MinecraftDirectory $SourceMinecraftDir -TemplateDirectory $SeedTemplateDir
    )
    $allManagedSourceFiles = @(
        $managedSourceCandidates |
            ForEach-Object {
                $item = Get-Item -LiteralPath $_.full
                [pscustomobject]@{ full = $_.full; path = $_.path; size = $item.Length; sha256 = (Get-Sha256 $_.full) }
            }
    )
    $optionalAxiomSources = @($allManagedSourceFiles | Where-Object {
        $path = [string]$_.path
        $path.StartsWith('mods/', [StringComparison]::OrdinalIgnoreCase) -and
            $path.Substring('mods/'.Length).StartsWith('axiom', [StringComparison]::OrdinalIgnoreCase) -and
            $path.Substring('mods/'.Length) -imatch '\.jar(?:\.disabled)?$'
    })
    $shaderOptionSeedSources = @($allManagedSourceFiles | Where-Object {
        $path = [string]$_.path
        if (-not $path.StartsWith('shaderpacks/', [StringComparison]::OrdinalIgnoreCase)) { return $false }
        $underShaderpacks = $path.Substring('shaderpacks/'.Length)
        return -not $underShaderpacks.Contains('/') -and $underShaderpacks.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)
    })
    $sourceFiles = @($allManagedSourceFiles | Where-Object {
        $candidate = $_
        -not ($optionalAxiomSources | Where-Object { $_.path -ieq $candidate.path }) -and
            -not ($shaderOptionSeedSources | Where-Object { $_.path -ieq $candidate.path })
    })

    $effectiveSeedPaths = @($SeedFiles | Where-Object { $OfficialPackProfilePaths -inotcontains $_.Replace('\', '/') }) + @($optionalAxiomSources | ForEach-Object { $_.path }) +
        @($shaderOptionSeedSources | ForEach-Object { $_.path })
    $seedSourceFiles = @(
        Get-CobbleSeedSourceFiles -SourceMinecraftDir $SourceMinecraftDir -SeedFiles $effectiveSeedPaths `
            -SeedRoots $SeedRoots -ExcludeFiles $IncludeFiles -SeedTemplateDir $SeedTemplateDir |
            ForEach-Object {
                $item = Get-Item -LiteralPath $_.full
                [pscustomobject]@{ full = $_.full; path = $_.path; size = $item.Length; sha256 = (Get-Sha256 $_.full) }
            }
    )

    $currentSet = ConvertTo-CobbleFileRecordSet -Entries @($sourceFiles) -Context 'current authoritative files'
    $seedSet = ConvertTo-CobbleSeedFileRecordSet -Entries @($seedSourceFiles) -Context 'current create-only defaults'
    foreach ($seed in $seedSet.Entries) {
        if ($currentSet.ByKey.ContainsKey($seed.path.Normalize([Text.NormalizationForm]::FormC).ToUpperInvariant())) {
            throw "Create-only default overlaps an authoritative managed file: $($seed.path)"
        }
    }
    $reofferSeedPaths = [Collections.Generic.List[string]]::new()
    $reofferSeedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($requestedPath in $ReofferSeedFiles) {
        $normalized = $requestedPath.Replace('\', '/')
        Assert-CobbleSeedPathPolicy -Path $normalized -Context 're-offered create-only default' | Out-Null
        $key = Get-CobblePathKey $normalized
        if (-not $reofferSeedKeys.Add($key)) {
            throw "Duplicate re-offered create-only default: $normalized"
        }
        if (-not $seedSet.ByKey.ContainsKey($key)) {
            throw "Re-offered create-only default is not present in the signed seed set: $normalized"
        }
        $reofferSeedPaths.Add([string]$seedSet.ByKey[$key].path)
    }
    if ($FullBaseline -and $reofferSeedPaths.Count -ne 0) {
        throw 'Full baselines cannot re-offer create-only defaults; they already initialize all supplied seeds for fresh installs.'
    }
    $seedTextReplacements = @(Read-SeedTextReplacementManifest $SeedTextReplacementManifest)
    foreach ($replacement in $seedTextReplacements) {
        $key = Get-CobblePathKey ([string]$replacement.path)
        $isOptionsMigration = [string]$replacement.path -ieq 'options.txt'
        if (-not $seedSet.ByKey.ContainsKey($key) -or
            ($isOptionsMigration -and $reofferSeedKeys.Contains($key)) -or
            (-not $isOptionsMigration -and -not $reofferSeedKeys.Contains($key))) {
            throw "Seed text replacement has an invalid seed/re-offer relationship: $($replacement.path)"
        }
    }
    if ($FullBaseline -and $seedTextReplacements.Count -ne 0) {
        throw 'Full baselines cannot carry one-time seed text replacements.'
    }

    $managedRepairKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $baseFilesByKey = ConvertTo-CobbleFileRecordSet -Entries @($baseFiles) -Context 'signed base repair candidates' -AllowEmpty
    foreach ($currentFile in $currentSet.Entries) {
        $key = Get-CobblePathKey $currentFile.path
        if ($baseFilesByKey.ByKey.ContainsKey($key) -and
            -not (Test-CobbleSameFileRecord -Left $currentFile -Right $baseFilesByKey.ByKey[$key])) {
            [void]$managedRepairKeys.Add($key)
        }
    }
    $potentialCleanupOverlaps = @($currentSet.Entries) + @($baseFiles) + @($seedSet.Entries)
    $forbiddenCleanupPaths = @($potentialCleanupOverlaps | Where-Object {
        $key = Get-CobblePathKey $_.path
        -not $reofferSeedKeys.Contains($key) -and -not $managedRepairKeys.Contains($key)
    } | ForEach-Object { $_.path })
    $legacyCleanup = Read-LegacyCleanupManifest $LegacyCleanupManifest $forbiddenCleanupPaths
    $legacyCleanupSet = ConvertTo-CobbleLegacyCleanupSet -Entries @($legacyCleanup) -Context 'legacy cleanup manifest'
    $sourcePathByKey = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $sourceFiles) { $sourcePathByKey.Add($source.path.Normalize([Text.NormalizationForm]::FormC), $source.full) }
    $seedSourcePathByKey = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $seedSourceFiles) { $seedSourcePathByKey.Add($source.path.Normalize([Text.NormalizationForm]::FormC), $source.full) }

    if ($FullBaseline) {
        $authoritativeFiles = @($currentSet.Entries)
        $payloadFiles = @($authoritativeFiles)
        $deletedFiles = @()
    }
    else {
        $ownershipTransitions = @($baseFiles | Where-Object {
            $seedSet.ByKey.ContainsKey((Get-CobblePathKey $_.path))
        })
        foreach ($transition in $ownershipTransitions) {
            $key = Get-CobblePathKey $transition.path
            $hasExactTransitionIdentity = $legacyCleanupSet.ByKey.ContainsKey($key) -and @(
                $legacyCleanupSet.ByKey[$key] | Where-Object { Test-CobbleSameFileRecord -Left $_ -Right $transition }
            ).Count -gt 0
            if (-not $reofferSeedKeys.Contains($key) -or -not $hasExactTransitionIdentity) {
                throw "Managed-to-player-owned transition requires an exact signed cleanup identity and a corrective re-offer: $($transition.path)"
            }
        }
        $deltaPlan = New-CobbleDeltaPlan -CurrentFiles @($currentSet.Entries) -BaseFiles $baseFiles `
            -OwnershipTransitionPaths @($ownershipTransitions | ForEach-Object { $_.path })
        $authoritativeFiles = @($deltaPlan.Files)
        $payloadFiles = @($deltaPlan.PayloadFiles)
        $deletedFiles = @($deltaPlan.DeletedFiles)
        Write-Host "Delta plan: $($payloadFiles.Count) changed/new, $($deletedFiles.Count) deleted, $($deltaPlan.UnchangedFiles.Count) unchanged."
    }

    $explicitSourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($explicitPath in $IncludeFiles) {
        [void]$explicitSourcePaths.Add($explicitPath.Replace('\', '/').Normalize([Text.NormalizationForm]::FormC))
    }
    foreach ($baseFile in $baseFiles) {
        $normalized = $baseFile.path.Normalize([Text.NormalizationForm]::FormC)
        Assert-CobbleSourcePathPolicy -Path $baseFile.path -Context 'signed base manifest file' `
            -ExplicitSourceFile:($explicitSourcePaths.Contains($normalized)) | Out-Null
    }
    foreach ($authoritativeFile in $authoritativeFiles) {
        $normalized = $authoritativeFile.path.Normalize([Text.NormalizationForm]::FormC)
        Assert-CobbleSourcePathPolicy -Path $authoritativeFile.path -Context 'final authoritative manifest file' `
            -ExplicitSourceFile:($explicitSourcePaths.Contains($normalized)) | Out-Null
    }

    foreach ($payloadFile in $payloadFiles) {
        $key = $payloadFile.path.Normalize([Text.NormalizationForm]::FormC)
        if (-not $sourcePathByKey.ContainsKey($key)) { throw "Payload file is not backed by a hashed canonical source: $($payloadFile.path)" }
    }

    $archiveFiles = @(
        foreach ($payloadFile in $payloadFiles) {
            $key = $payloadFile.path.Normalize([Text.NormalizationForm]::FormC)
            [pscustomobject]@{
                full = $sourcePathByKey[$key]
                path = $payloadFile.path
                size = $payloadFile.size
                sha256 = $payloadFile.sha256
            }
        }
        foreach ($seedFile in $seedSet.Entries) {
            $key = $seedFile.path.Normalize([Text.NormalizationForm]::FormC)
            if (-not $seedSourcePathByKey.ContainsKey($key)) { throw "Create-only default is not backed by a hashed source: $($seedFile.path)" }
            [pscustomobject]@{
                full = $seedSourcePathByKey[$key]
                path = $seedFile.path
                size = $seedFile.size
                sha256 = $seedFile.sha256
            }
        }
    )

    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    Assert-Under $OutputRoot $ReleaseOutputRoot
    $payloadResult = New-PayloadParts -PayloadFiles $archiveFiles -PayloadSeedFiles @($seedSet.Entries)

    $manifest = if ($FullBaseline) {
        [ordered]@{
            schemaVersion = 1
            modpackId = 'cobble-music'
            channel = 'stable'
            version = $Version
            releaseTag = "modpack-v$Version"
            minimumUpdaterVersion = $RequiredUpdaterVersion
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            payload = $payloadResult.Payload
            files = @($authoritativeFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            seedFiles = @($seedSet.Entries | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
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
            minimumUpdaterVersion = $RequiredUpdaterVersion
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            base = [ordered]@{ version = $BaseVersion; manifestSha256 = $baseHash }
            payload = $payloadResult.Payload
            files = @($authoritativeFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            payloadFiles = @($payloadFiles | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            seedFiles = @($seedSet.Entries | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
            reofferSeedPaths = @($reofferSeedPaths | Sort-Object)
            seedTextReplacements = @($seedTextReplacements)
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
- First-install player-owned defaults: $($seedSet.Entries.Count)
- Optional Axiom artifacts: $($optionalAxiomSources.Count)
- Exact signed-base deletions: $($deletedFiles.Count)
- Payload size: $($payloadResult.Size) bytes
- Parts: $($payloadResult.Parts.Count), each at most $ChunkSizeMiB MiB
- Source: reviewed canonical client snapshot supplied to this command
"@

    Get-ExpectedStagedAssets $signedManifest $stagedIdentity | Out-Null
    Write-Host "Staged signed release at $OutputRoot"
    Write-Host "Authoritative files: $($authoritativeFiles.Count); managed payload files: $($payloadFiles.Count); player-owned defaults: $($seedSet.Entries.Count); chunks: $($payloadResult.Parts.Count)"
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
