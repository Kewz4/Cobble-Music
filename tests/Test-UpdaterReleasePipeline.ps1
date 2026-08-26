[CmdletBinding()]
param(
    [string]$BuildInfoPath,
    [string]$ProjectPath,
    [string]$UpdaterExePath,
    [string]$BootstrapPath,
    [string]$ExpectedVersion,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$ExpectedRepository = 'Kewz4/Cobble-Music'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildInfoPath)) {
    $BuildInfoPath = Join-Path $Root 'updater\CobbleMusicUpdater\BuildInfo.cs'
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
}
if ([string]::IsNullOrWhiteSpace($UpdaterExePath)) {
    $UpdaterExePath = Join-Path $Root 'updater\dist\win-x64\CobbleMusicUpdater.exe'
}
if ([string]::IsNullOrWhiteSpace($BootstrapPath)) {
    $BootstrapPath = Join-Path $Root 'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
}

function Get-SingleQuotedAssignment([string]$Text, [string]$Name) {
    $pattern = '(?m)^\s*' + [regex]::Escape('$' + $Name) + '\s*=\s*''(?<value>[^''\r\n]*)''\s*$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one `$${Name} assignment, found $($matches.Count)." }
    return $matches[0].Groups['value'].Value
}

foreach ($path in @($BuildInfoPath, $ProjectPath, $UpdaterExePath, $BootstrapPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Updater release test input is missing: $path" }
}

$buildInfo = [IO.File]::ReadAllText($BuildInfoPath)
$versionMatches = [regex]::Matches(
    $buildInfo,
    '(?m)^\s*public\s+const\s+string\s+Version\s*=\s*"(?<version>[^"]+)"\s*;\s*$')
if ($versionMatches.Count -ne 1) { throw "Expected one BuildInfo.Version, found $($versionMatches.Count)." }
$sourceVersion = $versionMatches[0].Groups['version'].Value
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) { $ExpectedVersion = $sourceVersion }
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$' -or $sourceVersion -cne $ExpectedVersion) {
    throw "Updater source version mismatch. BuildInfo=$sourceVersion expected=$ExpectedVersion"
}

[xml]$project = [IO.File]::ReadAllText($ProjectPath)
$projectVersionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($projectVersionNodes.Count -ne 1 -or $projectVersionNodes[0].InnerText.Trim() -cne $ExpectedVersion) {
    throw 'The project Version does not exactly match BuildInfo.Version.'
}

$exe = Get-Item -LiteralPath $UpdaterExePath
if ($exe.Length -lt 1MB) { throw "Updater executable is unexpectedly small: $($exe.Length) bytes." }
$stream = [IO.File]::OpenRead($exe.FullName)
try {
    if ($stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) { throw 'Updater output is not a Windows PE executable.' }
}
finally { $stream.Dispose() }

try {
    $expectedAssemblyVersion = [Version]$ExpectedVersion
    $actualFileVersion = [Version]$exe.VersionInfo.FileVersion
}
catch { throw "Updater FileVersion is invalid: $($exe.VersionInfo.FileVersion)" }
if ($actualFileVersion.Major -ne $expectedAssemblyVersion.Major `
    -or $actualFileVersion.Minor -ne $expectedAssemblyVersion.Minor `
    -or $actualFileVersion.Build -ne $expectedAssemblyVersion.Build) {
    throw "Built updater FileVersion $actualFileVersion does not match BuildInfo version $ExpectedVersion."
}

$exeHash = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
$bootstrap = [IO.File]::ReadAllText($BootstrapPath)
$bootstrapRepository = Get-SingleQuotedAssignment $bootstrap 'Repository'
$bootstrapVersion = Get-SingleQuotedAssignment $bootstrap 'UpdaterVersion'
$bootstrapHash = Get-SingleQuotedAssignment $bootstrap 'ExpectedUpdaterSha256'
if ($bootstrapRepository -cne $ExpectedRepository) { throw "Bootstrap repository mismatch: $bootstrapRepository" }
if ($bootstrapVersion -cne $ExpectedVersion) { throw "Bootstrap version mismatch: $bootstrapVersion" }
if (-not $bootstrapHash.Equals($exeHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Bootstrap updater hash does not match the built EXE. Bootstrap=$bootstrapHash EXE=$exeHash"
}
if ($bootstrapHash -cnotmatch '^[0-9A-F]{64}$') { throw 'Bootstrap updater checksum must be canonical uppercase SHA-256.' }

$expectedAssetUri = 'https://github.com/$Repository/releases/download/updater-v$UpdaterVersion/CobbleMusicUpdater.exe'
$assetMatches = [regex]::Matches(
    $bootstrap,
    '(?m)^\s*\$assetUri\s*=\s*"(?<uri>[^"]+)"\s*$')
if ($assetMatches.Count -ne 1 -or $assetMatches[0].Groups['uri'].Value -cne $expectedAssetUri) {
    throw 'Bootstrap updater asset URI no longer binds its repository and version variables safely.'
}

$expectedCommand = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'
$preLaunchMatches = [regex]::Matches($bootstrap, '(?m)^\$preLaunch = ''(?<command>.*)''$')
if ($preLaunchMatches.Count -ne 1 -or $preLaunchMatches[0].Groups['command'].Value -cne $expectedCommand) {
    throw 'Bootstrap Prism pre-launch command is not QSettings-escaped correctly.'
}

Write-Host "Updater release pipeline checks passed for updater-v$ExpectedVersion ($exeHash)."
