Set-StrictMode -Version Latest

$script:AllowedRoots = @('mods', 'resourcepacks', 'shaderpacks', 'datapacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
$script:Sha256Pattern = '^[0-9a-f]{64}$'
$script:VersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
$script:PinnedUpdaterVersion = '1.2.16'
$script:MaximumReleaseAssetCount = 999
$script:ReservedReleaseMetadataAssetCount = 2
$script:MaximumPublicReleaseCount = 499
$script:ExactRetiredV1013SeedIdentities = @{
    'config/cobbreeding/encryption' = [pscustomobject]@{ size = 24L; sha256 = '2bb06f85c37e816eef81cde4eb4fbac3cce07a70b96136d03fe17ba8d71f1c2d' }
    'config/defaultoptions-common.toml.bak1' = [pscustomobject]@{ size = 333L; sha256 = 'a186d6ab6468353cb2a49f5a4229c16b004580a557d4bccaf12041e8a46f9d9e' }
    'config/dreamdisplays/config.yml' = [pscustomobject]@{ size = 261L; sha256 = '7b6b2deac4a3b0e43023590f1005686cf0328b4c005d30faacd7e58038d258e7' }
    'config/etf_warnings.json' = [pscustomobject]@{ size = 28L; sha256 = 'a5ba22e63061c1fb67f0f895f17681351eaeccc225faef966c29ee630593275e' }
    'config/jade/usernamecache.json' = [pscustomobject]@{ size = 918L; sha256 = '038776e7dcab245d02c0df6227f4797b1a1e36f0063fc8787f2640f87dabc2ce' }
    'config/sodium-fingerprint.json' = [pscustomobject]@{ size = 427L; sha256 = '7bb6d04a130e6c0826448c3d8531e81b90ba5fca2b3fb2ccaf95c7aa8f0f572c' }
    'config/spark/activity.json' = [pscustomobject]@{ size = 2L; sha256 = '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945' }
    'config/spark/tmp-client/about.txt' = [pscustomobject]@{ size = 585L; sha256 = 'd7514c0ddb6ae8611a281527bf04ca6cbcea1fa21758534fdcd08ed0f51c19c0' }
    'config/spark/tmp/about.txt' = [pscustomobject]@{ size = 585L; sha256 = 'd7514c0ddb6ae8611a281527bf04ca6cbcea1fa21758534fdcd08ed0f51c19c0' }
    'config/waystones-common.toml.bak1' = [pscustomobject]@{ size = 7899L; sha256 = '39b508ab2e4e5988b47d88869ecc349da01a95c86c5e71e5e14eddb4ba329f29' }
    'config/waystones-common.toml.bak2' = [pscustomobject]@{ size = 8329L; sha256 = '3c25e82516228487159648f1e4692e5a4d92318892e86d71e1741b868e94eec8' }
    'config/zoomify.json' = [pscustomobject]@{ size = 709L; sha256 = '18ad037a0087eea89db518710cc69fa750dcd2122c52219c1dd4182cb55e0a42' }
}

function Get-CobbleOptionalPropertyValue {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Object -is [Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { return $null }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-CobbleRuntimeCollectionState {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        # System.Text.Json preserves the C# model's initialized empty list when
        # a property is absent. An explicit JSON null instead overwrites that
        # default and is rejected by ManifestParser.
        return [pscustomobject]@{ WasPresent = $false; Entries = @() }
    }
    if ($null -eq $property.Value) { throw "$Context must not be explicit JSON null." }
    return [pscustomobject]@{ WasPresent = $true; Entries = @($property.Value) }
}

function Assert-CobbleSupportedMinimumUpdaterVersion {
    param(
        [AllowNull()][string]$MinimumUpdaterVersion,
        [string]$MaximumUpdaterVersion = $script:PinnedUpdaterVersion
    )

    [Version]$minimum = $null
    [Version]$maximum = $null
    if ($MinimumUpdaterVersion -cnotmatch $script:VersionPattern -or
        $MaximumUpdaterVersion -cnotmatch $script:VersionPattern -or
        -not [Version]::TryParse($MinimumUpdaterVersion, [ref]$minimum) -or
        -not [Version]::TryParse($MaximumUpdaterVersion, [ref]$maximum)) {
        throw 'Manifest minimum updater version or pinned updater version is not canonical major.minor.patch.'
    }
    if ($minimum -gt $maximum) {
        throw "Manifest requires updater $MinimumUpdaterVersion, newer than pinned distributed updater $MaximumUpdaterVersion."
    }
    return $true
}

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

function Assert-CobbleSourcePathPolicy {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Context = 'managed source file',
        [switch]$ExplicitSourceFile
    )

    $normalized = $Path.Replace('\', '/')
    Assert-CobbleManagedPath -Path $normalized -Context $Context | Out-Null

    # Minecraft stores downloaded native libraries and Chromium profile data
    # beside mod jars. Neither is authored pack content. Limiting `mods` to
    # top-level files prevents cache, cookies, IndexedDB, and other volatile
    # runtime state from ever becoming release payload data.
    if ($normalized.StartsWith('mods/', [StringComparison]::OrdinalIgnoreCase)) {
        $underMods = $normalized.Substring('mods/'.Length)
        if ($underMods.Contains('/')) {
            throw "$Context is nested runtime data under mods and may not be distributed: $normalized"
        }
        if ($underMods -inotmatch '\.jar(?:\.disabled)?$') {
            throw "$Context is not an approved top-level mod artifact: $normalized"
        }
    }

    if ($normalized.StartsWith('resourcepacks/', [StringComparison]::OrdinalIgnoreCase)) {
        $underResourcePacks = $normalized.Substring('resourcepacks/'.Length)
        if ($underResourcePacks.Contains('/')) {
            throw "$Context is nested generated data under resourcepacks and may not be distributed: $normalized"
        }
        $isZip = $underResourcePacks -imatch '\.zip$'
        $isReviewedSidecar = $ExplicitSourceFile -and $underResourcePacks -imatch '\.rpo$'
        if (-not $isZip -and -not $isReviewedSidecar) {
            throw "$Context is not an approved top-level resource-pack artifact: $normalized"
        }
    }

    if ($normalized -ieq 'config/MCBrowser/tabs.json') {
        throw "$Context is per-user browser state and may not be distributed: $normalized"
    }
    if ($normalized -ieq 'config/dreamdisplays/config.toml') {
        throw "$Context is credential-bearing service configuration and may not be distributed: $normalized"
    }
    if ($normalized -ieq 'config/cobbreeding/encryption' -or
        $normalized -ieq 'config/jade/usernamecache.json') {
        throw "$Context is generated private runtime state and may not be distributed: $normalized"
    }

    $forbiddenSegments = @('.git', '.hg', '.svn', '.idea', '.vscode', 'node_modules', '__pycache__')
    foreach ($segment in $normalized.Split('/')) {
        if ($forbiddenSegments -icontains $segment) {
            throw "$Context contains private or generated workspace state: $normalized"
        }
    }

    return $true
}

function Assert-CobbleSeedPathPolicy {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Context = 'create-only default'
    )

    $normalized = $Path.Replace('\', '/')
    if ($normalized -ceq 'options.txt') { return $true }
    if ($normalized.StartsWith('mods/', [StringComparison]::OrdinalIgnoreCase)) {
        $underMods = $normalized.Substring('mods/'.Length)
        if (-not $underMods.Contains('/') -and
            $underMods.StartsWith('axiom', [StringComparison]::OrdinalIgnoreCase) -and
            $underMods -imatch '\.jar(?:\.disabled)?$') {
            Assert-CobbleSourcePathPolicy -Path $normalized -Context $Context -ExplicitSourceFile | Out-Null
            return $true
        }
        throw "$Context is not an optional top-level Axiom artifact: $normalized"
    }
    if ($normalized.StartsWith('shaderpacks/', [StringComparison]::OrdinalIgnoreCase)) {
        $underShaderpacks = $normalized.Substring('shaderpacks/'.Length)
        if (-not $underShaderpacks.Contains('/') -and $underShaderpacks.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
            Assert-CobbleSourcePathPolicy -Path $normalized -Context $Context -ExplicitSourceFile | Out-Null
            return $true
        }
        throw "$Context is not an approved top-level Iris settings sidecar: $normalized"
    }
    if (-not $normalized.StartsWith('config/', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context is outside the create-only allowlist: $normalized"
    }
    if (Test-CobbleNeverDistributePath -Path $normalized) {
        throw "$Context is generated private runtime state and may not be distributed: $normalized"
    }
    Assert-CobbleSourcePathPolicy -Path $normalized -Context $Context -ExplicitSourceFile | Out-Null
    return $true
}

function Test-CobbleNeverDistributePath {
    param([Parameter(Mandatory)][string]$Path)

    $normalized = $Path.Replace('\', '/')
    $segments = @($normalized.Split('/'))
    $name = $segments[-1]
    return $normalized -ieq 'config/MCBrowser/tabs.json' -or
        $normalized -ieq 'config/packed_packs/__version.json' -or
        $normalized -ieq 'config/dreamdisplays/config.toml' -or
        $normalized -ieq 'config/dreamdisplays/config.yml' -or
        $normalized -ieq 'config/cobbreeding/encryption' -or
        $normalized -ieq 'config/jade/usernamecache.json' -or
        $normalized -ieq 'config/zoomify.json' -or
        $normalized -ieq 'config/etf_warnings.json' -or
        $normalized -ieq 'config/sodium-fingerprint.json' -or
        $normalized -ieq 'config/spark/activity.json' -or
        $normalized -ieq 'config/spark/tmp/about.txt' -or
        $normalized -ieq 'config/spark/tmp-client/about.txt' -or
        ($segments[0] -ieq 'config' -and $segments -icontains 'cache') -or
        $name -imatch '(?:\.bak(?:\d+|[-._].*)?|\.old(?:\d+|[-._].*)?|~)$' -or
        $name -ieq 'thumbs.db' -or
        $name -ieq '.ds_store'
}

function Assert-CobbleSeedTextReplacementPathPolicy {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Context = 'seed text replacement'
    )

    $normalized = $Path.Replace('\', '/')
    Assert-CobbleSeedPathPolicy -Path $normalized -Context $Context | Out-Null
    if ($normalized -ine 'config/iris.properties' -and $normalized -ine 'options.txt') {
        throw "$Context may only migrate the obsolete Iris selector or the reviewed K-key collision: $normalized"
    }
    return $true
}

function Test-CobbleSeedTreeExclusion {
    param([Parameter(Mandatory)][string]$Path)

    return Test-CobbleNeverDistributePath -Path $Path
}

function Get-CobbleManagedSourceFiles {
    param(
        [Parameter(Mandatory)][string]$SourceMinecraftDir,
        [Parameter(Mandatory)][string[]]$IncludeRoots,
        [Parameter(Mandatory)][string[]]$IncludeFiles,
        [Parameter(Mandatory)][string[]]$AllowedRoots
    )

    $sourceRoot = [IO.Path]::GetFullPath($SourceMinecraftDir)
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rootName in $IncludeRoots | Sort-Object -Unique) {
        if ($AllowedRoots -cnotcontains $rootName) {
            throw "Include root is outside the updater allowlist: $rootName"
        }
        $rootPath = Join-Path $sourceRoot $rootName
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
        $rootItem = Get-Item -LiteralPath $rootPath
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Include root may not be a reparse point: $rootPath"
        }

        # Both loaders consume top-level artifacts. Hidden `.index` folders and
        # other directories below these roots are generated runtime state.
        $rootFiles = if ($rootName -in @('mods', 'resourcepacks', 'datapacks')) {
            $topLevelFiles = @(Get-ChildItem -LiteralPath $rootPath -File -Force)
            $approvedPattern = if ($rootName -ceq 'mods') {
                '\.jar(?:\.disabled)?$'
            }
            else {
                '\.zip$'
            }
            $excluded = @($topLevelFiles | Where-Object {
                $_.Name -inotmatch $approvedPattern -and -not ($rootName -ceq 'resourcepacks' -and $_.Name -imatch '\.rpo$')
            })
            if ($excluded.Count -gt 0) {
                Write-Warning "Excluded non-pack artifacts under $rootName`: $($excluded.Name -join ', ')"
            }
            @($topLevelFiles | Where-Object { $_.Name -imatch $approvedPattern })
        }
        else {
            @(Get-ChildItem -LiteralPath $rootPath -File -Force -Recurse)
        }
        foreach ($file in $rootFiles) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Managed source may not be a reparse point: $($file.FullName)"
            }
            $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
            Assert-CobbleSourcePathPolicy -Path $relative -Context 'source file' | Out-Null
            if (Test-CobbleNeverDistributePath -Path $relative) {
                throw "Managed source includes generated private/runtime state: $relative"
            }
            $key = $relative.Normalize([Text.NormalizationForm]::FormC)
            if (-not $byPath.TryAdd($key, [pscustomobject]@{ full = $file.FullName; path = $relative })) {
                throw "Managed source contains a duplicate path: $relative"
            }
        }
    }

    foreach ($relativeInput in $IncludeFiles | Sort-Object -Unique) {
        $relative = $relativeInput.Replace('\', '/')
        Assert-CobbleSourcePathPolicy -Path $relative -Context 'included file' -ExplicitSourceFile | Out-Null
        if (Test-CobbleNeverDistributePath -Path $relative) {
            throw "Explicitly included source is generated private/runtime state: $relative"
        }
        $full = Join-Path $sourceRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $full
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Managed source may not be a reparse point: $($item.FullName)"
        }
        $key = $relative.Normalize([Text.NormalizationForm]::FormC)
        if (-not $byPath.ContainsKey($key)) {
            $byPath.Add($key, [pscustomobject]@{ full = $item.FullName; path = $relative })
        }
    }

    return @($byPath.Values | Sort-Object path)
}

function Get-CobbleSeedSourceFiles {
    param(
        [Parameter(Mandatory)][string]$SourceMinecraftDir,
        [Parameter(Mandatory)][string[]]$SeedFiles,
        [string[]]$SeedRoots = @(),
        [string[]]$ExcludeFiles = @(),
        [AllowEmptyString()][string]$SeedTemplateDir = ''
    )

    $sourceRoot = [IO.Path]::GetFullPath($SourceMinecraftDir)
    $templateRoot = if ([string]::IsNullOrWhiteSpace($SeedTemplateDir)) { $null } else { [IO.Path]::GetFullPath($SeedTemplateDir) }
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeInput in $ExcludeFiles) {
        [void]$excluded.Add($relativeInput.Replace('\', '/').Normalize([Text.NormalizationForm]::FormC))
    }
    $treeSeedFiles = [Collections.Generic.List[string]]::new()
    foreach ($rootInput in $SeedRoots | Sort-Object -Unique) {
        $rootName = $rootInput.Replace('\', '/').Trim('/')
        if ($rootName -cne 'config') {
            throw "Create-only seed root is outside the reviewed tree allowlist: $rootName"
        }
        $rootPath = Join-Path $sourceRoot $rootName
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
        $unsafeLinks = @(Get-ChildItem -LiteralPath $rootPath -Recurse -Force | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($unsafeLinks.Count -gt 0) {
            throw "Create-only seed tree contains a reparse point: $($unsafeLinks[0].FullName)"
        }
        foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force) {
            $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
            $key = $relative.Normalize([Text.NormalizationForm]::FormC)
            if (-not $excluded.Contains($key) -and -not (Test-CobbleSeedTreeExclusion -Path $relative)) {
                $treeSeedFiles.Add($relative)
            }
        }
    }

    foreach ($relativeInput in @($SeedFiles) + @($treeSeedFiles) | Sort-Object -Unique) {
        $relative = $relativeInput.Replace('\', '/')
        Assert-CobbleSeedPathPolicy -Path $relative | Out-Null

        $full = $null
        if ($null -ne $templateRoot) {
            $template = Join-Path $templateRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if (Test-Path -LiteralPath $template -PathType Leaf) { $full = $template }
        }
        if ($null -eq $full) {
            $full = Join-Path $sourceRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Required create-only default is missing from both the template and canonical instance: $relative"
        }
        $item = Get-Item -LiteralPath $full
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Create-only default may not be a reparse point: $($item.FullName)"
        }
        $key = $relative.Normalize([Text.NormalizationForm]::FormC)
        if (-not $byPath.TryAdd($key, [pscustomobject]@{ full = $item.FullName; path = $relative })) {
            throw "Create-only defaults contain a duplicate path: $relative"
        }
    }
    return @($byPath.Values | Sort-Object path)
}

function Test-CobblePathAtOrUnder {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Base
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullBase = [IO.Path]::GetFullPath($Base)
    $baseRoot = [IO.Path]::GetPathRoot($fullBase)
    if ($fullBase.Length -gt $baseRoot.Length) {
        $fullBase = $fullBase.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    if ($fullPath.Equals($fullBase, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $prefix = $fullBase
    if (-not $prefix.EndsWith([IO.Path]::DirectorySeparatorChar) -and -not $prefix.EndsWith([IO.Path]::AltDirectorySeparatorChar)) {
        $prefix += [IO.Path]::DirectorySeparatorChar
    }
    return $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CobblePrivateKeyIsolation {
    param(
        [Parameter(Mandatory)][string]$PrivateKeyPath,
        [Parameter(Mandatory)][string]$SourceMinecraftDir,
        [Parameter(Mandatory)][string]$ReleaseOutputRoot,
        [AllowEmptyCollection()][string[]]$ManagedRoots = @()
    )

    $forbidden = @(
        [pscustomobject]@{ Path = $SourceMinecraftDir; Description = 'Minecraft source directory' },
        [pscustomobject]@{ Path = $ReleaseOutputRoot; Description = 'release staging directory' }
    ) + @($ManagedRoots | ForEach-Object {
        [pscustomobject]@{ Path = $_; Description = 'inventoried managed root' }
    })
    foreach ($location in $forbidden) {
        if (Test-CobblePathAtOrUnder -Path $PrivateKeyPath -Base $location.Path) {
            throw "Private signing key must not be inside the $($location.Description): $PrivateKeyPath"
        }
    }
    return $true
}

function Open-CobbleLockedFileSnapshot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int64]$MaximumBytes = 16MB
    )

    if ($MaximumBytes -lt 1 -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Snapshot source is missing or has an invalid size limit: $Path"
    }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $stream = [IO.File]::Open($resolved, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt $MaximumBytes -or $stream.Length -gt [int]::MaxValue) {
            throw "Snapshot source exceeds the safe in-memory limit: $resolved"
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        [int]$offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "Snapshot source ended before its locked length: $resolved" }
            $offset += $read
        }
        $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        return [pscustomobject]@{
            name = [IO.Path]::GetFileName($resolved)
            path = $resolved
            size = [int64]$bytes.Length
            sha256 = $sha256
            bytes = $bytes
            stream = $stream
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Assert-CobbleLockedFileSnapshot {
    param([Parameter(Mandatory)]$Snapshot)

    if ($null -eq $Snapshot.stream -or -not $Snapshot.stream.CanRead) { throw "Locked snapshot is closed: $($Snapshot.path)" }
    $Snapshot.stream.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [Convert]::ToHexString($hasher.ComputeHash($Snapshot.stream)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ([int64]$Snapshot.stream.Length -ne [int64]$Snapshot.size -or $actualHash -cne [string]$Snapshot.sha256) {
        throw "Locked snapshot no longer matches its captured bytes: $($Snapshot.path)"
    }
    return $true
}

function Close-CobbleLockedFileSnapshot {
    param([AllowNull()]$Snapshot)

    if ($null -ne $Snapshot -and $null -ne $Snapshot.stream) { $Snapshot.stream.Dispose() }
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

function ConvertTo-CobbleSeedFileRecordSet {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][string]$Context,
        [switch]$AllowExactRetiredV1013Seeds
    )

    $byKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $result = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        if ($null -eq $entry) { throw "$Context contains a null file entry." }
        $path = [string]$entry.path
        $normalizedPath = $path.Replace('\', '/')
        $isExactRetiredV1013Seed = $AllowExactRetiredV1013Seeds -and
            $script:ExactRetiredV1013SeedIdentities.ContainsKey($normalizedPath)
        if ($isExactRetiredV1013Seed) {
            # v1.0.13 accidentally shipped a small, fixed set of generated
            # files as seeds. They are accepted only while authenticating that
            # exact signed base so a clean delta can retire them. They are
            # never accepted into a new manifest/payload or deleted locally.
            Assert-CobbleManagedPath -Path $path -Context $Context | Out-Null
        }
        else {
            Assert-CobbleSeedPathPolicy -Path $path -Context $Context | Out-Null
        }

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
        if ($isExactRetiredV1013Seed) {
            $expectedRetiredIdentity = $script:ExactRetiredV1013SeedIdentities[$normalizedPath]
            if ($size -ne [int64]$expectedRetiredIdentity.size -or
                $sha256 -cne [string]$expectedRetiredIdentity.sha256) {
                throw "$Context does not match the exact retired v1.0.13 seed identity: $path"
            }
        }

        $key = Get-CobblePathKey $path
        if ($byKey.ContainsKey($key)) {
            throw "$Context contains a duplicate or Windows/Unicode-colliding path: $($byKey[$key].path) / $path"
        }
        $record = [pscustomobject]@{ path = $path; size = $size; sha256 = $sha256 }
        $byKey.Add($key, $record)
        $result.Add($record)
    }
    $ordered = @($result | Sort-Object -Property @{ Expression = { Get-CobblePathKey $_.path }; Ascending = $true })
    return [pscustomobject]@{ Entries = $ordered; ByKey = $byKey }
}

function ConvertTo-CobbleLegacyCleanupSet {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][string]$Context
    )

    $byKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
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
        $identity = "$key`0$size`0$sha256"
        if (-not $identities.Add($identity)) {
            throw "$Context contains a duplicate cleanup identity: $path ($size, $sha256)"
        }
        if (-not $byKey.ContainsKey($key)) {
            $byKey.Add($key, [Collections.Generic.List[object]]::new())
        }
        $record = [pscustomobject]@{ path = $path; size = $size; sha256 = $sha256 }
        $byKey[$key].Add($record)
        $result.Add($record)
    }
    $ordered = @($result | Sort-Object `
        -Property @{ Expression = { Get-CobblePathKey $_.path }; Ascending = $true }, size, sha256)
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

function Assert-CobbleStagedPayloadParts {
    param(
        [AllowNull()]$Payload,
        [Parameter(Mandatory)][string]$StagingRoot
    )

    if ($null -eq $Payload) { return @() }
    Assert-CobblePayloadMetadata $Payload

    $root = [IO.Path]::GetFullPath($StagingRoot)
    $identities = [Collections.Generic.List[object]]::new()
    $aggregate = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = [byte[]]::new(4MB)
    [int64]$total = 0
    try {
        foreach ($part in @($Payload.parts)) {
            $name = [string]$part.name
            $path = [IO.Path]::GetFullPath((Join-Path $root $name))
            if (-not (Test-CobblePathAtOrUnder -Path $path -Base $root) -or
                -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Staged payload part is missing or outside staging: $name"
            }

            $partHash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($stream.Length -ne [int64]$part.size) {
                    throw "Staged payload part size does not match its signed manifest: $name"
                }
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $partHash.AppendData($buffer, 0, $read)
                    $aggregate.AppendData($buffer, 0, $read)
                    if ($total -gt [int64]::MaxValue - $read) { throw 'Staged payload size overflows Int64.' }
                    $total += $read
                }
                $actualPartHash = [Convert]::ToHexString($partHash.GetHashAndReset()).ToLowerInvariant()
            }
            finally {
                $stream.Dispose()
                $partHash.Dispose()
            }
            if ($actualPartHash -cne [string]$part.sha256) {
                throw "Staged payload part SHA-256 does not match its signed manifest: $name"
            }
            $identities.Add([pscustomobject]@{ name = $name; path = $path; size = [int64]$part.size; sha256 = $actualPartHash })
        }

        $actualPayloadHash = [Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $aggregate.Dispose() }

    if ($total -ne [int64]$Payload.size -or $actualPayloadHash -cne [string]$Payload.sha256) {
        throw 'Ordered staged payload parts do not reconstruct the exact signed payload size and SHA-256.'
    }
    return @($identities)
}

function Assert-CobbleReleaseAssetCount {
    param([Parameter(Mandatory)][int64]$PayloadPartCount)

    $maximumPayloadPartCount = $script:MaximumReleaseAssetCount - $script:ReservedReleaseMetadataAssetCount
    if ($PayloadPartCount -lt 0 -or $PayloadPartCount -gt $maximumPayloadPartCount) {
        throw "A release may contain at most $maximumPayloadPartCount payload parts because the manifest and signature consume two of the $($script:MaximumReleaseAssetCount) safe asset slots; requested parts: $PayloadPartCount."
    }
    return $true
}

function Assert-CobblePublicReleaseCapacity {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$Releases,
        [ValidateRange(0, 1)]
        [int]$AdditionalPublicReleases = 1
    )

    $ids = [Collections.Generic.HashSet[int64]]::new()
    [int]$publicCount = 0
    foreach ($release in $Releases) {
        $idProperty = $release.PSObject.Properties['id']
        $draftProperty = $release.PSObject.Properties['draft']
        [int64]$releaseId = 0
        if ($null -eq $idProperty -or $null -eq $draftProperty -or -not ($draftProperty.Value -is [bool]) -or
            -not [int64]::TryParse(
                [Convert]::ToString($idProperty.Value, [Globalization.CultureInfo]::InvariantCulture),
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$releaseId) -or $releaseId -le 0 -or -not $ids.Add($releaseId)) {
            throw 'GitHub release index contains a missing, invalid, or duplicate release identity.'
        }
        # GitHub's unauthenticated release index omits drafts but includes
        # prereleases, so every non-draft consumes one updater scan slot.
        if (-not $draftProperty.Value) { $publicCount++ }
    }

    if ($publicCount + $AdditionalPublicReleases -gt $script:MaximumPublicReleaseCount) {
        throw "Publishing would expose $($publicCount + $AdditionalPublicReleases) GitHub releases, exceeding the updater's safe maximum of $($script:MaximumPublicReleaseCount)."
    }
    return $true
}

function Assert-CobbleLegacyCleanup {
    param(
        [AllowEmptyCollection()][Parameter(Mandatory)][object[]]$Entries,
        [Parameter(Mandatory)][object[]]$ForbiddenSets,
        [string[]]$AllowedOverlapPaths = @()
    )

    $legacy = ConvertTo-CobbleLegacyCleanupSet -Entries $Entries -Context 'legacyCleanup'
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $AllowedOverlapPaths) { [void]$allowed.Add((Get-CobblePathKey $path)) }
    foreach ($entry in $legacy.Entries) {
        $key = Get-CobblePathKey $entry.path
        foreach ($forbidden in $ForbiddenSets) {
            if ($forbidden.ByKey.ContainsKey($key) -and -not $allowed.Contains($key)) {
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
    Assert-CobbleSupportedMinimumUpdaterVersion -MinimumUpdaterVersion ([string](Get-CobbleOptionalPropertyValue $Manifest 'minimumUpdaterVersion')) | Out-Null

    $base = Get-CobbleOptionalPropertyValue $Manifest 'base'
    $payloadFiles = Get-CobbleRuntimeCollectionState $Manifest 'payloadFiles' 'Baseline payloadFiles'
    $deletedFiles = Get-CobbleRuntimeCollectionState $Manifest 'deletedFiles' 'Baseline deletedFiles'
    $filesState = Get-CobbleRuntimeCollectionState $Manifest 'files' 'Baseline files'
    $seedFilesState = Get-CobbleRuntimeCollectionState $Manifest 'seedFiles' 'Baseline seedFiles'
    $reofferSeedPaths = Get-CobbleRuntimeCollectionState $Manifest 'reofferSeedPaths' 'Baseline reofferSeedPaths'
    $seedTextReplacements = Get-CobbleRuntimeCollectionState $Manifest 'seedTextReplacements' 'Baseline seedTextReplacements'
    $deletePaths = Get-CobbleRuntimeCollectionState $Manifest 'deletePaths' 'Baseline deletePaths'
    $legacyCleanup = Get-CobbleRuntimeCollectionState $Manifest 'legacyCleanup' 'Baseline legacyCleanup'
    if ($null -ne $base -or $payloadFiles.Entries.Count -ne 0 -or $deletedFiles.Entries.Count -ne 0 -or
        $reofferSeedPaths.Entries.Count -ne 0 -or $seedTextReplacements.Entries.Count -ne 0) {
        throw 'Baseline manifests cannot contain delta-only base, payloadFiles, deletedFiles, reofferSeedPaths, or seedTextReplacements data.'
    }

    $files = ConvertTo-CobbleFileRecordSet -Entries @($filesState.Entries) -Context 'baseline authoritative files'
    $seedFiles = ConvertTo-CobbleSeedFileRecordSet -Entries @($seedFilesState.Entries) -Context 'baseline create-only defaults'
    foreach ($seed in $seedFiles.Entries) {
        if ($files.ByKey.ContainsKey((Get-CobblePathKey $seed.path))) {
            throw "Baseline create-only default overlaps a managed file: $($seed.path)"
        }
    }
    if ($seedFiles.Entries.Count -gt 0 -and [Version]$Manifest.minimumUpdaterVersion -lt [Version]'1.2.6') {
        throw 'Baselines with create-only defaults must require updater 1.2.6 or newer.'
    }
    $payload = Get-CobbleOptionalPropertyValue $Manifest 'payload'
    Assert-CobblePayloadMetadata $payload

    $deleteKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($pathValue in @($deletePaths.Entries)) {
        $path = [string]$pathValue
        Assert-CobbleManagedPath -Path $path -Context 'baseline deletePaths' | Out-Null
        $key = Get-CobblePathKey $path
        if (-not $deleteKeys.Add($key) -or $files.ByKey.ContainsKey($key) -or $seedFiles.ByKey.ContainsKey($key)) {
            throw "Baseline deletePaths is duplicate or overlaps files/defaults: $path"
        }
    }
    Assert-CobbleLegacyCleanup -Entries @($legacyCleanup.Entries) -ForbiddenSets @($files, $seedFiles) | Out-Null
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
    $seedFilesState = Get-CobbleRuntimeCollectionState $Manifest 'seedFiles' 'Signed base seedFiles'
    $seedSet = ConvertTo-CobbleSeedFileRecordSet -Entries @($seedFilesState.Entries) `
        -Context 'signed base create-only defaults' `
        -AllowExactRetiredV1013Seeds:([string]$Manifest.version -ceq '1.0.13')
    foreach ($seed in $seedSet.Entries) {
        if ($fileSet.ByKey.ContainsKey((Get-CobblePathKey $seed.path))) {
            throw "Signed base overlaps managed files and create-only defaults: $($seed.path)"
        }
    }
    return $fileSet
}

function New-CobbleDeltaPlan {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$CurrentFiles,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][object[]]$BaseFiles,
        [string[]]$OwnershipTransitionPaths = @()
    )

    $currentSet = ConvertTo-CobbleFileRecordSet -Entries $CurrentFiles -Context 'current authoritative files'
    $baseSet = ConvertTo-CobbleFileRecordSet -Entries $BaseFiles -Context 'signed base manifest files'
    $payload = [Collections.Generic.List[object]]::new()
    $deleted = [Collections.Generic.List[object]]::new()
    $unchanged = [Collections.Generic.List[object]]::new()
    $ownershipTransitions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $OwnershipTransitionPaths) { [void]$ownershipTransitions.Add((Get-CobblePathKey $path)) }

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
        $key = Get-CobblePathKey $base.path
        if (-not $currentSet.ByKey.ContainsKey($key) -and -not $ownershipTransitions.Contains($key)) {
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
    $filesState = Get-CobbleRuntimeCollectionState $Manifest 'files' 'Delta files'
    $payloadFilesState = Get-CobbleRuntimeCollectionState $Manifest 'payloadFiles' 'Delta payloadFiles'
    $deletedFilesState = Get-CobbleRuntimeCollectionState $Manifest 'deletedFiles' 'Delta deletedFiles'
    $seedFilesState = Get-CobbleRuntimeCollectionState $Manifest 'seedFiles' 'Delta seedFiles'
    $reofferSeedPathsState = Get-CobbleRuntimeCollectionState $Manifest 'reofferSeedPaths' 'Delta reofferSeedPaths'
    $seedTextReplacementsState = Get-CobbleRuntimeCollectionState $Manifest 'seedTextReplacements' 'Delta seedTextReplacements'
    $deletePaths = Get-CobbleRuntimeCollectionState $Manifest 'deletePaths' 'Delta deletePaths'
    $legacyCleanup = Get-CobbleRuntimeCollectionState $Manifest 'legacyCleanup' 'Delta legacyCleanup'
    if ($deletePaths.Entries.Count -ne 0) {
        throw 'Delta manifests must use exact deletedFiles entries instead of path-only deletePaths.'
    }
    $seedFiles = ConvertTo-CobbleSeedFileRecordSet -Entries @($seedFilesState.Entries) -Context 'delta create-only defaults'
    $reofferSeedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($pathValue in @($reofferSeedPathsState.Entries)) {
        $path = [string]$pathValue
        Assert-CobbleSeedPathPolicy -Path $path -Context 'delta re-offered default' | Out-Null
        $key = Get-CobblePathKey $path
        if (-not $reofferSeedKeys.Add($key) -or -not $seedFiles.ByKey.ContainsKey($key)) {
            throw "Delta reofferSeedPaths is duplicate or does not reference a declared seed file: $path"
        }
    }
    $seedTextReplacementKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $hasOptionsTextReplacement = $false
    $optionsReplacementCount = 0
    $hasIrisToggleReplacement = $false
    $hasFancyToastsReplacement = $false
    foreach ($replacement in @($seedTextReplacementsState.Entries)) {
        $path = [string]$replacement.path
        Assert-CobbleSeedTextReplacementPathPolicy -Path $path -Context 'delta seed text replacement' | Out-Null
        $key = Get-CobblePathKey $path
        $oldText = [string]$replacement.oldText
        $newText = [string]$replacement.newText
        $requiredLinesValue = Get-CobbleOptionalPropertyValue $replacement 'requiredLines'
        $requiredLines = @(if ($null -ne $requiredLinesValue) {
            @($requiredLinesValue | ForEach-Object { [string]$_ })
        } else { @() })
        $migrationIdValue = Get-CobbleOptionalPropertyValue $replacement 'migrationId'
        $migrationId = if ($null -eq $migrationIdValue) { '' } else { [string]$migrationIdValue }
        $identity = $key + [char]0 + $oldText
        $safeText = -not [string]::IsNullOrEmpty($oldText) -and -not [string]::IsNullOrEmpty($newText) -and
            $oldText -cne $newText -and $oldText.Length -le 4096 -and $newText.Length -le 4096 -and
            -not $oldText.Contains([char]0) -and -not $newText.Contains([char]0) -and
            -not $oldText.Contains("`n") -and -not $oldText.Contains("`r") -and
            -not $newText.Contains("`n") -and -not $newText.Contains("`r")
        $validIris = $path -ieq 'config/iris.properties' -and $requiredLines.Count -eq 0 -and
            [string]::IsNullOrEmpty($migrationId) -and
            $oldText.StartsWith('shaderPack=', [StringComparison]::Ordinal) -and
            $newText.StartsWith('shaderPack=', [StringComparison]::Ordinal)
        $contestTrackerK = 'key_key.companion_bonds.open_contest_tracker:key.keyboard.k'
        $optionsMigrationId = 'options-contest-tracker-k-collision-v1'
        $validOptions = $path -ieq 'options.txt' -and $requiredLines.Count -eq 1 -and
            $migrationId -ceq $optionsMigrationId -and
            $requiredLines[0] -ceq $contestTrackerK -and
            (($oldText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.k' -and
                $newText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.unknown') -or
             ($oldText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.k' -and
                $newText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.unknown'))
        if (-not $seedTextReplacementKeys.Add($identity) -or
            -not $seedFiles.ByKey.ContainsKey($key) -or
            ($validIris -and -not $reofferSeedKeys.Contains($key)) -or
            ($validOptions -and $reofferSeedKeys.Contains($key)) -or
            -not $safeText -or -not ($validIris -or $validOptions)) {
            throw "Delta seedTextReplacements contains an unsafe, duplicate, or undeclared replacement: $path"
        }
        if ($validOptions) {
            $hasOptionsTextReplacement = $true
            $optionsReplacementCount++
            if ($oldText -ceq 'key_iris.keybind.toggleShaders:key.keyboard.k') { $hasIrisToggleReplacement = $true }
            if ($oldText -ceq 'key_key.fancytoasts.config_menu:key.keyboard.k') { $hasFancyToastsReplacement = $true }
        }
    }
    if ($optionsReplacementCount -ne 0 -and
        ($optionsReplacementCount -ne 2 -or -not $hasIrisToggleReplacement -or -not $hasFancyToastsReplacement)) {
        throw 'The options migration must contain the complete reviewed Iris and Fancy Toasts repair pair.'
    }
    $legacySet = ConvertTo-CobbleLegacyCleanupSet -Entries @($legacyCleanup.Entries) -Context 'delta legacyCleanup'
    $declaredPayloadForFeaturePolicy = ConvertTo-CobbleFileRecordSet `
        -Entries @($payloadFilesState.Entries) -Context 'delta payloadFiles feature policy' -AllowEmpty
    $managedRepairPaths = @($legacySet.Entries | Where-Object {
        $declaredPayloadForFeaturePolicy.ByKey.ContainsKey((Get-CobblePathKey $_.path))
    } | ForEach-Object { [string]$_.path } | Sort-Object -Unique)
    $refreshesExactSeeds = @($legacySet.Entries | Where-Object {
        $key = Get-CobblePathKey $_.path
        $seedFiles.ByKey.ContainsKey($key) -and $reofferSeedKeys.Contains($key)
    }).Count -gt 0
    $minimumFloor = if ($managedRepairPaths.Count -gt 0) {
        [Version]'1.2.13'
    }
    elseif ($hasOptionsTextReplacement) {
        [Version]'1.2.11'
    }
    elseif ($seedTextReplacementKeys.Count -gt 0 -or $refreshesExactSeeds) {
        [Version]'1.2.10'
    }
    elseif ($reofferSeedKeys.Count -gt 0) {
        [Version]'1.2.9'
    }
    elseif ($seedFiles.Entries.Count -gt 0) {
        [Version]'1.2.6'
    }
    else {
        [Version]'1.2.0'
    }
    $minimumUpdaterVersion = [string](Get-CobbleOptionalPropertyValue $Manifest 'minimumUpdaterVersion')
    Assert-CobbleSupportedMinimumUpdaterVersion -MinimumUpdaterVersion $minimumUpdaterVersion | Out-Null
    if ([Version]$minimumUpdaterVersion -lt $minimumFloor) {
        throw "Delta manifest minimum updater version is below $minimumFloor for its feature set."
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

    $baseSet = ConvertTo-CobbleFileRecordSet -Entries @($BaseManifest.files) -Context 'delta signed-base files'
    $fullSet = ConvertTo-CobbleFileRecordSet -Entries @($filesState.Entries) -Context 'delta authoritative files'
    $ownershipTransitions = [Collections.Generic.List[string]]::new()
    foreach ($baseFile in $baseSet.Entries) {
        $key = Get-CobblePathKey $baseFile.path
        $hasExactTransitionIdentity = $legacySet.ByKey.ContainsKey($key) -and @(
            $legacySet.ByKey[$key] | Where-Object { Test-CobbleSameFileRecord -Left $_ -Right $baseFile }
        ).Count -gt 0
        if (-not $fullSet.ByKey.ContainsKey($key) -and $seedFiles.ByKey.ContainsKey($key) -and
            $reofferSeedKeys.Contains($key) -and $hasExactTransitionIdentity) {
            $ownershipTransitions.Add([string]$baseFile.path)
        }
    }
    $expected = New-CobbleDeltaPlan -CurrentFiles @($filesState.Entries) -BaseFiles @($BaseManifest.files) `
        -OwnershipTransitionPaths @($ownershipTransitions)
    $actualPayload = ConvertTo-CobbleFileRecordSet -Entries @($payloadFilesState.Entries) -Context 'delta payloadFiles' -AllowEmpty
    $actualDeleted = ConvertTo-CobbleFileRecordSet -Entries @($deletedFilesState.Entries) -Context 'delta deletedFiles' -AllowEmpty
    $expectedPayload = ConvertTo-CobbleFileRecordSet -Entries @($expected.PayloadFiles) -Context 'expected delta payloadFiles' -AllowEmpty
    $expectedDeleted = ConvertTo-CobbleFileRecordSet -Entries @($expected.DeletedFiles) -Context 'expected delta deletedFiles' -AllowEmpty

    foreach ($seed in $seedFiles.Entries) {
        $key = Get-CobblePathKey $seed.path
        if ($actualPayload.ByKey.ContainsKey($key) -or $actualDeleted.ByKey.ContainsKey($key) -or
            $fullSet.ByKey.ContainsKey($key)) {
            throw "Delta create-only default overlaps a managed, changed, or deleted file: $($seed.path)"
        }
    }
    foreach ($repairPath in $managedRepairPaths) {
        $key = Get-CobblePathKey $repairPath
        if (-not $baseSet.ByKey.ContainsKey($key) -or -not $actualPayload.ByKey.ContainsKey($key)) {
            throw "Exact managed repair is not a changed signed-base payload path: $repairPath"
        }
        $replacement = $actualPayload.ByKey[$key]
        if (@($legacySet.ByKey[$key] | Where-Object { Test-CobbleSameFileRecord -Left $_ -Right $replacement }).Count -gt 0) {
            throw "Exact managed repair identity equals its signed replacement: $repairPath"
        }
    }

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

    if ($actualPayload.Entries.Count -eq 0 -and $seedFiles.Entries.Count -eq 0) {
        if ($null -ne (Get-CobbleOptionalPropertyValue $Manifest 'payload')) { throw 'A deletion-only delta must have a null payload.' }
        if ($actualDeleted.Entries.Count -eq 0) { throw 'A delta must change or delete at least one file.' }
    }
    elseif ($null -eq (Get-CobbleOptionalPropertyValue $Manifest 'payload')) {
        throw 'A delta with changed/new files must declare a payload.'
    }
    else {
        Assert-CobblePayloadMetadata (Get-CobbleOptionalPropertyValue $Manifest 'payload')
    }

    $allowedCleanupOverlaps = @($reofferSeedPathsState.Entries | ForEach-Object { [string]$_ }) + @($managedRepairPaths)
    Assert-CobbleLegacyCleanup -Entries @($legacyCleanup.Entries) -ForbiddenSets @($fullSet, $baseSet, $seedFiles) `
        -AllowedOverlapPaths $allowedCleanupOverlaps | Out-Null

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

function Assert-CobblePublishedRequiredAssets {
    param(
        [Parameter(Mandatory)][object[]]$LocalAssets,
        [Parameter(Mandatory)][object[]]$RemoteAssets,
        [Parameter(Mandatory)][string[]]$RequiredNames,
        [Parameter(Mandatory)][string]$Context
    )

    $local = (New-CobbleExpectedAssetIndex $LocalAssets).ByName
    if ($local.Count -ne $RequiredNames.Count -or $RequiredNames.Where({ -not $local.ContainsKey($_) }).Count -gt 0) {
        throw "$Context local identity does not contain exactly the required assets."
    }
    foreach ($requiredName in $RequiredNames) {
        if ($local[$requiredName].name -cne $requiredName -or $local[$requiredName].size -le 0) {
            throw "$Context local asset identity is invalid: $requiredName"
        }
    }

    $seenRemote = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($remote in $RemoteAssets) {
        $name = [string]$remote.name
        if (-not $seenRemote.Add($name)) { throw "$Context published release contains duplicate or case-colliding assets: $name" }
        if (-not $local.ContainsKey($name)) { continue }

        $wanted = $local[$name]
        if ($name -cne $wanted.name -or [string]$remote.state -cne 'uploaded' -or
            [int64]$remote.size -ne $wanted.size -or [string]$remote.digest -cne "sha256:$($wanted.sha256)") {
            throw "$Context local asset does not exactly match the uploaded published release asset: $name"
        }
        [void]$matched.Add($name)
    }
    $missing = @($RequiredNames | Where-Object { -not $matched.Contains($_) })
    if ($missing.Count -gt 0) { throw "$Context published release is missing exact uploaded identity assets: $($missing -join ', ')" }
    return $true
}

function Assert-CobblePublishedBaseAssets {
    param(
        [Parameter(Mandatory)][object[]]$LocalAssets,
        [Parameter(Mandatory)][object[]]$RemoteAssets
    )

    return Assert-CobblePublishedRequiredAssets -LocalAssets $LocalAssets -RemoteAssets $RemoteAssets `
        -RequiredNames @('cobble-music-update.json', 'cobble-music-update.sig') -Context 'Signed base'
}

function Assert-CobblePublishedUpdaterAsset {
    param(
        [Parameter(Mandatory)]$LocalAsset,
        [Parameter(Mandatory)][object[]]$RemoteAssets
    )

    return Assert-CobblePublishedRequiredAssets -LocalAssets @($LocalAsset) -RemoteAssets $RemoteAssets `
        -RequiredNames @('CobbleMusicUpdater.exe') -Context 'Pinned updater'
}

function Test-CobblePaginationHasNextPage {
    param(
        [Parameter(Mandatory)][int]$Page,
        [Parameter(Mandatory)][int]$ResultCount,
        [int]$PageSize = 100,
        [Parameter(Mandatory)][int]$MaximumPages,
        [string]$Context = 'GitHub API result'
    )

    if ($Page -lt 1 -or $PageSize -lt 1 -or $MaximumPages -lt 1 -or $ResultCount -lt 0 -or $ResultCount -gt $PageSize) {
        throw "$Context returned invalid pagination metadata."
    }
    if ($ResultCount -lt $PageSize) { return $false }
    if ($Page -ge $MaximumPages) {
        throw "$Context reached its $MaximumPages-page safety cap; refusing a truncated result."
    }
    return $true
}

function Assert-CobblePayloadZipInventory {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][object[]]$ExpectedFiles,
        [AllowEmptyCollection()][object[]]$ExpectedSeedFiles = @()
    )

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "Payload ZIP was not found: $ZipPath" }
    $declaredSeeds = ConvertTo-CobbleSeedFileRecordSet -Entries $ExpectedSeedFiles -Context 'payload ZIP declared seed files'
    $managedEntries = @($ExpectedFiles | Where-Object {
        -not $declaredSeeds.ByKey.ContainsKey((Get-CobblePathKey ([string]$_.path)))
    })
    $seedEntries = @($ExpectedFiles | Where-Object {
        $declaredSeeds.ByKey.ContainsKey((Get-CobblePathKey ([string]$_.path)))
    })
    $managed = ConvertTo-CobbleFileRecordSet -Entries $managedEntries -Context 'payload ZIP expected managed files' -AllowEmpty
    $seeds = ConvertTo-CobbleSeedFileRecordSet -Entries $seedEntries -Context 'payload ZIP expected seed files'
    if ($seeds.Entries.Count -ne $declaredSeeds.Entries.Count) {
        throw 'Payload ZIP expected files do not contain the exact declared seed inventory.'
    }
    foreach ($seed in $declaredSeeds.Entries) {
        $key = Get-CobblePathKey $seed.path
        if (-not $seeds.ByKey.ContainsKey($key) -or -not (Test-CobbleSameFileRecord $seed $seeds.ByKey[$key])) {
            throw "Payload ZIP declared seed identity differs from its expected file: $($seed.path)"
        }
    }
    $expectedByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($managed.Entries) + @($seeds.Entries)) {
        $key = Get-CobblePathKey $entry.path
        if (-not $expectedByKey.TryAdd($key, $entry)) {
            throw "Payload ZIP expected managed and seed files overlap: $($entry.path)"
        }
    }
    $expectedEntries = @($expectedByKey.Values)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $fileStream = [IO.File]::Open($ZipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            foreach ($entry in $archive.Entries) {
                $path = [string]$entry.FullName
                if ([string]::IsNullOrWhiteSpace([string]$entry.Name)) { throw "Payload ZIP contains a directory or unnamed entry: $path" }
                $key = Get-CobblePathKey $path
                if ($declaredSeeds.ByKey.ContainsKey($key)) {
                    Assert-CobbleSeedPathPolicy -Path $path -Context 'payload ZIP seed entry' | Out-Null
                }
                else {
                    Assert-CobbleManagedPath -Path $path -Context 'payload ZIP managed entry' | Out-Null
                }
                if (-not $seen.Add($key)) { throw "Payload ZIP contains a duplicate or case/Unicode-colliding entry: $path" }
                if (-not $expectedByKey.ContainsKey($key)) { throw "Payload ZIP contains an unexpected entry: $path" }

                $wanted = $expectedByKey[$key]
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

    $missing = @($expectedEntries | Where-Object { -not $seen.Contains((Get-CobblePathKey $_.path)) })
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

function Get-CobbleStarterAssetForDeletion {
    param(
        [Parameter(Mandatory)]$Candidate,
        [AllowEmptyCollection()][Parameter(Mandatory)][object[]]$ExpectedAssets,
        [AllowEmptyCollection()][Parameter(Mandatory)][object[]]$RemoteAssets
    )

    $repairable = @(Get-CobbleRepairableStarterAssets -ExpectedAssets $ExpectedAssets -RemoteAssets $RemoteAssets)
    $matches = @($repairable | Where-Object {
        [int64]$_.id -eq [int64]$Candidate.id -and [string]$_.name -ceq [string]$Candidate.name
    })
    if ($matches.Count -gt 1) { throw "Starter repair candidate is ambiguous: $($Candidate.name)" }
    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Assert-CobbleReleaseIdentityState {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][int64]$ExpectedId,
        [Parameter(Mandatory)][string]$ExpectedTag,
        [Parameter(Mandatory)][ValidateSet('draft', 'public')][string]$ExpectedState
    )

    if ($null -eq $Release) { throw "GitHub release identity is missing for reserved tag $ExpectedTag." }
    foreach ($requiredProperty in @('id', 'tag_name', 'draft', 'prerelease')) {
        if ($null -eq $Release.PSObject.Properties[$requiredProperty]) {
            throw "GitHub release identity is missing $requiredProperty for reserved tag $ExpectedTag."
        }
    }
    $draftValue = Get-CobbleOptionalPropertyValue $Release 'draft'
    $prereleaseValue = Get-CobbleOptionalPropertyValue $Release 'prerelease'
    if (-not ($draftValue -is [bool]) -or -not ($prereleaseValue -is [bool])) {
        throw "GitHub release identity has a non-Boolean draft/prerelease state for reserved tag $ExpectedTag."
    }
    [int64]$actualId = 0
    if (-not [int64]::TryParse(
        [Convert]::ToString((Get-CobbleOptionalPropertyValue $Release 'id'), [Globalization.CultureInfo]::InvariantCulture),
        [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$actualId) -or $actualId -ne $ExpectedId -or
        [string](Get-CobbleOptionalPropertyValue $Release 'tag_name') -cne $ExpectedTag -or $prereleaseValue) {
        throw "GitHub release identity/state changed for reserved tag $ExpectedTag."
    }
    $isDraft = $draftValue
    if (($ExpectedState -ceq 'draft' -and -not $isDraft) -or ($ExpectedState -ceq 'public' -and $isDraft)) {
        throw "GitHub release is not in expected $ExpectedState state: $ExpectedTag"
    }
    if ($ExpectedState -ceq 'public') {
        $publishedAt = $Release.PSObject.Properties['published_at']
        if ($null -eq $publishedAt -or [string]::IsNullOrWhiteSpace([string]$publishedAt.Value)) {
            throw "GitHub release has no public publication timestamp: $ExpectedTag"
        }
    }
    return $true
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
    'Get-CobbleOptionalPropertyValue',
    'Get-CobblePathKey',
    'Assert-CobbleManagedPath',
    'Assert-CobbleSourcePathPolicy',
    'Assert-CobbleSeedPathPolicy',
    'Assert-CobbleSeedTextReplacementPathPolicy',
    'Test-CobbleNeverDistributePath',
    'Get-CobbleManagedSourceFiles',
    'Get-CobbleSeedSourceFiles',
    'Assert-CobblePrivateKeyIsolation',
    'Open-CobbleLockedFileSnapshot',
    'Assert-CobbleLockedFileSnapshot',
    'Close-CobbleLockedFileSnapshot',
    'ConvertTo-CobbleFileRecordSet',
    'Test-CobbleSameFileRecord',
    'ConvertTo-CobbleLegacyCleanupSet',
    'ConvertTo-CobbleSeedFileRecordSet',
    'Assert-CobbleSupportedMinimumUpdaterVersion',
    'Assert-CobbleVersionAdvance',
    'Assert-CobbleBaseManifest',
    'Assert-CobbleV1Manifest',
    'New-CobbleDeltaPlan',
    'Assert-CobbleDeltaManifest',
    'Assert-CobblePublishedBaseAssets',
    'Assert-CobblePublishedUpdaterAsset',
    'Assert-CobbleStagedPayloadParts',
    'Assert-CobbleReleaseAssetCount',
    'Assert-CobblePublicReleaseCapacity',
    'Assert-CobblePayloadZipInventory',
    'Test-CobblePaginationHasNextPage',
    'Get-CobbleRepairableStarterAssets',
    'Get-CobbleStarterAssetForDeletion',
    'Assert-CobbleReleaseIdentityState',
    'Assert-CobbleRemoteAssetInventory'
)
