<#
One-time setup for a Prism instance without the Kewz's Cobblemon pre-launch
updater. This script is intentionally a release asset rather than
a command that runs on every launch. It verifies the exact updater EXE before
installing it and refuses to replace another custom Prism pre-launch command.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstanceDirectory,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Repository = 'Kewz4/Cobble-Music'
$UpdaterVersion = '1.2.3'
# SHA-256 of CobbleMusicUpdater.exe from updater-v1.2.3.
$ExpectedUpdaterSha256 = '2DE9058F00955ACB970F673A6B6D325E5AF5F5D9F43288632EEA5560DC40F1FA'
# Prism's QSettings INI parser requires escaped quotes in the physical
# instance.cfg value. Without the backslashes it will later rewrite the command
# and concatenate quoted arguments (notably paths under Program Files).
$preLaunch = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'
$instance = [IO.Path]::GetFullPath($InstanceDirectory)
$minecraft = Join-Path $instance 'minecraft'
$instanceConfig = Join-Path $instance 'instance.cfg'
$targetDirectory = Join-Path $minecraft 'cobble-music-updater'
$targetExe = Join-Path $targetDirectory 'CobbleMusicUpdater.exe'
$configPath = Join-Path $targetDirectory 'updater.json'
$assetUri = "https://github.com/$Repository/releases/download/updater-v$UpdaterVersion/CobbleMusicUpdater.exe"
$temporaryDownload = Join-Path ([IO.Path]::GetTempPath()) ("CobbleMusicUpdater-" + [Guid]::NewGuid().ToString('N') + '.download')
$temporaryExe = $null

function Write-Utf8Atomically([string]$Path, [string]$Text) {
    $temporary = "$Path.new-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText($temporary, $Text, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
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

function Assert-WritableDirectory([string]$Path) {
    $probe = Join-Path $Path ('.cobble-music-write-probe-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllBytes($probe, [byte[]]@())
    }
    finally {
        if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
    }
}

function Test-WindowsExecutable([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 2) { return $false }
        return $stream.ReadByte() -eq 0x4d -and $stream.ReadByte() -eq 0x5a
    }
    finally {
        $stream.Dispose()
    }
}

try {
    foreach ($path in @($instance, $minecraft)) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Expected Prism instance path is missing: $path"
        }
    }
    if (-not (Test-Path -LiteralPath $instanceConfig -PathType Leaf)) {
        throw "Prism instance configuration is missing: $instanceConfig"
    }
    Assert-PrismLauncherNotRunning
    $instanceText = [IO.File]::ReadAllText($instanceConfig)
    $lineEnding = if ($instanceText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = @($instanceText -split "`r?`n")
    $existingPreLaunchValues = @(Get-InstanceSettingValues $lines 'PreLaunchCommand')
    $conflictingPreLaunchValues = @($existingPreLaunchValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -cne $preLaunch })
    if ($conflictingPreLaunchValues.Count -gt 0 -and -not $Force) {
        throw "This Prism instance already has a different PreLaunchCommand. The bootstrap will not replace it. Review it and rerun with -Force only if replacement is intentional. Existing command: $($conflictingPreLaunchValues[0])"
    }

    Assert-WritableDirectory $minecraft

    Write-Host "Downloading the verified Kewz's Cobblemon updater..."
    Invoke-WebRequest -Uri $assetUri -OutFile $temporaryDownload
    $actualHash = (Get-FileHash -LiteralPath $temporaryDownload -Algorithm SHA256).Hash
    if (-not $actualHash.Equals($ExpectedUpdaterSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Updater checksum mismatch. Expected $ExpectedUpdaterSha256 but downloaded $actualHash. Nothing was installed."
    }
    if (-not (Test-WindowsExecutable $temporaryDownload)) {
        throw 'The verified updater asset is not a Windows executable. Nothing was installed.'
    }

    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    Assert-WritableDirectory $targetDirectory
    $temporaryExe = "$targetExe.new-$([Guid]::NewGuid().ToString('N'))"
    [IO.File]::Copy($temporaryDownload, $temporaryExe, $true)
    $copiedHash = (Get-FileHash -LiteralPath $temporaryExe -Algorithm SHA256).Hash
    if (-not $copiedHash.Equals($ExpectedUpdaterSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The updater copy failed checksum verification. Nothing was installed.'
    }
    [IO.File]::Move($temporaryExe, $targetExe, $true)
    $temporaryExe = $null

    $configuration = [ordered]@{
        schemaVersion = 1
        modpackId = 'cobble-music'
        repository = $Repository
        channel = 'stable'
        manifestAsset = 'cobble-music-update.json'
        signatureAsset = 'cobble-music-update.sig'
        networkTimeoutSeconds = 30
        allowOfflineLaunch = $true
        allowedRoots = @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
    }
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $configBackup = "$configPath.cobble-music-updater-$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
        Copy-Item -LiteralPath $configPath -Destination $configBackup
        Write-Host "Backed up existing updater configuration: $configBackup"
    }
    Write-Utf8Atomically $configPath ($configuration | ConvertTo-Json -Depth 8)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $instanceBackup = "$instanceConfig.cobble-music-updater-$stamp.bak"
    Copy-Item -LiteralPath $instanceConfig -Destination $instanceBackup
    $lines = Set-InstanceSetting $lines 'OverrideCommands' 'true'
    $lines = Set-InstanceSetting $lines 'PreLaunchCommand' $preLaunch
    $lines = Set-InstanceSetting $lines 'LogPrePostOutput' 'true'
    Assert-PrismLauncherNotRunning
    Write-Utf8Atomically $instanceConfig ($lines -join $lineEnding)

    Write-Host "Installed verified updater: $targetExe"
    Write-Host "Configured Prism pre-launch update checks for: $(Split-Path -Leaf $instance)"
    Write-Host "Prism configuration backup: $instanceBackup"
    Write-Host "Future Prism Play launches now check for signed Kewz's Cobblemon updates before Minecraft starts."
}
finally {
    if ($temporaryExe -and (Test-Path -LiteralPath $temporaryExe)) { Remove-Item -LiteralPath $temporaryExe -Force }
    if (Test-Path -LiteralPath $temporaryDownload) { Remove-Item -LiteralPath $temporaryDownload -Force }
}
