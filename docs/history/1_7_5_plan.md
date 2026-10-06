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

## SNP0009 attribution: one call is 97% of the pass

`UnusedLocalVariableAnalyser` was the largest single contributor, so it was profiled directly with
temporary env-gated instrumentation (`SNIPPER_PROFILE=localvar`), since region-level timers inside
the loop attribute cost far better than a sampling profiler at this resolution. **The scaffolding
was deleted after the run and is not in the tree.** Two lessons from building it are recorded at the
top of what it replaced, because both produced confidently wrong numbers first time:

- Counters held in `ref` fields belonging to the *analyser*, while the printer read its own fields.
  Every counter printed zero, and the run still looked plausible.
- A counter incremented **per node** rather than per candidate made the pass **2.7x slower**
  (254.8 s against a 94.7 s baseline) - tens of millions of contended `Interlocked` operations on
  one cache line across eight threads. A profiler that slows the thing it profiles by 2.7x cannot
  attribute it.

With those fixed, one MILKRUN run:

| Region | Thread-seconds | Calls |
|---|---|---|
| `GetDeclaredSymbol` | **72.4 s** | 18,494 |
| &nbsp;&nbsp;first call in a document | 21.1 s | ~3,004 (7.0 ms each) |
| &nbsp;&nbsp;every subsequent call | 51.2 s | ~15,490 (**3.3 ms each**) |
| `HasReference` | 1.3 s | 18,494 |
| `DocumentIdentifierIndex.Build` | 0.1 s | 3,004 documents |
| `AnalyzeControlFlow` | 0.0 s | 132 |
| `GetSemanticModelAsync` | 0.0 s | 3,004 |
| Whole `DescendantNodes` walk | 74.4 s | - |
| **Pass wall clock** | **76.2 s** | |

`GetDeclaredSymbol` is **97% of the pass**, at 3.3 ms per call on a warm model. That is the
anomaly worth explaining: declaring the symbol of a local variable should be microseconds once the
model is built. The cost is Roslyn constructing the **method-body binder** for each method that
contains a local declaration - roughly 15,500 method bodies, priced at a few milliseconds each.

**The fast path in front of it fires 31 times in 18,534.** `IsNameUsedInDocument` asks whether the
name appears in a usage position *anywhere in the document*, and local names are reused across
methods in the same file, so 99.83% of candidates fall straight through to semantic binding. The
gate is not merely weak, it is close to inert, and it is guarding a 3.3 ms call.

The fix that follows, **not implemented here** because it changes what the rule proves and deserves
its own change with its own tests: a local cannot be referenced outside the block that declares it,
so the occurrence test should be **block-scoped rather than document-scoped**. `DocumentIdentifierIndex`
already holds every `SimpleNameSyntax` position per name, so the test is a span containment check
over already-built data - no binding at all. Anything the block-scoped test cannot clear still takes
the existing slow path unchanged, so it can only convert work, never lose a finding.

This is also the codebase's own idiom, used correctly twice already: `UnreachableCodeGate` puts a
hand-rolled syntactic gate in front of `AnalyzeControlFlow` (and it works - 132 calls out of 18,534
candidates), and `RedundancyAnalyser` replaced five tree walks with one. SNP0009 has the same shape
with the gate at the wrong granularity.

**Why the prize is large.** The pass consumed 72.4 s of thread time in 76.2 s of wall clock, so it
runs **effectively serially** - it is a plain sequential `foreach`, not a `Parallel.ForEach`. Its
cost converts to wall clock nearly one-for-one, and because it is the longest analyser it sets the
analysis phase's critical path. Cutting 90% of the 72.4 s would take this pass to roughly 10 s and
let the 152.7 s analysis phase fall toward the next-longest analyser (~51 s). That is a far bigger
lever than anything else measured in this wave, and unlike the earlier estimates it comes from a
measurement rather than an extrapolation.

## A caveat the harness does not cover: output is not stable across time

Two reports of the same clean binary on the same target, roughly an hour apart, disagreed:

| Rule | Run A | Run B |
|---|---|---|
| SNP0003 | 16 | 15 |
| SNP0006 | 1,501 | 1,484 |
| SNP0019 | 190 | 202 |
| SNP0024 | 728 | 729 |
| **Total** | **2,782** | **2,777** |

SNP0009 emitted 56 findings in both, and the profiling run made the same four rules move, which
briefly looked like the instrumentation's fault. Reverting the instrumentation and rebuilding
reproduced **2,777** - so the profiler was innocent and the drift is environmental. The rules that
moved are binding- and restore-dependent, and milkrun's `obj/` and `project.assets.json` state is
part of the effective input whether or not the sources changed.

`measure-run.ps1` verifies byte-identical output across **iterations of one invocation**, which is
exactly what an A/B needs and is why every comparison in this document held. It does **not** cover
drift between sessions, and that has a practical consequence: **a hash mismatch on an A/B may be
environmental rather than a regression.** Interleave the two builds, or re-run a baseline before
attributing a difference to a code change. The alternative - assuming any mismatch is your fault -
would send the next person hunting a bug that is not there.

## NEXT CHANGE (not started): block-scoped occurrence gate for SNP0009

Everything below is the specification for the next piece of work, written down so it can be picked
up without re-deriving the analysis. **Version is undecided**: 1.7.5 is not yet packed or
version-bumped, so this could fold into it or ship as 1.7.6. It is a distinct change with its own
tests either way.

### The task

In `UnusedLocalVariableAnalyser.AnalyzeAsync`, the slow path is entered 18,494 times out of 18,534
candidates and costs 72.4 s of the pass's 76.2 s. Add a **block-scoped** syntactic occurrence test in
front of it.

### Why block scope, and why it is sound

A local cannot be referenced from outside the subtree that declares it - that is the premise the
rule's own class comment already rests on ("a local cannot be referenced from outside its declaring
method, by reflection, or from another file"). The existing fast path is **document**-scoped, so a
`count` in method A forces semantic binding for an unrelated `count` in method B, which is why it
fires 31 times in 18,534.

Every syntactic form of a reference to a local is an `IdentifierNameSyntax`, which is what
`DocumentIdentifierIndex` already buckets by name:

- bare `x` -> `IdentifierNameSyntax`
- `x.Y` -> the `Expression` of the `MemberAccessExpressionSyntax`
- `nameof(x)` -> `IdentifierNameSyntax`

So "no indexed position for this name inside the declaring member" **proves** absence, and a test
built on it can only ever convert work, never lose a finding. Anything it cannot clear falls through
to the existing slow path unchanged.

### Design constraints

- **Container choice matters and must be conservative.** Use the enclosing **member**
  (`BaseMethodDeclarationSyntax`, `AccessorDeclarationSyntax`, `LocalFunctionStatementSyntax`),
  falling back to the compilation unit for top-level statements. Do **not** use the innermost
  `BlockSyntax`: for `if (TryGet(out var v)) { Use(v); }`, `v`'s scope is the enclosing block, so an
  inner block would be too narrow and could wrongly report `v` as unused. A member is a superset of
  every block inside it, so it is always sound.
- **Verify the declaration itself is not in the index.** `VariableDeclaratorSyntax.Identifier` is a
  `SyntaxToken`, not a `SimpleNameSyntax`, so it should not appear - confirm this rather than assume,
  and exclude its span explicitly if it does.
- The check is a span-containment test over already-built data, so it should be a loop over
  `_positionsByName[name]` testing `position.SpanStart` against the container's `FullSpan`. No
  binding, no allocation.
- New API belongs on `DocumentIdentifierIndex` (it owns `_positionsByName`), named for what it
  proves, e.g. `HasOccurrenceWithin(string name, SyntaxNode container)`.

### Verification, in order

1. `dotnet build Snipper.slnx --nologo` - 0 errors.
2. `dotnet test Snipper.slnx --nologo` - 590/590, the current baseline.
3. **SNP0009 must still emit exactly 56 findings on MILKRUN**, unchanged. This is the load-bearing
   correctness check; the harness hash guard also covers it.
4. Measure the pass wall clock from the analyser's own progress line
   (`UnusedLocalVariableAnalyser: N finding(s) in X.Xs`). Baseline **76.2 s**; the analysis phase is
   **152.7 s** and the next-longest analyser is ~51 s, so this pass sets the critical path.
5. Self-analysis must stay at the 3 pre-existing findings, none in changed files.

### Measurement commands

```console
# baseline capture (do this immediately before changing anything - see the drift caveat below)
.\scripts\measure-run.ps1 -Target C:\ws\milkrun\MILKRUN.slnx -Iterations 3 -MaxDop 8 `
    -OutputPath artifacts\perf-before.json -ResultPath artifacts\perf-before.result.json

# after the change, guarded by the baseline hash
.\scripts\measure-run.ps1 -Target C:\ws\milkrun\MILKRUN.slnx -Iterations 3 -MaxDop 8 `
    -BaselineHash <hash from before> -OutputPath artifacts\perf-after.json
```

Set `SNIPPER_PROFILE=localvar` to re-enable profiling if the file is restored from history; as
committed it is **deleted**, deliberately, because it is scaffolding rather than a product feature.

### Traps already hit in this wave - do not re-walk them

| Assumption | Reality |
|---|---|
| `SyntaxToken.ValueSpan`, `SyntaxToken.GetLinePosition()` | Neither exists in Roslyn 5.9. Check the API surface before designing around it. |
| `MemoryExtensions.Split` on `ReadOnlySpan<char>` | Takes a **caller-supplied `Span<Range>`** and truncates **silently** on overflow. A short buffer would stop matching a nested path. |
| Profiler counters via `ref` to another class's fields | Printed all zeros while still looking plausible. |
| One `Interlocked` counter **per node** | Made the pass **2.7x slower**; tens of millions of contended writes on one cache line. |
| A hash mismatch means the change broke output | It may be **environmental drift**. Re-run a baseline before blaming the code. |

## Not done, and why

- **`MSBuildWorkspace.Create()` tuning - rejected on measurement.** Workspace load is 14.6% of a
  run, so this cannot be where the time is. Attempting it would have been optimising a phase that
  has to happen anyway.
- **`UnusedLocalVariableAnalyser` - profiled, fix identified, not implemented.** 94.7 s, 53% of the
  analysis phase. `GetDeclaredSymbol` is 97% of it, at 3.3 ms per call because it builds a
  method-body binder, and the document-scoped fast path in front of it fires 31 times in 18,534.
  The fix is a block-scoped occurrence test. See "SNP0009 attribution" above. Not implemented here
  because it changes what the rule proves and needs its own tests.
- **A pre-existing caveat, now documented:** report output is not stable across sessions on a target
  whose restore state can move under the tool. See "output is not stable across time" above.
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
