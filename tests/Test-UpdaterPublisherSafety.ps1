[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Publisher = Join-Path $Root 'tools\Publish-CobbleMusicUpdater.ps1'
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw "Updater publisher is missing: $Publisher" }

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw "Updater publisher has PowerShell parse errors: $([string]::Join('; ', @($parseErrors.Message)))"
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
