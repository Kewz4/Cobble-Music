[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$Output = Join-Path $Root "updater\dist\$Runtime"
$Staging = Join-Path $Root ("updater\publish-staging\$Runtime-" + [Guid]::NewGuid().ToString('N'))
$StagedExe = Join-Path $Staging 'CobbleMusicUpdater.exe'
$TargetExe = Join-Path $Output 'CobbleMusicUpdater.exe'

try {
    New-Item -ItemType Directory -Path $Staging -Force | Out-Null
    & dotnet publish $Project --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded --output $Staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $StagedExe -PathType Leaf)) { throw "dotnet publish did not create $StagedExe" }

    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    # Publishing into a fresh staging folder avoids the .NET single-file
    # bundler reopening a previous EXE. Same-volume File.Move makes the final
    # replacement atomic when no updater process is running.
    [IO.File]::Move($StagedExe, $TargetExe, $true)
    Write-Host "Built self-contained updater: $TargetExe"
}
finally {
    if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
}
