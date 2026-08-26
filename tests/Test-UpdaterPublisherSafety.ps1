[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Publisher = Join-Path $Root 'tools\Publish-CobbleMusicUpdater.ps1'
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw "Updater publisher is missing: $Publisher" }
$Builder = Join-Path $Root 'tools\Build-CobbleMusicUpdater.ps1'
$GlobalJson = Join-Path $Root 'global.json'
$Attributes = Join-Path $Root '.gitattributes'
foreach ($path in @($Builder, $GlobalJson, $Attributes)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Updater build-safety input is missing: $path" }
}
$ConsoleHarnessProject = Join-Path $Root 'updater\CobbleMusicUpdater.Tests\CobbleMusicUpdater.Tests.csproj'
if (-not (Test-Path -LiteralPath $ConsoleHarnessProject -PathType Leaf)) { throw "Console updater test harness is missing: $ConsoleHarnessProject" }

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw "Updater publisher has PowerShell parse errors: $([string]::Join('; ', @($parseErrors.Message)))"
}

# CobbleMusicUpdater.Tests is deliberately a console executable rather than a
# Microsoft.NET.Test.Sdk project. `dotnet test` can return success after merely
# building it, so the publisher must use `dotnet run` and require Program.Main's
# unambiguous success marker.
[xml]$consoleHarness = [IO.File]::ReadAllText($ConsoleHarnessProject)
$consoleOutputTypes = @($consoleHarness.SelectNodes('/Project/PropertyGroup/OutputType') | ForEach-Object { $_.InnerText.Trim() } | Sort-Object -Unique)
if ($consoleOutputTypes.Count -ne 1 -or $consoleOutputTypes[0] -cne 'Exe') {
    throw 'The updater publisher regression fixture is no longer a console executable; update this safety test with its new execution contract.'
}
$publisherText = [IO.File]::ReadAllText($Publisher)
$requiredExecutionFragments = @(
    '& dotnet run --project $testProject.FullName --configuration Release',
    "'CobbleMusicUpdater.Tests.csproj' = 'Schema-v2 delta, release-chain, exact-baseline adoption, base-integrity, and journal commit-boundary checks passed.'",
    'exited successfully without its required execution marker'
)
foreach ($fragment in $requiredExecutionFragments) {
    if (-not $publisherText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Updater publisher no longer proves console test execution. Missing contract fragment: $fragment"
    }
}

if (-not $publisherText.Contains('& $BuildScript -Runtime win-x64 -OutputDirectory $publishOutput', [StringComparison]::Ordinal)) {
    throw 'Updater publisher bypasses the centralized reproducible builder.'
}
if ($publisherText.Contains('& dotnet publish $ProjectPath', [StringComparison]::Ordinal)) {
    throw 'Updater publisher reintroduced a second, drifting dotnet publish path.'
}

$builderText = [IO.File]::ReadAllText($Builder)
foreach ($fragment in @(
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:Deterministic=true',
    '-p:DeterministicSourcePaths=true',
    '-p:ContinuousIntegrationBuild=true',
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:EnableSourceLink=false',
    '-p:EnableSourceControlManagerQueries=false',
    '"-p:PathMap=$PathMap"'
)) {
    if (-not $builderText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Updater builder lost a reproducibility requirement: $fragment"
    }
}
if ($builderText.Contains('-p:DebugType=embedded', [StringComparison]::Ordinal)) {
    throw 'Updater release builder reintroduced path-sensitive embedded debug data.'
}

$sdk = Get-Content -LiteralPath $GlobalJson -Raw | ConvertFrom-Json
if ([string]$sdk.sdk.version -cne '10.0.103' `
    -or [string]$sdk.sdk.rollForward -cne 'disable' `
    -or $sdk.sdk.PSObject.Properties.Name -notcontains 'allowPrerelease' `
    -or [bool]$sdk.sdk.allowPrerelease) {
    throw 'global.json no longer pins the reviewed .NET SDK exactly.'
}
$attributesText = [IO.File]::ReadAllText($Attributes)
foreach ($fragment in @('*.cs text eol=lf', '*.ps1 text eol=lf', '*.json text eol=lf', '*.exe binary', '*.dll binary')) {
    if (-not $attributesText.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Stable text/binary Git attribute is missing: $fragment"
    }
}

$commentPatternAssignment = [regex]::Match(
    $publisherText,
    '(?m)^[ \t]*\$commentPattern[ \t]*=[ \t]*''(?<pattern>[^''\r\n]+)''[ \t]*\r?$')
if (-not $commentPatternAssignment.Success) { throw 'Could not locate the updater checksum-comment regex in the publisher.' }
$commentExpression = [regex]::new($commentPatternAssignment.Groups['pattern'].Value)
foreach ($fixture in @(
    [pscustomobject]@{ Name = 'LF'; Eol = "`n"; CapturedEol = '' },
    [pscustomobject]@{ Name = 'CRLF'; Eol = "`r`n"; CapturedEol = "`r" }
)) {
    $oldComment = "# SHA-256 of CobbleMusicUpdater.exe from updater-v1.1.0.$($fixture.Eol)"
    $matches = $commentExpression.Matches($oldComment)
    if ($matches.Count -ne 1 -or $matches[0].Groups['eol'].Value -cne $fixture.CapturedEol) {
        throw "Updater checksum-comment regex is not safe for $($fixture.Name) line endings."
    }
    $replacement = $commentExpression.Replace(
        $oldComment,
        [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            '# SHA-256 of CobbleMusicUpdater.exe from updater-v9.9.9.' + $match.Groups['eol'].Value
        },
        1)
    $expected = "# SHA-256 of CobbleMusicUpdater.exe from updater-v9.9.9.$($fixture.Eol)"
    if ($replacement -cne $expected) { throw "Updater checksum-comment replacement changed $($fixture.Name) line endings." }
}

$cases = @(
    [pscustomobject]@{
        Name = 'dry-run cannot mutate a draft'
        Arguments = @('-DryRun', '-UploadDraft')
        Expected = '-DryRun cannot be combined with -UploadDraft or -Publish.'
    },
    [pscustomobject]@{
        Name = 'publication requires confirmation'
        Arguments = @('-Publish')
        Expected = 'Final publication requires both -Publish and -ConfirmPublish.'
    },
    [pscustomobject]@{
        Name = 'confirmation cannot stand alone'
        Arguments = @('-ConfirmPublish')
        Expected = '-ConfirmPublish is valid only with -Publish.'
    }
)

foreach ($case in $cases) {
    $output = @(& pwsh -NoProfile -File $Publisher @($case.Arguments) 2>&1)
    $exitCode = $LASTEXITCODE
    $text = [string]::Join([Environment]::NewLine, $output)
    if ($exitCode -eq 0) { throw "Publisher safety case unexpectedly succeeded: $($case.Name)" }
    if (-not $text.Contains($case.Expected, [StringComparison]::Ordinal)) {
        throw "Publisher safety case returned the wrong failure: $($case.Name)`n$text"
    }
}

Write-Host 'Updater publisher mode-gate, reproducible-builder, and LF/CRLF checksum-comment checks passed without building or contacting GitHub.'
