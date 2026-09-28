<#
.SYNOPSIS
    Fails if any tracked file contains a non-ASCII codepoint outside the allowlist.

.DESCRIPTION
    Enforces the ASCII-only rule in AGENTS.md section 4. That section says the gate
    "must encode exactly the table above", so the $Allowed set below is a transcription
    of it and nothing else. Adding a codepoint here without adding the matching row to
    AGENTS.md - or the reverse - is the drift this check exists to prevent.

    Only tracked files are scanned. Build output and local machine state are excluded by
    .gitignore and are not this gate's concern.

.PARAMETER Fix
    Report only, with a suggested ASCII substitute per finding. Does not modify files;
    substitutions are a judgement call and belong in a reviewed edit.

.EXAMPLE
    .\scripts\Test-Ascii.ps1

.EXAMPLE
    .\scripts\Test-Ascii.ps1 -Fix
#>
[CmdletBinding()]
param(
    [switch] $Fix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Transcribed from the AGENTS.md section 4 allowlist table. Keep the two in sync.
$allowed = @{
    0x00A7 = 'section sign - cross-references in docs'
    0x2500 = 'box horizontal - component diagrams'
    0x2502 = 'box vertical - component diagrams'
    0x250C = 'box down-and-right - component diagrams'
    0x2510 = 'box down-and-left - component diagrams'
    0x2514 = 'box up-and-right - component diagrams'
    0x2518 = 'box up-and-left - component diagrams'
    0x252C = 'box down-and-horizontal - component diagrams'
    0x25BC = 'down-pointing triangle - arrowheads in component diagrams'
}

# Suggested substitutes, per the guidance in AGENTS.md section 4.
$substitutes = @{
    0x2014 = '-'
    0x2013 = '-'
    0x2026 = '...'
    0x2192 = '->'
    0x2194 = '<->'
    0x2018 = "'"
    0x2019 = "'"
    0x201C = '"'
    0x201D = '"'
    0x00B7 = '-'
    0x2264 = '<='
    0x2265 = '>='
    0x2260 = '!='
    0x00D7 = 'x'
    0x2713 = '[x]'
    0x2714 = '[x]'
}

$repoRoot = (git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) {
    throw 'Not inside a git repository.'
}

$trackedFiles = git -C $repoRoot ls-files
$findings = [System.Collections.Generic.List[object]]::new()

foreach ($relativePath in $trackedFiles) {
    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($fullPath)) {
        $lineNumber++
        $column = 0
        foreach ($character in $line.ToCharArray()) {
            $column++
            $codepoint = [int]$character
            if ($codepoint -le 127 -or $allowed.ContainsKey($codepoint)) {
                continue
            }

            $findings.Add([pscustomobject]@{
                    File       = $relativePath
                    Line       = $lineNumber
                    Column     = $column
                    Codepoint  = 'U+{0:X4}' -f $codepoint
                    Character  = $character
                    Substitute = if ($substitutes.ContainsKey($codepoint)) { $substitutes[$codepoint] } else { '(no suggestion)' }
                })
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host "ASCII check: clean ($($trackedFiles.Count) tracked files)."
    exit 0
}

Write-Host ''
Write-Host "ASCII check: $($findings.Count) violation(s) outside the AGENTS.md section 4 allowlist."
Write-Host ''

if ($Fix) {
    $findings | Format-Table File, Line, Column, Codepoint, Character, Substitute -AutoSize | Out-String | Write-Host
    Write-Host 'Reported only. Apply substitutions by hand - the right replacement is a'
    Write-Host 'judgement call, and a blind rewrite of a deliberate glyph is worse than the'
    Write-Host 'violation. If a codepoint genuinely belongs, add a row to the AGENTS.md'
    Write-Host 'section 4 table and to $allowed in this script, with a reason.'
}
else {
    $findings |
        Group-Object File |
        ForEach-Object {
            Write-Host "  $($_.Name)"
            foreach ($finding in $_.Group) {
                Write-Host "    line $($finding.Line), col $($finding.Column): $($finding.Codepoint) -> use '$($finding.Substitute)'"
            }
        }
    Write-Host ''
    Write-Host 'Re-run with -Fix for a table view.'
}

exit 1
