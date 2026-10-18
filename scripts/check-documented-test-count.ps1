<#
.SYNOPSIS
  Fails when a document states a test count that the suite no longer reports.

.DESCRIPTION
  The suite count is a number people restate by hand, and a hand-restated
  number drifts. This repo has already carried 689, 713, 714 and 755 in
  current-state documents, and 714 was worse than wrong: it was one *above* the
  real 713, which is what a number nudged by hand looks like.

  Every current-state document that quotes the count is listed in $Anchors with
  the exact shape of the sentence it must appear in. That precision is the point.
  These documents also contain historical counts that must NOT be gated - "Wave 1
  SHIPPED as 1.2.0 (16 rules, 127 tests)", "~560 tests never noticed", "581 tests
  still passed" - and a scan for /N tests/ across the docs would fail on all of
  them. The anchors select only the assertions about the *current* release.

  If an anchor stops matching, this fails rather than passing quietly: a reworded
  sentence must not be able to switch the check off.

.PARAMETER TestOutputPath
  A saved copy of `dotnet test` console output. When omitted the script runs the
  suite itself, which is what you want locally and what CI skips.

  This reads the console summary rather than a .trx on purpose. The per-assembly
  .trx agrees with it, but a solution-level run.trx written into the same
  directory does not, and which of the two a `find` hands you is not something a
  correctness check should depend on. The console line is the number the developer
  just watched being printed.

.PARAMETER Solution
  Solution to test when no -TestOutputPath is given.

.EXAMPLE
  pwsh scripts/check-documented-test-count.ps1
  pwsh scripts/check-documented-test-count.ps1 -TestOutputPath out/test.log
#>
[CmdletBinding()]
param(
    [string] $TestOutputPath,
    [string] $Solution = 'Snipper.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

# Each anchor is one sentence in one current-state document that asserts the live
# suite count. Add a document here deliberately, rather than widening the scan.
$Anchors = @(
    [pscustomobject]@{
        Path    = 'README.md'
        Pattern = 'analysers,\s+(?<n>[\d,]+)\s+tests\.'
    }
    [pscustomobject]@{
        Path    = 'docs/code_architecture.md'
        Pattern = 'analysers,\s+30 rule IDs,\s+(?<n>[\d,]+)\s+tests\)'
    }
    [pscustomobject]@{
        Path    = 'docs/code_architecture.md'
        Pattern = 'FluentAssertions\.\s+(?<n>[\d,]+)\s+tests,'
    }
    [pscustomobject]@{
        Path    = 'docs/Snipper-Feature-Parity-Roadmap.md'
        Pattern = '\*\*Current release:\*\*.*?(?<n>[\d,]+)\s+tests\*\*'
    }
    [pscustomobject]@{
        Path    = 'docs/competitive-analysis.md'
        Pattern = 'analysers;\s+(?<n>[\d,]+)\s+tests\)'
    }
)

function Get-CountFromOutput {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Output)

    $match = [regex]::Match($Output, 'Passed:\s+(?<n>[\d,]+)')
    if (-not $match.Success) {
        throw 'Could not read a test count from the output. Expected a "Passed: N" summary line.'
    }

    return [int] ($match.Groups['n'].Value -replace ',', '')
}

if ($TestOutputPath) {
    if (-not (Test-Path -LiteralPath $TestOutputPath)) { throw "Test output not found: $TestOutputPath" }
    $actual = Get-CountFromOutput -Output (Get-Content -LiteralPath $TestOutputPath -Raw)
}
else {
    Write-Host 'No -TestOutputPath given; running the suite to get the count...'
    Push-Location $RepoRoot
    try {
        $output = & dotnet test $Solution --nologo -v q 2>&1 | Out-String
    }
    finally {
        Pop-Location
    }

    $actual = Get-CountFromOutput -Output $output
}

Write-Host "Suite reports $actual tests."

$failures = [System.Collections.Generic.List[string]]::new()

foreach ($anchor in $Anchors) {
    $full = Join-Path $RepoRoot $anchor.Path
    if (-not (Test-Path -LiteralPath $full)) {
        $failures.Add("$($anchor.Path): file not found")
        continue
    }

    $matches = [regex]::Matches((Get-Content -LiteralPath $full -Raw), $anchor.Pattern)
    if ($matches.Count -eq 0) {
        $failures.Add(
            "$($anchor.Path): no longer matches $($anchor.Pattern) - the sentence was reworded or removed, " +
            "so this check would now pass vacuously. Update the anchor in $PSCommandPath, or restore the count.")
        continue
    }

    foreach ($m in $matches) {
        $documented = [int] ($m.Groups['n'].Value -replace ',', '')
        if ($documented -ne $actual) {
            $line = ($m.Value -replace '\s+', ' ').Trim()
            $failures.Add("$($anchor.Path): says $documented, suite reports $actual  ->  `"$line`"")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    # Not Write-Error: $ErrorActionPreference is 'Stop', which would make this
    # terminating and swallow the per-file detail that follows it - which is the
    # entire reason someone is reading this output.
    Write-Host 'Documented test count does not match the suite:' -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Update the count in the documents above, or revert whatever changed the suite.'
    exit 1
}

Write-Host "All $($Anchors.Count) documented counts match."