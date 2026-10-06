# 1.7.5 - data structures and allocations, measured rather than extrapolated

## Purpose

Audit every hot path for the wrong data structure or an avoidable allocation, then fix only what
could be shown to matter at real scale. The 1.7.4 wave withdrew a claim because a fixture
disproved it, so the bar for 1.7.5 was the same in reverse: no performance claim without a
measurement behind it.

## The scale problem, and how it was solved

`docs\code_architecture.md` recorded the honest gap that made this wave necessary: the largest
real solution available was **119 files**, and the only scale target was a synthetic
40-project / 800-file one. Every allocation multiplier in the audit was therefore arithmetic
extrapolated from 800 files to 3,900, and the audit said so itself.

`C:\ws\milkrun\MILKRUN.slnx` is 3,864 `.cs` files across 82 projects and was already on disk. So
the wave started by building the measuring instrument rather than the fixes:

- `scripts\measure-run.ps1` - N timed runs, min/median wall clock, total allocated bytes, GC
  counts, peak working set, plus a **determinism guard** that hashes each report with
  `generatedAtUtc` normalised out and exits 1 on any mismatch.
- `Cli\PerfSummary.cs` - opt-in via `SNIPPER_PERF=1`, one stderr line at exit. An environment
  variable rather than a flag so the tool's public surface and report bytes are untouched.

The guard earned its place twice: it caught `--duplicate-detection` being silently swallowed as a
`-BaselineHash` value, which had produced a clone baseline that was really a second default run.

## What the audit found, and what survived contact with the code

Verified by reading the code rather than the audit:

| Claim | Verdict |
|---|---|
| `FrozenSet`/`FrozenDictionary` under-used | **False.** 73 uses across 27 files, all frozen-once-then-queried with explicit comparers. Two real gaps, both minor. |
| No `Span`/`Memory`/`ArrayPool` in production | **True** - zero uses. But the top wins are not `Span` wins. |
| `IBufferWriter<T>` missing from the write path | **True**, and worth ~2 MB of LOH. Not where the win is. |
| `TokenShingleIndex.Normalize` allocates per token | **True** - fixed. |
| 4 tree walks per file where 1 suffices | **True** - not fixed, see below. |
| `seen` HashSet is provably dead in production | **True** - not fixed, see below. |
| `FrameworkEvidenceIndex` receiver text is "gated on the identifier first" | **False.** `code_architecture.md:839` and `1_7_0_plan.md:964` both claim this; `FrameworkEvidenceIndex.cs:374` evaluates `Expression.ToString()` before the gate at `:386`. Documentation is wrong about the code. |

Three suggested changes were **rejected after checking the real API**:

- `PackageAssemblyUsage.ExtractPackageId` -> `MemoryExtensions.Split`. On .NET 10 that overload
  takes a caller-supplied `Span<Range>` destination and reports only how many entries it wrote -
  a short buffer **truncates silently**, which would quietly stop matching a deeply nested path.
  Reverted; the reasoning is recorded at the call site.
- `SyntaxToken.GetLinePosition()` and `SyntaxToken.ValueSpan`. Neither exists in Roslyn 5.9.
  Reverted, and the site comment now records why.
- ~20 collections that read as `Frozen` candidates but are mutated after construction
  (`fingerprintMemo`, `SourceLineCache._linesByPath`, `GlobPattern.Cache`, `nameEvidence`, ...).
  Freezing any of them is a **bug**, not an optimisation. Listed so a later reader does not
  "helpfully" apply it.

## Measured result

MILKRUN, 3,864 files, 16-core x64, `SNIPPER_MAX_DOP=8`, Release. Two profiles, because
`--duplicate-detection` is opt-in and the shingling pass never runs on a default invocation.

### Default profile - 3 iterations, 2,782 findings

| Metric | Before | After | Delta |
|---|---|---|---|
| Allocated (min) | 27,406 MB | 25,922 MB | **-1,484 MB (-5.4%)** |
| Allocated (median) | 27,575 MB | 26,659 MB | -916 MB (-3.3%) |
| gen0 collections (min) | 3,576 | 3,375 | **-201 (-5.6%)** |
| Wall clock (min) | 193.48 s | 196.72 s | +3.2 s, inside a 14.8-25.2 s spread |
| Peak working set (min) | 1,929 MB | 2,007 MB | +78 MB, inside spread |

Every "after" allocation figure is below every "before" figure, so this is separation rather than
noise. Report hash `1c7c03c0...` **identical before and after** - the guard's PASS.

### Clone profile - 2 iterations, 7,135 findings

The first baseline read 136.9 s; the "after" read 178.1 s, which looked like a 30% regression. An
**interleaved** re-measurement of the previous commit read 176.3 s. The 136.9 s figure was taken
under quieter conditions, and the spread on that profile is 5-31 s. Compared like for like:

| Metric | Before (interleaved) | After | Delta |
|---|---|---|---|
| Allocated (min) | 28,852 MB | 28,462 MB | -390 MB (-1.4%) |
| gen0 collections (min) | 3,710 | 3,677 | -33 (-0.9%) |
| Wall clock (min) | 176.34 s | 178.08 s | +1.7 s, inside spread |
| Peak working set (min) | 2,244 MB | 2,234 MB | flat |

Report hash `82a05dd1...` **identical before and after**. This is the load-bearing result of the
wave: substituting `SyntaxFacts.GetText(kind) ?? token.Text` for `token.Text` in token
normalization left all 7,135 clone findings byte-identical, which is the only evidence that could
establish it as behaviour-preserving.

### What this wave did *not* buy, stated plainly

**No measurable wall-clock improvement.** The audit predicted "~120-190 MB of Gen0 garbage per
pass" and "3x CPU on the shingling pass" from the same arithmetic that produced a 7.5 M-entry dead
set. Measured, the whole fix-now batch is worth 1-5% of allocations and nothing on the clock.

The reason is the finding the audit ranked as a side note and that measurement promotes: **a
MILKRUN run allocates ~27 GB and spends its time in work this wave never touches.**
`MSBuildWorkspace.Create()` (`CliRunner.cs:70`) is called with default properties, which
design-time-builds every project *and every referenced project* to resolve SDK imports. Removing
Gen0 churn in the analysis phase cannot move a total that is dominated by workspace load. The
audit's own numbers were extrapolations and overstated by roughly an order of magnitude; the
honest result is that they were not worth acting on as stated.

## Changes

| File | Change |
|---|---|
| `Cli\PerfSummary.cs`, `Program.cs` | Opt-in `SNIPPER_PERF=1` resource summary |
| `scripts\measure-run.ps1` | Measurement harness with determinism guard |
| `Analysis\TokenShingleIndex.cs` | `SyntaxFacts.GetText` for fixed-text kinds; `token.Text` read once instead of twice; `Collect` returns both halves from **one** walk |
| `Analysis\DuplicateFragmentAnalyser.cs` | Hands `Build` the tokens `CollectFiles` already produced — **four tree walks per file reduced to one** |
| `Analysis\TokenShingleIndex.cs` | Shingle dedup by **path** rather than by `(path, window index)`: ~7.5M entries -> <4,000, guarantee and test unchanged |
| `Analysis\ExclusionEngine.cs` | `obj` segment hoisted to static fields; both separator spellings tested as spans instead of copying the path |
| `Cli\ConsoleRenderer.cs` | `StringComparer.Ordinal` on the path sort - **a determinism fix, not a perf one** |
| `Cli\GitMetadata.cs`, `Analysis\GitHistory.cs` | `int.TryParse` span overloads; the sliced strings existed only to be parsed |

The `ConsoleRenderer` sort is worth calling out. Without an explicit comparer it fell back to
culture-sensitive ordering, so the console table could list the same findings in a different order
than the JSON and SARIF writers, varying with machine locale. Every other sort in the tool is
explicit ordinal for exactly this reason.

## Phase split - where the time actually goes

The audit ranked "tune `MSBuildWorkspace.Create()`" as a side note while I promoted it in
conversation to "the dominant cost, possibly bigger than everything combined". **Both were wrong,
and measuring the split is what caught it.** One MILKRUN clone run, timings the tool already
prints:

| Phase | Wall clock | Share |
|---|---|---|
| Workspace load, config, report write | 26.1 s | 14.6% |
| Analysis | 152.7 s | **85.4%** |
| Total process | 178.8 s | |

So workspace loading is a seventh of the run, not the bulk of it. MSBuildWorkspace tuning was
**not attempted**: the ceiling on the whole idea is 14.6%, and the load has to happen to analyse a
solution at all.

Per-analyser wall clock (parallel, DOP 8, so these overlap and sum past the phase total):

| Analyser | Wall clock |
|---|---|
| `UnusedLocalVariableAnalyser` | **94.7 s** |
| `UnusedUsingDirectiveAnalyser` | 51.4 s |
| `UnusedPrivateMemberAnalyser` | 44.8 s |
| `UnusedParameterAnalyser` | 40.3 s |
| `DuplicateFragmentAnalyser` | 28.7 s |
| `ObsoleteMemberAnalyser` | 12.0 s |
| `WriteOnlyFieldAnalyser` | 9.4 s |
| `HierarchyDeadCodeAnalyser` | 7.1 s |
| `UnreachableCodeAnalyser` | 5.4 s |
| `CommentedCodeAnalyser` | 2.6 s |
| `OrphanProjectAnalyser` | 1.1 s |
| `EventNeverInvokedAnalyser` | 0.8 s |

One analyser is **53% of the analysis phase on its own**. No amount of tuning in the other eleven
competes with that, and it is not where the audit pointed.

## Shingling: one walk per file, and a set that was 7.5M entries

`TokenShingleIndex` walked every tree **four times** per file. `DuplicateFragmentAnalyser.CollectFiles`
called `Tokenize` and `TokenLines` - each a full recursive traversal - and then handed `Build` the
bare roots so it could re-derive both. `TokenShingleIndex.Collect` now returns a `TokenizedFile`
(tokens plus aligned line positions) from one walk, and `Build` has an overload that takes
already-tokenized files and walks nothing. The syntax-tree overload is retained for the tests and
delegates through a **lazy** projection, so each file's tokens are still released after its windows
are hashed rather than every stream being held at once.

The dead `seen` set was **not** deleted, which is the decision worth recording. Its documented
guarantee - a linked file or multi-TFM document contributing one copy - is real and is covered by
`Dedupe_Locations_By_Path`. But the guarantee never depended on the per-window key: a repeated path
means every window it could contribute is already recorded, so skipping the file outright is
equivalent. Deduplicating **by path** keeps the guarantee and its test, and replaces a set with one
entry per window with one entry per file. The only behaviour this gives up is a pathological caller
handing the same path two different token streams, which contradicts the path-identity contract the
type documents.

Measured on the clone profile, 3 iterations, report bytes identical:

| Metric | Before | After | Delta |
|---|---|---|---|
| Allocated (min) | 28,852 MB | 28,082 MB | -770 MB (-2.7%) |
| Peak working set (min) | 2,244 MB | 2,213 MB | -31 MB (-1.4%) |
| gen0 collections (min) | 3,710 | 3,638 | -72 |
| Wall clock (min) | 176.34 s | 179.31 s | inside a 43 s spread |

The audit predicted roughly 150 MB of live set for that `seen` entry. Measured, removing it freed
**31 MB**. Third extrapolation in this project to overstate by about an order of magnitude, and
the pattern is consistent enough to be worth naming: multiplying a small per-item allocation by a
file count describes Gen0 churn accurately and describes peak memory very badly, because the
garbage is collectable and was never resident all at once.

Note also that `DuplicateFragmentAnalyser` as a whole is **28.7 s of a 152.7 s phase**. Cutting
three of its four tree walks cannot show up in the total, which is exactly what was observed.

## Not done, and why

- **`MSBuildWorkspace.Create()` tuning - rejected on measurement.** Workspace load is 14.6% of a
  run, so this cannot be where the time is. Attempting it would have been optimising a phase that
  has to happen anyway.
- **`UnusedLocalVariableAnalyser` - identified, not attempted.** 94.7 s, 53% of the analysis
  phase, and by a wide margin the best remaining target. It is a different analyser with a
  different set of candidate fixes (a per-document `GetSemanticModelAsync`, `AnalyzeControlFlow`
  per candidate, `FirstAncestorOrSelf` per candidate, and the `DocumentIdentifierIndex` build),
  and attributing its cost needs profiling rather than another extrapolation. Deliberately left
  for its own change.
- **`ConfigurationBindingAnalyser.FindKeyLocation`** re-reads the whole file per unbound JSON key -
  genuinely quadratic, with no cache. Bounded by config files, so it does not appear in the
  per-analyser table above, which covers symbol analysers.
- **`ReportWriter` streaming.** Worth ~2 MB of LOH and one fewer transcode, but the `string` return
  type is load-bearing for 10 test call sites and the newline bytes must be verified unchanged.
- **`DocumentIdentifierIndex` -> `FrozenDictionary`.** Correct but adds ~1.8 MB peak at scale to
  save modest CPU. Poor trade.

## Verification

- Build: 0 errors. Test suite: **590/590 passing**, unchanged from 1.7.4.
- Self-analysis: zero findings attributable to this wave; the 3 pre-existing self-findings are
  unchanged.
- Report bytes identical on both MILKRUN profiles, enforced by the harness rather than asserted.
