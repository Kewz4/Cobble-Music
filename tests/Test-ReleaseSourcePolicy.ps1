$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'tools\Test-CobbleMusicReleasePolicy.ps1')
