[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestRoot,
    [string]$OutputPath = (Join-Path $PSScriptRoot '1.0.13-legacy-cleanup.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedManifests = [ordered]@{
    '1.0.5'  = 'a157823846268d26dce30fbbb21758220316f4ff00e4c98810875d2d80e0016d'
    '1.0.6'  = '1f4e88da8e344ab74c843d5153eaaf7cc429add06b43d23a88260465aab304ad'
    '1.0.7'  = '78890c0b6427c86bd3a5b59c88624bab78da09f08e1de160e307db9ef4580531'
    '1.0.8'  = '1d152b3f33ca8d57b4d162433641a6ef6e77b6f91d970c2f08c7d723f2b50f8f'
    '1.0.9'  = '6e00451c9c27ad0e5648bb9f350b7e8c7fc45ae9cceefa0bbc60ee9cdba1fa0c'
    '1.0.10' = '2999a69644ee1580fffb9a2d146f6755a13f7ea2242d8e5da78dd9867ec36f67'
    '1.0.11' = '485377958696874bc29df44f48b39bc37b54aa72d75e241425113fc0963fc5f1'
    '1.0.12' = '1631d00f895dc4b8b3a333f1dc107a6f1f0bc3f475ed54427d1120dc7e7ad0ac'
}

$manifests = @{}
foreach ($version in $expectedManifests.Keys) {
    $path = Join-Path (Join-Path $ManifestRoot $version) 'cobble-music-update.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing signed public manifest snapshot: $path"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedManifests[$version]) {
        throw "Public manifest snapshot hash mismatch for $version."
    }
    $manifests[$version] = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

$records = [Collections.Generic.List[object]]::new()
$identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-CleanupIdentity([string]$Path, [int64]$Size, [string]$Sha256) {
    $identity = "$Path`0$Size`0$Sha256"
    if ($identities.Add($identity)) {
        $records.Add([ordered]@{ path = $Path; size = $Size; sha256 = $Sha256 })
    }
}

$legacyShaderPrefix = 'shaderpacks/Max Quality (Iteration RP - Path Traced)/'
foreach ($entry in @($manifests['1.0.7'].files) + @($manifests['1.0.11'].deletedFiles)) {
    if ([string]$entry.path -clike "$legacyShaderPrefix*") {
        Add-CleanupIdentity ([string]$entry.path) ([int64]$entry.size) ([string]$entry.sha256)
    }
}

# v1.0.12 incorrectly managed player-owned Iris option sidecars. Their exact
# signed identities authorize the one-time managed-to-seed transition.
foreach ($entry in @($manifests['1.0.12'].files)) {
    $path = [string]$entry.path
    if ($path.StartsWith('shaderpacks/', [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $path.Substring('shaderpacks/'.Length)
        if (-not $relative.Contains('/') -and $relative.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
            Add-CleanupIdentity $path ([int64]$entry.size) ([string]$entry.sha256)
        }
    }
}

# Accept every publicly shipped Packed Packs profile identity. Unknown or
# player-edited profiles are deliberately preserved.
foreach ($manifest in $manifests.Values) {
    $seedProperty = $manifest.PSObject.Properties['seedFiles']
    if ($null -eq $seedProperty -or $null -eq $seedProperty.Value) { continue }
    foreach ($entry in @($seedProperty.Value)) {
        if ([string]$entry.path -in @(
            'config/packed_packs/profiles/resourcepacks/Default.profile.json',
            'config/packed_packs/profiles/resourcepacks/Realistic.profile.json')) {
            Add-CleanupIdentity ([string]$entry.path) ([int64]$entry.size) ([string]$entry.sha256)
        }
    }
}

# Reviewed local pack variants that predate or differ only by the final LF
# from the public signed profile snapshots.
foreach ($entry in @(
    @('config/packed_packs/profiles/resourcepacks/Default.profile.json', 2567, 'a057e1ffd86685e98871cd6ca0b59c55051edebdec1db450ad73f1ba4a4d4cf9'),
    @('config/packed_packs/profiles/resourcepacks/Realistic.profile.json', 3196, '157f7c0c034da1b8ccaff56d544059883cecea097841fab158344ee235277028')
)) {
    Add-CleanupIdentity $entry[0] ([int64]$entry[1]) $entry[2]
}

# Exact retired runtime/config/Prism-metadata identities. These cover Tough As
# Nails and Xaero's Maps x Waystones (xmxw), including the BK-derived build.
foreach ($entry in @(
    @('mods/ToughAsNails-fabric-1.21.1-10.1.0.13.jar', 666863, '097dfeabbc2bf85b872e4e43ba0ad1297783bc9a4a464aa5fc973c5b402f595f'),
    @('mods/ToughAsNails-fabric-1.21.1-10.1.0.13-FORGIVING.jar', 667562, '1d9390db1fd657891705d0d583380f85a508e4fa07cbf2ded37513513479868d'),
    @('mods/xmxw-2.11.1+1.21.1-fabric.jar', 130604, '6ff49e4bce08e74108dd9b9f489f9442606973f25ce3d1ff8659c09268984078'),
    @('mods/xmxw-2.10.0+1.21.1-fabric.jar', 130560, '91d2cf17717fe804c8e3a0ef3e2923637591740be0aa2fcc6b9ead3f940836dc'),
    @('mods/.index/tough-as-nails.pw.toml', 563, '352534f97ffbc9760cc3a25f37996d9d9023383ae78004d446fceeb4620edb78'),
    @('mods/.index/xaeros-maps-x-waystones.pw.toml', 714, '22d792336b7b744a57f9e2b02da3a60124db68894f41b19a3ea033946ac0d980'),
    @('mods/.index/xaeros-maps-x-waystones.pw.toml', 714, 'e7e7cddd27126bd7c15889b74d2fb9fc1036ee43cc9b8c3fdc53014fa4fc9004'),
    @('config/toughasnails/client.toml', 423, '38db2f6a8bae065d6bf89327cea92c55d7370db7c0afcb13b494b985776f31c7'),
    @('config/toughasnails/temperature.toml', 2735, '23d0ecc9637943d1e5300f753d1768d7a8673b48c8bc97b29df082f852cbe44d'),
    @('config/toughasnails/thirst.toml', 661, 'edc02c61b24515600f8cc60545ab99b4bc9aea490e7ac348bcbca85da4d14199')
)) {
    Add-CleanupIdentity $entry[0] ([int64]$entry[1]) $entry[2]
}

$ordered = @($records | Sort-Object `
    -Property @{ Expression = { $_.path.ToUpperInvariant() }; Ascending = $true }, size, sha256)
$json = $ordered | ConvertTo-Json -Depth 4
[IO.Directory]::CreateDirectory((Split-Path -Parent $OutputPath)) | Out-Null
[IO.File]::WriteAllText($OutputPath, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($ordered.Count) exact cleanup identities to $OutputPath"
