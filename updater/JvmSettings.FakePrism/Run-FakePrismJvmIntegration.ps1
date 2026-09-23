<#
1.2.18 jvm track: integration test 1 and the guard tests of workspace/shared/updater0923/jvm-args/FINDINGS.md section 11,
against a FAKE prismlauncher.exe (JvmSettings.FakePrism) in a throwaway folder. Written for the integrator; not run by the
publisher (it is not a tests/Test-*.ps1 script) and never touches a real Prism install, instance or Minecraft.

What it does:
1. Builds CobbleMusicUpdater (Release) and the fake Prism.
2. Lays out a portable fake Prism folder under -WorkRoot: portable.txt, the sanitized prismlauncher.cfg fixture,
   instances/kewz/instance.cfg (the "bedrock" fixture: OverrideMemory=false, so memory AND flags are missing), the updater
   copied into instances/kewz/minecraft/cobble-music-updater, and an updater.json whose repository does not exist, so the
   release check fails like an outage and the updater takes the offline-fallback path (exit 0) without downloading a pack.
3. Sets PreLaunchCommand to either
   - PowerShell (default): Windows PowerShell 5.1 running a copy of bootstrap lines 403-416 (Start-Process -PassThru -Wait,
     exit code ignored, always returns 0) - the chain prismlauncher -> powershell -> updater of the pinned friend command;
   - Direct: the legacy "$INST_MC_DIR/.../CobbleMusicUpdater.exe" --prism-prelaunch command (prismlauncher -> updater).
4. Starts the fake Prism with --launch kewz and waits for: "Pre-Launch command failed with code 3." from the first Prism
   process, a second Prism process started (by Explorer) with --launch kewz, and "Pre-Launch command ran successfully."
5. Asserts the instance.cfg edit, the backup, the logs folder and the marker state (verified), then stops every process
   started from -WorkRoot.
With -DummyGame the fake Prism also starts a child that stands in for a running game: the updater must then defer (no code 3,
no restart, instance.cfg unchanged).
#>
[CmdletBinding()]
param(
    [ValidateSet('PowerShell', 'Direct')]
    [string]$Chain = 'PowerShell',
    [switch]$DummyGame,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ("cm-jvm-fakeprism-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$repoUpdater = Split-Path -Parent $PSScriptRoot
$fixtures = Join-Path $repoUpdater 'CobbleMusicUpdater.Tests\Fixtures\Jvm'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
foreach ($forbidden in @('C:\Program Files\Prism Launcher', (Join-Path $env:APPDATA 'PrismLauncher'))) {
    if ($WorkRoot.StartsWith($forbidden, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to use a real Prism folder: $WorkRoot" }
}
if (Test-Path -LiteralPath $WorkRoot) { throw "WorkRoot already exists: $WorkRoot" }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
    Write-Host "ok - $Message"
}

function ConvertTo-QtIniString([string]$Value) {
    # QSettingsPrivate::iniEscapedString for the characters these commands contain.
    $escaped = $Value.Replace('\', '\\').Replace('"', '\"')
    if ($escaped -match '[;,=]' -or $escaped.StartsWith(' ') -or $escaped.EndsWith(' ')) { $escaped = '"' + $escaped + '"' }
    return $escaped
}

Write-Host "Building the updater and the fake Prism..."
& dotnet build (Join-Path $repoUpdater 'CobbleMusicUpdater\CobbleMusicUpdater.csproj') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
& dotnet build (Join-Path $PSScriptRoot 'JvmSettings.FakePrism.csproj') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Fake Prism build failed.' }
$updaterBin = Join-Path $repoUpdater 'CobbleMusicUpdater\bin\Release\net10.0-windows\win-x64'
$fakeBin = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64'

$prismDir = Join-Path $WorkRoot 'Prism'
$instanceDir = Join-Path $prismDir 'instances\kewz'
$minecraftDir = Join-Path $instanceDir 'minecraft'
$updaterDir = Join-Path $minecraftDir 'cobble-music-updater'
New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null
Copy-Item -Path (Join-Path $fakeBin '*') -Destination $prismDir -Recurse
Copy-Item -Path (Join-Path $updaterBin '*') -Destination $updaterDir -Recurse
Set-Content -LiteralPath (Join-Path $prismDir 'portable.txt') -Value '' -NoNewline
# Harness-only marker: the fake Prism keeps every pre-launch child offline (dead proxy), including after the
# helper's Explorer relaunch, so the updater deterministically takes the offline-fallback path.
Set-Content -LiteralPath (Join-Path $prismDir 'cm-fake-dead-proxy.txt') -Value '' -NoNewline
Copy-Item -LiteralPath (Join-Path $fixtures 'prismlauncher.cfg') -Destination (Join-Path $prismDir 'prismlauncher.cfg')
@{
    schemaVersion = 1; modpackId = 'cobble-music'; repository = 'cm-jvm-integration-invalid-owner/does-not-exist'; channel = 'stable'
    manifestAsset = 'cobble-music-update.json'; signatureAsset = 'cobble-music-update.sig'; networkTimeoutSeconds = 10
    allowOfflineLaunch = $true; allowedRoots = @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $updaterDir 'updater.json') -Encoding utf8

if ($Chain -eq 'PowerShell') {
    $wrapper = Join-Path $WorkRoot 'prelaunch-wrapper.ps1'
    @'
# Bootstrap-CobbleMusicUpdater.ps1 lines 403-416 (PrismPreLaunch branch), unchanged in behaviour: the exit code is ignored.
$ErrorActionPreference = 'Stop'
$instance = [IO.Path]::GetFullPath($env:INST_DIR)
$minecraft = Join-Path $instance 'minecraft'
$targetExe = Join-Path $minecraft 'cobble-music-updater\CobbleMusicUpdater.exe'
try {
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
'@ | Set-Content -LiteralPath $wrapper -Encoding utf8
    $preLaunch = "powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$wrapper`""
}
else {
    $preLaunch = '"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe" --instance-dir "$INST_DIR" --minecraft-dir "$INST_MC_DIR" --prism-prelaunch'
}

$cfgText = [IO.File]::ReadAllText((Join-Path $fixtures 'instance-bedrock.cfg'))
$cfgText = $cfgText.Replace("`r`nOverrideCommands=false`r`n", "`r`nOverrideCommands=true`r`n")
$cfgText = $cfgText.Replace("`r`nPreLaunchCommand=`r`n", "`r`nPreLaunchCommand=$(ConvertTo-QtIniString $preLaunch)`r`n")
$instanceCfg = Join-Path $instanceDir 'instance.cfg'
[IO.File]::WriteAllText($instanceCfg, $cfgText, [Text.UTF8Encoding]::new($false))
$originalCfg = [IO.File]::ReadAllBytes($instanceCfg)

$identity = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($instanceDir)).ToUpperInvariant()
$hash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '').ToLowerInvariant()).Substring(0, 16)
$localData = Join-Path $env:LOCALAPPDATA "CobbleMusicUpdater\$hash"
$markerPath = Join-Path $localData 'jvm-settings.json'
$fakeLog = Join-Path $prismDir 'fake-prism.log'
$updaterLog = Join-Path $updaterDir 'updater.log'

$fakeArguments = @('--launch', 'kewz')
if ($DummyGame) {
    # via the environment: an extra command-line argument would trip the updater's relaunch guard first
    $env:CM_FAKE_DUMMY_GAME_SECONDS = '600'
}
Write-Host "Starting the fake Prism ($Chain chain) in $WorkRoot ..."
Start-Process -FilePath (Join-Path $prismDir 'prismlauncher.exe') -ArgumentList $fakeArguments | Out-Null

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$done = $false
while (-not $done -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 2
    $text = if (Test-Path -LiteralPath $fakeLog) { [IO.File]::ReadAllText($fakeLog) } else { '' }
    if ($DummyGame) { $done = $text.Contains('Pre-Launch command ran successfully.') }
    else { $done = $text.Contains('Pre-Launch command ran successfully.') -and $text.Contains('failed with code 3.') }
}

Write-Host '----- fake-prism.log -----'
if (Test-Path -LiteralPath $fakeLog) { Get-Content -LiteralPath $fakeLog }
Write-Host '----- updater.log (memory settings lines) -----'
if (Test-Path -LiteralPath $updaterLog) { Get-Content -LiteralPath $updaterLog | Where-Object { $_ -match 'Memory settings|Stopped the pre-launch|Prism runs the updater' } }
Write-Host '----- marker -----'
if (Test-Path -LiteralPath $markerPath) { Get-Content -LiteralPath $markerPath }

try {
    Assert-True $done "the expected launch sequence completed within $TimeoutSeconds s"
    $log = [IO.File]::ReadAllText($fakeLog)
    if ($DummyGame) {
        Assert-True (-not $log.Contains('failed with code 3.')) 'a running game: the launch was not stopped'
        Assert-True ([IO.File]::ReadAllBytes($instanceCfg).Length -eq $originalCfg.Length -and [Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($instanceCfg), [byte[]]$originalCfg)) 'a running game: instance.cfg unchanged'
        Assert-True ((Get-Content -LiteralPath $updaterLog -Raw).Contains('deferred')) 'a running game: the updater deferred'
    }
    else {
        $starts = @([regex]::Matches($log, 'pid=(\d+) started:') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        Assert-True ($starts.Count -ge 2) 'Prism was started a second time'
        Assert-True ($log.IndexOf('failed with code 3.') -lt $log.LastIndexOf('Pre-Launch command ran successfully.')) 'first launch failed with code 3, the relaunch succeeded'
        $cfg = [IO.File]::ReadAllText($instanceCfg)
        Assert-True ($cfg.Contains("`r`nOverrideMemory=true`r`n")) 'OverrideMemory=true written'
        Assert-True ($cfg -match "`r`nMaxMemAlloc=(7168|8192|10000|12288)`r`n") 'MaxMemAlloc raised to the tier value'
        Assert-True ($cfg.Contains('-XX:+UseG1GC') -and $cfg.Contains('file=logs/gc.log')) 'JvmArgs carry the policy flags'
        Assert-True (@(Get-ChildItem -LiteralPath $instanceDir -Filter 'instance.cfg.cobble-music-jvm-*.bak').Count -eq 1) 'one backup'
        Assert-True (Test-Path -LiteralPath (Join-Path $minecraftDir 'logs') -PathType Container) 'logs folder exists'
        Assert-True ((Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json).state -eq 'verified') 'marker verified after the relaunch'
        Assert-True ($log.Contains('INST_JAVA_ARGS=') -and $log -match 'INST_JAVA_ARGS=.*-XX:\+UseG1GC.*-Xmx(7168|8192|10000|12288)m') 'the relaunch used the new arguments'
    }
    Write-Host 'Fake-Prism jvm-args integration passed.'
}
finally {
    Remove-Item -Path 'Env:CM_FAKE_DUMMY_GAME_SECONDS' -ErrorAction SilentlyContinue
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($WorkRoot, [StringComparison]::OrdinalIgnoreCase) })) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}
