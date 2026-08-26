<#
Builds and validates the self-contained win-x64 updater, then optionally
uploads its two public assets to a persistent GitHub draft release.

Safety model:
  * -DryRun never changes tracked files or GitHub.
  * The default mode atomically refreshes the bootstrap's pinned updater
    version/hash after every test passes, but makes no GitHub change.
  * -UploadDraft requires those pins and all release inputs to be committed,
    creates or resumes one exact updater-v* draft, and leaves it as a draft.
  * -Publish additionally requires -ConfirmPublish and changes draft=false
    only after GitHub reports the exact expected asset names, sizes, states,
    and SHA-256 digests.

The script uses GitHub CLI's existing credential store. It accepts, reads,
and prints no access tokens or other secrets.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'Kewz4/Cobble-Music',

    [switch]$DryRun,
    [switch]$UploadDraft,
    [switch]$Publish,
    [switch]$ConfirmPublish,

    # Recovery is intentionally separate from ordinary resume. It is valid
    # only for an already-existing draft after the operator has confirmed no
    # GitHub CLI upload is still active.
    [switch]$RepairStaleUploads,

    # Read-only diagnostic used by the safety suite to prove that a publisher
    # invoked by full path never binds Git operations to the caller's CWD.
    [switch]$VerifySourceBinding
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$BuildInfoPath = Join-Path $Root 'updater\CobbleMusicUpdater\BuildInfo.cs'
$ProjectPath = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$BuildScript = Join-Path $Root 'tools\Build-CobbleMusicUpdater.ps1'
$GlobalJsonPath = Join-Path $Root 'global.json'
$NuGetConfigPath = Join-Path $Root 'NuGet.Config'
$BootstrapPath = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
$DistExe = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe'
$PipelineTest = Join-Path $Root 'tests\Test-UpdaterReleasePipeline.ps1'
$ReproducibilityTest = Join-Path $Root 'tests\Test-UpdaterBuildReproducibility.ps1'
$TemporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-music-updater-release-' + [Guid]::NewGuid().ToString('N'))
$GitHubMutation = $UploadDraft -or $Publish

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Atomically([string]$Path, [string]$Text) {
    $directory = Split-Path -Parent $Path
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.new-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($temporary, $Text, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Copy-Atomically([string]$Source, [string]$Destination) {
    $directory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Destination) + '.new-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::Copy($Source, $temporary, $true)
        [IO.File]::Move($temporary, $Destination, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function ConvertTo-CanonicalUpdaterVersion([string]$Version, [string]$Context) {
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Context must be a canonical three-part numeric version with no leading zeroes: $Version"
    }
    try { $parsed = [Version]::new($Version) }
    catch { throw "$Context is outside the supported numeric version range: $Version" }
    if ($parsed.ToString(3) -cne $Version) {
        throw "$Context is not canonical: $Version"
    }
    return $parsed
}

function Get-BuildVersion([string]$SourceBuildInfoPath, [string]$SourceProjectPath) {
    if (-not (Test-Path -LiteralPath $SourceBuildInfoPath -PathType Leaf)) { throw "BuildInfo.cs is missing: $SourceBuildInfoPath" }
    if (-not (Test-Path -LiteralPath $SourceProjectPath -PathType Leaf)) { throw "Updater project is missing: $SourceProjectPath" }

    $buildInfo = [IO.File]::ReadAllText($SourceBuildInfoPath)
    $versionMatches = [regex]::Matches(
        $buildInfo,
        '(?m)^\s*public\s+const\s+string\s+Version\s*=\s*"(?<version>[^"]+)"\s*;\s*$')
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one BuildInfo.Version constant, found $($versionMatches.Count)."
    }
    $version = $versionMatches[0].Groups['version'].Value
    $null = ConvertTo-CanonicalUpdaterVersion $version 'BuildInfo.Version'

    [xml]$project = [IO.File]::ReadAllText($SourceProjectPath)
    $projectVersionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    if ($projectVersionNodes.Count -ne 1) {
        throw "Expected exactly one <Version> in the updater project, found $($projectVersionNodes.Count)."
    }
    $projectVersion = $projectVersionNodes[0].InnerText.Trim()
    if ($projectVersion -cne $version) {
        throw "BuildInfo.Version ($version) does not exactly match the project Version ($projectVersion)."
    }
    return $version
}

function Get-SingleQuotedAssignment([string]$Text, [string]$Name) {
    $pattern = '(?m)^[ \t]*' + [regex]::Escape('$' + $Name) + '[ \t]*=[ \t]*''(?<value>[^''\r\n]*)''[ \t]*\r?$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one `$${Name} assignment, found $($matches.Count)." }
    return $matches[0].Groups['value'].Value
}

function Set-SingleQuotedAssignment([string]$Text, [string]$Name, [string]$Value) {
    if ($Value.Contains("'")) { throw "Unsafe single quote in replacement value for `$${Name}." }
    $pattern = '(?m)^(?<prefix>[ \t]*' + [regex]::Escape('$' + $Name) + '[ \t]*=[ \t]*)''(?<value>[^''\r\n]*)''(?<suffix>[ \t]*)(?<eol>\r?)$'
    $expression = [regex]::new($pattern)
    $matches = $expression.Matches($Text)
    if ($matches.Count -ne 1) { throw "Expected exactly one `$${Name} assignment, found $($matches.Count)." }
    return $expression.Replace(
        $Text,
        [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            return $match.Groups['prefix'].Value + "'" + $Value + "'" + $match.Groups['suffix'].Value + $match.Groups['eol'].Value
        },
        1)
}

function Get-ProposedBootstrap([string]$Version, [string]$UpdaterSha256, [string]$SourceBootstrapPath) {
    if (-not (Test-Path -LiteralPath $SourceBootstrapPath -PathType Leaf)) { throw "Bootstrap script is missing: $SourceBootstrapPath" }
    $text = [IO.File]::ReadAllText($SourceBootstrapPath)
    $bootstrapRepository = Get-SingleQuotedAssignment $text 'Repository'
    if ($bootstrapRepository -cne $Repository) {
        throw "Bootstrap repository ($bootstrapRepository) does not match requested repository ($Repository)."
    }
    $text = Set-SingleQuotedAssignment $text 'UpdaterVersion' $Version
    $text = Set-SingleQuotedAssignment $text 'ExpectedUpdaterSha256' $UpdaterSha256.ToUpperInvariant()

    $commentPattern = '(?m)^# SHA-256 of CobbleMusicUpdater\.exe from updater-v[^\r\n]+(?<eol>\r?)$'
    $commentMatches = [regex]::Matches($text, $commentPattern)
    if ($commentMatches.Count -ne 1) {
        throw "Expected exactly one updater checksum comment, found $($commentMatches.Count)."
    }
    $commentExpression = [regex]::new($commentPattern)
    $text = $commentExpression.Replace(
        $text,
        [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            return "# SHA-256 of CobbleMusicUpdater.exe from updater-v$Version." + $match.Groups['eol'].Value
        },
        1)

    if ((Get-SingleQuotedAssignment $text 'UpdaterVersion') -cne $Version) { throw 'Bootstrap version replacement did not verify.' }
    if (-not (Get-SingleQuotedAssignment $text 'ExpectedUpdaterSha256').Equals($UpdaterSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Bootstrap checksum replacement did not verify.'
    }
    return $text
}

function Invoke-Checked([string]$Description, [scriptblock]$Action) {
    Write-Host $Description
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

function Invoke-RootGit([string[]]$Arguments) {
    $output = @(& git -C $Root @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git -C $Root $($Arguments -join ' ') failed with exit code ${exitCode}: $([string]::Join([Environment]::NewLine, $output))"
    }
    return @($output)
}

function Get-BoundSourceCommit {
    $topLevelOutput = @(Invoke-RootGit @('rev-parse', '--show-toplevel'))
    if ($topLevelOutput.Count -ne 1) { throw 'Git did not return exactly one repository root.' }
    $topLevel = [IO.Path]::GetFullPath(([string]$topLevelOutput[0]).Trim()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $topLevel.Equals($Root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publisher source root $Root does not exactly match its Git repository root $topLevel."
    }

    $commitOutput = @(Invoke-RootGit @('rev-parse', '--verify', 'HEAD^{commit}'))
    if ($commitOutput.Count -ne 1) { throw 'Git did not return exactly one source commit.' }
    $commit = ([string]$commitOutput[0]).Trim().ToLowerInvariant()
    if ($commit -notmatch '^[0-9a-f]{40,64}$') { throw "Git returned an invalid source commit: $commit" }
    return $commit
}

function Assert-NoArchiveTransformAttributes([string]$Commit) {
    $treePaths = @(Invoke-RootGit @('ls-tree', '-r', '--name-only', $Commit, '--'))
    $attributePaths = @($treePaths | Where-Object { [IO.Path]::GetFileName([string]$_) -ceq '.gitattributes' })
    foreach ($attributePath in $attributePaths) {
        $attributeLines = @(Invoke-RootGit @('show', "${Commit}:$attributePath"))
        foreach ($line in $attributeLines) {
            $trimmed = ([string]$line).Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#', [StringComparison]::Ordinal)) { continue }
            if ($trimmed -match '(?i)(^|\s)export-(ignore|subst)(\s|$)') {
                throw "Exact source export forbids export-ignore/export-subst attributes: $attributePath"
            }
        }
    }
}

function Export-CommitTree([string]$Commit, [string]$Destination) {
    if ($Commit -notmatch '^[0-9a-f]{40,64}$') { throw "Refusing to export invalid source commit: $Commit" }
    if (Test-Path -LiteralPath $Destination) { throw "Commit export destination already exists: $Destination" }
    Assert-NoArchiveTransformAttributes $Commit
    $archivePath = Join-Path $TemporaryRoot ('exact-source-' + [Guid]::NewGuid().ToString('N') + '.zip')
    Invoke-RootGit @('archive', '--format=zip', "--output=$archivePath", $Commit) | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Git did not create the exact source archive.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $Destination
    Remove-Item -LiteralPath $archivePath -Force
}

function Invoke-GhJson([string[]]$Arguments) {
    $output = @(& gh @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code ${exitCode}: $([string]::Join([Environment]::NewLine, $output))"
    }
    $json = [string]::Join([Environment]::NewLine, $output)
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }
    try { return $json | ConvertFrom-Json -Depth 100 }
    catch { throw "GitHub CLI returned invalid JSON for gh $($Arguments -join ' '): $($_.Exception.Message)" }
}

function Invoke-GhCommand([string[]]$Arguments) {
    & gh @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Get-AllReleases {
    $releases = [Collections.Generic.List[object]]::new()
    $page = 1
    while ($true) {
        $batch = @(Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases?per_page=100&page=$page"))
        foreach ($release in $batch) { $releases.Add($release) }
        if ($batch.Count -lt 100) { break }
        $page++
        if ($page -gt 100) { throw 'Refusing to scan more than 10,000 GitHub Releases.' }
    }
    return @($releases)
}

function ConvertFrom-UpdaterReleaseTag([string]$Tag) {
    if (-not $Tag.StartsWith('updater-v', [StringComparison]::Ordinal)) { return $null }
    $numeric = $Tag.Substring('updater-v'.Length)
    if ($numeric -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Published stable updater release has an unorderable tag: $Tag"
    }
    $parsed = $null
    if (-not [Version]::TryParse($numeric, [ref]$parsed) -or $parsed.Build -lt 0 -or $parsed.Revision -ge 0) {
        throw "Published stable updater release has an unsupported numeric tag: $Tag"
    }
    return $parsed
}

function Assert-UpdaterVersionReservation([object[]]$Releases, [string]$Version, [string]$Tag) {
    $candidate = ConvertTo-CanonicalUpdaterVersion $Version 'Updater release version'
    $exact = @($Releases | Where-Object { [string]$_.tag_name -ceq $Tag })
    if ($exact.Count -gt 1) { throw "More than one GitHub Release uses exact tag $Tag." }
    if ($exact.Count -eq 1 -and [bool]$exact[0].prerelease) {
        throw "Reserved stable updater tag $Tag is unexpectedly marked prerelease."
    }

    # Re-checking an already-published exact release is idempotent and cannot
    # create a downgrade. Inventory and tag-target validation happen later.
    if ($exact.Count -eq 1 -and -not [bool]$exact[0].draft) { return }

    foreach ($release in @($Releases)) {
        if ([bool]$release.draft -or [bool]$release.prerelease) { continue }
        $remoteTag = [string]$release.tag_name
        if (-not $remoteTag.StartsWith('updater-v', [StringComparison]::Ordinal)) { continue }
        $remoteVersion = ConvertFrom-UpdaterReleaseTag $remoteTag
        $comparison = $candidate.CompareTo($remoteVersion)
        if ($comparison -eq 0 -and $remoteTag -cne $Tag) {
            throw "Updater version $Version is a semantic duplicate of existing stable release $remoteTag."
        }
        if ($comparison -le 0) {
            throw "Fresh updater version $Version must be strictly newer than existing stable release $remoteTag."
        }
    }
}

function Get-Release([int64]$ReleaseId) {
    $release = Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/$ReleaseId")
    # The assets embedded in a release response may be paginated/truncated.
    # Load the dedicated endpoint so "exact inventory" really means every
    # remote asset, not merely the first response page.
    $allAssets = [Collections.Generic.List[object]]::new()
    $page = 1
    while ($true) {
        $batch = @(Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/$ReleaseId/assets?per_page=100&page=$page"))
        foreach ($asset in $batch) { $allAssets.Add($asset) }
        if ($batch.Count -lt 100) { break }
        $page++
        if ($page -gt 100) { throw 'Refusing to scan more than 10,000 assets on one GitHub Release.' }
    }
    $release.assets = @($allAssets)
    return $release
}

function Get-NormalizedAssetDigest($Asset) {
    $digest = [string]$Asset.digest
    if ($digest -notmatch '^sha256:(?<hash>[0-9a-fA-F]{64})$') { return $null }
    return $Matches['hash'].ToLowerInvariant()
}

function Test-RemoteAsset($Asset, $Expected) {
    if ([string]$Asset.state -cne 'uploaded') { return $false }
    if ([int64]$Asset.size -ne [int64]$Expected.Size) { return $false }
    $digest = Get-NormalizedAssetDigest $Asset
    return $null -ne $digest -and $digest -ceq $Expected.Sha256
}

function Assert-ExactRemoteInventory($Release, [hashtable]$ExpectedAssets) {
    $remoteAssets = @($Release.assets)
    if ($remoteAssets.Count -ne $ExpectedAssets.Count) {
        throw "Draft asset inventory is not exact: expected $($ExpectedAssets.Count), found $($remoteAssets.Count)."
    }
    foreach ($expectedName in $ExpectedAssets.Keys) {
        $named = @($remoteAssets | Where-Object { [string]$_.name -ceq $expectedName })
        if ($named.Count -ne 1) { throw "Expected exactly one remote asset named $expectedName, found $($named.Count)." }
        if (-not (Test-RemoteAsset $named[0] $ExpectedAssets[$expectedName])) {
            $actualDigest = Get-NormalizedAssetDigest $named[0]
            if ($null -eq $actualDigest) { $actualDigest = '<missing>' }
            throw "Remote asset validation failed for $expectedName (state=$($named[0].state), size=$($named[0].size), sha256=$actualDigest)."
        }
    }
    foreach ($asset in $remoteAssets) {
        if (-not $ExpectedAssets.ContainsKey([string]$asset.name)) {
            throw "Unexpected remote asset blocks publication: $($asset.name)"
        }
    }
}

function Get-ValidatedDraftAssetPlan([hashtable]$ExpectedAssets, [object[]]$RemoteAssets, [switch]$AllowStarter) {
    $expectedNames = @($ExpectedAssets.Keys)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $starterAssets = [Collections.Generic.List[object]]::new()

    foreach ($remote in @($RemoteAssets)) {
        $name = [string]$remote.name
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'GitHub draft contains an asset without a usable name.' }
        if (-not $seen.Add($name)) { throw "GitHub draft contains duplicate or case-colliding asset names: $name" }

        $exactNames = @($expectedNames | Where-Object { $_ -ceq $name })
        if ($exactNames.Count -ne 1) {
            $caseCollision = @($expectedNames | Where-Object { $_ -ieq $name })
            if ($caseCollision.Count -gt 0) { throw "GitHub asset name casing does not exactly match expected staging: $name" }
            throw "GitHub draft contains an unexpected asset: $name"
        }

        $expected = $ExpectedAssets[$exactNames[0]]
        $state = [string]$remote.state
        if ($state -ceq 'uploaded') {
            if (-not (Test-RemoteAsset $remote $expected)) {
                $actualDigest = Get-NormalizedAssetDigest $remote
                if ($null -eq $actualDigest) { $actualDigest = '<missing>' }
                throw "GitHub uploaded asset does not match local staging: $name (size=$($remote.size), sha256=$actualDigest)."
            }
            continue
        }
        if ($state -cne 'starter') {
            throw "GitHub asset has an unsupported state and cannot be repaired automatically: $name ($state)"
        }
        if (-not $AllowStarter) {
            throw "GitHub draft contains an unfinished starter asset for $name. No asset was deleted; after every uploader has stopped, rerun with -RepairStaleUploads."
        }

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
        $starterAssets.Add([pscustomobject]@{ Id = $assetId; Name = $name })
    }

    $missing = @($expectedNames | Where-Object { -not $seen.Contains($_) } | Sort-Object)
    return [pscustomobject]@{ MissingNames = $missing; StarterAssets = @($starterAssets) }
}

function Assert-CleanReleaseInputs {
    $paths = @(
        '.gitattributes',
        '.gitignore',
        'global.json',
        'NuGet.Config',
        'updater/CobbleMusicUpdater',
        'updater/CobbleMusicUpdater.Tests',
        'tools/Build-CobbleMusicUpdater.ps1',
        'tools/New-CobbleMusicPrismBootstrapCommand.ps1',
        'tools/Publish-CobbleMusicUpdater.ps1',
        'bootstrap/Bootstrap-CobbleMusicUpdater.ps1',
        'tests',
        'docs/UPDATER.md'
    )
    $arguments = @('status', '--porcelain=v1', '--') + $paths
    $dirty = @(Invoke-RootGit $arguments)
    if ($dirty.Count -gt 0) {
        throw "Updater release is blocked because source-binding inputs are not committed:`n$($dirty -join [Environment]::NewLine)`nCommit the reviewed inputs, then rerun so the build can use one exact commit export."
    }
}

function Assert-SourceStillBound([string]$ExpectedCommit) {
    Assert-CleanReleaseInputs
    $current = Get-BoundSourceCommit
    if ($current -cne $ExpectedCommit) {
        throw "Repository HEAD changed during release validation: expected $ExpectedCommit, now $current."
    }
}

function Get-ExactUpdaterTagRef([string]$Tag) {
    $matching = @(Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/git/matching-refs/tags/$Tag"))
    $exact = @($matching | Where-Object { [string]$_.ref -ceq "refs/tags/$Tag" })
    if ($exact.Count -gt 1) { throw "GitHub returned duplicate exact Git refs for $Tag." }
    if ($exact.Count -eq 0) { return $null }
    return $exact[0]
}

function Assert-ExactUpdaterTagRef($TagRef, [string]$Tag, [string]$ExpectedCommit) {
    if ($null -eq $TagRef) { throw "Reserved updater Git ref is missing: refs/tags/$Tag" }
    if ([string]$TagRef.ref -cne "refs/tags/$Tag") { throw "GitHub returned the wrong updater Git ref for $Tag." }
    if ([string]$TagRef.object.type -cne 'commit' -or [string]$TagRef.object.sha -cne $ExpectedCommit) {
        throw "Reserved updater tag $Tag is not a lightweight ref at exact source commit $ExpectedCommit."
    }
    return $TagRef
}

function Assert-ReservedUpdaterTagRef([string]$Tag, [string]$ExpectedCommit) {
    $exact = Get-ExactUpdaterTagRef $Tag
    $null = Assert-ExactUpdaterTagRef $exact $Tag $ExpectedCommit
}

function Reserve-UpdaterTagRef([string]$Tag, [string]$Commit) {
    $existing = Get-ExactUpdaterTagRef $Tag
    if ($null -ne $existing) {
        $null = Assert-ExactUpdaterTagRef $existing $Tag $Commit
        Write-Host "Retaining reserved lightweight updater tag $Tag at $Commit."
        return
    }

    $creationFailure = $null
    try {
        $null = Invoke-GhJson @(
            'api', '--method', 'POST',
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            "/repos/$Repository/git/refs",
            '-f', "ref=refs/tags/$Tag",
            '-f', "sha=$Commit"
        )
    }
    catch { $creationFailure = $_.Exception }

    # Always re-fetch rather than trusting the create response. A concurrent
    # creator is idempotent only when the exact lightweight ref now points at
    # the same captured commit; a foreign target remains a hard failure.
    try {
        $reserved = Get-ExactUpdaterTagRef $Tag
        $null = Assert-ExactUpdaterTagRef $reserved $Tag $Commit
    }
    catch {
        if ($null -ne $creationFailure) {
            throw "Updater tag reservation failed and the raced ref was not identical. Create error: $($creationFailure.Message) Re-fetch error: $($_.Exception.Message)"
        }
        throw
    }
    if ($null -ne $creationFailure) {
        Write-Host "A concurrent creator reserved the identical updater tag $Tag at $Commit; continuing idempotently."
    }
    else {
        Write-Host "Reserved lightweight updater tag $Tag at exact source commit $Commit."
    }
}

function New-OrResumeDraft([string]$Tag, [string]$Version, [string]$Commit, [string]$NotesPath, [object[]]$KnownReleases) {
    $tagMatches = @($KnownReleases | Where-Object { [string]$_.tag_name -ceq $Tag })
    if ($tagMatches.Count -gt 1) { throw "More than one GitHub Release uses exact tag $Tag." }
    if ($tagMatches.Count -eq 1) {
        $existing = Get-Release ([int64]$tagMatches[0].id)
        if ([string]$existing.tag_name -cne $Tag) { throw 'GitHub returned the wrong release tag.' }
        if ([bool]$existing.prerelease) { throw "Reserved stable updater tag $Tag is unexpectedly marked prerelease." }
        if (-not [bool]$existing.draft) {
            if ($RepairStaleUploads) { throw '-RepairStaleUploads is valid only when resuming an existing persistent draft.' }
            return $existing
        }
        if ([string]$existing.target_commitish -cne $Commit) {
            throw "Existing draft $Tag targets $($existing.target_commitish), not current committed updater source $Commit."
        }
        Write-Host "Resuming persistent draft $Tag (release id $($existing.id))."
        return $existing
    }

    if ($RepairStaleUploads) {
        throw '-RepairStaleUploads is valid only when resuming an existing persistent draft; no exact draft exists.'
    }

    $body = [IO.File]::ReadAllText($NotesPath)
    $payloadPath = Join-Path $TemporaryRoot 'create-release.json'
    $payload = [ordered]@{
        tag_name = $Tag
        target_commitish = $Commit
        name = "Kewz's Cobblemon Updater v$Version"
        body = $body
        draft = $true
        prerelease = $false
        generate_release_notes = $false
    }
    [IO.File]::WriteAllText($payloadPath, ($payload | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $created = Invoke-GhJson @('api', '--method', 'POST', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases", '--input', $payloadPath)
    if (-not [bool]$created.draft -or [string]$created.tag_name -cne $Tag) {
        throw 'GitHub did not create the expected draft release.'
    }
    Write-Host "Created persistent draft $Tag (release id $($created.id))."
    return $created
}

function Assert-UpdaterReleaseIdentity($Release, [int64]$ExpectedReleaseId, [string]$Tag, [string]$Commit) {
    if ($null -eq $Release) { throw 'GitHub returned no updater release identity.' }
    foreach ($requiredProperty in @('id', 'tag_name', 'target_commitish', 'draft', 'prerelease')) {
        if ($null -eq $Release.PSObject.Properties[$requiredProperty]) {
            throw "GitHub updater release identity is missing $requiredProperty."
        }
    }
    $draftValue = $Release.PSObject.Properties['draft'].Value
    $prereleaseValue = $Release.PSObject.Properties['prerelease'].Value
    if (-not ($draftValue -is [bool]) -or -not ($prereleaseValue -is [bool])) {
        throw 'GitHub updater release draft/prerelease state is not Boolean.'
    }
    [int64]$actualId = 0
    if (-not [int64]::TryParse(
        [Convert]::ToString($Release.PSObject.Properties['id'].Value, [Globalization.CultureInfo]::InvariantCulture),
        [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$actualId) -or $actualId -ne $ExpectedReleaseId) {
        throw 'GitHub returned the wrong updater release identifier.'
    }
    if ([string]$Release.PSObject.Properties['tag_name'].Value -cne $Tag) { throw 'GitHub returned the wrong updater release tag.' }
    if ([string]$Release.PSObject.Properties['target_commitish'].Value -cne $Commit) {
        throw "Updater draft $Tag no longer targets exact source commit $Commit."
    }
    if ($prereleaseValue) {
        throw "Reserved stable updater release $Tag unexpectedly became a prerelease."
    }
}

function Assert-DraftReleaseIdentity($Release, [int64]$ExpectedReleaseId, [string]$Tag, [string]$Commit) {
    Assert-UpdaterReleaseIdentity $Release $ExpectedReleaseId $Tag $Commit
    if (-not $Release.PSObject.Properties['draft'].Value) {
        throw "Updater release is not the expected persistent draft: $Tag"
    }
}

function Assert-PublishedReleaseIdentity($Release, [int64]$ExpectedReleaseId, [string]$Tag, [string]$Commit) {
    Assert-UpdaterReleaseIdentity $Release $ExpectedReleaseId $Tag $Commit
    if ($Release.PSObject.Properties['draft'].Value) {
        throw "Updater release did not become public: $Tag"
    }
}

function Sync-DraftAssets($Release, [hashtable]$ExpectedAssets, [string]$Tag, [string]$Commit) {
    $releaseId = [int64]$Release.id
    Assert-ReservedUpdaterTagRef $Tag $Commit
    $Release = Get-Release $releaseId
    if (-not [bool]$Release.draft) {
        Assert-PublishedReleaseIdentity $Release $releaseId $Tag $Commit
        Assert-ExactRemoteInventory $Release $ExpectedAssets
        Write-Host "$Tag is already published with the exact expected assets."
        return $Release
    }
    Assert-DraftReleaseIdentity $Release $releaseId $Tag $Commit

    if ($RepairStaleUploads) {
        # Validate the complete draft and exact tag target before every
        # deletion. Only an expected-name asset that is still in GitHub's
        # unfinished `starter` state is ever repairable; an uploaded mismatch,
        # unknown state, duplicate/case collision, or unexpected asset blocks
        # every deletion.
        Assert-ReservedUpdaterTagRef $Tag $Commit
        $repairPlan = Get-ValidatedDraftAssetPlan $ExpectedAssets @($Release.assets) -AllowStarter
        foreach ($approvedStarter in @($repairPlan.StarterAssets)) {
            $current = Get-Release $releaseId
            Assert-DraftReleaseIdentity $current $releaseId $Tag $Commit
            if (-not [bool]$current.draft) { throw 'Refusing to repair an asset on a published release.' }
            Assert-ReservedUpdaterTagRef $Tag $Commit
            $currentPlan = Get-ValidatedDraftAssetPlan $ExpectedAssets @($current.assets) -AllowStarter
            $stillStarter = @($currentPlan.StarterAssets | Where-Object {
                [int64]$_.Id -eq [int64]$approvedStarter.Id -and [string]$_.Name -ceq [string]$approvedStarter.Name
            })
            if ($stillStarter.Count -eq 0) {
                Write-Host "Starter asset changed state before repair; retaining current validated state: $($approvedStarter.Name)"
                continue
            }
            if ($stillStarter.Count -ne 1) { throw "Draft repair identity became ambiguous: $($approvedStarter.Name)" }
            Write-Host "Removing explicitly approved stale starter asset: $($approvedStarter.Name) (asset id $($approvedStarter.Id))"
            Invoke-GhCommand @('api', '--method', 'DELETE', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/assets/$($approvedStarter.Id)")
        }
    }

    $current = Get-Release $releaseId
    Assert-DraftReleaseIdentity $current $releaseId $Tag $Commit
    if (-not [bool]$current.draft) { throw 'Updater release became published before draft synchronization completed.' }
    $plan = Get-ValidatedDraftAssetPlan $ExpectedAssets @($current.assets)
    foreach ($expectedName in @($plan.MissingNames)) {
        $expected = $ExpectedAssets[$expectedName]
        Assert-ReservedUpdaterTagRef $Tag $Commit
        Write-Host "Uploading missing draft asset: $expectedName"
        Invoke-GhCommand @('release', 'upload', $Tag, $expected.Path, '--repo', $Repository)
    }

    $validated = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $validated = Get-Release $releaseId
        try {
            Assert-DraftReleaseIdentity $validated $releaseId $Tag $Commit
            Assert-ExactRemoteInventory $validated $ExpectedAssets
            Assert-ReservedUpdaterTagRef $Tag $Commit
            Write-Host 'GitHub reports the exact expected updater asset inventory, sizes, and SHA-256 digests.'
            return $validated
        }
        catch {
            if ($attempt -eq 19) { throw }
            Start-Sleep -Seconds 1
        }
    }
    throw 'Draft validation did not complete.'
}

if ($DryRun -and $GitHubMutation) { throw '-DryRun cannot be combined with -UploadDraft or -Publish.' }
if ($ConfirmPublish -and -not $Publish) { throw '-ConfirmPublish is valid only with -Publish.' }
if ($Publish -and -not $ConfirmPublish) { throw 'Final publication requires both -Publish and -ConfirmPublish.' }
if ($RepairStaleUploads -and -not $GitHubMutation) { throw '-RepairStaleUploads is allowed only while resuming an existing draft with -UploadDraft or -Publish.' }
if ($VerifySourceBinding -and ($DryRun -or $GitHubMutation -or $ConfirmPublish -or $RepairStaleUploads)) {
    throw '-VerifySourceBinding is a standalone read-only diagnostic.'
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required on PATH for exact updater source binding.' }
if ($VerifySourceBinding) {
    $diagnosticCommit = Get-BoundSourceCommit
    Write-Output "SOURCE_ROOT=$Root"
    Write-Output "SOURCE_COMMIT=$diagnosticCommit"
    exit 0
}

foreach ($required in @($BuildInfoPath, $ProjectPath, $BuildScript, $GlobalJsonPath, $NuGetConfigPath, $BootstrapPath, $PipelineTest, $ReproducibilityTest)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required release input is missing: $required" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is required on PATH.' }
if ($GitHubMutation) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required on PATH for draft upload or publication.' }
}

try {
    New-Item -ItemType Directory -Path $TemporaryRoot -Force | Out-Null
    Assert-CleanReleaseInputs
    $commit = Get-BoundSourceCommit
    $sourceRoot = Join-Path $TemporaryRoot 'exact-commit-source'
    Export-CommitTree $commit $sourceRoot

    $sourceBuildInfoPath = Join-Path $sourceRoot 'updater\CobbleMusicUpdater\BuildInfo.cs'
    $sourceProjectPath = Join-Path $sourceRoot 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
    $sourceBuildScript = Join-Path $sourceRoot 'tools\Build-CobbleMusicUpdater.ps1'
    $sourceGlobalJsonPath = Join-Path $sourceRoot 'global.json'
    $sourceNuGetConfigPath = Join-Path $sourceRoot 'NuGet.Config'
    $sourcePackagesPath = Join-Path $sourceRoot 'updater\packages'
    $sourceBootstrapPath = Join-Path $sourceRoot 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
    $sourcePipelineTest = Join-Path $sourceRoot 'tests\Test-UpdaterReleasePipeline.ps1'
    $sourceReproducibilityTest = Join-Path $sourceRoot 'tests\Test-UpdaterBuildReproducibility.ps1'
    foreach ($required in @($sourceBuildInfoPath, $sourceProjectPath, $sourceBuildScript, $sourceGlobalJsonPath, $sourceNuGetConfigPath, $sourceBootstrapPath, $sourcePipelineTest, $sourceReproducibilityTest)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Exact commit export is missing required release input: $required" }
    }
    if (Test-Path -LiteralPath $sourcePackagesPath) {
        throw "Exact commit unexpectedly contains the generated NuGet package cache: $sourcePackagesPath"
    }

    $version = Get-BuildVersion $sourceBuildInfoPath $sourceProjectPath
    $tag = "updater-v$version"
    Write-Host "Building exact source commit $commit as canonical $tag, reproducible self-contained win-x64."
    $publishOutput = Join-Path $TemporaryRoot 'publish'
    New-Item -ItemType Directory -Path $publishOutput -Force | Out-Null
    # The standalone builder owns the SDK pin, PathMap, debug policy, and all
    # determinism flags so local and release builds cannot silently diverge.
    Invoke-Checked 'Publishing reproducible self-contained win-x64 updater...' {
        & $sourceBuildScript -Runtime win-x64 -OutputDirectory $publishOutput
    }
    $builtExe = Join-Path $publishOutput 'CobbleMusicUpdater.exe'
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw "Updater build output is missing: $builtExe" }
    $stagedExe = Join-Path $TemporaryRoot 'CobbleMusicUpdater.exe'
    $stagedBootstrap = Join-Path $TemporaryRoot 'Bootstrap-CobbleMusicUpdater.ps1'
    [IO.File]::Copy($builtExe, $stagedExe, $true)
    $updaterSha256 = Get-Sha256 $stagedExe
    $proposedBootstrap = Get-ProposedBootstrap $version $updaterSha256 $sourceBootstrapPath
    [IO.File]::WriteAllText($stagedBootstrap, $proposedBootstrap, [Text.UTF8Encoding]::new($false))
    $bootstrapSha256 = Get-Sha256 $stagedBootstrap

    $fileVersion = (Get-Item -LiteralPath $stagedExe).VersionInfo.FileVersion
    Write-Host "Updater: version=$version fileVersion=$fileVersion size=$((Get-Item -LiteralPath $stagedExe).Length) sha256=$updaterSha256"
    Write-Host "Bootstrap: size=$((Get-Item -LiteralPath $stagedBootstrap).Length) sha256=$bootstrapSha256"

    if ($GitHubMutation) {
        $currentBootstrap = [IO.File]::ReadAllText($BootstrapPath)
        if ($currentBootstrap -cne $proposedBootstrap) {
            throw 'GitHub upload is blocked because the committed bootstrap does not already pin this exact build. Run without -UploadDraft, review and commit the bootstrap, then rerun.'
        }
    }

    # The dist EXE is deliberately not a committed source input, but the
    # modpack publisher safety suite verifies the exact checksum-pinned
    # updater/bootstrap pair that would be distributed. Install that pair only
    # into the disposable test export after the release artifact is complete.
    $sourceTestDistExe = Join-Path $sourceRoot 'updater\dist\win-x64\CobbleMusicUpdater.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sourceTestDistExe) -Force | Out-Null
    [IO.File]::Copy($stagedExe, $sourceTestDistExe, $true)
    [IO.File]::WriteAllText($sourceBootstrapPath, $proposedBootstrap, [Text.UTF8Encoding]::new($false))
    Write-Host 'Prepared the disposable exact-build updater/bootstrap pair for downstream publisher safety tests.'

    $ordinaryTests = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'tests') -Filter 'Test-*.ps1' -File |
        Where-Object { $_.FullName -cne $sourcePipelineTest -and $_.FullName -cne $sourceReproducibilityTest } |
        Sort-Object Name)
    foreach ($test in $ordinaryTests) {
        Invoke-Checked "Running $($test.Name)..." { & pwsh -NoProfile -File $test.FullName }
    }
    $dotnetTestProjects = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'updater') -Filter '*.Tests.csproj' -File -Recurse | Sort-Object FullName)
    $consoleTestMarkers = @{
        'CobbleMusicUpdater.Tests.csproj' = 'Schema-v2 delta, release-chain, exact-baseline adoption, base-integrity, and journal commit-boundary checks passed.'
    }
    foreach ($testProject in $dotnetTestProjects) {
        [xml]$testProjectXml = [IO.File]::ReadAllText($testProject.FullName)
        $outputTypes = @($testProjectXml.SelectNodes('/Project/PropertyGroup/OutputType') | ForEach-Object { $_.InnerText.Trim() } | Sort-Object -Unique)
        $isTestProjectValues = @($testProjectXml.SelectNodes('/Project/PropertyGroup/IsTestProject') | ForEach-Object { $_.InnerText.Trim() })
        $testSdkReferences = @($testProjectXml.SelectNodes('/Project/ItemGroup/PackageReference') | Where-Object { [string]$_.Include -ceq 'Microsoft.NET.Test.Sdk' })
        $isConsoleHarness = $outputTypes.Count -eq 1 -and $outputTypes[0] -ceq 'Exe' -and $testSdkReferences.Count -eq 0 -and -not ($isTestProjectValues -contains 'true')

        if ($isConsoleHarness) {
            if (-not $consoleTestMarkers.ContainsKey($testProject.Name)) {
                throw "Console test harness $($testProject.Name) has no required success marker in the updater publisher."
            }
            $expectedMarker = $consoleTestMarkers[$testProject.Name]
            Write-Host "Executing console test harness $($testProject.Name)..."
            $testOutput = @(& dotnet run --project $testProject.FullName --configuration Release 2>&1)
            $testExitCode = $LASTEXITCODE
            $testOutput | ForEach-Object { Write-Host ([string]$_) }
            if ($testExitCode -ne 0) { throw "$($testProject.Name) failed with exit code $testExitCode." }
            $testText = [string]::Join([Environment]::NewLine, $testOutput)
            if (-not $testText.Contains($expectedMarker, [StringComparison]::Ordinal)) {
                throw "$($testProject.Name) exited successfully without its required execution marker. The harness may have built without running."
            }
            Write-Host "Verified console test execution marker for $($testProject.Name)."
            continue
        }

        if (($isTestProjectValues -contains 'true') -or $testSdkReferences.Count -gt 0) {
            Invoke-Checked "Running test-SDK project $($testProject.Name)..." {
                & dotnet test $testProject.FullName --configuration Release
            }
            continue
        }
        throw "Cannot prove how to execute test project $($testProject.FullName). Declare OutputType=Exe for a marker-verified console harness, or configure a recognized test SDK."
    }
    Invoke-Checked 'Running updater release metadata/integrity checks...' {
        & pwsh -NoProfile -File $sourcePipelineTest `
            -BuildInfoPath $sourceBuildInfoPath `
            -ProjectPath $sourceProjectPath `
            -UpdaterExePath $stagedExe `
            -BootstrapPath $stagedBootstrap `
            -ExpectedVersion $version `
            -ExpectedRepository $Repository
    }
    Invoke-Checked 'Proving the exact release artifact against cold, warm, and distinct-root commit builds...' {
        & pwsh -NoProfile -File $sourceReproducibilityTest `
            -SourceRepositoryRoot $Root `
            -SourceCommit $commit `
            -ExpectedExePath $stagedExe
    }
    Assert-SourceStillBound $commit

    if ($DryRun) {
        Write-Host "Dry run passed for exact source commit $commit. Tracked bootstrap remains unchanged; GitHub was not contacted for mutation. Proposed tag: $tag"
        exit 0
    }

    Copy-Atomically $stagedExe $DistExe
    Write-Host "Installed verified local build output: $DistExe"

    if (-not $GitHubMutation) {
        if ([IO.File]::ReadAllText($BootstrapPath) -cne $proposedBootstrap) {
            Write-Utf8Atomically $BootstrapPath $proposedBootstrap
            Write-Host "Atomically pinned bootstrap to $tag and updater SHA-256 $updaterSha256."
        }
        else {
            Write-Host "Bootstrap already pins exact updater build $tag."
        }
        Write-Host 'No GitHub mutation was made. Review and commit the updater source, publisher, tests, documentation, and bootstrap before -UploadDraft.'
        exit 0
    }

    $null = Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/commits/$commit")
    $knownReleases = @(Get-AllReleases)
    Assert-UpdaterVersionReservation $knownReleases $version $tag
    Reserve-UpdaterTagRef $tag $commit

    $notesPath = Join-Path $TemporaryRoot 'RELEASE_NOTES.md'
    $notes = @"
# Kewz's Cobblemon Updater v$version

Self-contained Windows x64 updater and checksum-pinned one-time Prism bootstrap.

- ``CobbleMusicUpdater.exe`` SHA-256: ``$updaterSha256``
- ``Bootstrap-CobbleMusicUpdater.ps1`` SHA-256: ``$bootstrapSha256``
- Source commit: ``$commit``

The bootstrap verifies the exact updater executable before installation. No private signing keys, access tokens, hosting credentials, modpack payloads, or player data are included in this release.
"@
    [IO.File]::WriteAllText($notesPath, $notes, [Text.UTF8Encoding]::new($false))

    $expectedAssets = @{
        'CobbleMusicUpdater.exe' = [pscustomobject]@{
            Path = $stagedExe
            Size = (Get-Item -LiteralPath $stagedExe).Length
            Sha256 = $updaterSha256
        }
        'Bootstrap-CobbleMusicUpdater.ps1' = [pscustomobject]@{
            Path = $stagedBootstrap
            Size = (Get-Item -LiteralPath $stagedBootstrap).Length
            Sha256 = $bootstrapSha256
        }
    }

    $release = New-OrResumeDraft $tag $version $commit $notesPath $knownReleases
    $release = Sync-DraftAssets $release $expectedAssets $tag $commit

    if (-not $Publish) {
        if (-not [bool]$release.draft) {
            Write-Host "$tag was already published and matches this exact build."
        }
        else {
            Write-Host "Validated persistent draft $tag. It remains unpublished for review."
        }
        exit 0
    }

    if (-not [bool]$release.draft) {
        Write-Host "$tag is already published and matches this exact build."
        exit 0
    }
    # Re-read immediately before the only publication mutation, so a remote
    # inventory change between upload and approval cannot slip through.
    $validatedReleaseId = [int64]$release.id
    $release = Get-Release $validatedReleaseId
    Assert-DraftReleaseIdentity $release $validatedReleaseId $tag $commit
    Assert-ExactRemoteInventory $release $expectedAssets
    Assert-UpdaterVersionReservation @(Get-AllReleases) $version $tag
    Assert-SourceStillBound $commit
    $publishPayload = Join-Path $TemporaryRoot 'publish-release.json'
    [IO.File]::WriteAllText($publishPayload, '{"draft":false}', [Text.UTF8Encoding]::new($false))
    Assert-ReservedUpdaterTagRef $tag $commit
    # Fetch by immutable API ID again after every final source/version/tag
    # check. Do not let a draft/prerelease/target or asset race during those
    # checks reach the publication PATCH.
    $release = Get-Release $validatedReleaseId
    Assert-DraftReleaseIdentity $release $validatedReleaseId $tag $commit
    Assert-ExactRemoteInventory $release $expectedAssets
    Assert-SourceStillBound $commit
    Assert-ReservedUpdaterTagRef $tag $commit
    Invoke-GhJson @('api', '--method', 'PATCH', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/$validatedReleaseId", '--input', $publishPayload) | Out-Null
    Assert-ReservedUpdaterTagRef $tag $commit
    $published = Get-Release $validatedReleaseId
    Assert-PublishedReleaseIdentity $published $validatedReleaseId $tag $commit
    Assert-ExactRemoteInventory $published $expectedAssets
    Write-Host "Published validated updater release: $($published.html_url)"
}
finally {
    if (Test-Path -LiteralPath $TemporaryRoot) { Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force }
}
