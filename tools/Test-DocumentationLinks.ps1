<#
.SYNOPSIS
    Verifies that every relative link and anchor in the Markdown files resolves.

.DESCRIPTION
    Catches a relative link to a file that does not exist, and an anchor pointing at a heading that
    does not exist. External links are not checked: that needs network access and makes builds flaky.

.PARAMETER Path
    Root to scan. Defaults to the repository root.
#>
[CmdletBinding()]
param(
    [string] $Path = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HeadingAnchor {
    <# GitHub's slug rules: lowercase, drop punctuation, every space to a hyphen. #>
    param([string] $File)
    (Get-Content -LiteralPath $File -Raw) -replace '(?ms)^```.*?^```', '' -split '\r?\n' |
        Where-Object { $_ -match '^#{1,6}\s+' } |
        ForEach-Object { (($_ -replace '^#{1,6}\s+', '').ToLower() -replace '[^\w\s-]', '') -replace '\s', '-' }
}

$files = @(Get-ChildItem -Path $Path -Recurse -File -Filter '*.md' -Force |
        Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules|dist)[\\/]' })
Write-Host "Checking $($files.Count) Markdown files under $Path"

$findings = @()
foreach ($file in $files) {
    $source = [IO.Path]::GetRelativePath($Path, $file.FullName)
    # Links inside fenced code blocks are examples, not navigation.
    $content = (Get-Content -LiteralPath $file.FullName -Raw) -replace '(?ms)^```.*?^```', ''

    foreach ($match in [regex]::Matches($content, '\]\((?<target>[^)\s]*?)(?:#(?<anchor>[^)\s]+))?\)')) {
        $target = $match.Groups['target'].Value
        $anchor = $match.Groups['anchor'].Value
        if ($target -match '^(https?|mailto):') { continue }
        if (-not $target -and -not $anchor) { continue }

        $resolved = if ($target) { Join-Path $file.DirectoryName $target } else { $file.FullName }
        if (-not (Test-Path -LiteralPath $resolved)) {
            $findings += "missing file   $source -> $target"
            continue
        }
        if ($anchor -and $resolved -like '*.md' -and (Get-HeadingAnchor $resolved) -notcontains $anchor.ToLower()) {
            $findings += "missing anchor $source -> $target#$anchor"
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host 'All relative links and anchors resolve.' -ForegroundColor Green
    exit 0
}
Write-Host ''
Write-Host "$($findings.Count) broken link(s):" -ForegroundColor Red
$findings | ForEach-Object { Write-Host "  $_" }
exit 1
