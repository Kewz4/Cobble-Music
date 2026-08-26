$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$Module = Join-Path $Root 'tools\CobbleMusicRelease.Core.psm1'
$Publisher = Join-Path $Root 'tools\Publish-CobbleMusicRelease.ps1'
Import-Module $Module -Force

function New-Hash([char]$Character) { return [string]::new($Character, 64) }

function New-Record([string]$Path, [int64]$Size, [char]$HashCharacter) {
    return [pscustomobject]@{ path = $Path; size = $Size; sha256 = (New-Hash $HashCharacter) }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw "Expected failure did not occur: $Message" }
    catch {
        if ($_.Exception.Message -like 'Expected failure did not occur:*') { throw }
    }
}

$base = @(
    (New-Record 'mods/unchanged.jar' 10 'a'),
    (New-Record 'config/changed.json' 20 'b'),
    (New-Record 'resourcepacks/deleted.zip' 30 'c')
)
$current = @(
    (New-Record 'mods/unchanged.jar' 10 'a'),
    (New-Record 'config/changed.json' 21 'd'),
    (New-Record 'scripts/new.js' 40 'e')
)

$plan = New-CobbleDeltaPlan -CurrentFiles $current -BaseFiles $base
Assert-True ($plan.Files.Count -eq 3) 'Delta plan did not retain the complete authoritative file set.'
Assert-True ($plan.PayloadFiles.Count -eq 2) 'Delta plan did not select exactly changed/new files.'
Assert-True ($plan.DeletedFiles.Count -eq 1) 'Delta plan did not select exactly removed base files.'
Assert-True ($plan.UnchangedFiles.Count -eq 1) 'Delta plan did not classify unchanged files.'
$deleted = $plan.DeletedFiles[0]
Assert-True ($deleted.path -ceq 'resourcepacks/deleted.zip' -and $deleted.size -eq 30 -and $deleted.sha256 -ceq (New-Hash 'c')) 'Deletion metadata did not come exactly from the signed base.'

$baseManifest = [pscustomobject]@{
    schemaVersion = 1
    modpackId = 'cobble-music'
    channel = 'stable'
    version = '1.0.4'
    releaseTag = 'modpack-v1.0.4'
    files = $base
}
$baseHash = New-Hash 'f'
$manifest = [pscustomobject]@{
    schemaVersion = 2
    modpackId = 'cobble-music'
    channel = 'stable'
    version = '1.0.5'
    releaseTag = 'modpack-v1.0.5'
    minimumUpdaterVersion = '1.2.0'
    base = [pscustomobject]@{ version = '1.0.4'; manifestSha256 = $baseHash }
    payload = [pscustomobject]@{
        archiveName = 'cobble-music-payload.zip'
        size = 1
        sha256 = (New-Hash '1')
        parts = @([pscustomobject]@{ name = 'cobble-music-payload.part001'; size = 1; sha256 = (New-Hash '2') })
    }
    files = $plan.Files
    payloadFiles = $plan.PayloadFiles
    deletedFiles = $plan.DeletedFiles
    legacyCleanup = @()
}
Assert-True (Assert-CobbleDeltaManifest -Manifest $manifest -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash) 'Valid v2 manifest was rejected.'
$rawDeltaMissingDeletePaths = ($manifest | ConvertTo-Json -Depth 12) | ConvertFrom-Json
Assert-True ($null -eq $rawDeltaMissingDeletePaths.PSObject.Properties['deletePaths']) 'Raw delta fixture unexpectedly contains deletePaths.'
Assert-True (Assert-CobbleDeltaManifest -Manifest $rawDeltaMissingDeletePaths -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash) 'Missing v2 deletePaths did not preserve the runtime initialized empty list.'
foreach ($nullCollection in @('files', 'payloadFiles', 'deletedFiles', 'deletePaths', 'legacyCleanup')) {
    $deltaExplicitNull = $manifest.PSObject.Copy()
    if ($null -eq $deltaExplicitNull.PSObject.Properties[$nullCollection]) {
        $deltaExplicitNull | Add-Member -NotePropertyName $nullCollection -NotePropertyValue $null
    }
    else {
        $deltaExplicitNull.$nullCollection = $null
    }
    $deltaExplicitNull = ($deltaExplicitNull | ConvertTo-Json -Depth 12) | ConvertFrom-Json
    Assert-Throws { Assert-CobbleDeltaManifest -Manifest $deltaExplicitNull -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash } "V2 staged resume accepted explicit-null required collection $nullCollection."
}

$baselineManifest = [pscustomobject]@{
    schemaVersion = 1
    modpackId = 'cobble-music'
    channel = 'stable'
    version = '1.0.4'
    releaseTag = 'modpack-v1.0.4'
    minimumUpdaterVersion = '1.0.0'
    payload = [pscustomobject]@{
        archiveName = 'cobble-music-payload.zip'
        size = 1
        sha256 = (New-Hash '1')
        parts = @([pscustomobject]@{ name = 'cobble-music-payload.part001'; size = 1; sha256 = (New-Hash '2') })
    }
    files = $base
    deletePaths = @()
    legacyCleanup = @()
}
Assert-True (Assert-CobbleV1Manifest -Manifest $baselineManifest) 'Valid schema-v1 baseline was rejected.'
$rawLegacyMissingFields = ($baselineManifest | ConvertTo-Json -Depth 12) | ConvertFrom-Json
Assert-True ($null -eq $rawLegacyMissingFields.PSObject.Properties['payloadFiles']) 'Raw legacy fixture unexpectedly contains payloadFiles.'
Assert-True (Assert-CobbleV1Manifest -Manifest $rawLegacyMissingFields) 'Truly absent legacy v1 collections were not treated as initialized empty runtime lists.'

$legacyExplicitNullBase = $baselineManifest.PSObject.Copy()
$legacyExplicitNullBase | Add-Member -NotePropertyName base -NotePropertyValue $null
$legacyExplicitNullBase = ($legacyExplicitNullBase | ConvertTo-Json -Depth 12) | ConvertFrom-Json
Assert-True (Assert-CobbleV1Manifest -Manifest $legacyExplicitNullBase) 'Explicit null v1 base did not match the runtime nullable base model.'
foreach ($nullCollection in @('files', 'payloadFiles', 'deletedFiles', 'deletePaths', 'legacyCleanup')) {
    $legacyExplicitNull = $baselineManifest.PSObject.Copy()
    if ($null -eq $legacyExplicitNull.PSObject.Properties[$nullCollection]) {
        $legacyExplicitNull | Add-Member -NotePropertyName $nullCollection -NotePropertyValue $null
    }
    else {
        $legacyExplicitNull.$nullCollection = $null
    }
    $legacyExplicitNull = ($legacyExplicitNull | ConvertTo-Json -Depth 12) | ConvertFrom-Json
    Assert-True ($null -ne $legacyExplicitNull.PSObject.Properties[$nullCollection] -and $null -eq $legacyExplicitNull.$nullCollection) "Raw v1 $nullCollection fixture did not preserve explicit JSON null."
    Assert-Throws { Assert-CobbleV1Manifest -Manifest $legacyExplicitNull } "V1 staged resume accepted explicit-null required collection $nullCollection."
}

$badMinimumBaseline = $baselineManifest.PSObject.Copy()
$badMinimumBaseline.minimumUpdaterVersion = '1.02.0'
Assert-Throws { Assert-CobbleV1Manifest -Manifest $badMinimumBaseline } 'Non-canonical v1 minimumUpdaterVersion was accepted for staged resume.'
$futureMinimumBaseline = $baselineManifest.PSObject.Copy()
$futureMinimumBaseline.minimumUpdaterVersion = '1.2.3'
Assert-Throws { Assert-CobbleV1Manifest -Manifest $futureMinimumBaseline } 'V1 requiring a newer-than-pinned updater was accepted for staged resume.'
$deltaBaseInV1 = $baselineManifest.PSObject.Copy()
$deltaBaseInV1 | Add-Member -NotePropertyName base -NotePropertyValue ([pscustomobject]@{ version = '1.0.3'; manifestSha256 = (New-Hash 'a') })
Assert-Throws { Assert-CobbleV1Manifest -Manifest $deltaBaseInV1 } 'V1 staged resume accepted a delta-only base.'
$payloadFilesInV1 = $baselineManifest.PSObject.Copy()
$payloadFilesInV1 | Add-Member -NotePropertyName payloadFiles -NotePropertyValue @($base[0])
Assert-Throws { Assert-CobbleV1Manifest -Manifest $payloadFilesInV1 } 'V1 staged resume accepted nonempty payloadFiles.'
$deletedFilesInV1 = $baselineManifest.PSObject.Copy()
$deletedFilesInV1 | Add-Member -NotePropertyName deletedFiles -NotePropertyValue @($base[0])
Assert-Throws { Assert-CobbleV1Manifest -Manifest $deletedFilesInV1 } 'V1 staged resume accepted nonempty deletedFiles.'

$badBaseline = $baselineManifest.PSObject.Copy()
$badBaseline.deletePaths = @('mods/unchanged.jar')
Assert-Throws { Assert-CobbleV1Manifest -Manifest $badBaseline } 'Baseline deletion overlapping authoritative files was accepted.'
$fourPartBaseline = $baselineManifest.PSObject.Copy()
$fourPartBaseline.version = '1.0.4.0'
$fourPartBaseline.releaseTag = 'modpack-v1.0.4.0'
Assert-Throws { Assert-CobbleV1Manifest -Manifest $fourPartBaseline } 'Four-part baseline version was accepted.'

$deletionBase = @((New-Record 'mods/keep.jar' 1 'a'), (New-Record 'mods/remove.jar' 2 'b'))
$deletionCurrent = @((New-Record 'mods/keep.jar' 1 'a'))
$deletionPlan = New-CobbleDeltaPlan -CurrentFiles $deletionCurrent -BaseFiles $deletionBase
$deletionBaseManifest = [pscustomobject]@{ files = $deletionBase }
$deletionManifest = [pscustomobject]@{
    schemaVersion = 2
    modpackId = 'cobble-music'
    channel = 'stable'
    version = '1.0.6'
    releaseTag = 'modpack-v1.0.6'
    minimumUpdaterVersion = '1.2.0'
    base = [pscustomobject]@{ version = '1.0.5'; manifestSha256 = $baseHash }
    payload = $null
    files = $deletionPlan.Files
    payloadFiles = @()
    deletedFiles = $deletionPlan.DeletedFiles
    legacyCleanup = @()
}
$deletionBaseManifest | Add-Member -NotePropertyName version -NotePropertyValue '1.0.5'
Assert-True (Assert-CobbleDeltaManifest -Manifest $deletionManifest -BaseManifest $deletionBaseManifest -ExpectedBaseManifestSha256 $baseHash) 'Deletion-only delta was rejected.'

$noChangeManifest = $deletionManifest.PSObject.Copy()
$noChangeManifest.files = $deletionBase
$noChangeManifest.payloadFiles = @()
$noChangeManifest.deletedFiles = @()
Assert-Throws { Assert-CobbleDeltaManifest -Manifest $noChangeManifest -BaseManifest $deletionBaseManifest -ExpectedBaseManifestSha256 $baseHash } 'No-op delta was accepted.'

foreach ($unsafe in @(
    'mods\evil.jar',
    'mods/../evil.jar',
    'unknown/file.jar',
    'mods/CON.txt',
    'mods/trailing.',
    '/mods/rooted.jar'
)) {
    Assert-Throws { Assert-CobbleManagedPath -Path $unsafe | Out-Null } "Unsafe path was accepted: $unsafe"
}

Assert-Throws {
    ConvertTo-CobbleFileRecordSet -Entries @(
        (New-Record 'mods/Case.jar' 1 'a'),
        (New-Record 'mods/case.jar' 1 'b')
    ) -Context 'collision test' | Out-Null
} 'Case-insensitive Windows path collision was accepted.'

$precomposed = "mods/caf$([char]0x00e9).jar"
$decomposed = "mods/cafe$([char]0x0301).jar"
Assert-Throws {
    ConvertTo-CobbleFileRecordSet -Entries @(
        (New-Record $precomposed 1 'a'),
        (New-Record $decomposed 1 'b')
    ) -Context 'Unicode collision test' | Out-Null
} 'Unicode-normalization path collision was accepted.'

Assert-Throws {
    New-CobbleDeltaPlan -BaseFiles @((New-Record 'mods/Name.jar' 1 'a')) -CurrentFiles @((New-Record 'mods/name.jar' 1 'a')) | Out-Null
} 'Case-only delta rename was accepted.'

Assert-Throws { Assert-CobbleVersionAdvance -BaseVersion '1.0.5' -TargetVersion '1.0.5' } 'Equal delta version was accepted.'
Assert-Throws { Assert-CobbleVersionAdvance -BaseVersion '1.0.5' -TargetVersion '1.0.4' } 'Downgrade delta version was accepted.'
Assert-Throws { Assert-CobbleVersionAdvance -BaseVersion '1.0.4' -TargetVersion '1.00.5' } 'Non-canonical delta version was accepted.'
Assert-Throws { Assert-CobbleVersionAdvance -BaseVersion '1.0.4' -TargetVersion '1.0.5.0' } 'Four-part delta version was accepted.'
Assert-Throws { Assert-CobbleVersionAdvance -BaseVersion '01.0.4' -TargetVersion '1.0.5' } 'Leading-zero base version was accepted.'

$tamperedPayload = $manifest.PSObject.Copy()
$tamperedPayload.payloadFiles = @($plan.PayloadFiles | Where-Object path -ne 'config/changed.json')
Assert-Throws { Assert-CobbleDeltaManifest -Manifest $tamperedPayload -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash } 'Incomplete payloadFiles set was accepted.'

$tamperedDeletion = $manifest.PSObject.Copy()
$tamperedDeletion.deletedFiles = @((New-Record 'resourcepacks/deleted.zip' 30 '9'))
Assert-Throws { Assert-CobbleDeltaManifest -Manifest $tamperedDeletion -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash } 'Tampered deletedFiles metadata was accepted.'

$pathOnlyDeltaDeletion = $manifest.PSObject.Copy()
$pathOnlyDeltaDeletion | Add-Member -NotePropertyName deletePaths -NotePropertyValue @('mods/old.jar')
Assert-Throws { Assert-CobbleDeltaManifest -Manifest $pathOnlyDeltaDeletion -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash } 'V2 staged resume accepted nonempty path-only deletePaths.'

$overlappingLegacy = $manifest.PSObject.Copy()
$overlappingLegacy.legacyCleanup = @((New-Record 'mods/unchanged.jar' 10 'a'))
Assert-Throws { Assert-CobbleDeltaManifest -Manifest $overlappingLegacy -BaseManifest $baseManifest -ExpectedBaseManifestSha256 $baseHash } 'legacyCleanup overlapping the signed base was accepted.'

$expectedAssets = @(
    [pscustomobject]@{ name = 'cobble-music-update.json'; size = 100; sha256 = (New-Hash 'a') },
    [pscustomobject]@{ name = 'cobble-music-update.sig'; size = 200; sha256 = (New-Hash 'b') },
    [pscustomobject]@{ name = 'cobble-music-payload.part001'; size = 300; sha256 = (New-Hash 'c') }
)
$remoteAssets = @($expectedAssets | ForEach-Object {
    [pscustomobject]@{ name = $_.name; size = $_.size; digest = "sha256:$($_.sha256)"; state = 'uploaded' }
})
Assert-True (@(Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $remoteAssets -RequireComplete).Count -eq 0) 'Exact remote asset inventory was rejected.'
$localBaseAssets = @($expectedAssets | Where-Object name -in @('cobble-music-update.json', 'cobble-music-update.sig'))
Assert-True (Assert-CobblePublishedBaseAssets -LocalAssets $localBaseAssets -RemoteAssets $remoteAssets) 'Exact local base assets did not match the published release assets.'

$mutatedLocalBase = @($localBaseAssets | ForEach-Object { $_.PSObject.Copy() })
$mutatedLocalBase[0].sha256 = New-Hash '9'
Assert-Throws { Assert-CobblePublishedBaseAssets -LocalAssets $mutatedLocalBase -RemoteAssets $remoteAssets } 'Local base manifest differing from the published digest was accepted.'
$wrongSizeLocalBase = @($localBaseAssets | ForEach-Object { $_.PSObject.Copy() })
$wrongSizeLocalBase[1].size++
Assert-Throws { Assert-CobblePublishedBaseAssets -LocalAssets $wrongSizeLocalBase -RemoteAssets $remoteAssets } 'Local base signature differing from the published raw size was accepted.'

$unfinishedPublishedBase = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$unfinishedPublishedBase[1].state = 'starter'
Assert-Throws { Assert-CobblePublishedBaseAssets -LocalAssets $localBaseAssets -RemoteAssets $unfinishedPublishedBase } 'Non-uploaded published base signature was accepted.'

$missingPublishedBase = @($remoteAssets | Where-Object name -ne 'cobble-music-update.sig')
Assert-Throws { Assert-CobblePublishedBaseAssets -LocalAssets $localBaseAssets -RemoteAssets $missingPublishedBase } 'Published release missing its exact signature asset was accepted.'

$localUpdater = [pscustomobject]@{ name = 'CobbleMusicUpdater.exe'; size = 123456; sha256 = (New-Hash 'e') }
$publishedUpdaterAssets = @(
    [pscustomobject]@{ name = 'CobbleMusicUpdater.exe'; size = 123456; digest = "sha256:$(New-Hash 'e')"; state = 'uploaded' },
    [pscustomobject]@{ name = 'Bootstrap-CobbleMusicUpdater.ps1'; size = 999; digest = "sha256:$(New-Hash 'f')"; state = 'uploaded' }
)
Assert-True (Assert-CobblePublishedUpdaterAsset -LocalAsset $localUpdater -RemoteAssets $publishedUpdaterAssets) 'Exact pinned updater did not match its public stable release asset.'
$unfinishedUpdater = @($publishedUpdaterAssets | ForEach-Object { $_.PSObject.Copy() })
$unfinishedUpdater[0].state = 'starter'
Assert-Throws { Assert-CobblePublishedUpdaterAsset -LocalAsset $localUpdater -RemoteAssets $unfinishedUpdater } 'Non-uploaded public updater asset was accepted.'
$replacedUpdater = @($publishedUpdaterAssets | ForEach-Object { $_.PSObject.Copy() })
$replacedUpdater[0].digest = "sha256:$(New-Hash '9')"
Assert-Throws { Assert-CobblePublishedUpdaterAsset -LocalAsset $localUpdater -RemoteAssets $replacedUpdater } 'Public updater asset with a replaced digest was accepted.'
$missingUpdater = @($publishedUpdaterAssets | Where-Object name -ne 'CobbleMusicUpdater.exe')
Assert-Throws { Assert-CobblePublishedUpdaterAsset -LocalAsset $localUpdater -RemoteAssets $missingUpdater } 'Public stable updater release missing its EXE was accepted.'

Assert-True (Test-CobblePaginationHasNextPage -Page 9 -ResultCount 100 -MaximumPages 10 -Context 'test release index') 'A full nonterminal release-index page did not continue.'
Assert-True (-not (Test-CobblePaginationHasNextPage -Page 3 -ResultCount 99 -MaximumPages 10 -Context 'test release index')) 'A partial release-index page incorrectly continued.'
Assert-Throws { Test-CobblePaginationHasNextPage -Page 10 -ResultCount 100 -MaximumPages 10 -Context 'test release index' | Out-Null } 'A full final release-index page was silently truncated.'

Assert-True (Assert-CobbleReleaseAssetCount -PayloadPartCount 997) 'The safe 997-part / 999-total-asset boundary was rejected.'
Assert-Throws { Assert-CobbleReleaseAssetCount -PayloadPartCount 998 | Out-Null } 'A release with 998 payload parts / 1,000 total assets was accepted.'

$publicReleaseIndex498 = @(1..498 | ForEach-Object { [pscustomobject]@{ id = $_; draft = $false; prerelease = ($_ -eq 498) } })
$publicReleaseIndex499 = @($publicReleaseIndex498) + @([pscustomobject]@{ id = 499; draft = $false; prerelease = $false })
Assert-True (Assert-CobblePublicReleaseCapacity -Releases $publicReleaseIndex498 -AdditionalPublicReleases 1) 'Publishing the 499th public release was rejected.'
Assert-True (Assert-CobblePublicReleaseCapacity -Releases $publicReleaseIndex499 -AdditionalPublicReleases 0) 'An existing 499-release public index was rejected.'
Assert-Throws { Assert-CobblePublicReleaseCapacity -Releases $publicReleaseIndex499 -AdditionalPublicReleases 1 | Out-Null } 'Publishing a 500th public release was accepted.'
Assert-Throws { Assert-CobblePublicReleaseCapacity -Releases @([pscustomobject]@{ id = 1; draft = 'false' }) -AdditionalPublicReleases 1 | Out-Null } 'String-valued draft=false undercounted the public release index.'

$pathTestRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-key-isolation-$([Guid]::NewGuid().ToString('N'))"
$sourceRoot = Join-Path $pathTestRoot 'minecraft'
$releaseRoot = Join-Path $pathTestRoot 'release-output'
$externalManagedRoot = Join-Path $pathTestRoot 'external-managed'
Assert-True (Assert-CobblePrivateKeyIsolation -PrivateKeyPath (Join-Path $pathTestRoot 'safe\private.key') -SourceMinecraftDir $sourceRoot -ReleaseOutputRoot $releaseRoot -ManagedRoots @($externalManagedRoot)) 'Safe external private-key location was rejected.'
Assert-Throws { Assert-CobblePrivateKeyIsolation -PrivateKeyPath (Join-Path $sourceRoot 'mods\private.key') -SourceMinecraftDir $sourceRoot -ReleaseOutputRoot $releaseRoot -ManagedRoots @() } 'Private key under the source was accepted.'
Assert-Throws { Assert-CobblePrivateKeyIsolation -PrivateKeyPath (Join-Path $releaseRoot '1.0.5\private.key') -SourceMinecraftDir $sourceRoot -ReleaseOutputRoot $releaseRoot -ManagedRoots @() } 'Private key under release staging was accepted.'
Assert-Throws { Assert-CobblePrivateKeyIsolation -PrivateKeyPath (Join-Path $externalManagedRoot 'private.key') -SourceMinecraftDir $sourceRoot -ReleaseOutputRoot $releaseRoot -ManagedRoots @($externalManagedRoot) } 'Private key under an inventoried managed root was accepted.'

$lockTestRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-publisher-lock-test-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($lockTestRoot) | Out-Null
$manifestLock = $null
$signatureLock = $null
$boundaryLock = $null
try {
    $lockedManifestPath = Join-Path $lockTestRoot 'cobble-music-update.json'
    $lockedSignaturePath = Join-Path $lockTestRoot 'cobble-music-update.sig'
    [IO.File]::WriteAllText($lockedManifestPath, '{"locked":true}')
    [IO.File]::WriteAllText($lockedSignaturePath, '{"signature":"locked"}')
    $manifestLock = Open-CobbleLockedFileSnapshot $lockedManifestPath
    $signatureLock = Open-CobbleLockedFileSnapshot $lockedSignaturePath
    Assert-True (Assert-CobbleLockedFileSnapshot $manifestLock) 'Locked manifest snapshot failed its initial identity check.'
    Assert-True (Assert-CobbleLockedFileSnapshot $signatureLock) 'Locked signature snapshot failed its initial identity check.'

    Assert-Throws { [IO.File]::WriteAllText($lockedManifestPath, '{"swapped":true}') } 'Locked staged manifest could be overwritten between verification and upload.'
    $replacementPath = Join-Path $lockTestRoot 'replacement.json'
    [IO.File]::WriteAllText($replacementPath, '{"replacement":true}')
    Assert-Throws { [IO.File]::Move($replacementPath, $lockedManifestPath, $true) } 'Locked staged manifest could be atomically replaced between verification and upload.'
    Assert-Throws { [IO.File]::WriteAllText($lockedSignaturePath, '{"signature":"swapped"}') } 'Locked staged signature could be overwritten after verification.'

    $lockedBaseAssets = @(
        [pscustomobject]@{ name = 'cobble-music-update.json'; size = $manifestLock.size; sha256 = $manifestLock.sha256 },
        [pscustomobject]@{ name = 'cobble-music-update.sig'; size = $signatureLock.size; sha256 = $signatureLock.sha256 }
    )
    $lockedRemoteAssets = @($lockedBaseAssets | ForEach-Object {
        [pscustomobject]@{ name = $_.name; size = $_.size; digest = "sha256:$($_.sha256)"; state = 'uploaded' }
    })
    Assert-True (Assert-CobblePublishedBaseAssets -LocalAssets $lockedBaseAssets -RemoteAssets $lockedRemoteAssets) 'Exact locked base byte pair was not usable as its remote identity snapshot.'
    Assert-True (Assert-CobbleLockedFileSnapshot $manifestLock) 'Manifest lock identity changed after adversarial swap attempts.'
    Assert-True (Assert-CobbleLockedFileSnapshot $signatureLock) 'Signature lock identity changed after adversarial swap attempts.'

    foreach ($boundary in @(
        [pscustomobject]@{ Name = 'manifest'; MaximumBytes = [int64](8MB) },
        [pscustomobject]@{ Name = 'signature'; MaximumBytes = [int64](64KB) }
    )) {
        $exactBoundaryPath = Join-Path $lockTestRoot "$($boundary.Name)-exact.bin"
        $boundaryWriter = [IO.File]::Open($exactBoundaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $boundaryWriter.SetLength($boundary.MaximumBytes) }
        finally { $boundaryWriter.Dispose() }
        $boundaryLock = Open-CobbleLockedFileSnapshot -Path $exactBoundaryPath -MaximumBytes $boundary.MaximumBytes
        Assert-True ($boundaryLock.size -eq $boundary.MaximumBytes) "Exact $($boundary.Name) snapshot boundary was rejected."
        Close-CobbleLockedFileSnapshot $boundaryLock
        $boundaryLock = $null

        $oversizeBoundaryPath = Join-Path $lockTestRoot "$($boundary.Name)-oversize.bin"
        $boundaryWriter = [IO.File]::Open($oversizeBoundaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $boundaryWriter.SetLength($boundary.MaximumBytes + 1) }
        finally { $boundaryWriter.Dispose() }
        Assert-Throws { Open-CobbleLockedFileSnapshot -Path $oversizeBoundaryPath -MaximumBytes $boundary.MaximumBytes | Out-Null } "Oversize $($boundary.Name) snapshot was accepted."
    }
}
finally {
    Close-CobbleLockedFileSnapshot $boundaryLock
    Close-CobbleLockedFileSnapshot $signatureLock
    Close-CobbleLockedFileSnapshot $manifestLock
    if ([IO.Directory]::Exists($lockTestRoot)) { [IO.Directory]::Delete($lockTestRoot, $true) }
}

$partialRemote = @($remoteAssets | Where-Object name -ne 'cobble-music-payload.part001')
$missing = @(Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $partialRemote)
Assert-True ($missing.Count -eq 1 -and $missing[0] -ceq 'cobble-music-payload.part001') 'Resumable inventory did not identify the exact missing asset.'
Assert-Throws { Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $partialRemote -RequireComplete | Out-Null } 'Incomplete remote inventory passed final validation.'

$starter = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$starter[2].state = 'starter'
$starter[2] | Add-Member -NotePropertyName id -NotePropertyValue 12345
Assert-Throws { Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $starter | Out-Null } 'Unfinalized GitHub starter asset was accepted.'
$repairable = @(Get-CobbleRepairableStarterAssets -ExpectedAssets $expectedAssets -RemoteAssets $starter)
Assert-True ($repairable.Count -eq 1 -and $repairable[0].id -eq 12345 -and $repairable[0].name -ceq 'cobble-music-payload.part001') 'Explicit repair did not select only the expected-name starter asset.'
$deleteCandidate = Get-CobbleStarterAssetForDeletion -Candidate $repairable[0] -ExpectedAssets $expectedAssets -RemoteAssets $starter
Assert-True ($null -ne $deleteCandidate -and $deleteCandidate.id -eq 12345) 'Immediately revalidated starter candidate was not deletable.'
$starterBecameUploaded = @($starter | ForEach-Object { $_.PSObject.Copy() })
$starterBecameUploaded[2].state = 'uploaded'
$skipUploadedCandidate = Get-CobbleStarterAssetForDeletion -Candidate $repairable[0] -ExpectedAssets $expectedAssets -RemoteAssets $starterBecameUploaded
Assert-True ($null -eq $skipUploadedCandidate) 'Repair would delete an asset that transitioned from starter to uploaded.'
$replacementStarter = @($starter | ForEach-Object { $_.PSObject.Copy() })
$replacementStarter[2].id = 67890
$skipReplacementCandidate = Get-CobbleStarterAssetForDeletion -Candidate $repairable[0] -ExpectedAssets $expectedAssets -RemoteAssets $replacementStarter
Assert-True ($null -eq $skipReplacementCandidate) 'Repair would delete a replacement starter with a different API identity.'

$starterWithoutId = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$starterWithoutId[2].state = 'starter'
Assert-Throws { Get-CobbleRepairableStarterAssets -ExpectedAssets $expectedAssets -RemoteAssets $starterWithoutId | Out-Null } 'Starter asset without a safe API id was repairable.'

$wrongDigest = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$wrongDigest[2].digest = "sha256:$(New-Hash 'd')"
Assert-Throws { Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $wrongDigest | Out-Null } 'Wrong GitHub asset digest was accepted.'

$starterWithMismatch = @($starter | ForEach-Object { $_.PSObject.Copy() })
$starterWithMismatch[0].digest = "sha256:$(New-Hash 'd')"
Assert-Throws { Get-CobbleRepairableStarterAssets -ExpectedAssets $expectedAssets -RemoteAssets $starterWithMismatch | Out-Null } 'Starter repair proceeded beside an uploaded mismatched asset.'

$draftRelease = [pscustomobject]@{ id = 1001; tag_name = 'modpack-v1.0.5'; draft = $true; prerelease = $false; published_at = $null }
Assert-True (Assert-CobbleReleaseIdentityState -Release $draftRelease -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState draft) 'Exact draft release identity was rejected.'
$publicRelease = $draftRelease.PSObject.Copy()
$publicRelease.draft = $false
$publicRelease.published_at = '2026-08-26T12:00:00Z'
Assert-True (Assert-CobbleReleaseIdentityState -Release $publicRelease -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState public) 'Exact post-publish release identity was rejected.'
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $draftRelease -ExpectedId 1002 -ExpectedTag 'modpack-v1.0.5' -ExpectedState draft } 'Changed release ID was accepted before mutation.'
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $draftRelease -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.6' -ExpectedState draft } 'Changed release tag was accepted before mutation.'
$prereleaseDraft = $draftRelease.PSObject.Copy()
$prereleaseDraft.prerelease = $true
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $prereleaseDraft -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState draft } 'Prerelease draft was accepted for mutation.'
$stringDraftState = $draftRelease.PSObject.Copy()
$stringDraftState.draft = 'true'
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $stringDraftState -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState draft } 'String-valued draft state was accepted for mutation.'
$stringPrereleaseState = $draftRelease.PSObject.Copy()
$stringPrereleaseState.prerelease = 'false'
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $stringPrereleaseState -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState draft } 'String-valued prerelease state was accepted for mutation.'
$publicWithoutTimestamp = $publicRelease.PSObject.Copy()
$publicWithoutTimestamp.published_at = $null
Assert-Throws { Assert-CobbleReleaseIdentityState -Release $publicWithoutTimestamp -ExpectedId 1001 -ExpectedTag 'modpack-v1.0.5' -ExpectedState public } 'Post-publication state without published_at was accepted.'

$unexpected = @($remoteAssets) + @([pscustomobject]@{ name = 'extra.bin'; size = 0; digest = "sha256:$(New-Hash '0')"; state = 'uploaded' })
Assert-Throws { Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $unexpected | Out-Null } 'Unexpected draft asset was accepted.'

$zipTestRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-publisher-zip-test-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($zipTestRoot) | Out-Null
try {
    $zipPath = Join-Path $zipTestRoot 'payload.zip'
    $content = [Text.Encoding]::UTF8.GetBytes('inventoried payload bytes')
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $entry = $zip.CreateEntry('mods/inventoried.txt')
            $entryStream = $entry.Open()
            try { $entryStream.Write($content, 0, $content.Length) }
            finally { $entryStream.Dispose() }
        }
        finally { $zip.Dispose() }
    }
    finally { $zipStream.Dispose() }

    $contentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content)).ToLowerInvariant()
    $zipExpected = @([pscustomobject]@{ path = 'mods/inventoried.txt'; size = $content.Length; sha256 = $contentHash })
    Assert-True (Assert-CobblePayloadZipInventory -ZipPath $zipPath -ExpectedFiles $zipExpected) 'Exact streamed ZIP inventory was rejected.'

    $mutatedInventory = @([pscustomobject]@{ path = 'mods/inventoried.txt'; size = $content.Length; sha256 = (New-Hash '8') })
    Assert-Throws { Assert-CobblePayloadZipInventory -ZipPath $zipPath -ExpectedFiles $mutatedInventory } 'ZIP content changed after source inventory was accepted.'
    $wrongPathInventory = @([pscustomobject]@{ path = 'mods/different.txt'; size = $content.Length; sha256 = $contentHash })
    Assert-Throws { Assert-CobblePayloadZipInventory -ZipPath $zipPath -ExpectedFiles $wrongPathInventory } 'ZIP unexpected/missing entry set was accepted.'
    $missingEntryInventory = @($zipExpected) + @([pscustomobject]@{ path = 'mods/missing.txt'; size = 1; sha256 = (New-Hash '7') })
    Assert-Throws { Assert-CobblePayloadZipInventory -ZipPath $zipPath -ExpectedFiles $missingEntryInventory } 'ZIP missing an inventoried entry was accepted.'
}
finally {
    if ([IO.Directory]::Exists($zipTestRoot)) { [IO.Directory]::Delete($zipTestRoot, $true) }
}

$partsTestRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-publisher-parts-test-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($partsTestRoot) | Out-Null
try {
    $partOneBytes = [Text.Encoding]::UTF8.GetBytes('first staged chunk')
    $partTwoBytes = [Text.Encoding]::UTF8.GetBytes('second staged chunk')
    $allBytes = [byte[]]::new($partOneBytes.Length + $partTwoBytes.Length)
    [Array]::Copy($partOneBytes, 0, $allBytes, 0, $partOneBytes.Length)
    [Array]::Copy($partTwoBytes, 0, $allBytes, $partOneBytes.Length, $partTwoBytes.Length)
    $partOneHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($partOneBytes)).ToLowerInvariant()
    $partTwoHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($partTwoBytes)).ToLowerInvariant()
    $allHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($allBytes)).ToLowerInvariant()
    $partOne = [pscustomobject]@{ name = 'cobble-music-payload.part001'; size = $partOneBytes.Length; sha256 = $partOneHash }
    $partTwo = [pscustomobject]@{ name = 'cobble-music-payload.part002'; size = $partTwoBytes.Length; sha256 = $partTwoHash }
    [IO.File]::WriteAllBytes((Join-Path $partsTestRoot $partOne.name), $partOneBytes)
    [IO.File]::WriteAllBytes((Join-Path $partsTestRoot $partTwo.name), $partTwoBytes)
    $stagedPayload = [pscustomobject]@{
        archiveName = 'cobble-music-payload.zip'
        size = $allBytes.Length
        sha256 = $allHash
        parts = @($partOne, $partTwo)
    }

    $stagedPartIdentities = @(Assert-CobbleStagedPayloadParts -Payload $stagedPayload -StagingRoot $partsTestRoot)
    Assert-True ($stagedPartIdentities.Count -eq 2) 'Exact staged parts failed ordered streaming validation.'

    $reorderedPayload = $stagedPayload.PSObject.Copy()
    $reorderedPayload.parts = @($partTwo, $partOne)
    Assert-Throws { Assert-CobbleStagedPayloadParts -Payload $reorderedPayload -StagingRoot $partsTestRoot | Out-Null } 'Reordered staged parts passed the signed aggregate payload hash.'

    [IO.File]::WriteAllBytes((Join-Path $partsTestRoot $partOne.name), [Text.Encoding]::UTF8.GetBytes('alter staged chunk'))
    Assert-Throws { Assert-CobbleStagedPayloadParts -Payload $stagedPayload -StagingRoot $partsTestRoot | Out-Null } 'Mutated staged part passed resume streaming validation.'
    [IO.File]::WriteAllBytes((Join-Path $partsTestRoot $partOne.name), $partOneBytes)

    $wrongAggregatePayload = $stagedPayload.PSObject.Copy()
    $wrongAggregatePayload.sha256 = New-Hash '8'
    Assert-Throws { Assert-CobbleStagedPayloadParts -Payload $wrongAggregatePayload -StagingRoot $partsTestRoot | Out-Null } 'Wrong signed aggregate payload digest passed resume validation.'

    [IO.File]::Delete((Join-Path $partsTestRoot $partTwo.name))
    Assert-Throws { Assert-CobbleStagedPayloadParts -Payload $stagedPayload -StagingRoot $partsTestRoot | Out-Null } 'Missing staged part passed resume validation.'
}
finally {
    if ([IO.Directory]::Exists($partsTestRoot)) { [IO.Directory]::Delete($partsTestRoot, $true) }
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'Publisher script has PowerShell parse errors.'
$chunkParameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ChunkSizeMiB' }
Assert-True ($null -ne $chunkParameter -and $chunkParameter.DefaultValue.Extent.Text -eq '256') 'Future release chunk default is not 256 MiB.'
$publisherText = [IO.File]::ReadAllText($Publisher)
Assert-True ($publisherText -notmatch '(?i)\bdotnet\b|UpdaterProject|UpdaterDll|Build-UpdaterSigningTool') 'Modpack publisher still builds or executes a mutable working-tree updater.'
Assert-True ($publisherText -match [regex]::Escape("updater\dist\win-x64\CobbleMusicUpdater.exe")) 'Modpack publisher does not use the distributed updater artifact.'
Assert-True ($publisherText -match 'Get-SingleQuotedBootstrapAssignment[\s\S]+ExpectedUpdaterSha256') 'Modpack publisher does not bind the distributed updater to the bootstrap version/hash.'
foreach ($snapshotLimitFragment in @(
    '$MaximumManifestSnapshotBytes = 8MB',
    '$MaximumSignatureSnapshotBytes = 64KB',
    'Open-CobbleLockedFileSnapshot -Path $ManifestPath -MaximumBytes $MaximumManifestSnapshotBytes',
    'Open-CobbleLockedFileSnapshot -Path $SignaturePath -MaximumBytes $MaximumSignatureSnapshotBytes'
)) {
    Assert-True ($publisherText.Contains($snapshotLimitFragment, [StringComparison]::Ordinal)) "Publisher lost an updater-parity snapshot limit: $snapshotLimitFragment"
}

$newPayloadFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-PayloadParts' }, $true)
Assert-True ($newPayloadFunction.Extent.Text.Contains('Assert-CobbleReleaseAssetCount -PayloadPartCount $parts.Count', [StringComparison]::Ordinal)) 'Fresh staging does not enforce the payload-part asset ceiling before signing.'
$getExpectedFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ExpectedStagedAssets' }, $true)
$getExpectedFunctionText = $getExpectedFunction.Extent.Text
Assert-True ($getExpectedFunctionText.Contains("Get-CobbleOptionalPropertyValue -Object `$Manifest -Name 'payload'", [StringComparison]::Ordinal)) 'Staged resume directly dereferences optional payload under StrictMode.'
Assert-True ($getExpectedFunctionText.Contains('Assert-CobbleReleaseAssetCount -PayloadPartCount $partIdentities.Count', [StringComparison]::Ordinal)) 'Staged resume does not enforce the payload-part asset ceiling.'

# Execute the real expected-assets function against raw JSON that intentionally
# omits optional payload. This is the deletion-only v2 shape produced by
# System.Text.Json and must remain resumable under StrictMode.
$missingPayloadFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-missing-payload-$([Guid]::NewGuid().ToString('N'))"
$missingPayloadManifestLock = $null
$missingPayloadSignatureLock = $null
try {
    [IO.Directory]::CreateDirectory($missingPayloadFixtureRoot) | Out-Null
    $missingPayloadManifestPath = Join-Path $missingPayloadFixtureRoot 'cobble-music-update.json'
    $missingPayloadSignaturePath = Join-Path $missingPayloadFixtureRoot 'cobble-music-update.sig'
    [IO.File]::WriteAllText($missingPayloadManifestPath, '{"schemaVersion":2,"payloadFiles":[],"deletedFiles":[{"path":"mods/old.jar","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}')
    [IO.File]::WriteAllBytes($missingPayloadSignaturePath, [byte[]](1..64))
    $rawDeletionOnlyManifest = [IO.File]::ReadAllText($missingPayloadManifestPath) | ConvertFrom-Json
    Assert-True ($null -eq $rawDeletionOnlyManifest.PSObject.Properties['payload']) 'Deletion-only raw manifest fixture unexpectedly contains payload.'
    $missingPayloadManifestLock = Open-CobbleLockedFileSnapshot $missingPayloadManifestPath
    $missingPayloadSignatureLock = Open-CobbleLockedFileSnapshot $missingPayloadSignaturePath
    $missingPayloadIdentity = [pscustomobject]@{
        Manifest = $missingPayloadManifestLock
        Signature = $missingPayloadSignatureLock
        Assets = @(
            [pscustomobject]@{ name = 'cobble-music-update.json'; path = $missingPayloadManifestLock.path; size = $missingPayloadManifestLock.size; sha256 = $missingPayloadManifestLock.sha256 },
            [pscustomobject]@{ name = 'cobble-music-update.sig'; path = $missingPayloadSignatureLock.path; size = $missingPayloadSignatureLock.size; sha256 = $missingPayloadSignatureLock.sha256 }
        )
    }
    $missingPayloadHarness = [scriptblock]::Create(
        'param($Manifest, $StagedIdentity, [string]$FixtureRoot)' + [Environment]::NewLine +
        'Set-StrictMode -Version Latest' + [Environment]::NewLine +
        $getExpectedFunctionText + [Environment]::NewLine +
        '$OutputRoot = $FixtureRoot' + [Environment]::NewLine +
        'Get-ExpectedStagedAssets $Manifest $StagedIdentity')
    $missingPayloadAssets = @(& $missingPayloadHarness $rawDeletionOnlyManifest $missingPayloadIdentity $missingPayloadFixtureRoot)
    Assert-True ($missingPayloadAssets.Count -eq 2 -and $missingPayloadAssets[0].name -ceq 'cobble-music-update.json' -and $missingPayloadAssets[1].name -ceq 'cobble-music-update.sig') 'Deletion-only raw manifest without payload did not resume to exactly its two metadata assets.'
}
finally {
    Close-CobbleLockedFileSnapshot $missingPayloadSignatureLock
    Close-CobbleLockedFileSnapshot $missingPayloadManifestLock
    if ([IO.Directory]::Exists($missingPayloadFixtureRoot)) { [IO.Directory]::Delete($missingPayloadFixtureRoot, $true) }
}

$publishFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Publish-StagedRelease' }, $true)
$publishFunctionText = $publishFunction.Extent.Text
$patchIndex = $publishFunctionText.IndexOf("'draft=false'", [StringComparison]::Ordinal)
$patchCallStartIndex = $publishFunctionText.IndexOf("Invoke-GhJson -Arguments @('api', '--method', 'PATCH'", [StringComparison]::Ordinal)
$finalUpdaterCheckIndex = $publishFunctionText.LastIndexOf('Assert-PublishedPinnedUpdater', [StringComparison]::Ordinal)
$finalBaseCheckIndex = $publishFunctionText.LastIndexOf('Assert-PublishedBaseReleaseIdentity', [StringComparison]::Ordinal)
Assert-True ($patchIndex -gt 0 -and $finalUpdaterCheckIndex -gt 0 -and $finalUpdaterCheckIndex -lt $patchIndex) 'Pinned updater is not revalidated immediately before publication.'
Assert-True ($finalBaseCheckIndex -gt 0 -and $finalBaseCheckIndex -lt $patchIndex) 'Signed base identity is not revalidated immediately before delta publication.'
$finalDraftIndex = $publishFunctionText.LastIndexOf("Get-ExactReleaseSnapshotById -ReleaseId `$releaseId -Tag `$tag -State 'draft'", [StringComparison]::Ordinal)
$finalInventoryStatement = 'Assert-CobbleRemoteAssetInventory -ExpectedAssets $expected -RemoteAssets @($finalDraft.Assets) -RequireComplete | Out-Null'
$finalInventoryIndex = $publishFunctionText.LastIndexOf($finalInventoryStatement, $patchIndex, [StringComparison]::Ordinal)
$postPublicIndex = $publishFunctionText.IndexOf("Get-ExactReleaseSnapshotById -ReleaseId `$releaseId -Tag `$tag -State 'public'", [StringComparison]::Ordinal)
Assert-True ($finalDraftIndex -gt $finalBaseCheckIndex -and $finalInventoryIndex -gt $finalDraftIndex -and $finalInventoryIndex -lt $patchIndex) 'Final PATCH does not immediately refetch and validate the exact draft ID/tag/assets.'
Assert-True ($postPublicIndex -gt $patchIndex) 'Publication does not postvalidate the same release ID/tag/public state.'
$preCapacityIndex = $publishFunctionText.LastIndexOf('Assert-CobblePublicReleaseCapacity -Releases @(Get-GitHubReleaseIndex) -AdditionalPublicReleases 1', $patchIndex, [StringComparison]::Ordinal)
$postCapacityIndex = $publishFunctionText.IndexOf('Assert-CobblePublicReleaseCapacity -Releases @(Get-GitHubReleaseIndex) -AdditionalPublicReleases 0', $patchIndex, [StringComparison]::Ordinal)
Assert-True ($preCapacityIndex -gt $finalBaseCheckIndex -and $preCapacityIndex -lt $finalDraftIndex) 'Publication does not gate capacity before the final exact draft/inventory re-fetch.'
$betweenFinalInventoryAndPatch = $publishFunctionText.Substring($finalInventoryIndex + $finalInventoryStatement.Length, $patchCallStartIndex - ($finalInventoryIndex + $finalInventoryStatement.Length))
Assert-True ([string]::IsNullOrWhiteSpace($betweenFinalInventoryAndPatch)) 'Another operation can race after final modpack draft/inventory validation and before PATCH.'
Assert-True ($postCapacityIndex -gt $postPublicIndex) 'Publication does not postvalidate the updater-safe public release count.'
$deleteIndex = $publishFunctionText.IndexOf("'DELETE'", [StringComparison]::Ordinal)
$deleteRefetchIndex = $publishFunctionText.LastIndexOf("Get-ExactReleaseSnapshotById -ReleaseId `$releaseId -Tag `$tag -State 'draft'", $deleteIndex, [StringComparison]::Ordinal)
$deleteCandidateIndex = $publishFunctionText.LastIndexOf('Get-CobbleStarterAssetForDeletion', $deleteIndex, [StringComparison]::Ordinal)
Assert-True ($deleteRefetchIndex -ge 0 -and $deleteCandidateIndex -gt $deleteRefetchIndex -and $deleteCandidateIndex -lt $deleteIndex) 'Starter DELETE is not guarded by an immediate exact draft/assets re-fetch and state transition check.'
Assert-True ([regex]::Matches($publishFunctionText, 'Assert-ManifestSignatureIdentity').Count -ge 4) 'Locked manifest/signature bytes are not reverified before every remote mutation boundary.'

$resumeFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-And-ValidateStagedManifest' }, $true)
$resumeFunctionText = $resumeFunction.Extent.Text
Assert-True ($resumeFunctionText -match 'Assert-CobbleV1Manifest' -and $resumeFunctionText -match 'Assert-CobbleDeltaManifest') 'Staged resume does not run the schema-specific publisher validators.'
Assert-True ($resumeFunctionText -match 'Get-ExpectedStagedAssets') 'Staged resume does not stream-validate the signed payload parts.'
Assert-True ($resumeFunctionText.IndexOf('Open-ManifestSignatureIdentity', [StringComparison]::Ordinal) -lt $resumeFunctionText.IndexOf('Assert-ManifestSignatureIdentity', [StringComparison]::Ordinal) -and
    $resumeFunctionText.IndexOf('Assert-ManifestSignatureIdentity', [StringComparison]::Ordinal) -lt $resumeFunctionText.IndexOf('Read-JsonSnapshot', [StringComparison]::Ordinal)) 'Staged resume does not verify and parse the same locked manifest/signature bytes.'
$baseResolver = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-BaseArtifacts' }, $true)
$baseResolverText = $baseResolver.Extent.Text
Assert-True ($baseResolverText.IndexOf('Open-ManifestSignatureIdentity', [StringComparison]::Ordinal) -lt $baseResolverText.IndexOf('Assert-CobblePublishedBaseAssets', [StringComparison]::Ordinal)) 'Base remote identity is not bound to a locked exact local byte pair.'
Assert-True ($publisherText.IndexOf('Assert-CobblePrivateKeyIsolation', [StringComparison]::Ordinal) -lt $publisherText.IndexOf('Building authoritative file manifest', [StringComparison]::Ordinal)) 'Private key isolation does not run before source inventory.'

$keyIsolationRoot = Join-Path ([IO.Path]::GetTempPath()) "cobble-publisher-key-gate-$([Guid]::NewGuid().ToString('N'))"
$keyIsolationSource = Join-Path $keyIsolationRoot 'minecraft'
$keyIsolationKey = Join-Path $keyIsolationSource 'mods\private.key'
[IO.Directory]::CreateDirectory((Split-Path -Parent $keyIsolationKey)) | Out-Null
[IO.File]::WriteAllText($keyIsolationKey, 'test key must never be inventoried')
$keyIsolationVersion = '98.76.54'
$keyIsolationOutput = Join-Path $Root "release-output\$keyIsolationVersion"
try {
    if (Test-Path -LiteralPath $keyIsolationOutput) { throw "Reserved key-isolation test output already exists: $keyIsolationOutput" }
    $keyGateOutput = @(& pwsh -NoProfile -File $Publisher -Version $keyIsolationVersion -FullBaseline -SourceMinecraftDir $keyIsolationSource -PrivateKeyPath $keyIsolationKey 2>&1)
    Assert-True ($LASTEXITCODE -ne 0 -and ($keyGateOutput -join "`n") -like '*Private signing key must not be inside*') 'Publisher did not reject a private key inside managed source before inventory.'
    Assert-True (-not (Test-Path -LiteralPath $keyIsolationOutput)) 'Private-key rejection created release staging before failing.'
}
finally {
    if ([IO.Directory]::Exists($keyIsolationRoot)) { [IO.Directory]::Delete($keyIsolationRoot, $true) }
}

# Exercise the real resume entrypoint with a valid public signature. The
# fixture is intentionally not a publishable Cobble payload, so validation
# must stop before any GitHub lookup or mutation after the pinned EXE verifies
# its signature.
$resumeFixtureVersion = '99.0.0'
$resumeFixtureRoot = Join-Path $Root "release-output\$resumeFixtureVersion"
$resumeFixtureManifest = Join-Path $Root 'updater\testdata\manifest-signature-fixture.json'
$resumeFixtureSignature = Join-Path $Root 'updater\testdata\manifest-signature-fixture.sig'
try {
    if (Test-Path -LiteralPath $resumeFixtureRoot) { throw "Reserved staged-resume fixture output already exists: $resumeFixtureRoot" }
    [IO.Directory]::CreateDirectory($resumeFixtureRoot) | Out-Null
    [IO.File]::Copy($resumeFixtureManifest, (Join-Path $resumeFixtureRoot 'cobble-music-update.json'))
    [IO.File]::Copy($resumeFixtureSignature, (Join-Path $resumeFixtureRoot 'cobble-music-update.sig'))
    [IO.File]::WriteAllText((Join-Path $resumeFixtureRoot 'RELEASE_NOTES.md'), 'test-only invalid resume fixture')
    $resumeGateOutput = @(& pwsh -NoProfile -File $Publisher -Version $resumeFixtureVersion -ResumePublish -ConfirmDistributionRights 2>&1)
    Assert-True ($LASTEXITCODE -ne 0 -and ($resumeGateOutput -join "`n") -like '*Payload metadata is missing or invalid*') "Real staged resume did not use the pinned verifier and fail closed on invalid staging: $($resumeGateOutput -join ' | ')"
}
finally {
    if ([IO.Directory]::Exists($resumeFixtureRoot)) { [IO.Directory]::Delete($resumeFixtureRoot, $true) }
}

$modeGateOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.0.0 -FullBaseline -RepairStaleUploads 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($modeGateOutput -join "`n") -like '*allowed only with -ResumePublish*') '-RepairStaleUploads did not fail closed outside resume mode.'

$fourPartOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.0.0.0 -FullBaseline 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($fourPartOutput -join "`n") -like '*does not match*pattern*') 'Publisher parameter binding accepted a four-part version.'
$leadingZeroOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.00.0 -FullBaseline 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($leadingZeroOutput -join "`n") -like '*does not match*pattern*') 'Publisher parameter binding accepted a leading-zero version.'

Write-Host 'Delta publisher validation, deletion-only, collision, version, and remote-inventory checks passed.'
