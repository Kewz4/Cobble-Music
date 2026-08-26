Set-StrictMode -Version Latest

$script:AllowedRoots = @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
$script:Sha256Pattern = '^[0-9a-f]{64}$'
$script:VersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

function Get-CobblePathKey {
    param([Parameter(Mandatory)][string]$Path)

    return $Path.Normalize([Text.NormalizationForm]::FormC).ToUpperInvariant()
}

function Assert-CobbleManagedPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Context = 'managed file'
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -ne $Path.Trim()) {
        throw "$Context has an empty path or surrounding whitespace: '$Path'"
    }
    if ($Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/') -or $Path.Contains('//')) {
        throw "$Context must use one canonical forward-slash relative path: $Path"
    }
    if ($Path.IndexOfAny([char[]]@('<', '>', ':', '"', '|', '?', '*')) -ge 0 -or $Path.ToCharArray().Where({ [char]::IsControl($_) }).Count -gt 0) {
        throw "$Context contains a Windows-unsafe character: $Path"
    }

    $segments = @($Path.Split('/'))
    if ($segments.Count -lt 2 -or $script:AllowedRoots -cnotcontains $segments[0]) {
        throw "$Context is outside the updater allowlist: $Path"
    }
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..' -or $segment.EndsWith('.') -or $segment.EndsWith(' ')) {
            throw "$Context contains an unsafe or ambiguous segment: $Path"
        }
        $stem = $segment.Split('.')[0]
        if ($stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "$Context contains a reserved Windows device name: $Path"
        }
    }

    return $Path
}

function ConvertTo-CobbleFileRecordSet {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][string]$Context,
        [switch]$AllowEmpty
    )

    if (-not $AllowEmpty -and $Entries.Count -eq 0) {
        throw "$Context must contain at least one file."
    }

    $byKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $result = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        if ($null -eq $entry) { throw "$Context contains a null file entry." }
        $path = [string]$entry.path
        Assert-CobbleManagedPath -Path $path -Context $Context | Out-Null

        [int64]$size = 0
        if ($null -eq $entry.size -or -not [int64]::TryParse(
            [Convert]::ToString($entry.size, [Globalization.CultureInfo]::InvariantCulture),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$size) -or $size -lt 0) {
            throw "$Context contains an invalid size for $path."
        }
        $sha256 = [string]$entry.sha256
        if ($sha256 -cnotmatch $script:Sha256Pattern) {
            throw "$Context contains a non-canonical SHA-256 for $path."
        }

        $key = Get-CobblePathKey $path
        if ($byKey.ContainsKey($key)) {
            $other = $byKey[$key].path
            throw "$Context contains a duplicate or Windows/Unicode-colliding path: $other / $path"
        }

        $record = [pscustomobject]@{
            path = $path
            size = $size
            sha256 = $sha256
        }
        $byKey.Add($key, $record)
        $result.Add($record)
    }

    $ordered = @($result | Sort-Object -Property @{ Expression = { Get-CobblePathKey $_.path }; Ascending = $true })
    return [pscustomobject]@{ Entries = $ordered; ByKey = $byKey }
}

function Test-CobbleSameFileRecord {
    param([Parameter(Mandatory)]$Left, [Parameter(Mandatory)]$Right)

    return $Left.size -eq $Right.size -and $Left.sha256 -ceq $Right.sha256
}

function Assert-CobblePayloadMetadata {
    param([Parameter(Mandatory)]$Payload)

    if ($null -eq $Payload -or [string]$Payload.archiveName -cne 'cobble-music-payload.zip' -or
        [string]$Payload.sha256 -cnotmatch $script:Sha256Pattern -or [int64]$Payload.size -le 0) {
        throw 'Payload metadata is missing or invalid.'
    }
    $parts = @($Payload.parts)
    if ($parts.Count -eq 0) { throw 'A non-empty payload must contain at least one part.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [int64]$total = 0
    foreach ($part in $parts) {
        $name = [string]$part.name
        if ($name -cnotmatch '^cobble-music-payload\.part\d{3,}$' -or -not $names.Add($name) -or
            [int64]$part.size -le 0 -or [string]$part.sha256 -cnotmatch $script:Sha256Pattern) {
            throw "Payload part metadata is unsafe, duplicate, or invalid: $name"
        }
        if ($total -gt [int64]::MaxValue - [int64]$part.size) { throw 'Payload part sizes overflow Int64.' }
        $total += [int64]$part.size
    }
    if ($total -ne [int64]$Payload.size) { throw 'Payload part sizes do not equal the signed payload size.' }
}

function Assert-CobbleLegacyCleanup {
    param(
        [AllowEmptyCollection()][Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][object[]]$ForbiddenSets
    )

    $legacy = ConvertTo-CobbleFileRecordSet -Entries $Entries -Context 'legacyCleanup' -AllowEmpty
    foreach ($entry in $legacy.Entries) {
        $key = Get-CobblePathKey $entry.path
        foreach ($forbidden in $ForbiddenSets) {
            if ($forbidden.ByKey.ContainsKey($key)) {
                throw "legacyCleanup overlaps a managed or signed-base file: $($entry.path)"
            }
        }
    }
    return $legacy
}

function Assert-CobbleV1Manifest {
    param([Parameter(Mandatory)]$Manifest)

    if ([int]$Manifest.schemaVersion -ne 1 -or [string]$Manifest.modpackId -cne 'cobble-music' -or
        [string]$Manifest.channel -cne 'stable' -or [string]$Manifest.releaseTag -cne "modpack-v$($Manifest.version)") {
        throw 'Baseline manifest identity, schema, or release tag is invalid.'
    }
    [Version]$parsed = $null
    if ([string]$Manifest.version -cnotmatch $script:VersionPattern -or
        -not [Version]::TryParse([string]$Manifest.version, [ref]$parsed)) {
        throw 'Baseline manifest version must use canonical major.minor.patch form.'
    }
    $files = ConvertTo-CobbleFileRecordSet -Entries @($Manifest.files) -Context 'baseline authoritative files'
    Assert-CobblePayloadMetadata $Manifest.payload

    $deleteKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($pathValue in @($Manifest.deletePaths)) {
        $path = [string]$pathValue
        Assert-CobbleManagedPath -Path $path -Context 'baseline deletePaths' | Out-Null
        $key = Get-CobblePathKey $path
        if (-not $deleteKeys.Add($key) -or $files.ByKey.ContainsKey($key)) {
            throw "Baseline deletePaths is duplicate or overlaps files: $path"
        }
    }
    Assert-CobbleLegacyCleanup -Entries @($Manifest.legacyCleanup) -ForbiddenSets @($files) | Out-Null
    return $true
}

function Assert-CobbleVersionAdvance {
    param(
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][string]$TargetVersion
    )

    [Version]$base = $null
    [Version]$target = $null
    if ($BaseVersion -cnotmatch $script:VersionPattern -or $TargetVersion -cnotmatch $script:VersionPattern -or
        -not [Version]::TryParse($BaseVersion, [ref]$base) -or -not [Version]::TryParse($TargetVersion, [ref]$target)) {
        throw "Base and target versions must use canonical major.minor.patch form: $BaseVersion -> $TargetVersion"
    }
    if ($target -le $base) {
        throw "A delta release must advance the version: $BaseVersion -> $TargetVersion"
    }
}

function Assert-CobbleBaseManifest {
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$TargetVersion
    )

    if ($null -eq $Manifest.schemaVersion -or [int]$Manifest.schemaVersion -notin @(1, 2) -or
        [string]$Manifest.modpackId -cne 'cobble-music' -or
        [string]$Manifest.channel -cne 'stable') {
        throw 'The signed base manifest is not a supported Cobble Music stable manifest.'
    }
    if ([string]$Manifest.version -cne $ExpectedVersion -or [string]$Manifest.releaseTag -cne "modpack-v$ExpectedVersion") {
        throw "The signed base manifest is not bound to requested base version $ExpectedVersion."
    }
    Assert-CobbleVersionAdvance -BaseVersion $ExpectedVersion -TargetVersion $TargetVersion

    $files = @($Manifest.files)
    $fileSet = ConvertTo-CobbleFileRecordSet -Entries $files -Context 'signed base manifest files'
    return $fileSet
}

function New-CobbleDeltaPlan {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$CurrentFiles,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$BaseFiles
    )

    $currentSet = ConvertTo-CobbleFileRecordSet -Entries $CurrentFiles -Context 'current authoritative files'
    $baseSet = ConvertTo-CobbleFileRecordSet -Entries $BaseFiles -Context 'signed base manifest files'
    $payload = [Collections.Generic.List[object]]::new()
    $deleted = [Collections.Generic.List[object]]::new()
    $unchanged = [Collections.Generic.List[object]]::new()

    foreach ($current in $currentSet.Entries) {
        $key = Get-CobblePathKey $current.path
        if (-not $baseSet.ByKey.ContainsKey($key)) {
            $payload.Add($current)
            continue
        }

        $base = $baseSet.ByKey[$key]
        if ($current.path -cne $base.path) {
            throw "Case-only or Unicode-normalization path changes are not supported in one delta: $($base.path) -> $($current.path)"
        }
        if (Test-CobbleSameFileRecord $current $base) { $unchanged.Add($current) }
        else { $payload.Add($current) }
    }

    foreach ($base in $baseSet.Entries) {
        if (-not $currentSet.ByKey.ContainsKey((Get-CobblePathKey $base.path))) {
            $deleted.Add($base)
        }
    }

    return [pscustomobject]@{
        Files = @($currentSet.Entries)
        PayloadFiles = @($payload)
        DeletedFiles = @($deleted)
        UnchangedFiles = @($unchanged)
    }
}

function Assert-CobbleDeltaManifest {
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)]$BaseManifest,
        [Parameter(Mandatory)][string]$ExpectedBaseManifestSha256
    )

    if ([int]$Manifest.schemaVersion -ne 2 -or [string]$Manifest.modpackId -cne 'cobble-music' -or [string]$Manifest.channel -cne 'stable') {
        throw 'Delta manifest identity or schema is invalid.'
    }
    if ([string]$Manifest.minimumUpdaterVersion -cne '1.2.0') {
        throw 'Delta manifests must require updater 1.2.0.'
    }
    if ($null -eq $Manifest.base -or [string]$Manifest.base.version -cne [string]$BaseManifest.version -or
        [string]$Manifest.base.manifestSha256 -cne $ExpectedBaseManifestSha256 -or
        [string]$Manifest.base.manifestSha256 -cnotmatch $script:Sha256Pattern) {
        throw 'Delta manifest base binding is invalid.'
    }
    if ([string]$Manifest.releaseTag -cne "modpack-v$($Manifest.version)") {
        throw 'Delta manifest release tag/version binding is invalid.'
    }
    Assert-CobbleVersionAdvance -BaseVersion ([string]$Manifest.base.version) -TargetVersion ([string]$Manifest.version)

    $expected = New-CobbleDeltaPlan -CurrentFiles @($Manifest.files) -BaseFiles @($BaseManifest.files)
    $actualPayload = ConvertTo-CobbleFileRecordSet -Entries @($Manifest.payloadFiles) -Context 'delta payloadFiles' -AllowEmpty
    $actualDeleted = ConvertTo-CobbleFileRecordSet -Entries @($Manifest.deletedFiles) -Context 'delta deletedFiles' -AllowEmpty
    $expectedPayload = ConvertTo-CobbleFileRecordSet -Entries @($expected.PayloadFiles) -Context 'expected delta payloadFiles' -AllowEmpty
    $expectedDeleted = ConvertTo-CobbleFileRecordSet -Entries @($expected.DeletedFiles) -Context 'expected delta deletedFiles' -AllowEmpty

    foreach ($comparison in @(
        [pscustomobject]@{ Name = 'payloadFiles'; Actual = $actualPayload; Expected = $expectedPayload },
        [pscustomobject]@{ Name = 'deletedFiles'; Actual = $actualDeleted; Expected = $expectedDeleted }
    )) {
        if ($comparison.Actual.Entries.Count -ne $comparison.Expected.Entries.Count) {
            throw "Delta $($comparison.Name) is not the exact signed-base difference."
        }
        foreach ($expectedRecord in $comparison.Expected.Entries) {
            $key = Get-CobblePathKey $expectedRecord.path
            if (-not $comparison.Actual.ByKey.ContainsKey($key)) {
                throw "Delta $($comparison.Name) omits $($expectedRecord.path)."
            }
            $actualRecord = $comparison.Actual.ByKey[$key]
            if ($actualRecord.path -cne $expectedRecord.path -or -not (Test-CobbleSameFileRecord $actualRecord $expectedRecord)) {
                throw "Delta $($comparison.Name) metadata does not exactly match $($expectedRecord.path)."
            }
        }
    }

    if ($actualPayload.Entries.Count -eq 0) {
        if ($null -ne $Manifest.payload) { throw 'A deletion-only delta must have a null payload.' }
        if ($actualDeleted.Entries.Count -eq 0) { throw 'A delta must change or delete at least one file.' }
    }
    elseif ($null -eq $Manifest.payload) {
        throw 'A delta with changed/new files must declare a payload.'
    }
    else {
        Assert-CobblePayloadMetadata $Manifest.payload
    }

    $fullSet = ConvertTo-CobbleFileRecordSet -Entries @($Manifest.files) -Context 'delta authoritative files'
    $baseSet = ConvertTo-CobbleFileRecordSet -Entries @($BaseManifest.files) -Context 'delta signed-base files'
    Assert-CobbleLegacyCleanup -Entries @($Manifest.legacyCleanup) -ForbiddenSets @($fullSet, $baseSet) | Out-Null

    return $true
}

function New-CobbleExpectedAssetIndex {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$ExpectedAssets
    )

    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $ExpectedAssets) {
        $name = [string]$asset.name
        if ([string]::IsNullOrWhiteSpace($name) -or $name -ne [IO.Path]::GetFileName($name) -or $expected.ContainsKey($name)) {
            throw "Expected release asset name is unsafe or duplicate: $name"
        }
        [int64]$size = [int64]$asset.size
        $sha256 = [string]$asset.sha256
        if ($size -lt 0 -or $sha256 -cnotmatch $script:Sha256Pattern) { throw "Expected release asset metadata is invalid: $name" }
        $expected.Add($name, [pscustomobject]@{ name = $name; size = $size; sha256 = $sha256 })
    }
    return [pscustomobject]@{ ByName = $expected }
}

function Assert-CobblePublishedBaseAssets {
    param(
        [Parameter(Mandatory)][object[]]$LocalAssets,
        [Parameter(Mandatory)][object[]]$RemoteAssets
    )

    $requiredNames = @('cobble-music-update.json', 'cobble-music-update.sig')
    $local = (New-CobbleExpectedAssetIndex $LocalAssets).ByName
    if ($local.Count -ne $requiredNames.Count -or $requiredNames.Where({ -not $local.ContainsKey($_) }).Count -gt 0) {
        throw 'Local base identity must contain exactly the manifest and detached signature assets.'
    }
    foreach ($requiredName in $requiredNames) {
        if ($local[$requiredName].name -cne $requiredName -or $local[$requiredName].size -le 0) {
            throw "Local base asset identity is invalid: $requiredName"
        }
    }

    $seenRemote = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($remote in $RemoteAssets) {
        $name = [string]$remote.name
        if (-not $seenRemote.Add($name)) { throw "Published base release contains duplicate or case-colliding assets: $name" }
        if (-not $local.ContainsKey($name)) { continue }

        $wanted = $local[$name]
        if ($name -cne $wanted.name -or [string]$remote.state -cne 'uploaded' -or
            [int64]$remote.size -ne $wanted.size -or [string]$remote.digest -cne "sha256:$($wanted.sha256)") {
            throw "Local base asset does not exactly match the uploaded published release asset: $name"
        }
        [void]$matched.Add($name)
    }
    $missing = @($requiredNames | Where-Object { -not $matched.Contains($_) })
    if ($missing.Count -gt 0) { throw "Published base release is missing exact uploaded identity assets: $($missing -join ', ')" }
    return $true
}

function Assert-CobblePayloadZipInventory {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][object[]]$ExpectedFiles
    )

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "Payload ZIP was not found: $ZipPath" }
    $expected = ConvertTo-CobbleFileRecordSet -Entries $ExpectedFiles -Context 'payload ZIP expected files'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $fileStream = [IO.File]::Open($ZipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            foreach ($entry in $archive.Entries) {
                $path = [string]$entry.FullName
                if ([string]::IsNullOrWhiteSpace([string]$entry.Name)) { throw "Payload ZIP contains a directory or unnamed entry: $path" }
                Assert-CobbleManagedPath -Path $path -Context 'payload ZIP entry' | Out-Null
                $key = Get-CobblePathKey $path
                if (-not $seen.Add($key)) { throw "Payload ZIP contains a duplicate or case/Unicode-colliding entry: $path" }
                if (-not $expected.ByKey.ContainsKey($key)) { throw "Payload ZIP contains an unexpected entry: $path" }

                $wanted = $expected.ByKey[$key]
                if ($path -cne $wanted.path -or [int64]$entry.Length -ne $wanted.size) {
                    throw "Payload ZIP path/size does not match the inventoried source: $path"
                }
                $entryStream = $entry.Open()
                $hasher = [Security.Cryptography.SHA256]::Create()
                try { $actualHash = [Convert]::ToHexString($hasher.ComputeHash($entryStream)).ToLowerInvariant() }
                finally {
                    $hasher.Dispose()
                    $entryStream.Dispose()
                }
                if ($actualHash -cne $wanted.sha256) { throw "Payload ZIP SHA-256 does not match the inventoried source: $path" }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fileStream.Dispose() }

    $missing = @($expected.Entries | Where-Object { -not $seen.Contains((Get-CobblePathKey $_.path)) })
    if ($missing.Count -gt 0) { throw "Payload ZIP is missing inventoried files: $($missing.path -join ', ')" }
    return $true
}

function Get-CobbleRepairableStarterAssets {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$ExpectedAssets,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$RemoteAssets
    )

    $expected = (New-CobbleExpectedAssetIndex $ExpectedAssets).ByName
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $repairable = [Collections.Generic.List[object]]::new()
    foreach ($remote in $RemoteAssets) {
        $name = [string]$remote.name
        if (-not $seen.Add($name)) { throw "GitHub draft contains duplicate asset names: $name" }
        if (-not $expected.ContainsKey($name)) { throw "GitHub draft contains an unexpected asset: $name" }

        $wanted = $expected[$name]
        if ($name -cne $wanted.name) { throw "GitHub asset name casing does not exactly match signed staging: $name" }
        $state = [string]$remote.state
        if ($state -ceq 'uploaded') {
            $digest = [string]$remote.digest
            if ([int64]$remote.size -ne $wanted.size -or $digest -cne "sha256:$($wanted.sha256)") {
                throw "GitHub uploaded asset size/digest does not match local signed staging: $name"
            }
            continue
        }
        if ($state -cne 'starter') { throw "GitHub asset has an unsupported state and cannot be repaired automatically: $name ($state)" }

        [int64]$assetId = 0
        $idProperty = $remote.PSObject.Properties['id']
        $idValue = if ($null -eq $idProperty) { $null } else { $idProperty.Value }
        if ($null -eq $idValue -or -not [int64]::TryParse(
            [Convert]::ToString($idValue, [Globalization.CultureInfo]::InvariantCulture),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$assetId) -or $assetId -le 0) {
            throw "GitHub starter asset has no safe API identifier: $name"
        }
        $repairable.Add([pscustomobject]@{ id = $assetId; name = $name })
    }
    return @($repairable)
}

function Assert-CobbleRemoteAssetInventory {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$ExpectedAssets,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$RemoteAssets,
        [switch]$RequireComplete
    )

    $expected = (New-CobbleExpectedAssetIndex $ExpectedAssets).ByName

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($remote in $RemoteAssets) {
        $name = [string]$remote.name
        if (-not $seen.Add($name)) { throw "GitHub draft contains duplicate asset names: $name" }
        if (-not $expected.ContainsKey($name)) { throw "GitHub draft contains an unexpected asset: $name" }
        if ([string]$remote.state -cne 'uploaded') { throw "GitHub asset is not safely finalized yet: $name ($($remote.state))" }

        $wanted = $expected[$name]
        $digest = [string]$remote.digest
        if ($name -cne $wanted.name -or [int64]$remote.size -ne $wanted.size -or $digest -cne "sha256:$($wanted.sha256)") {
            throw "GitHub asset size/digest does not match local signed staging: $name"
        }
    }

    $missing = @($expected.Keys | Where-Object { -not $seen.Contains($_) } | Sort-Object)
    if ($RequireComplete -and $missing.Count -gt 0) {
        throw "GitHub release is missing expected assets: $($missing -join ', ')"
    }
    return $missing
}

Export-ModuleMember -Function @(
    'Assert-CobbleManagedPath',
    'ConvertTo-CobbleFileRecordSet',
    'Assert-CobbleVersionAdvance',
    'Assert-CobbleBaseManifest',
    'Assert-CobbleV1Manifest',
    'New-CobbleDeltaPlan',
    'Assert-CobbleDeltaManifest',
    'Assert-CobblePublishedBaseAssets',
    'Assert-CobblePayloadZipInventory',
    'Get-CobbleRepairableStarterAssets',
    'Assert-CobbleRemoteAssetInventory'
)
