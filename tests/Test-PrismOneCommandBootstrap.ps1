[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$GeneratorPath = Join-Path $Root 'tools\New-CobbleMusicPrismBootstrapCommand.ps1'
$BootstrapPath = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
$TemporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('cobble-music-prism-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-EncodedPayload([string]$Command) {
    $pattern = '^powershell\.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand (?<payload>[A-Za-z0-9+/]+={0,2})$'
    $match = [regex]::Match($Command, $pattern)
    if (-not $match.Success) { throw 'Generated Prism command does not have the one-process encoded-command shape.' }
    return [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($match.Groups['payload'].Value))
}

function Set-SingleQuotedAssignment([string]$Text, [string]$Name, [string]$Value) {
    $pattern = '(?m)^(?<prefix>[ \t]*' + [regex]::Escape('$' + $Name) + '[ \t]*=[ \t]*)''[^''\r\n]*''(?<suffix>[ \t]*\r?)$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Expected one `$${Name} assignment, found $($matches.Count)." }
    return [regex]::Replace($Text, $pattern, '${prefix}''' + $Value + '''${suffix}', 1)
}

foreach ($required in @($GeneratorPath, $BootstrapPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required test input is missing: $required" }
}

$savedInstance = [Environment]::GetEnvironmentVariable('INST_DIR')
$savedMinecraft = [Environment]::GetEnvironmentVariable('INST_MC_DIR')
$savedLog = [Environment]::GetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG')
try {
    New-Item -ItemType Directory -Path $TemporaryRoot -Force | Out-Null
    $instance = Join-Path $TemporaryRoot 'Instance With Spaces'
    $minecraft = Join-Path $instance 'minecraft'
    $updaterDirectory = Join-Path $minecraft 'cobble-music-updater'
    New-Item -ItemType Directory -Path $updaterDirectory -Force | Out-Null

    $fakeBootstrapSource = Join-Path $TemporaryRoot 'known-bootstrap.ps1'
    $fakeBootstrapText = @'
[CmdletBinding()]
param([string]$InstanceDirectory, [switch]$PrismPreLaunch)
[IO.File]::WriteAllLines(
    [Environment]::GetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG'),
    [string[]]@($InstanceDirectory, [string]$PrismPreLaunch))
'@
    [IO.File]::WriteAllText($fakeBootstrapSource, $fakeBootstrapText, [Text.UTF8Encoding]::new($false))
    $bootstrapHash = Get-Sha256 $fakeBootstrapSource
    $commandOutput = @(& $GeneratorPath `
        -UpdaterVersion '1.2.4' `
        -ExpectedBootstrapSha256 $bootstrapHash)
    if ($commandOutput.Count -ne 1) {
        throw 'Prism command generator did not emit exactly one successful command.'
    }
    $command = [string]$commandOutput[0]
    Assert-True ($command.Length -lt 8191) "Generated Prism command is unexpectedly long: $($command.Length) characters."
    $payload = Get-EncodedPayload $command
    Assert-True ($payload.IndexOf("updater-v1.2.4/Bootstrap-CobbleMusicUpdater.ps1", [StringComparison]::Ordinal) -ge 0) 'Encoded command does not pin the requested bootstrap release.'
    Assert-True ($payload.IndexOf($bootstrapHash, [StringComparison]::Ordinal) -ge 0) 'Encoded command does not pin the bootstrap SHA-256.'
    Assert-True ($payload.IndexOf("GetEnvironmentVariable('INST_DIR')", [StringComparison]::Ordinal) -ge 0) 'Encoded command does not consume Prism INST_DIR safely.'
    Assert-True ($payload.IndexOf("GetEnvironmentVariable('INST_MC_DIR')", [StringComparison]::Ordinal) -ge 0) 'Encoded command does not consume Prism INST_MC_DIR safely.'
    Assert-True ($payload.IndexOf('-PrismPreLaunch', [StringComparison]::Ordinal) -ge 0) 'Encoded command does not invoke bootstrap PrismPreLaunch mode.'
    Assert-True ($payload.IndexOf('Invoke-Expression', [StringComparison]::OrdinalIgnoreCase) -lt 0) 'Encoded command must not invoke downloaded text through Invoke-Expression.'
    $tokens = $null
    $parseErrors = $null
    $payloadAst = [Management.Automation.Language.Parser]::ParseInput($payload, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "Decoded Prism payload has parse errors: $($parseErrors.Message -join '; ')" }
    $payloadBlock = $payloadAst.GetScriptBlock()

    $cachedBootstrap = Join-Path $updaterDirectory 'Bootstrap-CobbleMusicUpdater.ps1'
    [IO.File]::Copy($fakeBootstrapSource, $cachedBootstrap, $true)
    $bootstrapLog = Join-Path $TemporaryRoot 'bootstrap-args.txt'
    [Environment]::SetEnvironmentVariable('INST_DIR', $instance)
    [Environment]::SetEnvironmentVariable('INST_MC_DIR', $minecraft)
    [Environment]::SetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG', $bootstrapLog)
    $script:downloadCount = 0
    $script:downloadSource = $fakeBootstrapSource
    function Invoke-WebRequest {
        param([switch]$UseBasicParsing, [string]$Uri, [string]$OutFile)
        $script:downloadCount++
        if ($Uri -cne 'https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.4/Bootstrap-CobbleMusicUpdater.ps1') {
            throw "Unexpected bootstrap URI: $Uri"
        }
        [IO.File]::Copy($script:downloadSource, $OutFile, $true)
    }

    & $payloadBlock
    Assert-True ($script:downloadCount -eq 0) 'An exact cached bootstrap unexpectedly triggered a download.'
    $loggedArguments = @([IO.File]::ReadAllLines($bootstrapLog))
    Assert-True ($loggedArguments.Count -eq 2 -and $loggedArguments[0] -ceq [IO.Path]::GetFullPath($instance) -and $loggedArguments[1] -ceq 'True') 'Cached bootstrap did not receive the exact instance and PrismPreLaunch arguments.'

    $windowsPowerShell = Get-Command 'powershell.exe' -ErrorAction Stop
    Remove-Item -LiteralPath $bootstrapLog -Force
    $encodedArgument = ($command -split ' ')[-1]
    & $windowsPowerShell.Source -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand $encodedArgument | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The exact generated command failed under Windows PowerShell with exit code $LASTEXITCODE." }
    $loggedArguments = @([IO.File]::ReadAllLines($bootstrapLog))
    Assert-True ($loggedArguments.Count -eq 2 -and $loggedArguments[0] -ceq [IO.Path]::GetFullPath($instance) -and $loggedArguments[1] -ceq 'True') 'Windows PowerShell did not preserve the generated command argument boundaries.'

    [IO.File]::WriteAllText($cachedBootstrap, 'corrupt-cache', [Text.UTF8Encoding]::new($false))
    & $payloadBlock
    Assert-True ($script:downloadCount -eq 1) 'A corrupt cached bootstrap was not downloaded exactly once.'
    Assert-True ((Get-Sha256 $cachedBootstrap) -ceq $bootstrapHash) 'Downloaded bootstrap was not atomically installed with the pinned checksum.'

    $badDownload = Join-Path $TemporaryRoot 'bad-download.ps1'
    [IO.File]::WriteAllText($badDownload, 'wrong-download', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($cachedBootstrap, 'still-corrupt', [Text.UTF8Encoding]::new($false))
    $corruptCacheHash = Get-Sha256 $cachedBootstrap
    $script:downloadSource = $badDownload
    $checksumFailure = $null
    try { & $payloadBlock }
    catch { $checksumFailure = $_.Exception.Message }
    Assert-True ($null -ne $checksumFailure -and $checksumFailure.IndexOf('Bootstrap checksum mismatch', [StringComparison]::Ordinal) -ge 0) 'A bad bootstrap download did not fail closed on its checksum.'
    Assert-True ((Get-Sha256 $cachedBootstrap) -ceq $corruptCacheHash) 'A failed bootstrap download replaced the prior cache before verification.'

    $bootstrap = [IO.File]::ReadAllText($BootstrapPath)
    $fakeUpdater = Join-Path $updaterDirectory 'CobbleMusicUpdater.exe'
    [IO.File]::WriteAllBytes($fakeUpdater, [byte[]](0x4d, 0x5a, 0x01, 0x02, 0x03, 0x04))
    $fakeUpdaterHash = Get-Sha256 $fakeUpdater
    $bootstrap = Set-SingleQuotedAssignment $bootstrap 'ExpectedUpdaterSha256' $fakeUpdaterHash
    $downloadNeedle = '        Invoke-WebRequest -UseBasicParsing -Uri $AssetUri -OutFile $download'
    $downloadReplacement = "        throw 'NETWORK_CALLED_DURING_EXACT_UPDATER_REUSE'"
    Assert-True ([regex]::Matches($bootstrap, [regex]::Escape($downloadNeedle)).Count -eq 1) 'Bootstrap download call fixture was not unique.'
    $bootstrap = $bootstrap.Replace($downloadNeedle, $downloadReplacement)
    $invokeNeedle = "    & `$targetExe '--instance-dir' `$instance '--minecraft-dir' `$minecraft '--prism-prelaunch'"
    $invokeReplacement = @'
    [IO.File]::WriteAllLines(
        [Environment]::GetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG'),
        [string[]]@($instance, $minecraft, '--prism-prelaunch'))
    $LASTEXITCODE = 0
'@
    Assert-True ([regex]::Matches($bootstrap, [regex]::Escape($invokeNeedle)).Count -eq 1) 'Bootstrap updater invocation fixture was not unique.'
    $bootstrap = $bootstrap.Replace($invokeNeedle, $invokeReplacement.TrimEnd("`r", "`n"))
    $testBootstrap = Join-Path $TemporaryRoot 'Bootstrap-under-test.ps1'
    [IO.File]::WriteAllText($testBootstrap, $bootstrap, [Text.UTF8Encoding]::new($false))

    $instanceConfig = Join-Path $instance 'instance.cfg'
    $instanceConfigText = "InstanceType=OneSix`r`nOverrideCommands=true`r`nPreLaunchCommand=permanent-encoded-bootstrap`r`nUnrelated=keep`r`n"
    [IO.File]::WriteAllText($instanceConfig, $instanceConfigText, [Text.UTF8Encoding]::new($false))
    $instanceConfigHash = Get-Sha256 $instanceConfig
    $bootstrapLog = Join-Path $TemporaryRoot 'updater-args.txt'
    [Environment]::SetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG', $bootstrapLog)
    & $testBootstrap -InstanceDirectory $instance -PrismPreLaunch | Out-Null
    Assert-True ((Get-Sha256 $instanceConfig) -ceq $instanceConfigHash) 'PrismPreLaunch mode modified instance.cfg while Prism could be running.'
    Assert-True (@(Get-ChildItem -LiteralPath $instance -Filter 'instance.cfg.cobble-music-updater-*.bak' -File).Count -eq 0) 'PrismPreLaunch mode created an instance.cfg backup despite not owning that file.'
    $updaterArguments = @([IO.File]::ReadAllLines($bootstrapLog))
    Assert-True ($updaterArguments.Count -eq 3 `
        -and $updaterArguments[0] -ceq [IO.Path]::GetFullPath($instance) `
        -and $updaterArguments[1] -ceq [IO.Path]::GetFullPath($minecraft) `
        -and $updaterArguments[2] -ceq '--prism-prelaunch') 'PrismPreLaunch mode did not run the updater with exact argument boundaries.'
    $configurationPath = Join-Path $updaterDirectory 'updater.json'
    Assert-True (Test-Path -LiteralPath $configurationPath -PathType Leaf) 'PrismPreLaunch mode did not create updater.json on first use.'
    $configurationHash = Get-Sha256 $configurationPath
    & $testBootstrap -InstanceDirectory $instance -PrismPreLaunch | Out-Null
    Assert-True ((Get-Sha256 $configurationPath) -ceq $configurationHash) 'An unchanged updater.json was needlessly rewritten on a later launch.'
    Assert-True (@(Get-ChildItem -LiteralPath $updaterDirectory -Filter 'updater.json.cobble-music-updater-*.bak' -File).Count -eq 0) 'An unchanged updater.json created a redundant per-launch backup.'

    [Environment]::SetEnvironmentVariable('INST_DIR', (Join-Path $TemporaryRoot 'Wrong Instance'))
    $contextFailure = $null
    try { & $testBootstrap -InstanceDirectory $instance -PrismPreLaunch | Out-Null }
    catch { $contextFailure = $_.Exception.Message }
    Assert-True ($null -ne $contextFailure -and $contextFailure.IndexOf('do not match', [StringComparison]::Ordinal) -ge 0) 'PrismPreLaunch mode accepted mismatched Prism instance paths.'

    Write-Host 'One-command Prism bootstrap checks passed: encoded argument safety, exact cache reuse, fail-closed replacement, no live instance.cfg mutation, and same-launch updater invocation.'
}
finally {
    [Environment]::SetEnvironmentVariable('INST_DIR', $savedInstance)
    [Environment]::SetEnvironmentVariable('INST_MC_DIR', $savedMinecraft)
    [Environment]::SetEnvironmentVariable('COBBLE_MUSIC_TEST_BOOTSTRAP_LOG', $savedLog)
    if (Test-Path -LiteralPath $TemporaryRoot) { Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force }
}
