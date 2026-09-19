# Perf Wave Retrospective (1.4.2/1.4.3) — REVERT DECIDED 2026-09-18

**Verdict:** `SolutionReferenceIndex` is reverted; SNP0005/0006 and SNP0009 return to `FindReferencesAsync`/`SymbolReferenceQuery`. Kept: `UnreachableCodeGate` (SNP0002/0009 flow pre-filter), SNP0019 single-pass diagnostics, the 1.4.1 conditional-access speculation guard. **Status: decided, not yet executed — this document is the plan.**

## Immediate next actions (execute in order)

1. Delete `src/Snipper/Analysis/SolutionReferenceIndex.cs` and `test/Snipper.Tests/SolutionReferenceIndexShould.cs`.
2. Restore `src/Snipper/Analysis/SymbolUsageCollector.cs` (from git history, commit `58ce63e`) and `test/Snipper.Tests/SymbolUsageCollectorShould.cs`.
3. Restore `UnusedNonPrivateMemberAnalyser` to the `58ce63e` `FindReferencesAsync` path (usage index + `SymbolReferenceQuery` + the two async rescue passes).
4. Restore `ProjectPackageUsageCache` line to `SymbolUsageCollector.CollectUsedAssembliesAsync(project, cancellationToken)`.
5. Revert SNP0009's (`UnusedLocalVariableAnalyser`) slow-path lookup to `SymbolReferenceQuery.HasAnyReferenceAsync(local, solution, ImmutableHashSet.Create(document), ct)` — **keep** the `UnreachableCodeGate.MayStartUnreachable` flow guard and restore the `System.Collections.Immutable` using.
6. Keep untouched: `UnreachableCodeGate.cs`, the SNP0002 gate call, SNP0019's single-pass `compilation.GetDiagnostics`, the 1.4.1 `?.` guard, all docs commits.
7. Update this file + roadmap status to REVERTED. Validate: full suite (~189 tests), dogfood 0 findings, fixture2 pins 23/25. Commit as a revert-forward (no `git reset` — preserve history and docs commits). Bump 1.4.4, pack, `dotnet tool update`.
8. User re-runs the monorepo as the final gate: expect ~1,532 SNP0005/0006 findings and ~180s total.

## Why reverted (user-set acceptance gate, breached)

Monorepo A/B, SNP0005/0006 findings: **1,532 (1.4.1) → 3,769 (1.4.3)** — an increase, which blocks.

| Analyser | 1.4.1 baseline | 1.4.3 run | Verdict |
|---|---|---|---|
| UnusedNonPrivateMember (SNP0005/0006) | 105.1s / 1,532 | 1.0s / 3,769 | reverted (FP flood) |
| UnreachableCode (SNP0002) | 47.1s / 0 | 24.2s / 0 | kept (gate, syntax-only, parity-exact) |
| UnusedLocalVariable (SNP0009) | 28.4s / 56 | 50.6s / 55 | index reverted, flow-gate kept |
| UnreferencedPackage (SNP0003/0004) | 17.9s / 47 | 1.6s / 47 | reverted with the index (was count-identical) |
| UnusedUsingDirective (SNP0019) | 14.2s / 207 | 6.4s / 207 | kept (count-identical) |
| UnusedPrivateMember | 2.1s / 19 | 6.2s / 19 | untouched code — variance/memory pressure |
| UnusedParameter | 1.7s / 8 | 2.5s / 8 | untouched — variance |
| Redundancy (SNP0022/25/26) | 1.9s / 72 | 8.7s / 72 | the 1.4.1 `?.`-guard subtree scan — own fix candidate (ancestor-check first) |
| Total | 224.7s | ~120–140s (partial log) | mixed; revert lands ~180s with kept pieces |

## Root cause — UNSOLVED

Two doctrine gaps were found and fixed, yet the delta remained:

1. **Contract-family cascade.** `FindReferencesAsync` cascades a member search to the interface members it implements, fellow implementations of those members (contravariance included), and override chains — all `OriginalDefinition`-unified (probe-proven; the index replicated it record- and query-side).
2. **Candidate locations.** Name matches Roslyn cannot confirm (broken bindings, `dynamic` receivers) counted as references; the index replicated them via an unconfirmed-name evidence set.

Findings still rose 2.4×. **Diagnostic path for the next attempt:** diff the 1.4.1 vs 1.4.3 JSON reports by rule+path+member, take 3–5 newly-flagged members (e.g., `MilkrunCustomer.UpdateFirstName` — an interface-dispatched DDD aggregate method), and inspect how their call sites bind in the workspace. Ranked hypotheses:

- **H1** — a cascade shape beyond impl↔contract-family: explicit interface implementations via metadata, base-class chains across assemblies, event/delegate subscription shapes.
- **H2** — duplicate type definitions: the user's own `// ambiguos references` comment atop `MilkrunCustomer.cs` — CS0433-style ambiguity (same interface defined in two assemblies) breaking call-site binding differently than FindReferencesAsync's candidates handled it.
- **H3** — multi-TFM project duplication splitting symbol identity across compilations.
- **H4** — a bug in the query-side family walk for a property shape (auto-props with `init`/`protected set` implementing interface properties).

## Kept knowledge (do not lose)

- **`FindReferencesAsync` cascade semantics** (probe-proven 2026-09-18): a member search matches references to the symbol, to interface members it implements, to fellow implementations of those members, and to override-chain members — unified across generic instantiations via `OriginalDefinition`. Empirically, `SymbolEqualityComparer.Default.Equals(...)` call sites kept an unrelated `IEqualityComparer<INamedTypeSymbol>` implementation alive.
- **Extension invocations bind the reduced method** — record/query `ReducedFrom`.
- **CS8019 (unused using) needs method-body binding** (a using is "unused" only if no body touches the namespace); CS8933 (duplicate-of-global) is declaration-phase. SNP0019's correct shape is one `compilation.GetDiagnostics` per project (14.2s → 6.4s, count-identical on the monorepo).
- **`UnreachableCodeGate`**: a statement's start is provably reachable unless an earlier sibling in an enclosing block or switch section can break fall-through (an exit statement, a branch whose every arm exits, or a possibly-infinite loop; `while/for/do` treated as possibly-infinite unless the condition is a literal `false`). Parity-exact with unconditional flow analysis; monorepo SNP0002 47.1s → 24.2s.
- **Parallel-binding spike: GO** — per-document parallel `GetSymbolInfo` on an MSBuildWorkspace-loaded solution: 24,301 bindings, 0 drift vs sequential, 12.5× (7.5s → 0.6s on Snipper.slnx). Adoption (1.4.4+ candidate) = parallelize per-document sweeps and/or a corrected harvest behind a revertible switch. Note: `FindReferencesAsync` is workspace-level — its thread-safety story differs from per-document queries and needs its own spike before parallelizing.
- **Honest small-solution tradeoff learned:** a shared one-pass harvest costs small solutions ~+3–4s while saving monorepos ~100s. If a corrected index ever returns, consider size-gating it.

## Pending decisions (unchanged)

- **Wave 4 direction** — options and gap-filler inventory in `Snipper-Feature-Parity-Roadmap.md` → *Next steps*.
- **Parallel-binding adoption** (spike GO, above).
- **SNP0026 upcast variant** (needs the rebind gate).

## Commits of the reverted wave (for archaeology)

- `7d05c53` — P1+P2+P6: SolutionReferenceIndex + SNP0005/0006 rewire + SymbolUsageCollector absorption.
- `10c7eb3` — P3+P4+P5: UnreachableCodeGate (kept) + SNP0009 index lookup (reverted) + SNP0019 single-pass (kept).
- `95ee551` — 1.4.3 unconfirmed-name evidence tier (reverted with the index).
- `28fc89d` — Release 1.4.2 (superseded).
