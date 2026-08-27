[CmdletBinding()]
param(
    # Optional maintainer-only round trip. Normal CI and clean clones verify
    # the committed public fixture and never need a signing seed.
    [string]$PrivateKey
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'updater\CobbleMusicUpdater\CobbleMusicUpdater.csproj'
$UpdaterDll = Join-Path $Root 'updater\CobbleMusicUpdater\bin\Release\net10.0-windows\win-x64\CobbleMusicUpdater.dll'
$Fixture = Join-Path $Root 'updater\testdata\manifest-signature-fixture.json'
$FixtureSignature = Join-Path $Root 'updater\testdata\manifest-signature-fixture.sig'
$ChannelFixture = Join-Path $Root 'updater\testdata\updater-channel-signature-fixture.json'
$ChannelFixtureSignature = Join-Path $Root 'updater\testdata\updater-channel-signature-fixture.sig'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cobble-music-signature-test-" + [Guid]::NewGuid().ToString('N'))

try {
    & dotnet restore $Project --locked-mode | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater restore failed with exit code $LASTEXITCODE" }
    & dotnet build $Project --configuration Release --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater build failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $FixtureSignature -PathType Leaf)) { throw "Missing public fixture signature: $FixtureSignature" }

    & dotnet $UpdaterDll --verify-manifest $Fixture --signature-file $FixtureSignature | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Public fixture signature verification failed with exit code $LASTEXITCODE" }

    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    $MutatedManifest = Join-Path $TempRoot 'mutated-manifest.json'
    $Bytes = [IO.File]::ReadAllBytes($Fixture)
    $Bytes[$Bytes.Length - 2] = $Bytes[$Bytes.Length - 2] -bxor 1
    [IO.File]::WriteAllBytes($MutatedManifest, $Bytes)
    & dotnet $UpdaterDll --verify-manifest $MutatedManifest --signature-file $FixtureSignature 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'A modified manifest unexpectedly passed signature verification.' }

    $VerifiedChannel = Join-Path $TempRoot 'verified-channel.json'
    & dotnet $UpdaterDll --verify-updater-channel $ChannelFixture --signature-file $ChannelFixtureSignature --verified-output $VerifiedChannel | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $VerifiedChannel -PathType Leaf)) {
        throw 'Public updater-channel signature verification failed.'
    }
    if ([IO.File]::ReadAllText($VerifiedChannel) -cne [IO.File]::ReadAllText($ChannelFixture).TrimEnd("`r", "`n")) {
        throw 'Updater-channel verifier did not emit the exact canonical fixture.'
    }
    $MutatedChannel = Join-Path $TempRoot 'mutated-channel.json'
    $ChannelBytes = [IO.File]::ReadAllBytes($ChannelFixture)
    $ChannelBytes[$ChannelBytes.Length - 2] = $ChannelBytes[$ChannelBytes.Length - 2] -bxor 1
    [IO.File]::WriteAllBytes($MutatedChannel, $ChannelBytes)
    & dotnet $UpdaterDll --verify-updater-channel $MutatedChannel --signature-file $ChannelFixtureSignature --verified-output (Join-Path $TempRoot 'rejected-channel.json') 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'A modified updater channel unexpectedly passed signature verification.' }

    if (-not [string]::IsNullOrWhiteSpace($PrivateKey)) {
        $RoundTripSignature = Join-Path $TempRoot 'round-trip.sig'
        & dotnet $UpdaterDll --sign-manifest $Fixture --private-key-file $PrivateKey --signature-output $RoundTripSignature | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Maintainer signing round trip failed with exit code $LASTEXITCODE" }
        & dotnet $UpdaterDll --verify-manifest $Fixture --signature-file $RoundTripSignature | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Maintainer signature verification failed with exit code $LASTEXITCODE" }
    }

    Write-Host 'Public manifest signature checks passed.'
    $global:LASTEXITCODE = 0
}
finally {
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force }
}
