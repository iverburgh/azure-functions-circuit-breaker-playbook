<#
.SYNOPSIS
    Fails if any organisation-specific reference leaks into this repository.

.DESCRIPTION
    Everything published here must be generic: no organisation or product names, no internal
    hostnames, no work item numbers, no identifiers of real resources.

    Generic patterns are built in. The names of the organisation and product this playbook was
    extracted from are deliberately NOT in this file — listing them would publish them. They are
    loaded from:

      - tools/forbidden-terms.local.txt  (git-ignored; one regular expression per line, # comments)
      - the FORBIDDEN_TERMS environment variable (separated by newlines or semicolons), which CI
        fills from a repository secret

.PARAMETER Path
    Root to scan. Defaults to the repository root.

.EXAMPLE
    pwsh ./tools/Test-NoInternalReferences.ps1
#>
[CmdletBinding()]
param(
    [string] $Path = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$binary = '\.(png|jpe?g|gif|ico|svg|pdf|zip|gz|7z|dll|exe|pdb|db|pfx|snk|woff2?|ttf)$'
$localTerms = Join-Path $PSScriptRoot 'forbidden-terms.local.txt'

# Built-in Azure role definition IDs are public and identical in every tenant.
$publicGuids = @(
    'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' # Storage Blob Data Owner
    '974c5e8b-45b9-4653-ba55-5f855dd0fb88' # Storage Queue Data Contributor
    '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' # Storage Table Data Contributor
    '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b' # App Configuration Data Owner
    '090c5cfd-751d-490a-894a-3ce6f1109419' # Azure Service Bus Data Owner
)

# Scan everything except binaries, dependencies and build output: an extension allow-list would skip dotfiles, where stray references hide.
$files = @(Get-ChildItem -Path $Path -Recurse -File -Force |
        Where-Object {
            $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules|dist|TestResults)[\\/]' -and
            $_.Name -ne 'package-lock.json' -and
            $_.Name -ne 'local.settings.json' -and
            $_.Extension -notmatch $binary -and
            $_.FullName -ne $PSCommandPath -and
            $_.FullName -ne $localTerms
        })

$forbidden = [Collections.Generic.List[hashtable]]::new()
@(
    @{ Name = 'Organisation URL';     Pattern = 'dev\.azure\.com/(?!<|example/)[A-Za-z0-9._-]+/[A-Za-z0-9._-]'; Why = 'Use dev.azure.com/<org>/<project>.' }
    @{ Name = 'Legacy org URL';       Pattern = '(?<![./-])\b(?!(code|marketplace|app)\.)[A-Za-z0-9-]+\.visualstudio\.com'; Why = 'Use dev.azure.com/<org>.' }
    @{ Name = 'Private feed';         Pattern = 'pkgs\.dev\.azure\.com/(?!<)[A-Za-z0-9._-]+/';            Why = 'Use <org> and <feed> placeholders.' }
    @{ Name = 'Internal mail domain'; Pattern = '@(?!example\.com)[A-Za-z0-9-]+\.(nl|local|internal)\b';  Why = 'Use example.com.' }
    @{ Name = 'Work item reference';  Pattern = '(?i)\b(PBI|task|feature|bug)\s*#?\s*\d{5,}\b|(?<!:\s{0,3})#\d{6,}\b'; Why = 'Internal tracking numbers. (CSS colours are allowed.)' }
    @{ Name = 'Tenant or object ID';  Pattern = '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b'; Why = 'GUIDs identify real resources and identities; generate them at run time.' }
    @{ Name = 'Real hostname';        Pattern = '(?i)\b[a-z0-9-]+\.(azurewebsites\.net|azconfig\.io|servicebus\.windows\.net|blob\.core\.windows\.net)\b(?<!(my-[a-z0-9-]+|example)\.[a-z.]+)'; Why = 'Use placeholders such as my-store.azconfig.io.' }
    @{ Name = 'Windows user path';    Pattern = '(?i)C:\\Users\\(?!<)[A-Za-z0-9._-]+\\';                  Why = 'Personal paths identify people.' }
    @{ Name = 'Dutch text';           Pattern = '(?i)\b(werd|wordt|niet|geen|deze|zodat|omdat|waardoor|bijvoorbeeld|voorkomt)\b'; Why = 'Repository is English-only.' }
) | ForEach-Object { $forbidden.Add($_) }

$extra = @()
if (Test-Path $localTerms) { $extra += Get-Content $localTerms }
if ($env:FORBIDDEN_TERMS) { $extra += $env:FORBIDDEN_TERMS -split '[;\r\n]+' }
$extra = @($extra | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
foreach ($term in $extra) {
    $forbidden.Add(@{ Name = 'Organisation-specific term'; Pattern = "(?i)$term"; Why = 'Listed in forbidden-terms.local.txt or FORBIDDEN_TERMS.' })
}

Write-Host "Scanning $($files.Count) files under $Path"
if ($extra.Count -eq 0) {
    Write-Warning 'No organisation-specific terms loaded: create tools/forbidden-terms.local.txt or set FORBIDDEN_TERMS. Only generic patterns are checked.'
}
else {
    Write-Host "Loaded $($extra.Count) organisation-specific term(s)."
}

$findings = @()
foreach ($rule in $forbidden) {
    foreach ($hit in @($files | Select-String -Pattern $rule.Pattern -AllMatches -ErrorAction SilentlyContinue)) {
        $matched = @($hit.Matches | ForEach-Object { $_.Value.ToLowerInvariant() })
        if ($rule.Name -eq 'Tenant or object ID' -and -not ($matched | Where-Object { $publicGuids -notcontains $_ })) {
            continue
        }

        $findings += [pscustomobject]@{
            Rule = $rule.Name
            File = [IO.Path]::GetRelativePath($Path, $hit.Path)
            Line = $hit.LineNumber
            # CI logs are public: never echo an organisation-specific match.
            Text = if ($rule.Name -eq 'Organisation-specific term') { '(match hidden)' } else { $hit.Line.Trim() }
            Why  = $rule.Why
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host 'No internal references found.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "$($findings.Count) potential leak(s) found:" -ForegroundColor Red
foreach ($group in $findings | Group-Object Rule) {
    Write-Host ''
    Write-Host "  $($group.Name) - $($group.Group[0].Why)" -ForegroundColor Yellow
    foreach ($f in $group.Group) {
        $snippet = if ($f.Text.Length -gt 110) { $f.Text.Substring(0, 110) + '...' } else { $f.Text }
        Write-Host "    $($f.File):$($f.Line)  $snippet"
    }
}
Write-Host ''
exit 1
