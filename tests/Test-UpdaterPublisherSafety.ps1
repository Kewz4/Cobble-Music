[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Publisher = Join-Path $Root 'tools\Publish-CobbleMusicUpdater.ps1'
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw "Updater publisher is missing: $Publisher" }
$Builder = Join-Path $Root 'tools\Build-CobbleMusicUpdater.ps1'
$GlobalJson = Join-Path $Root 'global.json'
$NuGetConfig = Join-Path $Root 'NuGet.Config'
$LockFile = Join-Path $Root 'updater\CobbleMusicUpdater\packages.lock.json'
$GitIgnore = Join-Path $Root '.gitignore'
$Attributes = Join-Path $Root '.gitattributes'
$ReproducibilityTest = Join-Path $Root 'tests\Test-UpdaterBuildReproducibility.ps1'
foreach ($path in @($Builder, $GlobalJson, $NuGetConfig, $LockFile, $GitIgnore, $Attributes, $ReproducibilityTest)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Updater build-safety input is missing: $path" }
}
$ConsoleHarnessProject = Join-Path $Root 'updater\CobbleMusicUpdater.Tests\CobbleMusicUpdater.Tests.csproj'
if (-not (Test-Path -LiteralPath $ConsoleHarnessProject -PathType Leaf)) { throw "Console updater test harness is missing: $ConsoleHarnessProject" }

$tokens = $null
$parseErrors = $null
$publisherAst = [Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Updater publisher has PowerShell parse errors: $([string]::Join('; ', @($parseErrors.Message)))"
}

# CobbleMusicUpdater.Tests is deliberately a console executable rather than a
# Microsoft.NET.Test.Sdk project. `dotnet test` can return success after merely
# building it, so the publisher must use `dotnet run` and require Program.Main's
# unambiguous success marker.
[xml]$consoleHarness = [IO.File]::ReadAllText($ConsoleHarnessProject)
$consoleOutputTypes = @($consoleHarness.SelectNodes('/Project/PropertyGroup/OutputType') | ForEach-Object { $_.InnerText.Trim() } | Sort-Object -Unique)
if ($consoleOutputTypes.Count -ne 1 -or $consoleOutputTypes[0] -cne 'Exe') {
    throw 'The updater publisher regression fixture is no longer a console executable; update this safety test with its new execution contract.'
}
$publisherText = [IO.File]::ReadAllText($Publisher)
$requiredExecutionFragments = @(
    '& dotnet run --project $testProject.FullName --configuration Release',
    "'CobbleMusicUpdater.Tests.csproj' = 'Schema-v2 delta, release-chain, exact-baseline adoption, base-integrity, and journal commit-boundary checks passed.'",
    'exited successfully without its required execution marker'
)
foreach ($fragment in $requiredExecutionFragments) {
    if (-not $publisherText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Updater publisher no longer proves console test execution. Missing contract fragment: $fragment"
    }
}

if (-not $publisherText.Contains('& $sourceBuildScript -Runtime win-x64 -OutputDirectory $publishOutput', [StringComparison]::Ordinal)) {
    throw 'Updater publisher bypasses the centralized reproducible builder.'
}
if ($publisherText.Contains('& dotnet publish $ProjectPath', [StringComparison]::Ordinal)) {
    throw 'Updater publisher reintroduced a second, drifting dotnet publish path.'
}
foreach ($fragment in @(
    'Assert-CleanReleaseInputs',
    '$commit = Get-BoundSourceCommit',
    'Export-CommitTree $commit $sourceRoot',
    '& pwsh -NoProfile -File $sourceReproducibilityTest',
    '-ExpectedExePath $stagedExe',
    '-SourceCommit $commit',
    'Assert-SourceStillBound $commit'
)) {
    if (-not $publisherText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Updater publisher lost exact-commit/artifact binding: $fragment"
    }
}
$dryRunExitIndex = $publisherText.LastIndexOf('    if ($DryRun) {', [StringComparison]::Ordinal)
$reservationCallIndex = $publisherText.LastIndexOf('    Reserve-UpdaterTagRef $tag $commit', [StringComparison]::Ordinal)
$draftCallIndex = $publisherText.LastIndexOf('    $release = New-OrResumeDraft', [StringComparison]::Ordinal)
$patchCallIndex = $publisherText.LastIndexOf("Invoke-GhJson @('api', '--method', 'PATCH'", [StringComparison]::Ordinal)
$reservedRefNeedle = 'Assert-ReservedUpdaterTagRef $tag $commit'
$prePatchRefIndex = if ($patchCallIndex -ge 0) { $publisherText.LastIndexOf($reservedRefNeedle, $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$postPatchRefIndex = if ($patchCallIndex -ge 0) { $publisherText.IndexOf($reservedRefNeedle, $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$prePatchGetIndex = if ($patchCallIndex -ge 0) { $publisherText.LastIndexOf('$release = Get-Release $validatedReleaseId', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$prePatchIdentityIndex = if ($patchCallIndex -ge 0) { $publisherText.LastIndexOf('Assert-DraftReleaseIdentity $release $validatedReleaseId $tag $commit', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$postPatchGetIndex = if ($patchCallIndex -ge 0) { $publisherText.IndexOf('$published = Get-Release $validatedReleaseId', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$postPatchIdentityIndex = if ($patchCallIndex -ge 0) { $publisherText.IndexOf('Assert-PublishedReleaseIdentity $published $validatedReleaseId $tag $commit', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$postPatchInventoryIndex = if ($patchCallIndex -ge 0) { $publisherText.IndexOf('Assert-ExactRemoteInventory $published $expectedAssets', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$prePatchInventoryIndex = if ($patchCallIndex -ge 0) { $publisherText.LastIndexOf('Assert-ExactRemoteInventory $release $expectedAssets', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
$finalSourceBindingIndex = if ($patchCallIndex -ge 0) { $publisherText.LastIndexOf('Assert-SourceStillBound $commit', $patchCallIndex, [StringComparison]::Ordinal) } else { -1 }
if ($dryRunExitIndex -lt 0 -or $reservationCallIndex -le $dryRunExitIndex -or $draftCallIndex -le $reservationCallIndex) {
    throw 'Updater tag reservation is not confined to the post-DryRun mutation workflow before draft creation/upload.'
}
if ($patchCallIndex -lt 0 -or $prePatchRefIndex -lt 0 -or $prePatchRefIndex -ge $patchCallIndex `
    -or $postPatchRefIndex -le $patchCallIndex) {
    throw 'Updater publisher does not revalidate the exact reserved tag immediately around publication PATCH.'
}
if ($finalSourceBindingIndex -lt 0 -or $prePatchRefIndex -le $finalSourceBindingIndex -or $prePatchGetIndex -le $prePatchRefIndex `
    -or $prePatchIdentityIndex -le $prePatchGetIndex -or $prePatchInventoryIndex -le $prePatchIdentityIndex -or $prePatchInventoryIndex -ge $patchCallIndex `
    -or $postPatchGetIndex -le $patchCallIndex -or $postPatchIdentityIndex -le $postPatchGetIndex `
    -or $postPatchInventoryIndex -le $postPatchIdentityIndex) {
    throw 'Updater publisher does not fresh-fetch and fully validate exact release identity/state/assets on both sides of publication PATCH.'
}
$gitInvocations = @([regex]::Matches($publisherText, '(?m)&[ \t]+git\b[^\r\n]*') | ForEach-Object { $_.Value })
if ($gitInvocations.Count -lt 1) { throw 'Updater publisher has no inspectable Git invocation.' }
foreach ($invocation in $gitInvocations) {
    if (-not $invocation.StartsWith('& git -C $Root ', [StringComparison]::Ordinal)) {
        throw "Updater publisher contains a Git invocation not rooted at the script repository: $invocation"
    }
}

$reproText = [IO.File]::ReadAllText($ReproducibilityTest)
foreach ($fragment in @(
    'Assert-NoArchiveTransformAttributes',
    'Invoke-SourceGit @(''archive'', ''--format=zip'', "--output=$archivePath", $SourceCommit)',
    "Assert-ByteIdentical `$ExpectedExePath `$FirstBuildCopy 'Exact staged release artifact versus clean commit build'",
    "Assert-ByteIdentical `$FirstBuildCopy `$secondA.Exe 'Repeated updater build'",
    "Assert-ByteIdentical `$FirstBuildCopy `$firstB.Exe 'Cross-root updater build'",
    'forbidden-shared-global-packages',
    'Distinct source roots shared one mutable extracted-package cache.',
    "Get-ChildItem -LiteralPath `$packagesRoot -Filter '*.nupkg.sha512'"
)) {
    if (-not $reproText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Reproducibility suite lost an exact byte-comparison contract: $fragment"
    }
}

$builderText = [IO.File]::ReadAllText($Builder)
foreach ($fragment in @(
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:Deterministic=true',
    '-p:DeterministicSourcePaths=true',
    '-p:ContinuousIntegrationBuild=true',
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:EnableSourceLink=false',
    '-p:EnableSourceControlManagerQueries=false',
    '--configfile $NuGetConfig',
    '--packages $Packages',
    '--no-http-cache',
    '-p:RestoreLockedMode=true',
    '-p:RestorePackagesWithLockFile=true',
    'project.assets.json',
    '-p:ImportDirectoryBuildProps=false',
    '-p:ImportDirectoryBuildTargets=false',
    '-p:ManagePackageVersionsCentrally=false',
    '"-p:PathMap=$PathMap"'
)) {
    if (-not $builderText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Updater builder lost a reproducibility requirement: $fragment"
    }
}
if ($builderText.Contains('-p:DebugType=embedded', [StringComparison]::Ordinal)) {
    throw 'Updater release builder reintroduced path-sensitive embedded debug data.'
}

$sdk = Get-Content -LiteralPath $GlobalJson -Raw | ConvertFrom-Json
if ([string]$sdk.sdk.version -cne '10.0.103' `
    -or [string]$sdk.sdk.rollForward -cne 'disable' `
    -or $sdk.sdk.PSObject.Properties.Name -notcontains 'allowPrerelease' `
    -or [bool]$sdk.sdk.allowPrerelease) {
    throw 'global.json no longer pins the reviewed .NET SDK exactly.'
}
$attributesText = [IO.File]::ReadAllText($Attributes)
foreach ($fragment in @('.gitignore text eol=lf', 'NuGet.Config text eol=lf', '*.cs text eol=lf', '*.ps1 text eol=lf', '*.json text eol=lf', '*.exe binary', '*.dll binary')) {
    if (-not $attributesText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Stable text/binary Git attribute is missing: $fragment"
    }
}
$gitIgnoreText = [IO.File]::ReadAllText($GitIgnore)
if (-not $gitIgnoreText.Contains('updater/packages/', [StringComparison]::Ordinal)) {
    throw 'The per-source NuGet package cache is not excluded from commits.'
}
[xml]$nugetConfiguration = [IO.File]::ReadAllText($NuGetConfig)
$packageSources = @($nugetConfiguration.SelectNodes('/configuration/packageSources/*'))
if ($packageSources.Count -ne 2 -or $packageSources[0].Name -cne 'clear' `
    -or $packageSources[1].Name -cne 'add' -or [string]$packageSources[1].key -cne 'nuget.org' `
    -or [string]$packageSources[1].value -cne 'https://api.nuget.org/v3/index.json' `
    -or [string]$packageSources[1].protocolVersion -cne '3') {
    throw 'NuGet.Config must clear inherited feeds and declare only the reviewed nuget.org v3 source.'
}

$implicitFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-updater-implicit-input-' + [Guid]::NewGuid().ToString('N'))
$implicitSourceRoot = Join-Path $implicitFixtureRoot 'source'
try {
    New-Item -ItemType Directory -Path (Join-Path $implicitSourceRoot 'tools') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $implicitSourceRoot 'updater\CobbleMusicUpdater') -Force | Out-Null
    Copy-Item -LiteralPath $Builder -Destination (Join-Path $implicitSourceRoot 'tools\Build-CobbleMusicUpdater.ps1')
    Copy-Item -LiteralPath $GlobalJson -Destination (Join-Path $implicitSourceRoot 'global.json')
    Copy-Item -LiteralPath $NuGetConfig -Destination (Join-Path $implicitSourceRoot 'NuGet.Config')
    Copy-Item -LiteralPath $LockFile -Destination (Join-Path $implicitSourceRoot 'updater\CobbleMusicUpdater\packages.lock.json')
    [IO.File]::WriteAllText((Join-Path $implicitSourceRoot 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'), '<Project />', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $implicitFixtureRoot 'Directory.Build.props'), '<Project />', [Text.UTF8Encoding]::new($false))
    $implicitOutput = @(& pwsh -NoProfile -File (Join-Path $implicitSourceRoot 'tools\Build-CobbleMusicUpdater.ps1') -OutputDirectory (Join-Path $implicitFixtureRoot 'output') 2>&1)
    $implicitExitCode = $LASTEXITCODE
    $implicitText = [string]::Join([Environment]::NewLine, $implicitOutput)
    if ($implicitExitCode -eq 0 -or -not $implicitText.Contains('Implicit build input is forbidden', [StringComparison]::Ordinal) `
        -or -not $implicitText.Contains('Directory.Build.props', [StringComparison]::Ordinal)) {
        throw "Updater builder did not reject an implicit ancestor MSBuild input before build:`n$implicitText"
    }
}
finally {
    if (Test-Path -LiteralPath $implicitFixtureRoot) { Remove-Item -LiteralPath $implicitFixtureRoot -Recurse -Force }
}

$commentPatternAssignment = [regex]::Match(
    $publisherText,
    '(?m)^[ \t]*\$commentPattern[ \t]*=[ \t]*''(?<pattern>[^''\r\n]+)''[ \t]*\r?$')
if (-not $commentPatternAssignment.Success) { throw 'Could not locate the updater checksum-comment regex in the publisher.' }
$commentExpression = [regex]::new($commentPatternAssignment.Groups['pattern'].Value)
foreach ($fixture in @(
    [pscustomobject]@{ Name = 'LF'; Eol = "`n"; CapturedEol = '' },
    [pscustomobject]@{ Name = 'CRLF'; Eol = "`r`n"; CapturedEol = "`r" }
)) {
    $oldComment = "# SHA-256 of CobbleMusicUpdater.exe from updater-v1.1.0.$($fixture.Eol)"
    $matches = $commentExpression.Matches($oldComment)
    if ($matches.Count -ne 1 -or $matches[0].Groups['eol'].Value -cne $fixture.CapturedEol) {
        throw "Updater checksum-comment regex is not safe for $($fixture.Name) line endings."
    }
    $replacement = $commentExpression.Replace(
        $oldComment,
        [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            '# SHA-256 of CobbleMusicUpdater.exe from updater-v9.9.9.' + $match.Groups['eol'].Value
        },
        1)
    $expected = "# SHA-256 of CobbleMusicUpdater.exe from updater-v9.9.9.$($fixture.Eol)"
    if ($replacement -cne $expected) { throw "Updater checksum-comment replacement changed $($fixture.Name) line endings." }
}

function Get-PublisherFunctionText([string]$Name) {
    $matches = @($publisherAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true))
    if ($matches.Count -ne 1) { throw "Expected exactly one publisher function named $Name, found $($matches.Count)." }
    return $matches[0].Extent.Text
}

# Execute the publisher's pure version policy directly. This covers canonical
# source versions, downgrade prevention, and non-canonical remote tags that
# are semantic duplicates of a proposed canonical version.
$versionPolicyFunctions = @(
    Get-PublisherFunctionText 'ConvertTo-CanonicalUpdaterVersion'
    Get-PublisherFunctionText 'ConvertFrom-UpdaterReleaseTag'
    Get-PublisherFunctionText 'Assert-UpdaterVersionReservation'
) -join [Environment]::NewLine
$versionPolicyHarness = [scriptblock]::Create($versionPolicyFunctions + [Environment]::NewLine + @'
function Assert-Throws([scriptblock]$Action, [string]$ExpectedFragment) {
    try { & $Action; throw 'Expected failure did not occur.' }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedFragment, [StringComparison]::Ordinal)) { throw }
    }
}
foreach ($invalid in @('01.2.3', '1.02.3', '1.2.03', '1.2', '1.2.3.4')) {
    Assert-Throws { $null = ConvertTo-CanonicalUpdaterVersion $invalid 'Fixture version' } 'canonical three-part'
}
$olderStable = @([pscustomobject]@{ tag_name = 'updater-v1.3.0'; draft = $false; prerelease = $false })
Assert-Throws { Assert-UpdaterVersionReservation $olderStable '1.2.0' 'updater-v1.2.0' } 'strictly newer'
$semanticDuplicate = @([pscustomobject]@{ tag_name = 'updater-v01.2.0'; draft = $false; prerelease = $false })
Assert-Throws { Assert-UpdaterVersionReservation $semanticDuplicate '1.2.0' 'updater-v1.2.0' } 'semantic duplicate'
$exactPublished = @([pscustomobject]@{ tag_name = 'updater-v1.2.0'; draft = $false; prerelease = $false })
Assert-UpdaterVersionReservation $exactPublished '1.2.0' 'updater-v1.2.0'
$validUpgrade = @([pscustomobject]@{ tag_name = 'updater-v1.1.9'; draft = $false; prerelease = $false })
Assert-UpdaterVersionReservation $validUpgrade '1.2.0' 'updater-v1.2.0'
'version-policy-ok'
'@)
$versionPolicyResult = @(& $versionPolicyHarness)
if ($versionPolicyResult.Count -ne 1 -or [string]$versionPolicyResult[0] -cne 'version-policy-ok') {
    throw 'Updater version policy fixture did not complete exactly.'
}

$tagReservationFunctions = @(
    Get-PublisherFunctionText 'Assert-ExactUpdaterTagRef'
    Get-PublisherFunctionText 'Reserve-UpdaterTagRef'
) -join [Environment]::NewLine
$tagReservationHarness = [scriptblock]::Create('param([string]$Case)' + [Environment]::NewLine + $tagReservationFunctions + [Environment]::NewLine + @'
$Tag = 'updater-v1.2.0'
$Commit = 'a' * 40
$ForeignCommit = 'b' * 40
$Repository = 'fixture/repository'
$script:getCount = 0
$script:postCount = 0
function New-TagRef([string]$Sha, [string]$Type = 'commit') {
    [pscustomobject]@{ ref = "refs/tags/$Tag"; object = [pscustomobject]@{ type = $Type; sha = $Sha } }
}
$script:currentRef = switch ($Case) {
    'existing' { New-TagRef $Commit }
    'foreign' { New-TagRef $ForeignCommit }
    default { $null }
}
function Get-ExactUpdaterTagRef([string]$RequestedTag) {
    if ($RequestedTag -cne $Tag) { throw 'Tag fixture requested the wrong tag.' }
    $script:getCount++
    return $script:currentRef
}
function Invoke-GhJson([string[]]$Arguments) {
    $script:postCount++
    if ($Arguments -notcontains '/repos/fixture/repository/git/refs' `
        -or $Arguments -notcontains "ref=refs/tags/$Tag" -or $Arguments -notcontains "sha=$Commit") {
        throw "Reservation POST did not bind the exact tag and commit: $($Arguments -join ' ')"
    }
    if ($Case -ceq 'concurrent') {
        $script:currentRef = New-TagRef $Commit
        throw 'simulated ref-already-exists race'
    }
    if ($Case -ceq 'create') {
        $script:currentRef = New-TagRef $Commit
        return $script:currentRef
    }
    throw "Unexpected tag mutation in fixture case $Case"
}
$failure = $null
try { Reserve-UpdaterTagRef $Tag $Commit }
catch { $failure = $_.Exception.Message }
switch ($Case) {
    'existing' {
        if ($null -ne $failure -or $script:postCount -ne 0) { throw "Existing exact reservation was not idempotent: $failure" }
    }
    'foreign' {
        if ($null -eq $failure -or -not $failure.Contains('not a lightweight ref at exact source commit', [StringComparison]::Ordinal) -or $script:postCount -ne 0) {
            throw "Foreign tag was not rejected before mutation: failure=$failure posts=$script:postCount"
        }
    }
    'concurrent' {
        if ($null -ne $failure -or $script:postCount -ne 1 -or $script:getCount -lt 2) {
            throw "Identical concurrent reservation was not recovered idempotently: failure=$failure posts=$script:postCount gets=$script:getCount"
        }
    }
    'create' {
        if ($null -ne $failure -or $script:postCount -ne 1 -or $script:getCount -lt 2) {
            throw "Fresh lightweight reservation was not re-fetched and verified: failure=$failure posts=$script:postCount gets=$script:getCount"
        }
    }
    default { throw "Unknown tag reservation fixture: $Case" }
}
"tag-$Case-ok"
'@)
foreach ($tagCase in @('existing', 'foreign', 'concurrent', 'create')) {
    $tagResult = @(& $tagReservationHarness $tagCase)
    if ($tagResult.Count -ne 1 -or [string]$tagResult[0] -cne "tag-$tagCase-ok") {
        throw "Updater tag reservation fixture did not complete exactly: $tagCase"
    }
}

# Exercise the exact identity/state predicates used by the fresh pre-PATCH and
# post-PATCH GETs. Each remote mutation shape represents a release race that
# must fail before or immediately after the one publication mutation.
$releaseIdentityFunctions = @(
    Get-PublisherFunctionText 'Assert-UpdaterReleaseIdentity'
    Get-PublisherFunctionText 'Assert-DraftReleaseIdentity'
    Get-PublisherFunctionText 'Assert-PublishedReleaseIdentity'
) -join [Environment]::NewLine
$releaseIdentityHarness = [scriptblock]::Create($releaseIdentityFunctions + [Environment]::NewLine + @'
function Assert-Fails([scriptblock]$Action, [string]$Description) {
    try { & $Action; throw "Expected race was accepted: $Description" }
    catch {
        if ($_.Exception.Message.StartsWith('Expected race was accepted:', [StringComparison]::Ordinal)) { throw }
    }
}
$id = [int64]99
$tag = 'updater-v1.2.0'
$commit = 'c' * 40
$draft = [pscustomobject]@{ id = $id; tag_name = $tag; target_commitish = $commit; draft = $true; prerelease = $false; assets = @() }
Assert-DraftReleaseIdentity $draft $id $tag $commit
foreach ($mutation in @('id', 'tag', 'target', 'published', 'prerelease', 'draft-string', 'prerelease-string')) {
    $raced = $draft.PSObject.Copy()
    switch ($mutation) {
        'id' { $raced.id = 100 }
        'tag' { $raced.tag_name = 'updater-v1.2.1' }
        'target' { $raced.target_commitish = 'd' * 40 }
        'published' { $raced.draft = $false }
        'prerelease' { $raced.prerelease = $true }
        'draft-string' { $raced.draft = 'true' }
        'prerelease-string' { $raced.prerelease = 'false' }
    }
    Assert-Fails { Assert-DraftReleaseIdentity $raced $id $tag $commit } "pre-PATCH $mutation"
}
$published = $draft.PSObject.Copy()
$published.draft = $false
Assert-PublishedReleaseIdentity $published $id $tag $commit
foreach ($mutation in @('id', 'tag', 'target', 'still-draft', 'prerelease', 'draft-string', 'prerelease-string')) {
    $raced = $published.PSObject.Copy()
    switch ($mutation) {
        'id' { $raced.id = 100 }
        'tag' { $raced.tag_name = 'updater-v1.2.1' }
        'target' { $raced.target_commitish = 'd' * 40 }
        'still-draft' { $raced.draft = $true }
        'prerelease' { $raced.prerelease = $true }
        'draft-string' { $raced.draft = 'false' }
        'prerelease-string' { $raced.prerelease = 'false' }
    }
    Assert-Fails { Assert-PublishedReleaseIdentity $raced $id $tag $commit } "post-PATCH $mutation"
}
'release-identity-races-ok'
'@)
$releaseIdentityResult = @(& $releaseIdentityHarness)
if ($releaseIdentityResult.Count -ne 1 -or [string]$releaseIdentityResult[0] -cne 'release-identity-races-ok') {
    throw 'Updater pre/post publication identity-race fixture did not complete exactly.'
}

# Exercise Sync-DraftAssets with in-memory GitHub mocks. Ordinary resume must
# not delete a starter; uploaded mismatch blocks repair before deletion; and a
# starter that concurrently completes is retained rather than deleted.
$assetFunctionNames = @(
    'Get-NormalizedAssetDigest',
    'Test-RemoteAsset',
    'Assert-ExactRemoteInventory',
    'Get-ValidatedDraftAssetPlan',
    'Assert-UpdaterReleaseIdentity',
    'Assert-DraftReleaseIdentity',
    'Assert-PublishedReleaseIdentity',
    'Sync-DraftAssets'
)
$assetFunctions = @($assetFunctionNames | ForEach-Object { Get-PublisherFunctionText $_ }) -join [Environment]::NewLine
$assetHarness = [scriptblock]::Create('param([string]$Case)' + [Environment]::NewLine + $assetFunctions + [Environment]::NewLine + @'
$hashA = 'a' * 64
$hashB = 'b' * 64
$commit = 'c' * 40
$tag = 'updater-v1.2.0'
$Repository = 'fixture/repository'
$expected = @{
    'CobbleMusicUpdater.exe' = [pscustomobject]@{ Path = 'exe'; Size = [int64]7; Sha256 = $hashA }
    'Bootstrap-CobbleMusicUpdater.ps1' = [pscustomobject]@{ Path = 'bootstrap'; Size = [int64]5; Sha256 = $hashB }
}
function New-Remote([string]$Name, [string]$State, [int64]$Size, [string]$Digest, [int64]$Id) {
    [pscustomobject]@{ name = $Name; state = $State; size = $Size; digest = $Digest; id = $Id }
}
$goodExe = New-Remote 'CobbleMusicUpdater.exe' 'uploaded' 7 "sha256:$hashA" 11
$goodBootstrap = New-Remote 'Bootstrap-CobbleMusicUpdater.ps1' 'uploaded' 5 "sha256:$hashB" 12
$starterExe = New-Remote 'CobbleMusicUpdater.exe' 'starter' 0 $null 11
$badBootstrap = New-Remote 'Bootstrap-CobbleMusicUpdater.ps1' 'uploaded' 5 ('sha256:' + ('d' * 64)) 12
$script:currentAssets = switch ($Case) {
    'mismatch' { @($starterExe, $badBootstrap) }
    default { @($starterExe, $goodBootstrap) }
}
$script:fetchCount = 0
$script:deleteCount = 0
$script:uploadCount = 0
$script:goodAssets = @($goodExe, $goodBootstrap)
$RepairStaleUploads = $Case -in @('repair', 'concurrent', 'mismatch')
function Get-Release([int64]$ReleaseId) {
    $script:fetchCount++
    if ($Case -ceq 'concurrent' -and $script:fetchCount -ge 2) { $script:currentAssets = @($script:goodAssets) }
    [pscustomobject]@{
        id = $ReleaseId
        tag_name = $tag
        target_commitish = $commit
        draft = $true
        prerelease = $false
        assets = @($script:currentAssets)
    }
}
function Assert-ReservedUpdaterTagRef([string]$Tag, [string]$ExpectedCommit) {
    if ($Tag -cne $tag -or $ExpectedCommit -cne $commit) { throw 'Fixture tag validation mismatch.' }
}
function Invoke-GhCommand([string[]]$Arguments) {
    if ($Arguments -contains 'DELETE') {
        $script:deleteCount++
        $script:currentAssets = @($script:currentAssets | Where-Object { [string]$_.state -cne 'starter' })
        return
    }
    if ($Arguments.Count -gt 1 -and $Arguments[0] -ceq 'release' -and $Arguments[1] -ceq 'upload') {
        $script:uploadCount++
        $script:currentAssets = @($script:goodAssets)
        return
    }
    throw "Unexpected mocked gh command: $($Arguments -join ' ')"
}
$initial = [pscustomobject]@{ id = 99; tag_name = $tag; target_commitish = $commit; draft = $true; prerelease = $false; assets = @($script:currentAssets) }
$failure = $null
try { $null = Sync-DraftAssets $initial $expected $tag $commit }
catch { $failure = $_.Exception.Message }
switch ($Case) {
    'default' {
        if ($null -eq $failure -or -not $failure.Contains('No asset was deleted', [StringComparison]::Ordinal) -or $script:deleteCount -ne 0 -or $script:uploadCount -ne 0) {
            throw "Ordinary starter resume was not fail-closed: failure=$failure deletes=$script:deleteCount uploads=$script:uploadCount"
        }
    }
    'mismatch' {
        if ($null -eq $failure -or -not $failure.Contains('does not match local staging', [StringComparison]::Ordinal) -or $script:deleteCount -ne 0 -or $script:uploadCount -ne 0) {
            throw "Mismatched uploaded asset did not block every repair deletion: failure=$failure deletes=$script:deleteCount uploads=$script:uploadCount"
        }
    }
    'concurrent' {
        if ($null -ne $failure -or $script:deleteCount -ne 0 -or $script:uploadCount -ne 0) {
            throw "Concurrently completed starter was not retained: failure=$failure deletes=$script:deleteCount uploads=$script:uploadCount"
        }
    }
    'repair' {
        if ($null -ne $failure -or $script:deleteCount -ne 1 -or $script:uploadCount -ne 1) {
            throw "Explicit starter repair did not delete/upload exactly once: failure=$failure deletes=$script:deleteCount uploads=$script:uploadCount"
        }
    }
    default { throw "Unknown asset fixture: $Case" }
}
"asset-$Case-ok"
'@)
foreach ($assetCase in @('default', 'mismatch', 'concurrent', 'repair')) {
    $assetResult = @(& $assetHarness $assetCase)
    if ($assetResult.Count -ne 1 -or [string]$assetResult[0] -cne "asset-$assetCase-ok") {
        throw "Updater asset safety fixture did not complete exactly: $assetCase"
    }
}

# Invoke the real publisher by full path while the process CWD is an unrelated
# Git repository. The diagnostic must report the publisher's repository and
# commit, never the caller repository.
$foreignFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-updater-foreign-cwd-' + [Guid]::NewGuid().ToString('N'))
$sourceFixture = Join-Path $foreignFixtureRoot 'publisher source repository'
$foreignRepository = Join-Path $foreignFixtureRoot 'foreign caller repository'
function Invoke-FixtureGit([string]$RepositoryPath, [string[]]$Arguments) {
    $output = @(& git -C $RepositoryPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture git failed in ${RepositoryPath}: $([string]::Join([Environment]::NewLine, $output))"
    }
    return @($output)
}
try {
    New-Item -ItemType Directory -Path (Join-Path $sourceFixture 'tools') -Force | Out-Null
    New-Item -ItemType Directory -Path $foreignRepository -Force | Out-Null
    Copy-Item -LiteralPath $Publisher -Destination (Join-Path $sourceFixture 'tools\Publish-CobbleMusicUpdater.ps1')
    [IO.File]::WriteAllText((Join-Path $foreignRepository 'caller.txt'), 'foreign caller', [Text.UTF8Encoding]::new($false))
    foreach ($fixtureRepository in @($sourceFixture, $foreignRepository)) {
        Invoke-FixtureGit $fixtureRepository @('init', '--quiet') | Out-Null
        Invoke-FixtureGit $fixtureRepository @('config', 'user.name', 'Updater Safety Fixture') | Out-Null
        Invoke-FixtureGit $fixtureRepository @('config', 'user.email', 'updater-safety@example.invalid') | Out-Null
        Invoke-FixtureGit $fixtureRepository @('add', '--all') | Out-Null
        Invoke-FixtureGit $fixtureRepository @('commit', '--quiet', '-m', 'fixture') | Out-Null
    }
    $expectedSourceCommit = ([string]@(Invoke-FixtureGit $sourceFixture @('rev-parse', 'HEAD'))[0]).Trim().ToLowerInvariant()
    $foreignCommit = ([string]@(Invoke-FixtureGit $foreignRepository @('rev-parse', 'HEAD'))[0]).Trim().ToLowerInvariant()
    Push-Location $foreignRepository
    try {
        $bindingOutput = @(& pwsh -NoProfile -File (Join-Path $sourceFixture 'tools\Publish-CobbleMusicUpdater.ps1') -VerifySourceBinding 2>&1)
        $bindingExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($bindingExitCode -ne 0) { throw "Foreign-CWD source-binding diagnostic failed: $([string]::Join([Environment]::NewLine, $bindingOutput))" }
    $bindingText = [string]::Join([Environment]::NewLine, $bindingOutput)
    $expectedRoot = [IO.Path]::GetFullPath($sourceFixture).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $bindingText.Contains("SOURCE_ROOT=$expectedRoot", [StringComparison]::OrdinalIgnoreCase) `
        -or -not $bindingText.Contains("SOURCE_COMMIT=$expectedSourceCommit", [StringComparison]::Ordinal) `
        -or $bindingText.Contains("SOURCE_COMMIT=$foreignCommit", [StringComparison]::Ordinal)) {
        throw "Publisher invoked from a foreign repository did not bind exclusively to its script root:`n$bindingText"
    }
}
finally {
    if (Test-Path -LiteralPath $foreignFixtureRoot) { Remove-Item -LiteralPath $foreignFixtureRoot -Recurse -Force }
}

$cases = @(
    [pscustomobject]@{
        Name = 'dry-run cannot mutate a draft'
        Arguments = @('-DryRun', '-UploadDraft')
        Expected = '-DryRun cannot be combined with -UploadDraft or -Publish.'
    },
    [pscustomobject]@{
        Name = 'publication requires confirmation'
        Arguments = @('-Publish')
        Expected = 'Final publication requires both -Publish and -ConfirmPublish.'
    },
    [pscustomobject]@{
        Name = 'confirmation cannot stand alone'
        Arguments = @('-ConfirmPublish')
        Expected = '-ConfirmPublish is valid only with -Publish.'
    },
    [pscustomobject]@{
        Name = 'stale repair requires draft resume mode'
        Arguments = @('-RepairStaleUploads')
        Expected = '-RepairStaleUploads is allowed only while resuming an existing draft with -UploadDraft or -Publish.'
    }
)

foreach ($case in $cases) {
    $output = @(& pwsh -NoProfile -File $Publisher @($case.Arguments) 2>&1)
    $exitCode = $LASTEXITCODE
    $text = [string]::Join([Environment]::NewLine, $output)
    if ($exitCode -eq 0) { throw "Publisher safety case unexpectedly succeeded: $($case.Name)" }
    if (-not $text.Contains($case.Expected, [StringComparison]::Ordinal)) {
        throw "Publisher safety case returned the wrong failure: $($case.Name)`n$text"
    }
}

Write-Host 'Updater publisher wrong-CWD, exact-commit, isolated-package, foreign/concurrent-tag, dry-run-no-mutation, version, stale-repair, and LF/CRLF safety checks passed without building or contacting GitHub.'
