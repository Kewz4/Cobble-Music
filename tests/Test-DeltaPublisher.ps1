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

$baselineManifest = [pscustomobject]@{
    schemaVersion = 1
    modpackId = 'cobble-music'
    channel = 'stable'
    version = '1.0.4'
    releaseTag = 'modpack-v1.0.4'
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

$starterWithoutId = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$starterWithoutId[2].state = 'starter'
Assert-Throws { Get-CobbleRepairableStarterAssets -ExpectedAssets $expectedAssets -RemoteAssets $starterWithoutId | Out-Null } 'Starter asset without a safe API id was repairable.'

$wrongDigest = @($remoteAssets | ForEach-Object { $_.PSObject.Copy() })
$wrongDigest[2].digest = "sha256:$(New-Hash 'd')"
Assert-Throws { Assert-CobbleRemoteAssetInventory -ExpectedAssets $expectedAssets -RemoteAssets $wrongDigest | Out-Null } 'Wrong GitHub asset digest was accepted.'

$starterWithMismatch = @($starter | ForEach-Object { $_.PSObject.Copy() })
$starterWithMismatch[0].digest = "sha256:$(New-Hash 'd')"
Assert-Throws { Get-CobbleRepairableStarterAssets -ExpectedAssets $expectedAssets -RemoteAssets $starterWithMismatch | Out-Null } 'Starter repair proceeded beside an uploaded mismatched asset.'

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

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'Publisher script has PowerShell parse errors.'
$chunkParameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ChunkSizeMiB' }
Assert-True ($null -ne $chunkParameter -and $chunkParameter.DefaultValue.Extent.Text -eq '256') 'Future release chunk default is not 256 MiB.'

$modeGateOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.0.0 -FullBaseline -RepairStaleUploads 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($modeGateOutput -join "`n") -like '*allowed only with -ResumePublish*') '-RepairStaleUploads did not fail closed outside resume mode.'

$fourPartOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.0.0.0 -FullBaseline 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($fourPartOutput -join "`n") -like '*does not match*pattern*') 'Publisher parameter binding accepted a four-part version.'
$leadingZeroOutput = @(& pwsh -NoProfile -File $Publisher -Version 98.00.0 -FullBaseline 2>&1)
Assert-True ($LASTEXITCODE -ne 0 -and ($leadingZeroOutput -join "`n") -like '*does not match*pattern*') 'Publisher parameter binding accepted a leading-zero version.'

Write-Host 'Delta publisher validation, deletion-only, collision, version, and remote-inventory checks passed.'
