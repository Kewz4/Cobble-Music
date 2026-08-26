[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$UpdaterDll = Join-Path $Root 'updater\CobbleMusicUpdater\bin\Release\net10.0-windows\win-x64\CobbleMusicUpdater.dll'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cobble-music-recovery-test-" + [Guid]::NewGuid().ToString('N'))

function Get-LocalDataDirectory([string]$InstanceDirectory) {
    $fullInstance = [IO.Path]::GetFullPath($InstanceDirectory)
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fullInstance))
    $suffix = [Convert]::ToHexString($hash).ToLowerInvariant().Substring(0, 16)
    return Join-Path (Join-Path $env:LOCALAPPDATA 'CobbleMusicUpdater') $suffix
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

try {
    & dotnet restore $Project --locked-mode | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater restore failed with exit code $LASTEXITCODE" }
    & dotnet build $Project --configuration Release --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater build failed with exit code $LASTEXITCODE" }

    $instance = Join-Path $TempRoot 'instance'
    $minecraft = Join-Path $instance 'minecraft'
    $target = Join-Path $minecraft 'mods\example.jar'
    $localData = Get-LocalDataDirectory $instance
    $backup = Join-Path $localData 'rollback\test-transaction\files\mods\example.jar'
    New-Item -ItemType Directory -Path (Split-Path -Parent $target), (Split-Path -Parent $backup) -Force | Out-Null
    Write-Utf8 $target 'new-content'
    Write-Utf8 $backup 'old-content'
    $journal = [ordered]@{
        schemaVersion = 1
        committed = $false
        operations = @([ordered]@{ kind='replace'; targetPath=$target; backupPath=$backup })
    }
    New-Item -ItemType Directory -Path $localData -Force | Out-Null
    $journalPath = Join-Path $localData 'transaction.json'
    Write-Utf8 $journalPath ($journal | ConvertTo-Json -Depth 5)

    & dotnet $UpdaterDll --instance-dir $instance --minecraft-dir $minecraft --prism-prelaunch --no-ui | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Valid rollback test exited with $LASTEXITCODE" }
    if ((Get-Content -Raw -LiteralPath $target) -ne 'old-content') { throw 'Interrupted replacement was not restored from the rollback copy.' }
    if (Test-Path -LiteralPath $journalPath) { throw 'Recovered transaction journal was not removed.' }

    $lockPath = Join-Path $localData 'update.lock'
    $heldLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        & dotnet $UpdaterDll --instance-dir $instance --minecraft-dir $minecraft --prism-prelaunch --no-ui 2>$null
        if ($LASTEXITCODE -eq 0) { throw 'A concurrent updater lock unexpectedly allowed a Prism launch.' }
    }
    finally {
        $heldLock.Dispose()
    }

    $outside = Join-Path $TempRoot 'outside.txt'
    Write-Utf8 $outside 'do-not-touch'
    $unsafeJournal = [ordered]@{
        schemaVersion = 1
        committed = $false
        operations = @([ordered]@{ kind='delete'; targetPath=$outside; backupPath=$backup })
    }
    Write-Utf8 $journalPath ($unsafeJournal | ConvertTo-Json -Depth 5)
    & dotnet $UpdaterDll --instance-dir $instance --minecraft-dir $minecraft --prism-prelaunch --no-ui 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Unsafe transaction journal unexpectedly allowed a Prism launch.' }
    if ((Get-Content -Raw -LiteralPath $outside) -ne 'do-not-touch') { throw 'Unsafe journal changed a file outside the Minecraft directory.' }
    if (-not (Test-Path -LiteralPath $journalPath)) { throw 'Unsafe journal was removed instead of retained for repair.' }

    Write-Host 'Transaction recovery and journal containment checks passed.'
    $global:LASTEXITCODE = 0
}
finally {
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force }
    if ($localData -and (Test-Path -LiteralPath $localData)) { Remove-Item -LiteralPath $localData -Recurse -Force }
}
