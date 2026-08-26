[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$expectedCommand = '\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch'

foreach ($relativePath in @(
    'tools\Install-CobbleMusicUpdater.ps1',
    'bootstrap\Bootstrap-CobbleMusicUpdater.ps1'
)) {
    $path = Join-Path $Root $relativePath
    $text = Get-Content -LiteralPath $path -Raw
    $match = [regex]::Match($text, '(?m)^\$preLaunch = ''(?<command>.*)''$')
    if (-not $match.Success) {
        throw "Could not find the Prism pre-launch command in $relativePath"
    }
    if ($match.Groups['command'].Value -cne $expectedCommand) {
        throw "Prism command in $relativePath is not QSettings-escaped correctly. It must retain escaped quotes around each path variable."
    }
}

Write-Host 'Prism pre-launch command encoding checks passed.'
