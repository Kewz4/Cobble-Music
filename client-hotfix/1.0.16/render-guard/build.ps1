param(
    [string]$MinecraftJar = "C:\Users\Kenneth Rodas\.gradle\caches\fabric-loom\minecraftMaven\net\minecraft\minecraft-merged-intermediary\1.21.1-net.fabricmc.yarn.1_21_1.1.21.1+build.3-v2\minecraft-merged-intermediary-1.21.1-net.fabricmc.yarn.1_21_1.1.21.1+build.3-v2.jar",
    [string]$MixinJar = "C:\Users\Kenneth Rodas\.gradle\caches\modules-2\files-2.1\org.spongepowered\mixin\0.8.7\8ab114ac385e6dbdad5efafe28aba4df8120915f\mixin-0.8.7.jar",
    [string]$LoaderJar = "C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon\minecraft\step-next-integrations\.gradle-home\caches\modules-2\files-2.1\net.fabricmc\fabric-loader\0.19.3\354dfaa02d0552e11867f85dff7cdbfaf813ba3e\fabric-loader-0.19.3.jar",
    [string]$GsonJar = "C:\Users\Kenneth Rodas\.gradle\caches\modules-2\files-2.1\com.google.code.gson\gson\2.10.1\b3add478d4382b78ea20b1671390a858002feb6c\gson-2.10.1.jar"
)
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $PSScriptRoot 'build'
$classes = Join-Path $buildRoot 'classes'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $classes,$dist -Force | Out-Null
foreach ($dependency in @($MinecraftJar,$MixinJar,$LoaderJar,$GsonJar)) {
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "Missing cached input: $dependency" }
}
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/main/java') -Recurse -Filter '*.java' | Sort-Object FullName
& javac --release 21 -encoding UTF-8 -proc:none -classpath "$MinecraftJar;$MixinJar;$LoaderJar;$GsonJar" -d $classes @($sources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
# Java source deliberately uses verified 1.21.1 intermediary names. No remapping,
# annotation processor, Gradle cache writes, downloads, or game launch is needed.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$jarPath = Join-Path $dist 'kewz-render-guard-1.0.0+mc1.21.1.jar'
$memory = [IO.MemoryStream]::new()
$archive = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
try {
    $entries = @{
        'LICENSE' = Join-Path $PSScriptRoot 'LICENSE'
        'fabric.mod.json' = Join-Path $PSScriptRoot 'src/main/resources/fabric.mod.json'
        'kewz-render-guard.mixins.json' = Join-Path $PSScriptRoot 'src/main/resources/kewz-render-guard.mixins.json'
    }
    foreach ($source in $sources) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'src/main/java'),$source.FullName).Replace('\','/').Replace('.java','.class')
        $entries[$relative] = Join-Path $classes $relative
    }
    foreach ($entryName in ($entries.Keys | Sort-Object -CaseSensitive)) {
        $entry = $archive.CreateEntry($entryName,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
        $stream = $entry.Open()
        try { $bytes = [IO.File]::ReadAllBytes($entries[$entryName]); $stream.Write($bytes,0,$bytes.Length) }
        finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
try { [IO.File]::WriteAllBytes($jarPath,$memory.ToArray()) } finally { $memory.Dispose() }
Get-Item -LiteralPath $jarPath | Format-List FullName,Length
Get-FileHash -LiteralPath $jarPath -Algorithm SHA256 | Format-List Hash
