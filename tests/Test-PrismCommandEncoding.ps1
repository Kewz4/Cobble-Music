[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$expectedCommand = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'

function Get-FunctionText($Ast, [string]$Name, [string]$RelativePath) {
    $matches = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true))
    if ($matches.Count -ne 1) { throw "Expected one $Name function in $RelativePath, found $($matches.Count)." }
    return $matches[0].Extent.Text
}

foreach ($relativePath in @(
    'tools\Install-CobbleMusicUpdater.ps1',
    'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
)) {
    $path = Join-Path $Root $relativePath
    $text = [IO.File]::ReadAllText($path)
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "PowerShell parse errors in ${relativePath}: $($parseErrors.Message -join '; ')" }
    $match = [regex]::Match($text, '(?m)^\$preLaunch = ''(?<command>.*)''$')
    if (-not $match.Success) {
        throw "Could not find the Prism pre-launch command in $relativePath"
    }
    if ($match.Groups['command'].Value -cne $expectedCommand) {
        throw "Prism command in $relativePath is not QSettings-escaped correctly. It must retain escaped quotes around each path variable."
    }

    if ($text.Contains('Stop-Process', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$relativePath must never kill Prism Launcher to edit instance.cfg."
    }
    $guardCalls = [regex]::Matches($text, '(?m)^[ \t]*Assert-PrismLauncherNotRunning[ \t]*\r?$')
    if ($guardCalls.Count -lt 2) {
        throw "$relativePath must check Prism at startup and again immediately before editing instance.cfg."
    }
    $firstMutationNeedle = if ($relativePath.StartsWith('tools', [StringComparison]::Ordinal)) {
        'Copy-Item -LiteralPath $SourceExe -Destination $TargetExe -Force'
    }
    else {
        'Install-VerifiedUpdater $assetUri $ExpectedUpdaterSha256 $targetExe'
    }
    $instanceWriteNeedle = if ($relativePath.StartsWith('tools', [StringComparison]::Ordinal)) {
        'Write-Utf8 $InstanceConfig'
    }
    else {
        'Write-Utf8Atomically $instanceConfig'
    }
    $firstMutationIndex = $text.IndexOf($firstMutationNeedle, [StringComparison]::Ordinal)
    $instanceWriteIndex = $text.LastIndexOf($instanceWriteNeedle, [StringComparison]::Ordinal)
    $lastGuard = $guardCalls[$guardCalls.Count - 1]
    if ($guardCalls[0].Index -ge $firstMutationIndex -or $lastGuard.Index -ge $instanceWriteIndex) {
        throw "$relativePath does not fail on running Prism before installation and the final instance.cfg write."
    }
    $betweenGuardAndWrite = $text.Substring($lastGuard.Index + $lastGuard.Length, $instanceWriteIndex - ($lastGuard.Index + $lastGuard.Length))
    if (-not [string]::IsNullOrWhiteSpace($betweenGuardAndWrite)) {
        throw "$relativePath performs another operation between its final Prism process check and instance.cfg write."
    }

    $settingFunctions = @(
        (Get-FunctionText $ast 'Set-InstanceSetting' $relativePath),
        (Get-FunctionText $ast 'Get-InstanceSettingValues' $relativePath)
    ) -join [Environment]::NewLine
    $settingsHarness = [scriptblock]::Create('param([string]$ExpectedCommand)' + [Environment]::NewLine + $settingFunctions + [Environment]::NewLine + @'
$fixture = @(
    'InstanceType=OneSix',
    'OverrideCommands=false',
    'UnrelatedAlpha=keep',
    'PreLaunchCommand=',
    'LogPrePostOutput=false',
    "PreLaunchCommand=$ExpectedCommand",
    'OverrideCommands=false',
    'LogPrePostOutput=false',
    'UnrelatedBeta=also=keep'
)
$capturedCommands = @(Get-InstanceSettingValues $fixture 'PreLaunchCommand')
if ($capturedCommands.Count -ne 2 -or $capturedCommands[0] -cne '' -or $capturedCommands[1] -cne $ExpectedCommand) {
    throw 'Duplicate pre-launch values were not all inspected before canonicalization.'
}
$canonical = Set-InstanceSetting $fixture 'OverrideCommands' 'true'
$canonical = Set-InstanceSetting $canonical 'PreLaunchCommand' $ExpectedCommand
$canonical = Set-InstanceSetting $canonical 'LogPrePostOutput' 'true'
foreach ($managedKey in @('OverrideCommands', 'PreLaunchCommand', 'LogPrePostOutput')) {
    $matches = @($canonical | Where-Object { $_ -match ('^' + [regex]::Escape($managedKey) + '=') })
    if ($matches.Count -ne 1) { throw "Managed setting was not canonicalized exactly once: $managedKey" }
}
$expected = @(
    'InstanceType=OneSix',
    'OverrideCommands=true',
    'UnrelatedAlpha=keep',
    "PreLaunchCommand=$ExpectedCommand",
    'LogPrePostOutput=true',
    'UnrelatedBeta=also=keep'
)
if ([string]::Join("`n", $canonical) -cne [string]::Join("`n", $expected)) {
    throw 'Canonicalization changed unrelated instance.cfg settings or retained a duplicate managed key.'
}
'settings-ok'
'@)
    $settingsResult = @(& $settingsHarness $expectedCommand)
    if ($settingsResult.Count -ne 1 -or [string]$settingsResult[0] -cne 'settings-ok') {
        throw "Duplicate-key fixture did not complete exactly for $relativePath."
    }

    $guardFunction = Get-FunctionText $ast 'Assert-PrismLauncherNotRunning' $relativePath
    $guardHarness = [scriptblock]::Create('param([bool]$InitiallyRunning)' + [Environment]::NewLine + $guardFunction + [Environment]::NewLine + @'
$script:running = $InitiallyRunning
$script:probes = 0
function Get-Process {
    param([string]$Name, $ErrorAction)
    if ($Name -cne 'prismlauncher') { throw "Guard probed an unexpected process name: $Name" }
    $script:probes++
    if ($script:running) { return [pscustomobject]@{ ProcessName = 'prismlauncher'; Id = 1234 } }
    return @()
}
if ($InitiallyRunning) {
    $failure = $null
    try { Assert-PrismLauncherNotRunning }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains('Close every Prism Launcher window', [StringComparison]::Ordinal)) {
        throw "Running Prism did not fail clearly: $failure"
    }
}
else {
    Assert-PrismLauncherNotRunning
}
if ($script:probes -ne 1) { throw "Prism guard did not perform exactly one mocked process probe: $script:probes" }
'guard-ok'
'@)
    foreach ($running in @($false, $true)) {
        $guardResult = @(& $guardHarness $running)
        if ($guardResult.Count -ne 1 -or [string]$guardResult[0] -cne 'guard-ok') {
            throw "Running-Prism guard fixture did not complete exactly for $relativePath (running=$running)."
        }
    }
}

Write-Host 'Prism direct-command encoding, duplicate-key canonicalization, and manual running-launcher guard checks passed without touching a live instance or process.'
