param(
    [string]$GsonJar = "C:\Users\Kenneth Rodas\.gradle\caches\modules-2\files-2.1\com.google.code.gson\gson\2.10.1\b3add478d4382b78ea20b1671390a858002feb6c\gson-2.10.1.jar",
    [string]$LoaderJar = "C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon\minecraft\step-next-integrations\.gradle-home\caches\modules-2\files-2.1\net.fabricmc\fabric-loader\0.19.3\354dfaa02d0552e11867f85dff7cdbfaf813ba3e\fabric-loader-0.19.3.jar"
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -GsonJar $GsonJar -LoaderJar $LoaderJar
$artifact = Join-Path $PSScriptRoot 'dist/kewz-render-guard-1.0.0+mc1.21.1.jar'
$testClasses = Join-Path $PSScriptRoot 'build/test-classes'
New-Item -ItemType Directory -Path $testClasses -Force | Out-Null
$testSources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/test/java') -Filter '*.java' -Recurse | Sort-Object FullName
& javac --release 21 -encoding UTF-8 -proc:none -classpath "$artifact;$GsonJar;$LoaderJar" -d $testClasses @($testSources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& java -classpath "$testClasses;$artifact;$GsonJar;$LoaderJar" dev.kewz.renderguard.MigrationTest (Join-Path $PSScriptRoot 'build')
if ($LASTEXITCODE -ne 0) { throw 'Headless migration tests failed' }
$dump = (& javap -p -c -v -classpath $artifact dev.kewz.renderguard.mixin.GameRendererMixin dev.kewz.renderguard.mixin.LightmapTextureManagerMixin) -join "`n"
foreach ($required in @('method_3188(Lnet/minecraft/class_9779;)V','method_3313(F)V','RuntimeInvisibleAnnotations:','value="HEAD"','cancellable=true','require=1','allow=1','field_1724','field_1687','CallbackInfo.cancel:()V')) {
    if (-not $dump.Contains($required)) { throw "Static mixin assertion missing: $required" }
}
if ($dump -match '\bputfield\b|\bputstatic\b|Exception table:') { throw 'Render guards unexpectedly mutate fields or catch exceptions' }
$entries = (& jar tf $artifact)
if ($entries -match '^(net/minecraft|org/spongepowered|com/google|fudge/|.*Test)') { throw 'Dependency or test class bundled' }
'PASS static guards: exact intermediary descriptors, HEAD/cancellable/require=1, player/world reads only, no catch-all or state writes.'
$cachedMinecraft = 'C:\Users\Kenneth Rodas\.gradle\caches\fabric-loom\minecraftMaven\net\minecraft\minecraft-merged-intermediary\1.21.1-net.fabricmc.yarn.1_21_1.1.21.1+build.3-v2\minecraft-merged-intermediary-1.21.1-net.fabricmc.yarn.1_21_1.1.21.1+build.3-v2.jar'
$worldTarget = (& javap -p -s -classpath $cachedMinecraft net.minecraft.class_757) -join "`n"
$lightTarget = (& javap -p -s -classpath $cachedMinecraft net.minecraft.class_765) -join "`n"
if (-not $worldTarget.Contains('public void method_3188(net.minecraft.class_9779);') -or -not $worldTarget.Contains('(Lnet/minecraft/class_9779;)V') -or -not $worldTarget.Contains('final net.minecraft.class_310 field_4015;')) { throw 'GameRenderer target mismatch' }
if (-not $lightTarget.Contains('public void method_3313(float);') -or -not $lightTarget.Contains('private final net.minecraft.class_310 field_4137;')) { throw 'Lightmap target mismatch' }
'PASS exact cached Minecraft 1.21.1 target methods and owner-client shadow fields.'
$before = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
& (Join-Path $PSScriptRoot 'build.ps1') -GsonJar $GsonJar -LoaderJar $LoaderJar
if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $before) { throw 'Repeated build differed' }
'PASS deterministic repeated JAR build.'
