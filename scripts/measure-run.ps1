#Requires -Version 7.0
<#
.SYNOPSIS
    Repeatable timing harness for Snipper, with a determinism guard.

.DESCRIPTION
    Runs the locally built Snipper against a target N times and reports wall clock, total
    allocated bytes, GC collections and peak working set, as min/median across iterations.

    The determinism guard is the point. Snipper's report bytes are a hard contract, and the
    failure mode this harness exists to prevent is a performance change that quietly alters
    output. Every iteration's report is normalised for its volatile `generatedAtUtc` field and
    hashed; if any iteration disagrees with the others - or with -BaselineHash - the script
    fails rather than reporting a speedup. "Faster but wrong" is a regression, not a win.

    Statistics use min and median rather than mean. Repeat runs share warm MSBuild and NuGet
    caches, so the first iteration is systematically slower and a mean would be dragged by it;
    min is the least contaminated estimate of the underlying cost. A difference smaller than
    the spread between iterations is noise and should be reported as noise - see the measured
    cost tables in docs\history\1_7_0_plan.md.

.PARAMETER Target
    Solution or project file to analyse, e.g. .\MILKRUN.slnx

.PARAMETER Iterations
    Number of timed runs. Three is the practical minimum for a median worth quoting.

.PARAMETER MaxDop
    Pins SNIPPER_MAX_DOP so runs are comparable. Left at 0 the tool uses ProcessorCount, and
    timings then vary with machine load rather than with the code. Pin it when comparing two
    versions of the code; quote both numbers from the same setting.

.PARAMETER BaselineHash
    Report hash from a previous run. Supply it when measuring a change against a known-good
    build so the determinism guard compares across versions, not just within one invocation.

.PARAMETER SkipBuild
    Measure the existing Release output instead of rebuilding first.

.EXAMPLE
    .\scripts\measure-run.ps1 -Target C:\ws\milkrun\MILKRUN.slnx -Iterations 3 -MaxDop 8

.EXAMPLE
    # Before/after: capture a hash, change code, then assert output is unchanged.
    .\scripts\measure-run.ps1 -Target .\Snipper.slnx -Label before
    .\scripts\measure-run.ps1 -Target .\Snipper.slnx -Label after -BaselineHash <hash from before>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Target,

    [int] $Iterations = 3,

    [int] $MaxDop = 0,

    [string] $OutputPath = 'perf-report.json',

    [string] $BaselineHash = '',

    [string] $Label = '',

    [string] $ResultPath = '',

    [switch] $SkipBuild,

    # Explicit rather than ValueFromRemainingArguments: PowerShell treats a leading "--" as a
    # parameter prefix, so --duplicate-detection was silently bound to -BaselineHash instead of
    # reaching the tool. A harness that quietly drops the flag it was asked to measure is worse
    # than no harness, so extra flags must be passed by name.
    [string[]] $ExtraArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot 'src\Snipper\bin\Release\net10.0\Snipper.exe'

function Get-NormalisedReportHash {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Report not found at '$Path'. A run that produced no report cannot be compared."
    }

    # generatedAtUtc is DateTimeOffset.UtcNow by design and is the only field allowed to vary
    # between runs; ReportOrderingShould relies on it being the sole exception. Blank it, then
    # hash the raw bytes so the comparison covers formatting and ordering too, not just the
    # parsed values - a reordering that preserves the set would still change these bytes.
    $text = [IO.File]::ReadAllText($Path)
    $normalised = [regex]::Replace($text, '"generatedAtUtc"\s*:\s*"[^"]*"', '"generatedAtUtc":"<normalised>"')

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalised))
        return [Convert]::ToHexString($bytes).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-MeasuredRun {
    param([int] $Index)

    $stderrFile = [IO.Path]::GetTempFileName()
    $stdoutFile = [IO.Path]::GetTempFileName()

    try {
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $exe
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $arguments = @($Target, $OutputPath)
        if ($MaxDop -ge 1) {
            $psi.Environment['SNIPPER_MAX_DOP'] = [string] $MaxDop
        }
        $psi.Environment['SNIPPER_PERF'] = '1'

        # ArgumentList rather than a joined string: it quotes each argument itself, so a target
        # path containing spaces survives. A joined string would also be the natural place to
        # reintroduce the flag-dropping bug just fixed.
        foreach ($argument in ($arguments + $ExtraArgs)) {
            [void] $psi.ArgumentList.Add($argument)
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($psi)
        # Drained concurrently rather than after WaitForExit: a full stderr buffer on a long
        # run would otherwise fill and deadlock the child, which is the classic way a
        # measurement harness produces a hang instead of a number.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stopwatch.Stop()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            throw "Iteration ${Index}: snipper exited $($process.ExitCode).`n$stderr"
        }

        $perfLine = ($stderr -split "`r?`n" | Where-Object { $_ -like 'SNIPPER-PERF*' } | Select-Object -Last 1)
        if (-not $perfLine) {
            throw "Iteration ${Index}: no SNIPPER-PERF line on stderr. Was the binary rebuilt after PerfSummary was added?`n$stderr"
        }

        $perf = @{}
        foreach ($pair in ($perfLine -replace '^SNIPPER-PERF\s+', '') -split '\s+') {
            $kv = $pair -split '=', 2
            if ($kv.Count -eq 2) { $perf[$kv[0]] = $kv[1] }
        }

        return [pscustomobject]@{
            Index            = $Index
            WallSeconds      = [double] $stopwatch.Elapsed.TotalSeconds
            ReportedSeconds  = [double] ($perf['elapsed'] -replace 's$', '')
            AllocatedBytes   = [long] $perf['allocatedBytes']
            PeakWorkingSet   = [long] $perf['peakWorkingSetBytes']
            Gen0             = [int] $perf['gen0']
            Gen1             = [int] $perf['gen1']
            Gen2             = [int] $perf['gen2']
            ReportHash       = Get-NormalisedReportHash -Path $OutputPath
            FindingCount     = ([regex]::Match($stdout, 'Total Candidates Identified:\s*(\d+)')).Groups[1].Value
        }
    }
    finally {
        Remove-Item -LiteralPath $stderrFile, $stdoutFile -ErrorAction SilentlyContinue
    }
}

function Get-Median {
    param([double[]] $Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    $mid = [int] [Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $Target)) {
    throw "Target not found: $Target"
}

if (-not $SkipBuild) {
    Write-Host "Building Release..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'src\Snipper\Snipper.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Snipper.exe not found at '$exe'. Run without -SkipBuild."
}

Write-Host "Measuring: $Target" -ForegroundColor Cyan
Write-Host "  iterations=$Iterations  maxDop=$(if ($MaxDop -ge 1) { $MaxDop } else { 'default' })  label=$(if ($Label) { $Label } else { '-' })" -ForegroundColor DarkGray
if ($ExtraArgs.Count -gt 0) { Write-Host "  extra args: $($ExtraArgs -join ' ')" -ForegroundColor DarkGray }

$runs = @()
for ($i = 1; $i -le $Iterations; $i++) {
    Write-Host "  iteration $i/$Iterations ..." -ForegroundColor DarkGray
    $run = Invoke-MeasuredRun -Index $i
    $runs += $run
    Write-Host ("    wall {0,7:0.00}s   alloc {1,7:0.0} MB   peakWS {2,6:0} MB   gen0 {3,4}" -f `
        $run.WallSeconds, ($run.AllocatedBytes / 1MB), ($run.PeakWorkingSet / 1MB), $run.Gen0) -ForegroundColor DarkGray
}

$hashes = @($runs | Select-Object -ExpandProperty ReportHash -Unique)

Write-Host ''
Write-Host 'Results' -ForegroundColor Cyan
Write-Host ('  wall clock       min {0,7:0.00}s   median {1,7:0.00}s   spread {2,6:0.00}s' -f `
    ($runs.WallSeconds | Measure-Object -Minimum).Minimum, (Get-Median $runs.WallSeconds), `
    (($runs.WallSeconds | Measure-Object -Maximum).Maximum - ($runs.WallSeconds | Measure-Object -Minimum).Minimum))
Write-Host ('  allocated        min {0,7:0.0} MB   median {1,7:0.0} MB' -f `
    (($runs.AllocatedBytes | Measure-Object -Minimum).Minimum / 1MB), ((Get-Median $runs.AllocatedBytes) / 1MB))
Write-Host ('  peak working set min {0,7:0.0} MB   median {1,7:0.0} MB' -f `
    (($runs.PeakWorkingSet | Measure-Object -Minimum).Minimum / 1MB), ((Get-Median $runs.PeakWorkingSet) / 1MB))
Write-Host ('  gen0 collections min {0,7}' -f ($runs.Gen0 | Measure-Object -Minimum).Minimum)
Write-Host "  report hash      $($hashes[0])"
Write-Host "  findings         $($runs[0].FindingCount)"

# The guard. Three distinct failure modes worth naming separately, because "output changed"
# is not actionable on its own.
if ($hashes.Count -ne 1) {
    Write-Host ''
    Write-Host "FAIL: report hash differs between iterations ($($hashes.Count) distinct)." -ForegroundColor Red
    Write-Host '  Output is not deterministic across runs of the SAME binary.' -ForegroundColor Red
    foreach ($h in $hashes) { Write-Host "    $h" -ForegroundColor Red }
    exit 1
}

if ($BaselineHash -and $BaselineHash -ne $hashes[0]) {
    Write-Host ''
    Write-Host 'FAIL: report hash differs from -BaselineHash.' -ForegroundColor Red
    Write-Host "  expected $BaselineHash" -ForegroundColor Red
    Write-Host "  actual   $($hashes[0])" -ForegroundColor Red
    Write-Host '  A performance change must not alter report bytes. Reject or explain it.' -ForegroundColor Red
    exit 1
}

if ($BaselineHash) {
    Write-Host ''
    Write-Host 'PASS: report bytes identical to baseline.' -ForegroundColor Green
}

$result = [pscustomobject]@{
    label        = $Label
    target       = $Target
    iterations   = $Iterations
    maxDop       = $MaxDop
    extraArgs    = $ExtraArgs
    reportHash   = $hashes[0]
    findings     = $runs[0].FindingCount
    wallMin      = ($runs.WallSeconds | Measure-Object -Minimum).Minimum
    wallMedian   = Get-Median $runs.WallSeconds
    allocMin     = ($runs.AllocatedBytes | Measure-Object -Minimum).Minimum
    allocMedian  = Get-Median $runs.AllocatedBytes
    peakWsMin    = ($runs.PeakWorkingSet | Measure-Object -Minimum).Minimum
    peakWsMedian = Get-Median $runs.PeakWorkingSet
    runs         = $runs
}

if ($ResultPath) {
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8
    Write-Host "Result written to $ResultPath" -ForegroundColor DarkGray
}
