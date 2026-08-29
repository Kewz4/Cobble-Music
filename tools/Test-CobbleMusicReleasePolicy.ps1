[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'CobbleMusicRelease.Core.psm1'
Import-Module $modulePath -Force

function Write-DummyFile([string]$Path, [byte[]]$Bytes = ([byte[]](1, 2, 3))) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Assert-Rejected([scriptblock]$Action, [string]$Description) {
    $rejected = $false
    try { & $Action }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Source policy accepted $Description." }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase ("cobble-music-source-policy-" + [guid]::NewGuid().ToString('N'))))
$temporaryPrefix = $temporaryBase + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a policy fixture outside the temporary directory: $testRoot"
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Write-DummyFile (Join-Path $testRoot 'mods\approved.jar')
    Write-DummyFile (Join-Path $testRoot 'mods\approved.jar.disabled')
    Write-DummyFile (Join-Path $testRoot 'mods\legacy.jar.disabled-by-cobble-music')
    Write-DummyFile (Join-Path $testRoot 'mods\mcef-cache\Network\Cookies')
    Write-DummyFile (Join-Path $testRoot 'mods\mcef-libraries\bin\runtime.dll')
    Write-DummyFile (Join-Path $testRoot 'mods\.index\generated.json')
    Write-DummyFile (Join-Path $testRoot 'resourcepacks\approved.zip')
    Write-DummyFile (Join-Path $testRoot 'resourcepacks\reviewed.zip.rpo')
    Write-DummyFile (Join-Path $testRoot 'resourcepacks\backup.zip.bak')
    Write-DummyFile (Join-Path $testRoot 'resourcepacks\.index\generated.json')
    Write-DummyFile (Join-Path $testRoot 'config\ReactiveMusic.json5')
    Write-DummyFile (Join-Path $testRoot 'config\iris.properties')
    Write-DummyFile (Join-Path $testRoot 'config\fancymenu\layouts\main.txt')
    Write-DummyFile (Join-Path $testRoot 'config\inventory-particles\cache\generated.bin')
    Write-DummyFile (Join-Path $testRoot 'config\example.json.bak')
    Write-DummyFile (Join-Path $testRoot 'config\dreamdisplays\config.toml')
    Write-DummyFile (Join-Path $testRoot 'config\MCBrowser\tabs.json')
    Write-DummyFile (Join-Path $testRoot 'config\packed_packs\__version.json')
    Write-DummyFile (Join-Path $testRoot 'options.txt')
    Write-DummyFile (Join-Path $testRoot 'mods\Axiom-5.4.2-for-MC1.21.1.jar')
    $templateRoot = Join-Path $testRoot 'reviewed-templates'
    Write-DummyFile (Join-Path $templateRoot 'config\ReactiveMusic.json5') ([byte[]](9, 8, 7))

    $files = @(Get-CobbleManagedSourceFiles -SourceMinecraftDir $testRoot `
        -IncludeRoots @('mods', 'resourcepacks') `
        -IncludeFiles @('config/ReactiveMusic.json5', 'resourcepacks/reviewed.zip.rpo') `
        -AllowedRoots @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts'))
    $actual = @($files.path | Sort-Object)
    $expected = @(
        'config/ReactiveMusic.json5',
        'mods/approved.jar',
        'mods/approved.jar.disabled',
        'mods/Axiom-5.4.2-for-MC1.21.1.jar',
        'resourcepacks/approved.zip',
        'resourcepacks/reviewed.zip.rpo'
    )
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "Unexpected source-policy inventory.`nExpected:`n$($expected -join "`n")`nActual:`n$($actual -join "`n")"
    }

    foreach ($unsafe in @(
        'mods/mcef-cache/Network/Cookies',
        'mods/mcef-libraries/bin/runtime.dll',
        'mods/.index/generated.json',
        'mods/legacy.jar.disabled-by-cobble-music',
        'mods/legacy.jar.disabled-by-cobble-music.disabled',
        'resourcepacks/.index/generated.json',
        'resourcepacks/backup.zip.bak',
        'resourcepacks/unreviewed.zip.rpo',
        'config/MCBrowser/tabs.json',
        'config/dreamdisplays/config.toml',
        'resourcepacks/pack/.git/config'
    )) {
        Assert-Rejected { Assert-CobbleSourcePathPolicy -Path $unsafe | Out-Null } $unsafe
    }
    Assert-CobbleSourcePathPolicy -Path 'resourcepacks/reviewed.zip.rpo' -ExplicitSourceFile | Out-Null

    $seedSources = @(Get-CobbleSeedSourceFiles -SourceMinecraftDir $testRoot `
        -SeedFiles @('options.txt', 'config/ReactiveMusic.json5', 'mods/Axiom-5.4.2-for-MC1.21.1.jar') `
        -SeedRoots @('config') -ExcludeFiles @('config/iris.properties') `
        -SeedTemplateDir $templateRoot)
    if (($seedSources.path -join "`n") -cne (@('config/fancymenu/layouts/main.txt', 'config/ReactiveMusic.json5', 'mods/Axiom-5.4.2-for-MC1.21.1.jar', 'options.txt') -join "`n")) {
        throw 'Create-only source inventory was incomplete or unstable.'
    }
    $reactiveSeed = @($seedSources | Where-Object path -ceq 'config/ReactiveMusic.json5')
    if ($reactiveSeed.Count -ne 1 -or [IO.Path]::GetFullPath($reactiveSeed[0].full) -cne [IO.Path]::GetFullPath((Join-Path $templateRoot 'config\ReactiveMusic.json5'))) {
        throw 'Reviewed create-only template did not override the live mutable config.'
    }
    Assert-Rejected { Assert-CobbleSeedPathPolicy -Path 'mods/required.jar' | Out-Null } 'a non-Axiom optional mod'
    Assert-Rejected { Assert-CobbleSeedPathPolicy -Path 'servers.dat' | Out-Null } 'private server state as a default'
    Assert-Rejected { Assert-CobbleSeedPathPolicy -Path 'config/dreamdisplays/config.toml' | Out-Null } 'credential-bearing DreamDisplays config'

    Assert-Rejected {
        Get-CobbleManagedSourceFiles -SourceMinecraftDir $testRoot `
            -IncludeRoots @('mods') `
            -IncludeFiles @('config/MCBrowser/tabs.json') `
            -AllowedRoots @('mods', 'resourcepacks', 'config', 'defaultconfigs', 'kubejs', 'scripts') | Out-Null
    } 'an explicitly requested browser-tabs file'

    Write-Host "Cobble Music source policy tests passed ($($actual.Count) approved fixture files)."
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
