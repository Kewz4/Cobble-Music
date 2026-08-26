[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Publisher = Join-Path $Root 'tools\Publish-CobbleMusicUpdater.ps1'
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw "Updater publisher is missing: $Publisher" }
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

Write-Host 'Updater publisher mode-gate checks passed without building or contacting GitHub.'
