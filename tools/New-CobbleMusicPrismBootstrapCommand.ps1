<#
Produces the single command a Windows player pastes into a Prism instance's
Pre-launch Command field. Prism launches commands directly (without cmd.exe),
so the payload is UTF-16LE/Base64 encoded for deterministic argument handling.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$UpdaterVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedBootstrapSha256,

    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'Kewz4/Cobble-Music'
)

$ErrorActionPreference = 'Stop'
$hash = $ExpectedBootstrapSha256.ToUpperInvariant()
$payload = @'
$ErrorActionPreference='Stop'
$i=[Environment]::GetEnvironmentVariable('INST_DIR')
$m=[Environment]::GetEnvironmentVariable('INST_MC_DIR')
if([string]::IsNullOrWhiteSpace($i)-or[string]::IsNullOrWhiteSpace($m)){throw 'Prism did not provide INST_DIR and INST_MC_DIR.'}
$i=[IO.Path]::GetFullPath($i)
$m=[IO.Path]::GetFullPath($m)
if(-not $m.TrimEnd('\','/').Equals((Join-Path $i 'minecraft').TrimEnd('\','/'),[StringComparison]::OrdinalIgnoreCase)){throw 'Prism supplied inconsistent instance paths.'}
$d=Join-Path $m 'cobble-music-updater'
$b=Join-Path $d 'Bootstrap-CobbleMusicUpdater.ps1'
$h='__BOOTSTRAP_SHA256__'
$u='https://github.com/__REPOSITORY__/releases/download/updater-v__UPDATER_VERSION__/Bootstrap-CobbleMusicUpdater.ps1'
New-Item -ItemType Directory -Path $d -Force|Out-Null
$ok=(Test-Path -LiteralPath $b -PathType Leaf)-and((Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash -ceq $h)
if(-not $ok){
 $t="$b.download-$([Guid]::NewGuid().ToString('N'))"
 try{
  [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol-bor[Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -UseBasicParsing -Uri $u -OutFile $t
  $a=(Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash
  if($a -cne $h){throw "Bootstrap checksum mismatch. Expected $h but downloaded $a."}
  if(Test-Path -LiteralPath $b -PathType Leaf){$q="$b.replaced-$([Guid]::NewGuid().ToString('N'))";try{[IO.File]::Replace($t,$b,$q);Remove-Item -LiteralPath $q -Force;$q=$null}finally{if($q-and(Test-Path -LiteralPath $q)){Remove-Item -LiteralPath $q -Force}}}else{[IO.File]::Move($t,$b)}
  $t=$null
 }finally{if($t-and(Test-Path -LiteralPath $t)){Remove-Item -LiteralPath $t -Force}}
}
if((Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash -cne $h){throw 'Cached bootstrap checksum verification failed.'}
Unblock-File -LiteralPath $b
& $b -InstanceDirectory $i -PrismPreLaunch
'@

$payload = $payload.Replace('__REPOSITORY__', $Repository)
$payload = $payload.Replace('__UPDATER_VERSION__', $UpdaterVersion)
$payload = $payload.Replace('__BOOTSTRAP_SHA256__', $hash)
if ($payload.IndexOf('__', [StringComparison]::Ordinal) -ge 0) {
    throw 'The Prism bootstrap payload still contains an unreplaced template token.'
}

$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($payload))
$command = "powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand $encoded"
if ($command.Length -gt 30000) {
    throw "Generated Prism command is too long for Windows process creation: $($command.Length) characters."
}
Write-Output $command
