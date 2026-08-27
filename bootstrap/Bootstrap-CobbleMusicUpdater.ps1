<#
Installs and runs the Kewz's Cobblemon updater for one Prism instance.

The friend-facing Prism command pins this bootstrap by SHA-256. This script in
turn keeps one immutable, checksum-pinned verifier and uses it to authenticate
the signed stable updater channel before replacing CobbleMusicUpdater.exe.
Neither an untrusted branch pointer nor a GitHub release asset can authorize an
executable by itself.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstanceDirectory,

    [switch]$Force,

    [switch]$PrismPreLaunch
)

$ErrorActionPreference = 'Stop'
$Repository = 'Kewz4/Cobble-Music'
$VerifierVersion = '1.2.7'
# SHA-256 of CobbleMusicUpdater.exe from updater-v1.2.7. The release publisher
# replaces this placeholder only when staging the verifier generation.
$ExpectedVerifierSha256 = '18FCEB47A948632B3AA5C0C44D3E532C88253AD3989ED52567CB2E55FF85AE44'
$DownloadTimeoutSeconds = 30
$MaximumChannelBytes = 16KB
$MaximumSignatureBytes = 4KB
$preLaunch = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'
$instance = [IO.Path]::GetFullPath($InstanceDirectory)
$minecraft = Join-Path $instance 'minecraft'
$instanceConfig = Join-Path $instance 'instance.cfg'
$targetDirectory = Join-Path $minecraft 'cobble-music-updater'
$targetExe = Join-Path $targetDirectory 'CobbleMusicUpdater.exe'
$verifierExe = Join-Path $targetDirectory "CobbleMusicUpdaterVerifier-$VerifierVersion.exe"
$cachedChannelPath = Join-Path $targetDirectory 'installed-updater-channel.json'
$cachedSignaturePath = Join-Path $targetDirectory 'installed-updater-channel.sig'
$configPath = Join-Path $targetDirectory 'updater.json'
$verifierAssetUri = "https://github.com/$Repository/releases/download/updater-v$VerifierVersion/CobbleMusicUpdater.exe"
$channelUri = "https://raw.githubusercontent.com/$Repository/main/updater/channel/stable.json"
$channelSignatureUri = "https://raw.githubusercontent.com/$Repository/main/updater/channel/stable.sig"

function Move-FileAtomically([string]$Source, [string]$Destination) {
    $replacementBackup = $null
    try {
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            $replacementBackup = "$Destination.replaced-$([Guid]::NewGuid().ToString('N'))"
            [IO.File]::Replace($Source, $Destination, $replacementBackup)
        }
        else {
            [IO.File]::Move($Source, $Destination)
        }
    }
    finally {
        if ($replacementBackup -and (Test-Path -LiteralPath $replacementBackup)) {
            Remove-Item -LiteralPath $replacementBackup -Force
        }
    }
}

function Write-Utf8Atomically([string]$Path, [string]$Text) {
    $temporary = "$Path.new-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText($temporary, $Text, [Text.UTF8Encoding]::new($false))
        Move-FileAtomically $temporary $Path
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Copy-FileAtomically([string]$Source, [string]$Destination) {
    $temporary = "$Destination.new-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::Copy($Source, $temporary, $true)
        Move-FileAtomically $temporary $Destination
        $temporary = $null
    }
    finally {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Force }
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

function Assert-PrismPreLaunchContext([string]$ExpectedInstance, [string]$ExpectedMinecraft) {
    $prismInstance = [Environment]::GetEnvironmentVariable('INST_DIR')
    $prismMinecraft = [Environment]::GetEnvironmentVariable('INST_MC_DIR')
    if ([string]::IsNullOrWhiteSpace($prismInstance) -or [string]::IsNullOrWhiteSpace($prismMinecraft)) {
        throw 'PrismPreLaunch mode requires Prism Launcher INST_DIR and INST_MC_DIR environment variables.'
    }

    $actualInstance = [IO.Path]::GetFullPath($prismInstance).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actualMinecraft = [IO.Path]::GetFullPath($prismMinecraft).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $expectedInstancePath = $ExpectedInstance.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $expectedMinecraftPath = $ExpectedMinecraft.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $actualInstance.Equals($expectedInstancePath, [StringComparison]::OrdinalIgnoreCase) `
        -or -not $actualMinecraft.Equals($expectedMinecraftPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PrismPreLaunch paths do not match the instance selected by Prism Launcher.'
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
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 2) { return $false }
        return $stream.ReadByte() -eq 0x4d -and $stream.ReadByte() -eq 0x5a
    }
    finally {
        $stream.Dispose()
    }
}

function Get-CanonicalVersion([string]$Value, [string]$Context) {
    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Context is not a canonical three-part version: $Value"
    }
    try { $parsed = [Version]::new($Value) }
    catch { throw "$Context is outside the supported version range: $Value" }
    if ($parsed.ToString(3) -cne $Value) { throw "$Context is not canonical: $Value" }
    return $parsed
}

function Test-ExactExecutable([string]$Path, [int64]$ExpectedSize, [string]$ExpectedSha256) {
    if (-not (Test-WindowsExecutable $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $ExpectedSize) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals(
        $ExpectedSha256,
        [StringComparison]::OrdinalIgnoreCase)
}

function Install-PinnedVerifier {
    if ((Test-Path -LiteralPath $verifierExe -PathType Leaf) `
        -and (Get-FileHash -LiteralPath $verifierExe -Algorithm SHA256).Hash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-WindowsExecutable $verifierExe)) {
        return
    }

    if ((Test-Path -LiteralPath $targetExe -PathType Leaf) `
        -and (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-WindowsExecutable $targetExe)) {
        Copy-FileAtomically $targetExe $verifierExe
        return
    }

    $download = "$verifierExe.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Downloading the pinned updater verifier v$VerifierVersion..."
        Invoke-WebRequest -UseBasicParsing -Uri $verifierAssetUri -OutFile $download -TimeoutSec $DownloadTimeoutSeconds
        $actualHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
        if (-not $actualHash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Pinned updater verifier checksum mismatch. Expected $ExpectedVerifierSha256 but downloaded $actualHash."
        }
        if (-not (Test-WindowsExecutable $download)) { throw 'The pinned updater verifier is not a Windows executable.' }
        Move-FileAtomically $download $verifierExe
        $download = $null
    }
    finally {
        if ($download -and (Test-Path -LiteralPath $download)) { Remove-Item -LiteralPath $download -Force }
    }
}

function Invoke-ChannelVerifier([string]$DescriptorPath, [string]$SignaturePath) {
    $verifiedOutput = Join-Path $targetDirectory ('.verified-updater-channel-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $arguments = '--verify-updater-channel "{0}" --signature-file "{1}" --verified-output "{2}"' -f `
            $DescriptorPath.Replace('"', '""'), $SignaturePath.Replace('"', '""'), $verifiedOutput.Replace('"', '""')
        $process = Start-Process -FilePath $verifierExe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $verifiedOutput -PathType Leaf)) {
            throw 'The pinned verifier rejected the updater channel signature or metadata.'
        }
        $verifiedText = [IO.File]::ReadAllText($verifiedOutput)
        $channel = $verifiedText | ConvertFrom-Json
        $null = Get-CanonicalVersion ([string]$channel.updaterVersion) 'Verified updater channel version'
        return $channel
    }
    finally {
        if (Test-Path -LiteralPath $verifiedOutput) { Remove-Item -LiteralPath $verifiedOutput -Force }
    }
}

function Test-UpdaterMatchesChannel([object]$Channel) {
    if ($null -eq $Channel -or $null -eq $Channel.updater) { return $false }
    return Test-ExactExecutable $targetExe ([int64]$Channel.updater.size) ([string]$Channel.updater.sha256)
}

function Get-CachedChannel {
    if (-not (Test-Path -LiteralPath $cachedChannelPath -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $cachedSignaturePath -PathType Leaf)) {
        return $null
    }
    try {
        $channel = Invoke-ChannelVerifier $cachedChannelPath $cachedSignaturePath
        if (Test-UpdaterMatchesChannel $channel) { return $channel }
    }
    catch {
        Write-Host "Ignoring an invalid cached updater channel: $($_.Exception.Message)"
    }
    return $null
}

function Install-UpdaterFromChannel([object]$Channel) {
    $expectedSize = [int64]$Channel.updater.size
    $expectedHash = [string]$Channel.updater.sha256
    if ($expectedHash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase) `
        -and (Get-Item -LiteralPath $verifierExe).Length -eq $expectedSize) {
        Copy-FileAtomically $verifierExe $targetExe
        return
    }

    $assetUri = "https://github.com/$Repository/releases/download/$($Channel.releaseTag)/$($Channel.updater.name)"
    $download = "$targetExe.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Downloading verified updater v$($Channel.updaterVersion)..."
        Invoke-WebRequest -UseBasicParsing -Uri $assetUri -OutFile $download -TimeoutSec $DownloadTimeoutSeconds
        if (-not (Test-ExactExecutable $download $expectedSize $expectedHash)) {
            throw 'Downloaded updater does not match the signed channel size, SHA-256, or executable format.'
        }
        Move-FileAtomically $download $targetExe
        $download = $null
    }
    finally {
        if ($download -and (Test-Path -LiteralPath $download)) { Remove-Item -LiteralPath $download -Force }
    }
}

function Save-ChannelCache([string]$DescriptorPath, [string]$SignaturePath) {
    Copy-FileAtomically $DescriptorPath $cachedChannelPath
    Copy-FileAtomically $SignaturePath $cachedSignaturePath
}

function Get-CurrentUpdater {
    Install-PinnedVerifier
    $floorVersion = Get-CanonicalVersion $VerifierVersion 'Pinned verifier version'
    $currentChannel = Get-CachedChannel
    $currentVersion = if ($null -ne $currentChannel) {
        Get-CanonicalVersion ([string]$currentChannel.updaterVersion) 'Cached updater channel version'
    }
    else { $floorVersion }

    $remoteDescriptor = Join-Path $targetDirectory ('.updater-channel-' + [Guid]::NewGuid().ToString('N') + '.json')
    $remoteSignature = Join-Path $targetDirectory ('.updater-channel-' + [Guid]::NewGuid().ToString('N') + '.sig')
    try {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $channelUri -OutFile $remoteDescriptor -TimeoutSec $DownloadTimeoutSeconds
            Invoke-WebRequest -UseBasicParsing -Uri $channelSignatureUri -OutFile $remoteSignature -TimeoutSec $DownloadTimeoutSeconds
            if ((Get-Item -LiteralPath $remoteDescriptor).Length -gt $MaximumChannelBytes `
                -or (Get-Item -LiteralPath $remoteSignature).Length -gt $MaximumSignatureBytes) {
                throw 'Downloaded updater channel metadata exceeds its size limit.'
            }
            $remoteChannel = Invoke-ChannelVerifier $remoteDescriptor $remoteSignature
            $remoteVersion = Get-CanonicalVersion ([string]$remoteChannel.updaterVersion) 'Remote updater channel version'
            if ($remoteVersion -lt $floorVersion) {
                throw "Signed updater channel is older than the pinned verifier floor v$VerifierVersion."
            }
            if ($remoteVersion -lt $currentVersion) {
                Write-Host "Ignoring replayed updater channel v$remoteVersion; v$currentVersion is already trusted."
            }
            elseif ($remoteVersion -eq $currentVersion -and $null -ne $currentChannel -and (
                ([string]$remoteChannel.updater.sha256 -cne [string]$currentChannel.updater.sha256) -or
                ([int64]$remoteChannel.updater.size -ne [int64]$currentChannel.updater.size))) {
                throw "Signed updater channel reused version $remoteVersion with different executable metadata."
            }
            else {
                if (-not (Test-UpdaterMatchesChannel $remoteChannel)) {
                    Install-UpdaterFromChannel $remoteChannel
                }
                if (-not (Test-UpdaterMatchesChannel $remoteChannel)) {
                    throw 'Installed updater failed verification after atomic replacement.'
                }
                Save-ChannelCache $remoteDescriptor $remoteSignature
                $currentChannel = $remoteChannel
                $currentVersion = $remoteVersion
            }
        }
        catch {
            Write-Host "Could not adopt a newer updater; retaining the last verified version: $($_.Exception.Message)"
        }
    }
    finally {
        if (Test-Path -LiteralPath $remoteDescriptor) { Remove-Item -LiteralPath $remoteDescriptor -Force }
        if (Test-Path -LiteralPath $remoteSignature) { Remove-Item -LiteralPath $remoteSignature -Force }
    }

    if ($null -ne $currentChannel -and (Test-UpdaterMatchesChannel $currentChannel)) {
        return
    }
    if ((Test-Path -LiteralPath $targetExe -PathType Leaf) `
        -and (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-WindowsExecutable $targetExe)) {
        return
    }
    Copy-FileAtomically $verifierExe $targetExe
    if (-not (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash.Equals($ExpectedVerifierSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Pinned updater fallback failed checksum verification.'
    }
}

if ($PrismPreLaunch -and $Force) {
    throw '-Force is not valid in PrismPreLaunch mode because that mode never edits instance.cfg.'
}
foreach ($path in @($instance, $minecraft)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Expected Prism instance path is missing: $path"
    }
}
if (-not (Test-Path -LiteralPath $instanceConfig -PathType Leaf)) {
    throw "Prism instance configuration is missing: $instanceConfig"
}
if ($PrismPreLaunch) {
    Assert-PrismPreLaunchContext $instance $minecraft
}
else {
    Assert-PrismLauncherNotRunning
    $instanceText = [IO.File]::ReadAllText($instanceConfig)
    $lineEnding = if ($instanceText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = @($instanceText -split "`r?`n")
    $existingPreLaunchValues = @(Get-InstanceSettingValues $lines 'PreLaunchCommand')
    $conflictingPreLaunchValues = @($existingPreLaunchValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -cne $preLaunch })
    if ($conflictingPreLaunchValues.Count -gt 0 -and -not $Force) {
        throw "This Prism instance already has a different PreLaunchCommand. The bootstrap will not replace it. Review it and rerun with -Force only if replacement is intentional. Existing command: $($conflictingPreLaunchValues[0])"
    }
}

function Ensure-UpdaterConfiguration {
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
    $configurationText = $configuration | ConvertTo-Json -Depth 8
    $configurationChanged = -not (Test-Path -LiteralPath $configPath -PathType Leaf) `
        -or [IO.File]::ReadAllText($configPath) -cne $configurationText
    if ($configurationChanged) {
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            $configBackup = "$configPath.cobble-music-updater-$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
            Copy-Item -LiteralPath $configPath -Destination $configBackup
            Write-Host "Backed up existing updater configuration: $configBackup"
        }
        Write-Utf8Atomically $configPath $configurationText
    }
}

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Assert-WritableDirectory $targetDirectory
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if ($PrismPreLaunch) {
    Write-Host "Running the verified Kewz's Cobblemon updater for this launch..."
    try {
        Get-CurrentUpdater
        Ensure-UpdaterConfiguration
        $quotedInstance = $instance.Replace('"', '""')
        $quotedMinecraft = $minecraft.Replace('"', '""')
        $updaterArguments = '--instance-dir "{0}" --minecraft-dir "{1}" --prism-prelaunch' -f $quotedInstance, $quotedMinecraft
        $updaterProcess = Start-Process -FilePath $targetExe -ArgumentList $updaterArguments -PassThru -Wait
        if ($updaterProcess.ExitCode -ne 0) {
            Write-Host "The Kewz's Cobblemon updater returned code $($updaterProcess.ExitCode)."
            Write-Host 'Continuing launch and letting Minecraft start with current pack contents.'
        }
    }
    catch {
        Write-Host "Skipping updater launch and continuing launch: $($_.Exception.Message)"
        return
    }
    return
}

Get-CurrentUpdater
Ensure-UpdaterConfiguration

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
