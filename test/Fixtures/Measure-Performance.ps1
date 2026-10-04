#Requires -Version 7.0
<#
.SYNOPSIS
Performance and output-equivalence harness for Snipper.

.DESCRIPTION
Runs Snipper against one or more real solutions and reports wall clock, CPU time and a
canonical hash of the `findings` array. Two modes:

  -Timing (default)*   build the current Release binary and measure it. Use the findings
                        hash as an output-equivalence oracle for a behaviour-preserving
                        change: it must not move.

  -Compare <dir>*      interleaved A/B against a second build in <dir>. This is the mode to
                        trust for a performance claim.

WHY INTERLEAVED, AND WHY THE HASH
---------------------------------
Non-interleaved before/after comparisons on a shared box are not evidence. During the 1.7.0
perf work a real, interleaved-verified improvement measured 3.8% SLOWER when compared
non-interleaved, and a separate run of the same binaries showed a >2x swing from machine
load alone. Alternating the two binaries run-by-run cancels drift, because both sides
experience the same conditions. Always report the CPU delta alongside wall clock: CPU is far
less noisy, and CPU falling further than wall is the signature of genuinely less work rather
than luck.

The hashed value covers ONLY `findings`. `toolVersion`, `commitSha` and `generatedAtUtc`
are environment- and time-dependent and would differ on every run for reasons that have
nothing to do with the change under test.

DO NOT use Snipper-on-Snipper as the equivalence oracle. Snipper analyses its own source, so
editing Snipper legitimately changes its own findings and the comparison is meaningless.
That mistake cost real time during the 1.7.0 perf work. Use stable external targets.

SCALE WARNING
-------------
Per-symbol and per-node optimisations are invisible on small solutions. The memos added in
1.7.0 measured as neutral on every real solution available locally (largest: 119 .cs files)
and only paid off (-2.9% wall / -3.0% CPU) on a generated 40-project / 800-file target. If
you are judging that class of change, generate a large target rather than concluding it
does nothing.

.PARAMETER Mode
    Timing (default) or Compare.

.PARAMETER BaselineDir
    Directory containing the baseline build (must contain snipper.exe). Required for
    -Mode Compare.

.PARAMETER Targets
    Solution or project paths to measure. Defaults to this repository plus any paths given.

.PARAMETER Runs
    Timed runs per target per binary.

.PARAMETER ExtraArgs
    Additional Snipper arguments, e.g. '--baseline','--audit-suppressions'.

.EXAMPLE
    pwsh test/Fixtures/Measure-Performance.ps1
    pwsh test/Fixtures/Measure-Performance.ps1 -Mode Compare -BaselineDir C:\out\old -Runs 4
    pwsh test/Fixtures/Measure-Performance.ps1 -Targets C:\repos\a.sln,C:\repos\b.sln
#>
[CmdletBinding()]
param(
    [ValidateSet('Timing', 'Compare')]
    [string]$Mode = 'Timing',

    [string]$BaselineDir,

    [string[]]$Targets = @(),

    [ValidateRange(1, 20)]
    [int]$Runs = 3,

    [string[]]$ExtraArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exeName = if ($IsWindows) { 'snipper.exe' } else { 'snipper' }

function Resolve-SnipperExe([string]$dir) {
    $candidate = Join-Path $dir $exeName
    if (-not (Test-Path $candidate)) {
        throw "No Snipper executable in '$dir'. Build it first: dotnet build src/Snipper/Snipper.csproj -c Release"
    }
    return (Resolve-Path $candidate).Path
}

function Get-FindingsHash([string]$reportPath) {
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    # @() matters: a report with zero or one finding is not an array, and .Count on a
    # scalar is null - which silently breaks the equivalence check on a clean run.
    $findings = @($report.findings)

    # Every field the report emits, using the real schema names. Getting these wrong is not
    # a crash under default PowerShell - a missing property quietly becomes $null and you
    # end up hashing a weaker fingerprint than you think, so StrictMode is doing real work
    # here. Note this means the hash also moves if the TARGET's source changes, which is
    # the correct behaviour: that is a genuine output difference.
    $canonical = $findings |
        Sort-Object ruleId, filePath, lineNumber, characterOffset, certainty, category, message |
        ForEach-Object {
            '{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}' -f $_.ruleId, $_.filePath, $_.lineNumber,
                $_.characterOffset, $_.certainty, $_.category, $_.message, $_.lineText
        }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($canonical -join "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return [pscustomobject]@{
        Count = $findings.Count
        Hash  = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 16)
    }
}

function Invoke-SnipperRun([string]$exe, [string]$target, [string]$workDir) {
    $report = Join-Path $workDir 'report.json'
    $arguments = @($target, $report) + $ExtraArgs
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $workDir 'stdout.txt') `
        -RedirectStandardError (Join-Path $workDir 'stderr.txt')

    if ($process.ExitCode -ne 0) {
        $stderr = (Get-Content (Join-Path $workDir 'stderr.txt') -Raw -ErrorAction SilentlyContinue)
        throw "Snipper exited $($process.ExitCode) on '$target'.`n$stderr"
    }

    return [pscustomobject]@{
        Wall = $process.ExitTime - $process.StartTime
        Cpu  = $process.TotalProcessorTime.TotalSeconds
    }
}

# --- targets -------------------------------------------------------------

# `pwsh -File script.ps1 -Targets a,b` hands the parameter over as ONE string, so accept a
# comma-separated list and split it. Array binding only works when dot-sourcing or calling
# from inside PowerShell, and this script is meant to be runnable from a shell prompt.
$Targets = @(
    foreach ($entry in $Targets) {
        if ($null -eq $entry) { continue }
        $entry -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    }
)

if ($Targets.Count -eq 0) {
    $Targets = @((Join-Path $repoRoot 'src/Snipper/Snipper.csproj'))
}

$missing = $Targets | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Warning "skipping missing target(s): $($missing -join ', ')"
    $Targets = $Targets | Where-Object { Test-Path $_ }
}

if ($Targets.Count -eq 0) {
    throw 'No targets to measure. Pass -Targets <path>[,<path>...].'
}

# --- binaries ------------------------------------------------------------

if ($Mode -eq 'Compare' -and -not $BaselineDir) {
    throw '-Mode Compare requires -BaselineDir <dir> (the baseline build).'
}

$currentDir = Join-Path $repoRoot 'src/Snipper/bin/Release/net10.0'
$current = Resolve-SnipperExe $currentDir
$baseline = if ($Mode -eq 'Compare') { Resolve-SnipperExe $BaselineDir } else { $null }

Write-Host "Snipper perf harness - mode: $Mode - runs: $Runs per target per binary"
Write-Host "  current : $current"
if ($baseline) { Write-Host "  baseline: $baseline" }
if ($ExtraArgs.Count -gt 0) { Write-Host "  args    : $($ExtraArgs -join ' ')" }
Write-Host ''

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "snipper-perf-$PID"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$results = @()

try {
    foreach ($target in $Targets) {
        $name = Split-Path $target -Leaf
        $walls = @(); $cpus = @(); $baselineWalls = @(); $baselineCpus = @()

        for ($i = 0; $i -lt $Runs; $i++) {
            if ($Mode -eq 'Compare') {
                # Alternate so both binaries see the same machine conditions.
                $baselineRun = Invoke-SnipperRun $baseline $target $workDir
                $baselineWalls += $baselineRun.Wall.TotalSeconds
                $baselineCpus += $baselineRun.Cpu
            }

            $run = Invoke-SnipperRun $current $target $workDir
            $walls += $run.Wall.TotalSeconds
            $cpus += $run.Cpu
        }

        $findings = Get-FindingsHash (Join-Path $workDir 'report.json')

        $row = [ordered]@{
            Target    = $name
            Findings  = $findings.Count
            Hash      = $findings.Hash
            Wall      = [math]::Round(($walls | Measure-Object -Average).Average, 2)
            WallBest  = [math]::Round(($walls | Measure-Object -Minimum).Minimum, 2)
            Cpu       = [math]::Round(($cpus | Measure-Object -Average).Average, 2)
            WallDeltaPct = $null
            CpuDeltaPct  = $null
        }

        if ($Mode -eq 'Compare') {
            $bw = ($baselineWalls | Measure-Object -Average).Average
            $bc = ($baselineCpus | Measure-Object -Average).Average
            $row.WallBaseline = [math]::Round($bw, 2)
            $row.CpuBaseline = [math]::Round($bc, 2)
            # Kept numeric and formatted only for display. Judging the verdict by
            # string-matching a formatted percent is fragile - '{0:P1}' does not reliably
            # emit a leading '+', so a real regression reads as "flat".
            $row.WallDeltaPct = [math]::Round(100 * ((($walls | Measure-Object -Average).Average - $bw) / $bw), 1)
            $row.CpuDeltaPct = [math]::Round(100 * ((($cpus | Measure-Object -Average).Average - $bc) / $bc), 1)
        }

        $results += [pscustomobject]$row
    }
}
finally {
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- report --------------------------------------------------------------

if ($Mode -eq 'Compare') {
    $results | Select-Object Target, Findings, WallBaseline, Wall,
        @{ n = 'WallDelta'; e = { '{0:+0.0;-0.0;0.0}%' -f $_.WallDeltaPct } },
        CpuBaseline, Cpu,
        @{ n = 'CpuDelta'; e = { '{0:+0.0;-0.0;0.0}%' -f $_.CpuDeltaPct } } |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Host

    Write-Host 'Verdict:'
    foreach ($r in $results) {
        # Numeric comparison, and an explicit noise band. Comparing a binary against ITSELF
        # through this harness produced -4.6% to +12.9%, which is why anything inside the
        # band is reported as inconclusive rather than as a win.
        $classify = {
            param($pct)
            if ($null -eq $pct) { 'n/a' }
            elseif ($pct -lt -5) { 'faster' }
            elseif ($pct -gt 5) { 'SLOWER' }
            else { 'noise' }
        }
        Write-Host ("  {0,-24} wall {1,8} ({2,6}%)   cpu {3,8} ({4,6}%)" -f `
                $r.Target, (& $classify $r.WallDeltaPct), $r.WallDeltaPct,
                (& $classify $r.CpuDeltaPct), $r.CpuDeltaPct)
    }
    Write-Host ''
    Write-Host 'Anything inside +/-5% is reported as noise, not as a win. CPU is the more'
    Write-Host 'reliable signal; CPU falling further than wall means genuinely less work.'
    Write-Host 'Findings Hash must be identical across both binaries for a'
    Write-Host 'behaviour-preserving change - check it, do not assume it.'
}
else {
    $results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host 'Findings Hash is the output-equivalence oracle - it must not move across a'
    Write-Host 'behaviour-preserving change. Compare against a previous run, or use -Mode Compare.'
}