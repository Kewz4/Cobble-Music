<#
Proves that updater pre-staging (updater 1.2.18+, UpdaterPrestage*.cs) leaves the PINNED friend bootstrap
(bootstrap/Bootstrap-CobbleMusicUpdater.ps1, SHA-256 56E39784...) running the new updater with no download, and never
reaching its pinned-verifier fallback, both while stable.json still names the old version and after it advances.

Equivalence: the harness executes the bootstrap's own top-level variable assignments and EVERY function definition,
copied verbatim from the AST of the file whose SHA-256 must equal the friend-pinned hash, under Windows PowerShell 5.1
(what friends run). The only additions are (a) an Invoke-WebRequest test double that serves local files or fails like an
offline network and records every URI, and (b) in the key-fixture scenario, $verifierExe/$ExpectedVerifierSha256 point at
the test harness's --verify-updater-channel entry, which runs the production verifier CLI (byte-identical to the pinned
1.2.7 verifier's code) with one extra test key. The swapped files are produced by the real C# swap (UpdaterPrestageSwap).

Scenario A (real key, real artifacts): installed = the real signed 1.2.7 descriptor + the real pinned 1.2.7 executable;
next = the committed signed updater/channel/stable.json + the matching dist executable. The bootstrap's real pinned
verifier checks the real signatures.
Scenario B (fixture key, fake executables): the swap is killed (TerminateProcess) right after each step, and the bootstrap
is run on the torn state online (old stable / advanced stable) and offline, then again after UpdaterPrestageRecovery.
#>
[CmdletBinding()]
param(
    [string]$NewExePath,
    [string]$NewChannelPath,
    [string]$VerifierExePath
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$BootstrapPath = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
# The friend command pins this exact bootstrap (docs/UPDATER.md, "Permanent friend command"). It must never change.
$PinnedBootstrapSha256 = '56E39784A1470E5BF2923820AB9D96D6E88474FD679540B85C2B65387CD4301A'
$TestsProject = Join-Path $Root 'updater\CobbleMusicUpdater.Tests\CobbleMusicUpdater.Tests.csproj'
$FixtureExe = Join-Path $Root 'updater\CobbleMusicUpdater.Tests\bin\Release\net10.0-windows\win-x64\CobbleMusicUpdater.Tests.exe'
$OldChannelFixture = Join-Path $Root 'updater\testdata\updater-channel-1.2.7-stable.json'
if ([string]::IsNullOrWhiteSpace($NewExePath)) { $NewExePath = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe' }
if ([string]::IsNullOrWhiteSpace($NewChannelPath)) { $NewChannelPath = Join-Path $Root 'updater\channel\stable.json' }
if ([string]::IsNullOrWhiteSpace($VerifierExePath)) { $VerifierExePath = Join-Path $Root 'updater\verifier\win-x64\CobbleMusicUpdater.exe' }
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-prestage-bootstrap-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

foreach ($required in @($BootstrapPath, $TestsProject, $OldChannelFixture, "$OldChannelFixture".Replace('.json', '.sig'), $NewExePath, $NewChannelPath, [IO.Path]::ChangeExtension($NewChannelPath, '.sig'), $VerifierExePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Pre-staging bootstrap proof input is missing: $required (the updater publisher stages dist/verifier/channel for this test; locally, copy the matching build outputs there)."
    }
}
Assert-True ((Get-Sha256 $BootstrapPath) -ceq $PinnedBootstrapSha256) 'bootstrap/Bootstrap-CobbleMusicUpdater.ps1 is not the friend-pinned bootstrap; this proof only applies to the pinned bytes.'

# ------------------------------------------------------------------ harness from the real bootstrap

$tokens = $null
$parseErrors = $null
$bootstrapAst = [Management.Automation.Language.Parser]::ParseFile($BootstrapPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "Pinned bootstrap has parse errors: $($parseErrors.Message -join '; ')" }
$bootstrapText = [IO.File]::ReadAllText($BootstrapPath)
$assignments = [Collections.Generic.List[string]]::new()
$functions = [Collections.Generic.List[string]]::new()
$functionNames = [Collections.Generic.List[string]]::new()
$inPreamble = $true
foreach ($statement in $bootstrapAst.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) {
        $functions.Add($statement.Extent.Text)
        $functionNames.Add($statement.Name)
        continue
    }
    # The variable preamble ends at the first executable statement (line 344); later assignments belong to install mode.
    if ($inPreamble -and $statement -is [Management.Automation.Language.AssignmentStatementAst]) {
        $assignments.Add($statement.Extent.Text)
        continue
    }
    $inPreamble = $false
}
foreach ($name in @('Move-FileAtomically', 'Copy-FileAtomically', 'Test-WindowsExecutable', 'Get-CanonicalVersion', 'Test-ExactExecutable',
    'Install-PinnedVerifier', 'Invoke-ChannelVerifier', 'Test-UpdaterMatchesChannel', 'Get-CachedChannel', 'Install-UpdaterFromChannel',
    'Save-ChannelCache', 'Get-CurrentUpdater')) {
    Assert-True ($functionNames.Contains($name)) "The pinned bootstrap no longer defines $name."
}
foreach ($fragment in @($assignments) + @($functions)) {
    Assert-True ($bootstrapText.Contains($fragment, [StringComparison]::Ordinal)) 'Extracted bootstrap text is not verbatim.'
}
foreach ($variable in @('$targetExe', '$verifierExe', '$cachedChannelPath', '$cachedSignaturePath', '$channelUri', '$channelSignatureUri', '$ExpectedVerifierSha256')) {
    Assert-True (@($assignments | Where-Object { $_.StartsWith($variable + ' ', [StringComparison]::Ordinal) }).Count -eq 1) "Bootstrap preamble does not assign $variable exactly once."
}

$harnessSuffix = @'

# ---------------- test doubles (not bootstrap code) ----------------
$scenario = [IO.File]::ReadAllText($ScenarioFile) | ConvertFrom-Json
if ($scenario.verifierExe) {
    $verifierExe = [string]$scenario.verifierExe
    $ExpectedVerifierSha256 = [string]$scenario.verifierSha256
}
$script:downloads = New-Object 'System.Collections.Generic.List[string]'
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, [string]$Uri, [string]$OutFile, [int]$TimeoutSec)
    if ($Uri -ceq $channelUri -or $Uri -ceq $channelSignatureUri) {
        if ($scenario.offline) { throw (New-Object System.Net.WebException 'The remote name could not be resolved (test offline).') }
        $source = if ($Uri -ceq $channelUri) { [string]$scenario.remoteJson } else { [string]$scenario.remoteSig }
        [IO.File]::Copy($source, $OutFile, $true)
        return
    }
    $script:downloads.Add($Uri)
    $asset = $scenario.releases.PSObject.Properties[$Uri]
    if ($scenario.offline -or $null -eq $asset) { throw (New-Object System.Net.WebException "404 (test): $Uri") }
    [IO.File]::Copy([string]$asset.Value, $OutFile, $true)
}
$messages = New-Object 'System.Collections.Generic.List[string]'
$failure = $null
try {
    Get-CurrentUpdater 6>&1 | ForEach-Object { $messages.Add([string]$_) }
}
catch { $failure = $_.Exception.Message }
$result = [ordered]@{
    downloads = @($script:downloads)
    messages = @($messages)
    failure = $failure
    targetSha256 = if (Test-Path -LiteralPath $targetExe -PathType Leaf) { (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash } else { $null }
    cacheSha256 = if (Test-Path -LiteralPath $cachedChannelPath -PathType Leaf) { (Get-FileHash -LiteralPath $cachedChannelPath -Algorithm SHA256).Hash } else { $null }
}
[IO.File]::WriteAllText($ResultFile, ($result | ConvertTo-Json -Depth 5))
'@

$harnessText = "param([string]`$InstanceDirectory, [string]`$ScenarioFile, [string]`$ResultFile)`n" +
    "# ---------------- verbatim from the pinned bootstrap ----------------`n" +
    ($assignments -join "`n") + "`n" + ($functions -join "`n`n") + "`n" + $harnessSuffix

$windowsPowerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source

function Invoke-PinnedBootstrap([string]$Instance, [hashtable]$Scenario) {
    $scenarioFile = Join-Path $TempRoot ('scenario-' + [Guid]::NewGuid().ToString('N') + '.json')
    $resultFile = Join-Path $TempRoot ('result-' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($scenarioFile, ($Scenario | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $output = @(& $windowsPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script:HarnessPath -InstanceDirectory $Instance -ScenarioFile $scenarioFile -ResultFile $resultFile 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resultFile)) {
        throw "Bootstrap harness failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
    return [IO.File]::ReadAllText($resultFile) | ConvertFrom-Json
}

function Invoke-Fixture([string[]]$Arguments) {
    $output = @(& $FixtureExe @Arguments 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function New-Instance([string]$Name, [string]$OldExe, [string]$OldJson, [string]$NextJson, [string]$NextExe, [string]$NextVersion, [string]$NextSha256) {
    $instance = Join-Path $TempRoot $Name
    $target = Join-Path $instance 'minecraft\cobble-music-updater'
    $prestage = Join-Path $target 'prestage'
    New-Item -ItemType Directory -Path $prestage -Force | Out-Null
    [IO.File]::Copy($OldExe, (Join-Path $target 'CobbleMusicUpdater.exe'))
    [IO.File]::Copy($OldJson, (Join-Path $target 'installed-updater-channel.json'))
    [IO.File]::Copy([IO.Path]::ChangeExtension($OldJson, '.sig'), (Join-Path $target 'installed-updater-channel.sig'))
    [IO.File]::Copy($NextJson, (Join-Path $prestage "next-$NextVersion.json"))
    [IO.File]::Copy([IO.Path]::ChangeExtension($NextJson, '.sig'), (Join-Path $prestage "next-$NextVersion.sig"))
    [IO.File]::Copy($NextExe, (Join-Path $prestage ("CobbleMusicUpdater-$NextVersion-" + $NextSha256.Substring(0, 16).ToLowerInvariant() + '.exe')))
    return $instance
}

function Get-Outcome($Result, [hashtable]$Identities) {
    foreach ($name in $Identities.Keys) {
        if ([string]$Result.targetSha256 -ceq $Identities[$name]) { return $name }
    }
    return 'unknown'
}

try {
    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    $script:HarnessPath = Join-Path $TempRoot 'pinned-bootstrap-harness.ps1'
    [IO.File]::WriteAllText($script:HarnessPath, $harnessText, [Text.UTF8Encoding]::new($true))

    & dotnet build $TestsProject --configuration Release -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Pre-staging fixture build failed with exit code $LASTEXITCODE." }
    Assert-True (Test-Path -LiteralPath $FixtureExe -PathType Leaf) "Fixture executable is missing: $FixtureExe"

    # ============================== Scenario A: real key, real artifacts ==============================
    $newChannel = [IO.File]::ReadAllText($NewChannelPath) | ConvertFrom-Json
    $oldChannel = [IO.File]::ReadAllText($OldChannelFixture) | ConvertFrom-Json
    $newVersion = [string]$newChannel.updaterVersion
    $newSha = ([string]$newChannel.updater.sha256).ToUpperInvariant()
    $oldSha = ([string]$oldChannel.updater.sha256).ToUpperInvariant()
    Assert-True ((Get-Item -LiteralPath $NewExePath).Length -eq [int64]$newChannel.updater.size -and (Get-Sha256 $NewExePath) -ceq $newSha) `
        "The new executable ($NewExePath) does not match $NewChannelPath; run the publisher's default mode first or pass matching -NewExePath/-NewChannelPath."
    Assert-True ((Get-Sha256 $VerifierExePath) -ceq $oldSha) 'The pinned verifier executable is not the signed 1.2.7 updater named by the 1.2.7 channel fixture.'
    Assert-True ([Version]$newVersion -gt [Version]'1.2.7') 'The new channel must be newer than the 1.2.7 fixture.'
    $newAsset = "https://github.com/Kewz4/Cobble-Music/releases/download/updater-v$newVersion/CobbleMusicUpdater.exe"
    $realIdentities = @{ old = $oldSha; new = $newSha }

    function New-RealInstance([string]$Name) {
        $instance = New-Instance $Name $VerifierExePath $OldChannelFixture $NewChannelPath $NewExePath $newVersion $newSha
        [IO.File]::Copy($VerifierExePath, (Join-Path $instance 'minecraft\cobble-music-updater\CobbleMusicUpdaterVerifier-1.2.7.exe'))
        return $instance
    }
    $realRemote = @{
        old = @{ remoteJson = $OldChannelFixture; remoteSig = [IO.Path]::ChangeExtension($OldChannelFixture, '.sig') }
        new = @{ remoteJson = $NewChannelPath; remoteSig = [IO.Path]::ChangeExtension($NewChannelPath, '.sig') }
    }

    # Controls: without the swap, an advanced stable costs the slow bootstrap download; the harness must see it.
    $control = New-RealInstance 'real-control-new'
    $result = Invoke-PinnedBootstrap $control (@{ releases = @{ $newAsset = $NewExePath } } + $realRemote.new)
    Assert-True (@($result.downloads).Count -eq 1 -and [string]@($result.downloads)[0] -ceq $newAsset -and (Get-Outcome $result $realIdentities) -ceq 'new') `
        "Control failed: an un-staged instance must download the new updater once. $($result | ConvertTo-Json -Depth 4)"
    $control = New-RealInstance 'real-control-old'
    $result = Invoke-PinnedBootstrap $control (@{ releases = @{} } + $realRemote.old)
    Assert-True (@($result.downloads).Count -eq 0 -and (Get-Outcome $result $realIdentities) -ceq 'old') "Control failed: unchanged stable must run the installed updater. $($result | ConvertTo-Json -Depth 4)"

    $swapped = New-RealInstance 'real-swapped'
    $swap = Invoke-Fixture @('--prestage-fixture', 'swap', (Join-Path $swapped 'minecraft\cobble-music-updater'), $newVersion, 'none', '--real-key')
    Assert-True ($swap.ExitCode -eq 0) "Real-key swap failed: $($swap.Output)"
    $cacheBytes = [IO.File]::ReadAllBytes((Join-Path $swapped 'minecraft\cobble-music-updater\installed-updater-channel.json'))
    Assert-True ([Linq.Enumerable]::SequenceEqual($cacheBytes, [IO.File]::ReadAllBytes($NewChannelPath))) 'The swap did not write the exact signed next bytes as the bootstrap cache.'

    foreach ($case in @(
        @{ Name = 'stable still names 1.2.7'; Scenario = (@{ releases = @{ $newAsset = $NewExePath } } + $realRemote.old); Message = "Ignoring replayed updater channel v1.2.7; v$newVersion is already trusted." },
        @{ Name = 'stable advanced with the same bytes'; Scenario = (@{ releases = @{ $newAsset = $NewExePath } } + $realRemote.new); Message = $null },
        @{ Name = 'offline'; Scenario = @{ offline = $true; releases = @{} }; Message = 'Could not adopt a newer updater; retaining the last verified version' }
    )) {
        $result = Invoke-PinnedBootstrap $swapped $case.Scenario
        Assert-True ($null -eq $result.failure) "Real bootstrap threw ($($case.Name)): $($result.failure)"
        Assert-True (@($result.downloads).Count -eq 0) "Real bootstrap downloaded after the swap ($($case.Name)): $(@($result.downloads) -join ', ')"
        Assert-True ((Get-Outcome $result $realIdentities) -ceq 'new') "Real bootstrap did not keep the pre-staged updater ($($case.Name)): $($result | ConvertTo-Json -Depth 4)"
        if ($case.Message) {
            Assert-True (@($result.messages | Where-Object { ([string]$_).Contains($case.Message) }).Count -eq 1) "Expected bootstrap message missing ($($case.Name)): $($case.Message)`n$(@($result.messages) -join [Environment]::NewLine)"
        }
    }
    Write-Host "Scenario A passed: real pinned bootstrap + real 1.2.7 verifier accept the swapped $newVersion with no download (stable old, stable advanced, offline)."

    # ============================== Scenario B: fixture key, killed at every step ==============================
    $fixtures = Join-Path $TempRoot 'fixtures'
    $make = Invoke-Fixture @('--prestage-fixture', 'make', $fixtures, '1.2.30', '1.2.31')
    Assert-True ($make.ExitCode -eq 0) "Fixture creation failed: $($make.Output)"
    $oldExe = Join-Path $fixtures 'old.exe'
    $newExe = Join-Path $fixtures 'new.exe'
    $fixtureIdentities = @{ old = (Get-Sha256 $oldExe); new = (Get-Sha256 $newExe); verifier = (Get-Sha256 $FixtureExe) }
    $fixtureNewAsset = 'https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.31/CobbleMusicUpdater.exe'
    $fixtureOldAsset = 'https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.30/CobbleMusicUpdater.exe'
    $fixtureBase = @{ verifierExe = $FixtureExe; verifierSha256 = $fixtureIdentities.verifier; releases = @{ $fixtureNewAsset = $newExe; $fixtureOldAsset = $oldExe } }
    $fixtureRemote = @{
        old = @{ remoteJson = (Join-Path $fixtures 'old.json'); remoteSig = (Join-Path $fixtures 'old.sig') }
        new = @{ remoteJson = (Join-Path $fixtures 'new.json'); remoteSig = (Join-Path $fixtures 'new.sig') }
        offline = @{ offline = $true }
    }
    # Expected (final exe, downloads) per state and remote. "verifier" = the bootstrap's pinned-verifier fallback (lines 333-341).
    $expectations = [ordered]@{
        'not-swapped'        = @{ old = @('old', 0); new = @('new', 1); offline = @('old', 0); recovered = 'old' }
        'RollbackPrepared'   = @{ old = @('old', 0); new = @('new', 1); offline = @('old', 0); recovered = 'old' }
        'ChannelWritten'     = @{ old = @('old', 0); new = @('new', 1); offline = @('verifier', 0); recovered = 'old' }
        'SignatureWritten'   = @{ old = @('old', 0); new = @('new', 1); offline = @('verifier', 0); recovered = 'old' }
        'ExecutableReplaced' = @{ old = @('new', 0); new = @('new', 0); offline = @('new', 0); recovered = 'new' }
        'none'               = @{ old = @('new', 0); new = @('new', 0); offline = @('new', 0); recovered = 'new' }
    }
    $report = [Collections.Generic.List[string]]::new()
    foreach ($state in $expectations.Keys) {
        foreach ($remote in @('old', 'new', 'offline', 'recovered')) {
            $name = "fixture-$state-$remote"
            $instance = New-Instance $name $oldExe (Join-Path $fixtures 'old.json') (Join-Path $fixtures 'new.json') $newExe '1.2.31' $fixtureIdentities.new
            $install = Join-Path $instance 'minecraft\cobble-music-updater'
            if ($state -cne 'not-swapped') {
                $swap = Invoke-Fixture @('--prestage-fixture', 'swap', $install, '1.2.31', $state)
                if ($state -ceq 'none') { Assert-True ($swap.ExitCode -eq 0) "Fixture swap failed: $($swap.Output)" }
                else { Assert-True ($swap.ExitCode -ne 0) "Fixture swap was not killed after $state." }
            }
            $scenario = @{} + $fixtureBase
            if ($remote -ceq 'recovered') {
                $recover = Invoke-Fixture @('--prestage-fixture', 'recover', $install)
                Assert-True ($recover.ExitCode -eq 0) "Recovery after $state failed: $($recover.Output)"
                $scenario += $fixtureRemote.offline
                $expected = @($expectations[$state].recovered, 0)
            }
            else {
                $scenario += $fixtureRemote[$remote]
                $expected = $expectations[$state][$remote]
            }
            $result = Invoke-PinnedBootstrap $instance $scenario
            $outcome = Get-Outcome $result $fixtureIdentities
            $downloads = @($result.downloads).Count
            $report.Add(('{0,-19} {1,-9} -> runs {2,-8} downloads {3}' -f $state, $remote, $outcome, $downloads))
            Assert-True ($null -eq $result.failure) "Bootstrap threw for $name`: $($result.failure)"
            Assert-True ($outcome -ceq $expected[0] -and $downloads -eq $expected[1]) `
                "Bootstrap outcome for $name was ($outcome, $downloads downloads); expected ($($expected[0]), $($expected[1])).`n$(@($result.messages) -join [Environment]::NewLine)"
            if ($downloads -eq 1) {
                Assert-True ([string]@($result.downloads)[0] -ceq $fixtureNewAsset) "Unexpected download for $name`: $(@($result.downloads)[0])"
            }
        }
    }
    $report | ForEach-Object { Write-Host $_ }
    Write-Host 'Updater pre-staging bootstrap checks passed: the pinned bootstrap runs a swapped updater with no download and no fallback (stable old, stable advanced, offline); every torn state online runs the old updater with no extra download; only a process death between the two cache renames followed by an OFFLINE launch reaches the pinned-verifier fallback, and recovery removes even that.'
}
finally {
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
