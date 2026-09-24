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
            detection = [pscustomobject]@{ cpuScoreThreshold = 1050; fullGpuPatterns = @('RTX 3080'); liteGpuPatterns = @('RTX 3050', 'RADEON ###M') }
            disabledMods = @('mods/atmospherics-2.6.6.jar')
            removedPackIds = @("file/Toasty's Fresher Ferns.zip")
            settings = @([pscustomobject]@{ path = 'config/sodium-options.json'; format = 'json'; key = 'quality.leaves_quality'; value = '"FAST"' })
        }
    }
}

$normalized = ConvertTo-CobblePerformanceProfiles -Profiles (New-Profile) -FileSet $files -SeedFileSet $seeds
$json = [ordered]@{ performanceProfiles = $normalized } | ConvertTo-Json -Depth 12 -Compress
Assert-True ($json -ceq '{"performanceProfiles":{"lite":{"revisionId":"lite-1.0.61-v1","detection":{"cpuScoreThreshold":1050,"fullGpuPatterns":["RTX 3080"],"liteGpuPatterns":["RTX 3050","RADEON ###M"]},"disabledMods":["mods/atmospherics-2.6.6.jar"],"removedPackIds":["file/Toasty''s Fresher Ferns.zip"],"settings":[{"path":"config/sodium-options.json","format":"json","key":"quality.leaves_quality","value":"\"FAST\""}]}}}') `
    "Canonical performanceProfiles JSON changed: $json"
Assert-True (Test-CobbleShaderSettingPath -Path 'config/iris.properties') 'Iris settings were not recognized as shader settings.'
Assert-True (Test-CobbleShaderSettingPath -Path 'shaderpacks/ComplementaryReimagined_r5.5.1.zip.txt') 'Shaderpack options were not recognized.'
Assert-True (Test-CobbleShaderSettingPath -Path 'config/kewz-shader-profiles.json') 'Shader profiles were not recognized.'

$rejections = @(
    @{ Why = 'Iris settings'; Edit = { param($p) $p.lite.settings = @([pscustomobject]@{ path = 'config/iris.properties'; format = 'properties'; key = 'enableShaders'; value = 'false' }) } },
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
    @{ Why = 'a zero threshold'; Edit = { param($p) $p.lite.detection.cpuScoreThreshold = 0 } },
    @{ Why = 'a regex graphics pattern'; Edit = { param($p) $p.lite.detection.liteGpuPatterns = @('RTX.*') } },
    @{ Why = 'a duplicate graphics pattern'; Edit = { param($p) $p.lite.detection.fullGpuPatterns = @('RTX 3050') } },
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

Write-Host 'Performance profile publisher checks passed: canonical JSON, shader/Iris/keybind/pack-list rejection, exact managed jars, 1.2.22 floor.'
