<#
Installs the self-contained updater inside one Prism instance and wires it to
Prism's instance-specific Pre-launch command. The updater modifies no files
until a valid signed GitHub Release exists.
#>
[CmdletBinding()]
param(
    [string]$InstanceDirectory = "C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon - Client 1.0.1",
    [string]$Repository = 'Kewz4/Cobble-Music',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$MinecraftDirectory = Join-Path $InstanceDirectory 'minecraft'
$SourceExe = Join-Path $Root "updater\dist\$Runtime\CobbleMusicUpdater.exe"
$TargetDirectory = Join-Path $MinecraftDirectory 'cobble-music-updater'
$TargetExe = Join-Path $TargetDirectory 'CobbleMusicUpdater.exe'
$ConfigPath = Join-Path $TargetDirectory 'updater.json'
$InstanceConfig = Join-Path $InstanceDirectory 'instance.cfg'

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Set-InstanceSetting([string[]]$Lines, [string]$Name, [string]$Value) {
    $escapedName = [regex]::Escape($Name)
    $updated = [Collections.Generic.List[string]]::new()
    $inserted = $false
    foreach ($existingLine in $Lines) {
        if ($existingLine -match "^$escapedName=") {
            if (-not $inserted) {
                $updated.Add("$Name=$Value")
                $inserted = $true
            }
            continue
        }
        $updated.Add($existingLine)
    }
    if (-not $inserted) { $updated.Add("$Name=$Value") }
    return ,$updated.ToArray()
}

function Get-InstanceSettingValues([string[]]$Lines, [string]$Name) {
    $escapedName = [regex]::Escape($Name)
    $values = [Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        if ($line -match "^$escapedName=(.*)$") { $values.Add($Matches[1]) }
    }
    return $values.ToArray()
}

function Assert-PrismLauncherNotRunning {
    $running = @(Get-Process -Name 'prismlauncher' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw 'Prism Launcher is running. Close every Prism Launcher window and wait for prismlauncher.exe to exit, then rerun. instance.cfg was not changed.'
    }
}

foreach ($path in @($InstanceDirectory, $MinecraftDirectory)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Expected Prism instance path is missing: $path" }
}
if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw "Updater EXE not found: $SourceExe. Run Build-CobbleMusicUpdater.ps1 first." }
if (-not (Test-Path -LiteralPath $InstanceConfig -PathType Leaf)) { throw "Prism instance configuration is missing: $InstanceConfig" }
Assert-PrismLauncherNotRunning

$instanceText = [IO.File]::ReadAllText($InstanceConfig)
$lineEnding = if ($instanceText.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = @($instanceText -split "`r?`n")
# Prism's QSettings INI parser requires escaped quotes in the physical
# instance.cfg value. Without the backslashes it will later rewrite the command
# and concatenate quoted arguments (notably paths under Program Files).
$preLaunch = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'
$existingPreLaunchValues = @(Get-InstanceSettingValues $lines 'PreLaunchCommand')
$conflictingPreLaunchValues = @($existingPreLaunchValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -cne $preLaunch })
if ($conflictingPreLaunchValues.Count -gt 0 -and -not $Force) {
    throw "This Prism instance already has a different PreLaunchCommand. Refusing to replace it. Review it and rerun with -Force only if replacing it is intentional. Existing command: $($conflictingPreLaunchValues[0])"
}
New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
Copy-Item -LiteralPath $SourceExe -Destination $TargetExe -Force
$configuration = [ordered]@{
    schemaVersion = 1
    modpackId = 'cobble-music'
    repository = $Repository
    channel = 'stable'
    manifestAsset = 'cobble-music-update.json'
    signatureAsset = 'cobble-music-update.sig'
    networkTimeoutSeconds = 30
    allowOfflineLaunch = $true
    allowedRoots = @('mods', 'resourcepacks', 'shaderpacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
}
Write-Utf8 $ConfigPath ($configuration | ConvertTo-Json -Depth 8)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$InstanceConfig.cobble-music-updater-$stamp.bak"
Copy-Item -LiteralPath $InstanceConfig -Destination $backup
$lines = Set-InstanceSetting $lines 'OverrideCommands' 'true'
$lines = Set-InstanceSetting $lines 'PreLaunchCommand' $preLaunch
$lines = Set-InstanceSetting $lines 'LogPrePostOutput' 'true'
Assert-PrismLauncherNotRunning
Write-Utf8 $InstanceConfig ($lines -join $lineEnding)

Write-Host "Installed $TargetExe"
Write-Host "Configured Prism pre-launch updater for $(Split-Path -Leaf $InstanceDirectory)"
Write-Host "Backup: $backup"
Write-Host 'The updater is safe to run now: without a signed release it only reports that no update is published and Prism continues launching.'
