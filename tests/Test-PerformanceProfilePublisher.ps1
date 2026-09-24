$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Updater 1.2.22 lite mode: the publisher-side allow-list for the signed performanceProfiles section. It must accept
# exactly what the updater's PerformanceProfilePolicy accepts (never shader or Iris settings), and a manifest that
# carries the section must require updater 1.2.22.

$Root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $Root 'tools\CobbleMusicRelease.Core.psm1') -Force

function New-Hash([char]$Character) { return [string]::new($Character, 64) }
function New-Record([string]$Path, [int64]$Size, [char]$HashCharacter) {
    return [pscustomobject]@{ path = $Path; size = $Size; sha256 = (New-Hash $HashCharacter) }
}
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw "Expected failure did not occur: $Message" }
    catch { if ($_.Exception.Message -like 'Expected failure did not occur:*') { throw } }
}

$files = ConvertTo-CobbleFileRecordSet -Entries @(
    (New-Record 'mods/atmospherics-2.6.6.jar' 10 'a'),
    (New-Record 'mods/kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar' 13 '2'),
    (New-Record 'mods/InventoryParticles-3.0.0.jar' 11 'b'),
    (New-Record 'config/packed_packs/profiles/resourcepacks/Default.profile.json' 12 'c')
) -Context 'fixture files'
$seeds = ConvertTo-CobbleSeedFileRecordSet -Entries @(
    (New-Record 'options.txt' 20 'd'),
    (New-Record 'config/sodium-options.json' 21 'e'),
    (New-Record 'config/iris.properties' 22 'f'),
    (New-Record 'config/kewz-shader-profiles.json' 23 '1')
) -Context 'fixture defaults'

function New-Profile {
    return [pscustomobject]@{
        lite = [pscustomobject]@{
            revisionId = 'lite-1.0.61-v1'
            detection = [pscustomobject]@{ cpuSingleThreadBelow = 2500; gpuScoreBelow = 13000 }
            disabledMods = @('mods/atmospherics-2.6.6.jar')
            removedPackIds = @("file/Toasty's Fresher Ferns.zip")
            settings = @([pscustomobject]@{ path = 'config/sodium-options.json'; format = 'json'; key = 'quality.leaves_quality'; value = '"FAST"' })
        }
    }
}

$normalized = ConvertTo-CobblePerformanceProfiles -Profiles (New-Profile) -FileSet $files -SeedFileSet $seeds
$json = [ordered]@{ performanceProfiles = $normalized } | ConvertTo-Json -Depth 12 -Compress
Assert-True ($json -ceq '{"performanceProfiles":{"lite":{"revisionId":"lite-1.0.61-v1","detection":{"cpuSingleThreadBelow":2500,"gpuScoreBelow":13000},"disabledMods":["mods/atmospherics-2.6.6.jar"],"removedPackIds":["file/Toasty''s Fresher Ferns.zip"],"settings":[{"path":"config/sodium-options.json","format":"json","key":"quality.leaves_quality","value":"\"FAST\""}]}}}') `
    "Canonical performanceProfiles JSON changed: $json"
Assert-True (Test-CobbleShaderSettingPath -Path 'config/iris.properties') 'Iris settings were not recognized as shader settings.'
Assert-True (Test-CobbleShaderSettingPath -Path 'shaderpacks/ComplementaryReimagined_r5.5.1.zip.txt') 'Shaderpack options were not recognized.'
Assert-True (Test-CobbleShaderSettingPath -Path 'config/kewz-shader-profiles.json') 'Shader profiles were not recognized.'

$rejections = @(
    @{ Why = 'Iris settings'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/iris.properties'; format = 'properties'; key = 'maxShadowRenderDistance'; value = '16' }) } },
    @{ Why = 'shader profiles'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/kewz-shader-profiles.json'; format = 'json'; key = 'grassierGrassMode'; value = '"OFF"' }) } },
    @{ Why = 'shaderpack options'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'shaderpacks/x.zip.txt'; format = 'properties'; key = 'SHADOW_QUALITY'; value = '-1' }) } },
    @{ Why = 'the options pack list'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'options.txt'; format = 'options'; key = 'resourcePacks'; value = '[]' }) } },
    @{ Why = 'keybinds'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'options.txt'; format = 'options'; key = 'key_key.jump'; value = 'key.keyboard.x' }) } },
    @{ Why = 'a non-scalar JSON value'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/sodium-options.json'; format = 'json'; key = 'quality'; value = '{}' }) } },
    @{ Why = 'a wrong format'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/sodium-options.json'; format = 'options'; key = 'x'; value = '1' }) } },
    @{ Why = 'a file that is not a default of the release'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/voxy-config.json'; format = 'json'; key = 'enabled'; value = 'false' }) } },
    @{ Why = 'the same setting twice'; Edit = { param($p) $p.lite.settings = @($p.lite.settings[0], $p.lite.settings[0]) } },
    @{ Why = 'a mod the release does not ship'; Edit = { param($p) $p.lite.disabledMods = @('mods/not-shipped.jar') } },
    @{ Why = 'a mod path in the wrong case'; Edit = { param($p) $p.lite.disabledMods = @('mods/Atmospherics-2.6.6.jar') } },
    @{ Why = 'a managed non-jar'; Edit = { param($p) $p.lite.disabledMods = @('config/packed_packs/profiles/resourcepacks/Default.profile.json') } },
    @{ Why = 'a duplicate mod'; Edit = { param($p) $p.lite.disabledMods = @('mods/atmospherics-2.6.6.jar', 'mods/atmospherics-2.6.6.jar') } },
    @{ Why = 'the vanilla pack'; Edit = { param($p) $p.lite.removedPackIds = @('vanilla') } },
    @{ Why = 'a duplicate pack'; Edit = { param($p) $p.lite.removedPackIds = @('a', 'a') } },
    @{ Why = 'a bad revision id'; Edit = { param($p) $p.lite.revisionId = 'Lite V1' } },
    @{ Why = 'a zero processor line'; Edit = { param($p) $p.lite.detection.cpuSingleThreadBelow = 0 } },
    @{ Why = 'a zero graphics line'; Edit = { param($p) $p.lite.detection.gpuScoreBelow = 0 } },
    @{ Why = 'a processor line above the ceiling'; Edit = { param($p) $p.lite.detection.cpuSingleThreadBelow = 100001 } },
    @{ Why = 'a line written as text'; Edit = { param($p) $p.lite.detection.gpuScoreBelow = '13000' } },
    @{ Why = 'a fractional line'; Edit = { param($p) $p.lite.detection.cpuSingleThreadBelow = 2500.5 } },
    @{ Why = 'a missing graphics line'; Edit = { param($p) $p.lite.detection = [pscustomobject]@{ cpuSingleThreadBelow = 2500 } } },
    @{ Why = 'the round-1 threshold field'; Edit = { param($p) $p.lite.detection | Add-Member -NotePropertyName 'cpuScoreThreshold' -NotePropertyValue 1050 } },
    @{ Why = 'the Subtle Effects join fix'; Edit = { param($p) $p.lite.disabledMods = @('mods/kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar') } },
    @{ Why = 'an unknown property'; Edit = { param($p) $p.lite | Add-Member -NotePropertyName 'shaders' -NotePropertyValue @() } }
)
foreach ($case in $rejections) {
    $profile = New-Profile
    & $case.Edit $profile
    Assert-Throws { ConvertTo-CobblePerformanceProfiles -Profiles $profile -FileSet $files -SeedFileSet $seeds | Out-Null } "Accepted $($case.Why)."
}

# Manifest level: the section needs minimumUpdaterVersion 1.2.22, and explicit null is refused.
$manifest = [pscustomobject]@{ minimumUpdaterVersion = '1.2.22'; performanceProfiles = (New-Profile) }
Assert-True (Assert-CobblePerformanceProfilesManifest -Manifest $manifest -FileSet $files -SeedFileSet $seeds) 'A 1.2.22 manifest with a lite profile was rejected.'
$manifest.minimumUpdaterVersion = '1.2.21'
Assert-Throws { Assert-CobblePerformanceProfilesManifest -Manifest $manifest -FileSet $files -SeedFileSet $seeds } 'A lite profile was accepted below updater 1.2.22.'
$nullSection = [pscustomobject]@{ minimumUpdaterVersion = '1.2.22'; performanceProfiles = $null }
Assert-Throws { Assert-CobblePerformanceProfilesManifest -Manifest $nullSection -FileSet $files -SeedFileSet $seeds } 'Explicit null performanceProfiles was accepted.'
Assert-True (-not (Assert-CobblePerformanceProfilesManifest -Manifest ([pscustomobject]@{ minimumUpdaterVersion = '1.2.16' }) -FileSet $files -SeedFileSet $seeds)) 'A manifest without the section was treated as having one.'
$orderedManifest = [ordered]@{ minimumUpdaterVersion = '1.2.22'; performanceProfiles = $normalized }
Assert-True (Assert-CobblePerformanceProfilesManifest -Manifest ([pscustomobject]$orderedManifest) -FileSet $files -SeedFileSet $seeds) 'The publisher''s own ordered manifest shape was rejected.'

# Round 2: the dependency rule over a release tree's jars (same rule as the updater's LiteModGuard).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path ([IO.Path]::GetTempPath()) ('cobble-lite-closure-' + [guid]::NewGuid().ToString('N'))
$mods = Join-Path $work 'mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
function New-ZipBytes([hashtable]$Entries) {
    $memory = [IO.MemoryStream]::new()
    $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($name in @($Entries.Keys | Sort-Object)) {
        $entry = $archive.CreateEntry($name)
        $stream = $entry.Open()
        $bytes = if ($Entries[$name] -is [byte[]]) { $Entries[$name] } else { [Text.Encoding]::UTF8.GetBytes([string]$Entries[$name]) }
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Dispose()
    }
    $archive.Dispose()
    return ,$memory.ToArray()
}
function New-ModJar([string]$Folder, [string]$Name, [string]$Json, [hashtable]$Nested = @{}) {
    $entries = @{ 'fabric.mod.json' = $Json }
    foreach ($key in $Nested.Keys) { $entries[$key] = $Nested[$key] }
    [IO.File]::WriteAllBytes((Join-Path $Folder $Name), (New-ZipBytes $entries))
}
try {
    $libX = New-ZipBytes @{ 'fabric.mod.json' = '{"schemaVersion":1,"id":"libx","version":"1"}' }
    New-ModJar $mods 'atmospherics-2.6.6.jar' ("{`"schemaVersion`":1,`"id`":`"atmospherics`",`"version`":`"2.6.6`",`"description`":`"line one`nline two`",`"jars`":[{`"file`":`"META-INF/jars/libx.jar`"}]}") @{ 'META-INF/jars/libx.jar' = $libX }
    New-ModJar $mods 'InventoryParticles-3.0.0.jar' '{"schemaVersion":1,"id":"inventoryparticles","version":"3.0.0","recommends":{"atmospherics":"*"}}'
    New-ModJar $mods 'kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar' '{"schemaVersion":1,"id":"kewz_subtle_stub","version":"1.0.0","depends":{"minecraft":"1.21.1"}}'
    $safe = @(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/atmospherics-2.6.6.jar'))
    Assert-True ($safe.Count -eq 0) "A safe list was refused (recommends only, raw line break in a string): $($safe -join ' | ')"
    $ok = ConvertTo-CobblePerformanceProfiles -Profiles (New-Profile) -FileSet $files -SeedFileSet $seeds -ModsDirectory $mods
    Assert-True (@($ok['lite']['disabledMods']).Count -eq 1) 'The tree check dropped the mod list.'

    New-ModJar $mods 'libuser.jar' '{"schemaVersion":1,"id":"libuser","version":"1","depends":{"libx":">=1"}} // comment'
    $lost = @(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/atmospherics-2.6.6.jar'))
    Assert-True ($lost.Count -eq 1 -and $lost[0] -like 'libuser.jar (libuser) depends on libx, which only atmospherics-2.6.6.jar provides') "A library lost with its host was not caught: $($lost -join ' | ')"
    Assert-Throws { ConvertTo-CobblePerformanceProfiles -Profiles (New-Profile) -FileSet $files -SeedFileSet $seeds -ModsDirectory $mods | Out-Null } 'The publisher accepted a list that breaks a dependency.'
    New-ModJar $mods 'holder.jar' '{"schemaVersion":1,"id":"holder","version":"1","jars":[{"file":"META-INF/jars/libx.jar"}],}' @{ 'META-INF/jars/libx.jar' = $libX }
    Assert-True (@(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/atmospherics-2.6.6.jar')).Count -eq 0) 'A library another enabled jar nests was treated as lost.'

    New-ModJar $mods 'sky-addon.jar' '{"schemaVersion":1,"id":"sky_addon","version":"1","depends":{"atmospherics":">=2.6"}}'
    Assert-True (@(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/atmospherics-2.6.6.jar')).Count -eq 1) 'A direct depends on an off mod was not caught.'
    Remove-Item -LiteralPath (Join-Path $mods 'sky-addon.jar')
    $extra = Join-Path $work 'extra-addon.jar'
    New-ModJar $work 'extra-addon.jar' '{"schemaVersion":1,"id":"extra_addon","version":"1","depends":{"atmospherics":"*"}}'
    Assert-True (@(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/atmospherics-2.6.6.jar') -ExtraModJars @($extra)).Count -eq 1) 'An extra jar was not checked.'

    New-ModJar $mods 'join-fix.jar' '{"schemaVersion":1,"id":"kewz_subtle_stub","version":"1.0.0"}'
    $stubProblems = @(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/join-fix.jar'))
    Assert-True ($stubProblems.Count -eq 1 -and $stubProblems[0] -like '*may never switch off (kewz_subtle_stub)*') "The join fix under another file name was not refused: $($stubProblems -join ' | ')"
    New-ModJar $mods 'somelib.jar' '{"schemaVersion":1,"id":"somelib","version":"1","custom":{"modmenu":{"badges":["library"]}}}'
    Assert-True (@(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/somelib.jar'))[0] -like '*is a library') 'A library was not refused.'
    New-ModJar $mods 'sodium.jar' '{"schemaVersion":1,"id":"sodium","version":"0.8.13"}'
    Assert-True (@(Get-CobbleLiteModListProblems -ModsDirectory $mods -DisabledMods @('mods/sodium.jar')).Count -eq 1) 'A critical mod was not refused.'

    # Round-2 verifiers FIX 1: switching Subtle Effects off needs the join fix IN THE TREE; an extra jar does not count.
    $mods2 = Join-Path $work 'mods2'
    New-Item -ItemType Directory -Path $mods2 -Force | Out-Null
    New-ModJar $mods2 'SubtleEffects-fabric-1.21.1-1.14.3.jar' '{"schemaVersion":1,"id":"subtle_effects","version":"1.14.3"}'
    New-ModJar $work 'stub-extra.jar' '{"schemaVersion":1,"id":"kewz_subtle_stub","version":"1.0.0"}'
    $noStub = @(Get-CobbleLiteModListProblems -ModsDirectory $mods2 -DisabledMods @('mods/SubtleEffects-fabric-1.21.1-1.14.3.jar') -ExtraModJars @((Join-Path $work 'stub-extra.jar')))
    Assert-True ($noStub.Count -eq 1 -and $noStub[0] -like '*(subtle_effects) may only be switched off while kewz_subtle_stub is a managed, enabled jar in this release tree*') "Subtle Effects off without the join fix in the tree was not refused: $($noStub -join ' | ')"
    New-ModJar $mods2 'kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar' '{"schemaVersion":1,"id":"kewz_subtle_stub","version":"1.0.0"}'
    $withStub = @(Get-CobbleLiteModListProblems -ModsDirectory $mods2 -DisabledMods @('mods/SubtleEffects-fabric-1.21.1-1.14.3.jar'))
    Assert-True ($withStub.Count -eq 0) "Subtle Effects off with the join fix in the tree was refused: $($withStub -join ' | ')"
    $subtleProfile = New-Profile
    $subtleProfile.lite.disabledMods = @('mods/SubtleEffects-fabric-1.21.1-1.14.3.jar')
    $filesNoStub = ConvertTo-CobbleFileRecordSet -Entries @((New-Record 'mods/SubtleEffects-fabric-1.21.1-1.14.3.jar' 14 '3')) -Context 'no join fix'
    Assert-Throws { ConvertTo-CobblePerformanceProfiles -Profiles $subtleProfile -FileSet $filesNoStub -SeedFileSet $seeds | Out-Null } 'The publisher accepted Subtle Effects off in a release without the join fix.'
    $filesStub = ConvertTo-CobbleFileRecordSet -Entries @((New-Record 'mods/SubtleEffects-fabric-1.21.1-1.14.3.jar' 14 '3'), (New-Record 'mods/kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar' 13 '2')) -Context 'with join fix'
    $subtleOk = ConvertTo-CobblePerformanceProfiles -Profiles $subtleProfile -FileSet $filesStub -SeedFileSet $seeds
    Assert-True (@($subtleOk['lite']['disabledMods']).Count -eq 1) 'Subtle Effects off with the join fix in the release was refused.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Performance profile publisher checks passed: canonical JSON, table lines, shader/Iris/keybind/pack-list rejection, exact managed jars, join fix never listed and required when Subtle Effects is off, dependency/critical/library refusal from the tree, 1.2.22 floor.'
