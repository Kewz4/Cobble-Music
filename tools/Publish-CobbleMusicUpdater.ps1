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
    [switch]$ConfirmPublish
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$BuildInfoPath = Join-Path $Root 'updater\CobbleMusicUpdater\BuildInfo.cs'
$ProjectPath = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$BootstrapPath = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
$DistExe = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe'
$PipelineTest = Join-Path $Root 'tests\Test-UpdaterReleasePipeline.ps1'
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

function Get-BuildVersion {
    if (-not (Test-Path -LiteralPath $BuildInfoPath -PathType Leaf)) { throw "BuildInfo.cs is missing: $BuildInfoPath" }
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) { throw "Updater project is missing: $ProjectPath" }

    $buildInfo = [IO.File]::ReadAllText($BuildInfoPath)
    $versionMatches = [regex]::Matches(
        $buildInfo,
        '(?m)^\s*public\s+const\s+string\s+Version\s*=\s*"(?<version>[^"]+)"\s*;\s*$')
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one BuildInfo.Version constant, found $($versionMatches.Count)."
    }
    $version = $versionMatches[0].Groups['version'].Value
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "BuildInfo.Version must be a three-part numeric version for a stable updater release: $version"
    }

    [xml]$project = [IO.File]::ReadAllText($ProjectPath)
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
    $pattern = '(?m)^\s*' + [regex]::Escape('$' + $Name) + '\s*=\s*''(?<value>[^''\r\n]*)''\s*$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one `$${Name} assignment, found $($matches.Count)." }
    return $matches[0].Groups['value'].Value
}

function Set-SingleQuotedAssignment([string]$Text, [string]$Name, [string]$Value) {
    if ($Value.Contains("'")) { throw "Unsafe single quote in replacement value for `$${Name}." }
    $pattern = '(?m)^(?<prefix>\s*' + [regex]::Escape('$' + $Name) + '\s*=\s*)''(?<value>[^''\r\n]*)''(?<suffix>\s*)$'
    $expression = [regex]::new($pattern)
    $matches = $expression.Matches($Text)
    if ($matches.Count -ne 1) { throw "Expected exactly one `$${Name} assignment, found $($matches.Count)." }
    return $expression.Replace(
        $Text,
        [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            return $match.Groups['prefix'].Value + "'" + $Value + "'" + $match.Groups['suffix'].Value
        },
        1)
}

function Get-ProposedBootstrap([string]$Version, [string]$UpdaterSha256) {
    if (-not (Test-Path -LiteralPath $BootstrapPath -PathType Leaf)) { throw "Bootstrap script is missing: $BootstrapPath" }
    $text = [IO.File]::ReadAllText($BootstrapPath)
    $bootstrapRepository = Get-SingleQuotedAssignment $text 'Repository'
    if ($bootstrapRepository -cne $Repository) {
        throw "Bootstrap repository ($bootstrapRepository) does not match requested repository ($Repository)."
    }
    $text = Set-SingleQuotedAssignment $text 'UpdaterVersion' $Version
    $text = Set-SingleQuotedAssignment $text 'ExpectedUpdaterSha256' $UpdaterSha256.ToUpperInvariant()

    $commentPattern = '(?m)^# SHA-256 of CobbleMusicUpdater\.exe from updater-v[^\r\n]+$'
    $commentMatches = [regex]::Matches($text, $commentPattern)
    if ($commentMatches.Count -ne 1) {
        throw "Expected exactly one updater checksum comment, found $($commentMatches.Count)."
    }
    $commentExpression = [regex]::new($commentPattern)
    $text = $commentExpression.Replace(
        $text,
        "# SHA-256 of CobbleMusicUpdater.exe from updater-v$Version.",
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

function Get-ReleasesByTag([string]$Tag) {
    $matches = [Collections.Generic.List[object]]::new()
    $page = 1
    while ($true) {
        $batch = @(Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases?per_page=100&page=$page"))
        foreach ($release in $batch) {
            if ([string]$release.tag_name -ceq $Tag) { $matches.Add($release) }
        }
        if ($batch.Count -lt 100) { break }
        $page++
        if ($page -gt 100) { throw 'Refusing to scan more than 10,000 GitHub Releases.' }
    }
    return @($matches)
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

function Assert-CleanReleaseInputs {
    $paths = @(
        'updater/CobbleMusicUpdater',
        'updater/CobbleMusicUpdater.Tests',
        'tools/Build-CobbleMusicUpdater.ps1',
        'tools/Publish-CobbleMusicUpdater.ps1',
        'bootstrap/Bootstrap-CobbleMusicUpdater.ps1',
        'tests',
        'docs/UPDATER.md'
    )
    $arguments = @('status', '--porcelain=v1', '--') + $paths
    $dirty = @(& git @arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Git release inputs.' }
    if ($dirty.Count -gt 0) {
        throw "GitHub upload is blocked because updater release inputs are not committed:`n$($dirty -join [Environment]::NewLine)`nRun without -UploadDraft first, review and commit the result, then rerun."
    }
}

function Assert-TagTargetIfPresent([string]$Tag, [string]$ExpectedCommit) {
    $matching = @(Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/git/matching-refs/tags/$Tag"))
    $exact = @($matching | Where-Object { [string]$_.ref -ceq "refs/tags/$Tag" })
    if ($exact.Count -gt 1) { throw "GitHub returned duplicate exact Git refs for $Tag." }
    if ($exact.Count -eq 0) { return }

    $objectType = [string]$exact[0].object.type
    $objectSha = [string]$exact[0].object.sha
    for ($depth = 0; $objectType -ceq 'tag' -and $depth -lt 5; $depth++) {
        $tagObject = Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/git/tags/$objectSha")
        $objectType = [string]$tagObject.object.type
        $objectSha = [string]$tagObject.object.sha
    }
    if ($objectType -cne 'commit' -or $objectSha -cne $ExpectedCommit) {
        throw "Existing Git tag $Tag does not resolve to the committed updater source $ExpectedCommit."
    }
}

function New-OrResumeDraft([string]$Tag, [string]$Version, [string]$Commit, [string]$NotesPath) {
    $tagMatches = @(Get-ReleasesByTag $Tag)
    if ($tagMatches.Count -gt 1) { throw "More than one GitHub Release uses exact tag $Tag." }
    if ($tagMatches.Count -eq 1) {
        $existing = Get-Release ([int64]$tagMatches[0].id)
        if ([string]$existing.tag_name -cne $Tag) { throw 'GitHub returned the wrong release tag.' }
        if (-not [bool]$existing.draft) { return $existing }
        if ([string]$existing.target_commitish -cne $Commit) {
            throw "Existing draft $Tag targets $($existing.target_commitish), not current committed updater source $Commit."
        }
        Write-Host "Resuming persistent draft $Tag (release id $($existing.id))."
        return $existing
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

function Sync-DraftAssets($Release, [hashtable]$ExpectedAssets, [string]$Tag) {
    if (-not [bool]$Release.draft) {
        Assert-ExactRemoteInventory $Release $ExpectedAssets
        Write-Host "$Tag is already published with the exact expected assets."
        return $Release
    }

    $unexpected = @($Release.assets | Where-Object { -not $ExpectedAssets.ContainsKey([string]$_.name) })
    if ($unexpected.Count -gt 0) {
        throw "Draft contains unexpected assets; none were deleted: $([string]::Join(', ', @($unexpected.name)))"
    }

    foreach ($expectedName in @($ExpectedAssets.Keys | Sort-Object)) {
        $expected = $ExpectedAssets[$expectedName]
        $current = Get-Release ([int64]$Release.id)
        $named = @($current.assets | Where-Object { [string]$_.name -ceq $expectedName })
        if ($named.Count -eq 1 -and (Test-RemoteAsset $named[0] $expected)) {
            Write-Host "Retaining verified draft asset: $expectedName"
            continue
        }

        # Only assets with an exact expected name are replaceable. Unknown
        # draft assets are never silently deleted.
        foreach ($stale in $named) {
            if (-not [bool]$current.draft) { throw 'Refusing to replace an asset on a published release.' }
            Write-Host "Removing incomplete or mismatched draft asset: $expectedName (asset id $($stale.id))"
            Invoke-GhCommand @('api', '--method', 'DELETE', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/assets/$($stale.id)")
        }

        Write-Host "Uploading draft asset: $expectedName"
        Invoke-GhCommand @('release', 'upload', $Tag, $expected.Path, '--repo', $Repository)
    }

    $validated = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $validated = Get-Release ([int64]$Release.id)
        try {
            Assert-ExactRemoteInventory $validated $ExpectedAssets
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

foreach ($required in @($BuildInfoPath, $ProjectPath, $BootstrapPath, $PipelineTest)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required release input is missing: $required" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is required on PATH.' }
if ($GitHubMutation) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required on PATH for draft upload or publication.' }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required on PATH for draft upload or publication.' }
}

try {
    New-Item -ItemType Directory -Path $TemporaryRoot -Force | Out-Null
    $version = Get-BuildVersion
    $tag = "updater-v$version"
    Write-Host "Building exact BuildInfo version $version as self-contained win-x64."
    $publishOutput = Join-Path $TemporaryRoot 'publish'
    New-Item -ItemType Directory -Path $publishOutput -Force | Out-Null
    Invoke-Checked 'Restoring locked updater dependencies...' {
        & dotnet restore $ProjectPath --locked-mode --runtime win-x64
    }
    # Excluding SourceRevisionId from the informational version breaks a
    # checksum cycle: committing the freshly pinned bootstrap must not change
    # the next build's EXE merely because HEAD changed. The code/version still
    # has to be committed before any GitHub upload.
    Invoke-Checked 'Publishing deterministic self-contained win-x64 updater...' {
        & dotnet publish $ProjectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --no-restore `
            --output $publishOutput `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=embedded `
            -p:Deterministic=true `
            -p:ContinuousIntegrationBuild=true `
            -p:IncludeSourceRevisionInInformationalVersion=false `
            -p:EnableSourceLink=false `
            -p:EnableSourceControlManagerQueries=false `
            -p:EmbedUntrackedSources=false
    }
    $builtExe = Join-Path $publishOutput 'CobbleMusicUpdater.exe'
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw "Updater build output is missing: $builtExe" }
    $stagedExe = Join-Path $TemporaryRoot 'CobbleMusicUpdater.exe'
    $stagedBootstrap = Join-Path $TemporaryRoot 'Bootstrap-CobbleMusicUpdater.ps1'
    [IO.File]::Copy($builtExe, $stagedExe, $true)
    $updaterSha256 = Get-Sha256 $stagedExe
    $proposedBootstrap = Get-ProposedBootstrap $version $updaterSha256
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

    $ordinaryTests = @(Get-ChildItem -LiteralPath (Join-Path $Root 'tests') -Filter 'Test-*.ps1' -File |
        Where-Object { $_.FullName -cne $PipelineTest } |
        Sort-Object Name)
    foreach ($test in $ordinaryTests) {
        Invoke-Checked "Running $($test.Name)..." { & pwsh -NoProfile -File $test.FullName }
    }
    $dotnetTestProjects = @(Get-ChildItem -LiteralPath (Join-Path $Root 'updater') -Filter '*.Tests.csproj' -File -Recurse | Sort-Object FullName)
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
        & pwsh -NoProfile -File $PipelineTest `
            -UpdaterExePath $stagedExe `
            -BootstrapPath $stagedBootstrap `
            -ExpectedVersion $version `
            -ExpectedRepository $Repository
    }

    if ($DryRun) {
        Write-Host "Dry run passed. Tracked bootstrap remains unchanged; GitHub was not contacted for mutation. Proposed tag: $tag"
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

    Assert-CleanReleaseInputs
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') { throw 'Unable to resolve the committed updater source revision.' }
    $null = Invoke-GhJson @('api', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/commits/$commit")
    Assert-TagTargetIfPresent $tag $commit

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

    $release = New-OrResumeDraft $tag $version $commit $notesPath
    $release = Sync-DraftAssets $release $expectedAssets $tag

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
    $release = Get-Release ([int64]$release.id)
    Assert-ExactRemoteInventory $release $expectedAssets
    Assert-TagTargetIfPresent $tag $commit
    $publishPayload = Join-Path $TemporaryRoot 'publish-release.json'
    [IO.File]::WriteAllText($publishPayload, '{"draft":false}', [Text.UTF8Encoding]::new($false))
    $published = Invoke-GhJson @('api', '--method', 'PATCH', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2022-11-28', "/repos/$Repository/releases/$($release.id)", '--input', $publishPayload)
    if ([bool]$published.draft -or [string]$published.tag_name -cne $tag) { throw 'GitHub did not publish the exact validated updater draft.' }
    Assert-ExactRemoteInventory $published $expectedAssets
    Write-Host "Published validated updater release: $($published.html_url)"
}
finally {
    if (Test-Path -LiteralPath $TemporaryRoot) { Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force }
}
