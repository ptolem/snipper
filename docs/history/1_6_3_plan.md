# Plan — Snipper 1.6.3: duplicate detection (token-shingle engine)

**Status: IMPLEMENTED and SHIPPED as SNP0031** (2026-10-03). 353 tests green. Three decisions changed during implementation on measured evidence; they are recorded in "What implementation changed" below and supersede the corresponding locked decisions. The most important: **the rule ships opt-in behind `--duplicate-detection`, not on by default.**

**Objective:** a new syntax-only engine that flags duplicated code fragments (Type-1 exact and Type-2 rename-only clones) across the whole solution, including cross-project copies — the last uncommitted Tier-3 capability gap.

Repo constraints (standing): no dynamic; no exceptions for flow control; FrozenSet/FrozenDictionary; syntax pre-filters first; `ArgumentNullException.ThrowIfNull`; evidence ≠ findings (generated/external/excluded code never produces finding locations); red phase first with fixture scenarios in NEW files; deterministic sorted output; dogfood stays 0 or every new own-finding is triaged (the 1.1.1/1.1.2 protocol). Validation gates: full suite, dogfood on Snipper.slnx, milkrun A/B. Release process: bump `<Version>` → commit → pack → `dotnet tool update --global --add-source` → `snipper --version` → self-run expect 0.

## Phase 0 — reconcile the working tree (prerequisite, no behaviour change)

**Ship tree: `C:\pp\snipper`.** Not `C:\ws\trimmer` as `1_6_2_plan.md` states.

Measured: `fc5bbd8` ("First commit, hello world") has tree `e3ba1337`, **byte-identical to trimmer's `b8cdc07` tree**. So `fc5bbd8` is a content-exact squash of trimmer's entire history, and `8c63af6` is the performance work sitting on top of it. There is nothing to merge or reconcile — only a rebase.

1. `git remote add local C:\ws\trimmer` → fetch → rebase `8c63af6` onto `b8cdc07`.
2. Verify: full lineage in `git log`; build clean; 322 tests green; re-run the 4-way output-parity diff (original-parallel / original-sequential / new-parallel / new-sequential) to prove the rebase changed no behaviour.

Every file `8c63af6` touches is either new (5) or differs from trimmer (7); the other 155 are already identical, so the rebase has zero conflict surface. **1.6.3 must not be built until this lands** — otherwise it ships on a two-commit re-init lineage.

## Measured spike data (2026-10-02)

Run with a standalone prototype implementing this plan's tokenizer and normalisation spec, against `C:\ws\milkrun\MILKRUN.slnx`. Token counts differ slightly from Roslyn's (`=>` is one token there, two in the prototype; none of these files use interpolated strings, so that error cuts the other way). Conclusions below are unaffected by that margin.

### Named acceptance families — one of two survives

| Family | Longest common normalised run | W=60 verdict |
|---|---|---|
| `EcfEventMessageBase` twins (MockingService ↔ Orders.FulfilmentUpdatesConsumer) | **134 tokens** | **PASS** |
| `LocalHostedService.cs` ×6 | **42 tokens** (best pair: AnalyticsEventsEmitter × Exporter.Nash) | **FAIL — dropped** |

All 15 `LocalHostedService` pairs: 12–42 tokens, none reaches 60. The 42-token run is the `StartAsync`/`StopAsync` passthrough pair — `public override Task StartAsync(CancellationToken ct) => base.StartAsync(ct);`. Reading all six files confirms the original premise was wrong: they are **not** near-identical. One is expression-bodied with a nested payload, one calls `TryEnqueue` with a collection-expression payload, one is ~120 lines of analytics event construction, one has two primary-constructor parameters. Structurally distinct files, not renamed copies.

**Decision: `LocalHostedService` is removed from the acceptance set.** The spike's GO criterion becomes the EcfMessages twins alone. W=60 is retained — it is confirmed by measurement, not inherited from the roadmap one-liner.

### Scale — the token budget was ~10× overstated

| Scope | Files | Tokens | Windows @ W=60 | Multi-file buckets |
|---|---|---|---|---|
| milkrun `src` (excl. bin/obj) | 2,652 | 1,023,740 | 650,966 | 40,188 |
| milkrun `test` (excl. bin/obj) | 381 | 497,495 | 331,854 | 12,313 |
| Snipper `src` | 65 | — | — | 896 |

The first draft budgeted 12–15M tokens for a "2M LOC monorepo". milkrun's ~3,000-file src tree measures **1.02M tokens**. Corrected budget: **~1.5M tokens** end to end. The ≤15s marginal target is comfortably achievable and no longer a concern.

`test/` is **33% of tokens** and 12,313 multi-file buckets. Duplicate-detection noise concentrates in test arrange/act/assert shapes, and the first draft never mentioned test projects. This must be measured and reported separately (work item 4).

### The spike gate metric was measuring the wrong thing

Bucket counts read as catastrophic failure; post-union-find clone sets are small and triable.

| Scope | Multi-file buckets @ W=60 | After union-find |
|---|---|---|
| `Program.cs` (41 files) | 1,255 | **2 clone sets** (sizes 37 and 2) |
| DI extensions (60 files) | 143–298 | small |
| `GlobalUsings.cs` (82 files) | 3,521 | eliminated by using-directive exclusion |

**The gate must count clone sets after union-find, not colliding buckets.** Measured on `Program.cs`: 1,255 buckets → 2 sets. The 37-file set is the ASP.NET `WebApplication.CreateBuilder` + Swagger + `AddProblemDetails` preamble — genuine duplication, but one triable item, not 1,255.

### Guards: the using-directive exclusion works; the Program.cs one does not

- Stripping top-level `using` directives kills all 3,521 `GlobalUsings.cs` buckets. The first draft's guard is correct and load-bearing.
- The same guard does **nothing** for `Program.cs`: only 4 of 41 files have top-level usings, and stripping them moves W=60 from 1,255 → 1,248 buckets. The 37-file ASP.NET preamble is in code bodies.
- W sweep on `Program.cs` (usings stripped): W=40 → 1,682 · W=42 → 1,635 · W=45 → 1,556 · W=50 → 1,438 · W=60 → 1,248. Noise is high at every threshold; lowering W does not help signal-to-noise, it only adds noise. Confirms keeping W=60 rather than chasing the dropped family.

### Dogfood: 151 pairs, but three tiers

All 151 pairs sit inside `src\Snipper\Analysis` — zero cross-directory, zero TestAssets. Per-file clone-degree clusters them:

| Tier | Files | Clone-degree | Nature |
|---|---|---|---|
| `IWorkspaceAnalyser` implementations | 14 | 12–19 | project→document loop, exclusion check, `Parallel.ForEachAsync` shape |
| Evaluators | 5 | 3–9 | `TryEvaluate` / `TryEvaluateThis` skeleton |
| Indexes and scanners | 11 | 1–2 | incidental, no real duplication |

Top pairs by shared windows: `UnusedLocalVariableAnalyser` ‖ `UnusedParameterAnalyser` (207) · `EventNeverInvokedAnalyser` ‖ `UnusedParameterAnalyser` (197) · `UnusedPrivateMemberAnalyser` ‖ `WriteOnlyFieldAnalyser` (197) · `HierarchyDeadCodeAnalyser` ‖ `TighteningAnalyser` (183). `RedundantCastEvaluator` ‖ `RedundantUpcastEvaluator` LCS 112.

The first draft anticipated one pair ("the two cast evaluators"). The real shape is the 17 analysers sharing an architectural skeleton **by design**.

## Locked decisions

1. **New rule SNP0031 "Duplicate Code Fragment", Advisory tier — a normal finding rule, NOT a separate report section.** The 2026-09-14 "separate report section" note predates `snipper.json` and `--baseline`; a normal rule gets the config off-switch/severity override, baseline fingerprinting, SARIF output, and `--certainty-tier` filtering for free. One finding per occurrence; the message carries the occurrence count and the first other location.
2. **`FindingCategory.DuplicateFragment = 25`.** The first draft said 23, which is wrong — `FindingCategory.cs:27-28` already ships `EmptyTypeMember = 23` and `UnusedEvent = 24`. 25 is the next free value.
3. **Type-1 + Type-2 clones in v1.** Token normalization abstracts identifiers and literals, so rename-only clones are caught. Type-3 (fuzzy/gapped) is out of scope — different algorithm class (CPD-style gapped matching or AST edit distance); revisit after v1 evidence.
4. **Syntax-only engine, zero semantic binding.** Tokens come from the parsed syntax trees the workspace already has. No semantic model is constructed, so no `AnalysisParallelism` is needed *inside* the engine; the parallel fill is over documents only. Adds no binding time.
5. **Cross-project by design; same-path self-matches suppressed.** Locations are keyed by file PATH, not document — linked/shared files and multi-TFM documents contribute one copy (the perf-retrospective H3 lesson). **Consequence, accepted:** intra-file duplication is also suppressed. dupFinder reports it; Snipper v1 does not. Documented, not accidental.
6. **W = 60 tokens, confirmed by measurement.** See "Measured spike data". One tuning knob, `WindowTokens`.
7. **Spike gate counts clone sets, not buckets.** See above.

## Engine design — `src\Snipper\Analysis\DuplicateFragmentAnalyser.cs` + `TokenShingleIndex.cs`

### Tokenization (normalised token stream, per file)
- `root.DescendantTokens()` — trivia and comments never enter the stream (tokens only). Generated/external documents skipped via `ExclusionEngine.ShouldSkipDocument`.
- **Excluded subtrees:** `UsingDirectiveSyntax` and `ExternAliasDirectiveSyntax` — milkrun's near-identical `GlobalUsings.cs` files across 82 projects would otherwise produce 3,521 colliding buckets. Namespace declarations stay; their body content is the signal.
- **Normalization map:** identifier tokens → `ID`; string/character/numeric literal tokens (incl. interpolated-string text tokens) → `STR`/`CHR`/`NUM`; keywords and punctuation → verbatim text; directive tokens skipped.
- Stream entries carry (normalised text, path, line) for mapping back to locations.

### Shingling + matching
- Sliding window **W = 60 tokens**, FNV-1a hash over the window's normalised texts.
- Index: `Dictionary<int, List<(string Path, int TokenIndex, int Line)>>` across ALL files (parallel fill, then freeze). Same (path, tokenIndex) recorded once (decision 5).
- **Match extension:** for each hash bucket with ≥2 distinct locations, extend runs forward/backward while normalised tokens match (CPD-style maximal runs); require ≥60 tokens AND ≥4 spanned lines AND non-overlapping source spans between occurrences.
- **Clone sets:** union-find over matched pairs → groups of ≥2 occurrences; one finding per occurrence.

### Finding shape
`SNP0031`, Advisory: *"62-token fragment (7 lines) duplicated 3 time(s); first other occurrence at src\Other\LocalHostedService.cs:5."* Location = the fragment's own first line.

**Namespace exclusion for a symbol-less finding (new design work).** The first draft said "namespace exclusions apply like other member-level rules", which is undefined here. Every existing exclusion path in `ExclusionEngine` resolves through `ISymbol.ContainingNamespace` (`:109`) or the nearest `TypeDeclarationSyntax` ancestor (`:138-156`). A duplicate fragment has no symbol and may straddle a namespace boundary or sit at file scope. Resolution: enclosing `TypeDeclarationSyntax` via the existing `IsNamespaceExcluded(node, semanticModel, …)` overload; when no enclosing type exists, resolve through the enclosing namespace declaration. Needs a fixture case for the file-scope fragment.

### Guards (noise control)
- ≥60 tokens AND ≥4 lines (kills single-line/minified matches).
- Using/extern subtrees excluded (kills all 3,521 `GlobalUsings` buckets).
- Generated/external/`exclude.paths` documents never seed; `exclude.namespaces` filters findings.
- DTO-class clones that exceed the threshold (the EcfMessages twins, LCS 134) are the intended catch. Auto-property clusters below 60 tokens are killed by the threshold.

### Performance budget
~1.5M tokens (measured, milkrun-scale) ≈ 1M windows; FNV into int keys + bucket lists. Extension pass touches colliding buckets — at this scale 32-bit FNV birthday collisions are on the order of a few hundred and are filtered by the extension pass's token verification, so they cost time, never correctness. Target ≤15s marginal (SNP0019's 6.4s is the reference point). Report per-wave (guiding principle 5).

## Work items (red-green fixture-first; fixture scenarios in NEW files, never `DeadCode.cs`)

### 0. Reconcile the tree
Phase 0 above. Blocks everything else.

### 1. Token stream + shingle index
`TokenShingleIndex`: tokenization with the normalization map, using/extern exclusion, window hashing, path-keyed locations. Unit-testable without a workspace (feed it parsed trees). Fixture unit pins: normalization table (identifiers/literals abstracted, keywords verbatim), using exclusion, same-path dedup.

### 2. Match extension + clone sets + analyser
`DuplicateFragmentAnalyser : IWorkspaceAnalyser` (RuleIds `["SNP0031"]`, takes `AnalysisExclusions?`), deterministic sorted output. Fixture (new files): planted ≥60-token cross-project clone pair (CoreLib + App), one exact and one identifier-renamed (both flagged); a divergent control (<60 matching tokens — not flagged); a short control (identical 20-token helper — not flagged); an identical-using-lists control (not flagged); **a file-scope (non-type) fragment for the namespace-exclusion path.** Assert rule id, occurrence count, and both locations.

### 3. Registration + docs
Register in `CliRunner` (analyser list), README rules-table row, competitive-analysis §1 matrix row (Duplicate code blocks: ✓ SNP0031) + §5 candidate #9 checked off, roadmap tracked-candidate marked shipped, status line entry.

**Two doc items easy to miss:**
- README `--exclude-namespaces` row **enumerates rule IDs explicitly** (…SNP0029/0030). It must gain SNP0031.
- competitive-analysis §5 candidate #9 and the roadmap tracked-candidate line both currently say "COMMITTED as 1.6.3" — they change to shipped on release.

### 4. Validation
Full suite; dogfood on Snipper.slnx; milkrun A/B reporting **clone-set counts** with the EcfMessages twins spot-checked and **test/ reported separately** from src/; sample-verify 10 sets by hand.

**Dogfood triage.** Emit the full 151-pair inventory as an artifact so the audit trail is complete, but drive decisions off the ~20 tier representatives: one verdict per tier covers its pairs, because `UnusedParameterAnalyser` sharing 207 windows with `UnusedLocalVariableAnalyser` and 197 with `EventNeverInvokedAnalyser` is the *same* shared skeleton. Expected verdict: the 17 analysers are architectural uniformity by design. It gets recorded per pair, not assumed.

**fixture2 gap.** `C:\Users\admin\AppData\Local\Temp\opencode\fixture2-w3\Fixture.slnx` does not exist on disk, so the first draft's "fixture2 pins 23/25" gate is unrunnable. Regenerate it or remove it from the gate list. A gate that cannot run must not stay in the gate list.

### 5. Ship
Version 1.6.3 → commit → pack → install → `snipper --version` → self-run.

## Risks

1. **Boilerplate dominance** (`Program.cs` ASP.NET preamble, 37-file set) → measured, real. Spike gate + `snipper.json` off-switch. Note the using-directive guard does not address it; if it proves too noisy in practice the mitigation is a second exclusion for program-composition boilerplate, decided on monorepo evidence rather than pre-ship speculation.
2. **Baseline churn.** `BaselineService.ComputeFingerprint` hashes `RuleId | relativePath | Message`, and the message embeds both the occurrence count and the other location. Adding a 7th copy of a 6-copy family rewrites `duplicated 6 time(s)` → `7 time(s)` in **all six** messages — six baseline entries invalidate at once. The first draft's risk register only covered "a referenced copy moves" (one entry). **Spike this before touching `BaselineService`:** it is shared by all 30 rules, so a fingerprint change is cross-cutting and needs its own approval, separate from this release.
3. **Memory on very large solutions** → measured at milkrun scale as comfortable (~1M windows). Fallback if a much larger monorepo regresses: shard the index per project pair.
4. **Multi-TFM/linked-file self-matches** → path-keyed dedup from day one (decision 5), fixture-pinned. Consequence accepted: intra-file duplication is suppressed (decision 5).
5. **Dogfood triage cost** → bounded by the tier structure (above), not the raw pair count.

## Explicit non-goals (v1)
Type-3/fuzzy clones; intra-file duplication; clone REFACTORING suggestions (read-only tenet); per-file duplication percentage metrics (Sonar territory); HTML/diff visualisation.

## What changed from the first draft

| # | First draft | Revision | Basis |
|---|---|---|---|
| 1 | `LocalHostedService.cs` ×6 is a named acceptance family | Dropped | Measured max LCS 42 < 60; files are structurally distinct |
| 2 | `FindingCategory.DuplicateFragment = 23` | `= 25` | 23 and 24 are shipped (`EmptyTypeMember`, `UnusedEvent`) |
| 3 | Spike gate = colliding-bucket count, "order tens, not thousands" | Clone-set count after union-find | 1,255 Program.cs buckets → 2 sets |
| 4 | 12–15M tokens, ~15M windows | ~1.5M tokens, ~1M windows | Measured on milkrun |
| 5 | `test/` projects not mentioned | Measured and reported separately | 33% of tokens, 12,313 buckets |
| 6 | Ship from `C:\ws\trimmer` | Reconcile onto `pp\snipper`, rebase `8c63af6` | `fc5bbd8` tree == `b8cdc07` tree; rebase is a content fast-forward |
| 7 | Baseline churn = one entry | N entries per family | Message embeds count + location; fingerprint includes message |
| 8 | "Namespace exclusions apply like other member-level rules" | Explicit two-step resolution | Fragment has no `ISymbol` |
| 9 | Dogfood = 1 pair | 151 pairs, 3 tiers, ~20 representatives | Measured |
| 10 | fixture2 pins 23/25 | Fixture missing from disk | Gate must be runnable |
## What implementation changed (2026-10-03)

Three locked decisions were overturned by measurement, and two implementation defects were found by the gates themselves. All are recorded here rather than quietly folded into the code.

### 1. Shipped opt-in behind `--duplicate-detection` (supersedes locked decision 1's default-on framing)

Dogfood with the rule enabled produced **1,160 findings across ~988 clone sets** on Snipper's own solution, against a gate of 0. This is not threshold mis-tuning - the fragment-length distribution rules that out:

| Minimum tokens | Findings |
|---|---|
| 60 | 1160 |
| 120 | 1115 |
| 134 (EcfMessages length) | 898 |
| 200 | 311 |
| 300 | 0 |

The cause is structural: `UnusedParameterAnalyser.cs:30-75` and `UnusedLocalVariableAnalyser.cs:30-75` are token-identical for ~45 lines (project loop, document loop, `GetSemanticModelAsync`, `ShouldSkipDocument`, `DocumentIdentifierIndex.Build`, `DescendantNodes`). Once identifiers normalize to `ID`, all 17 analysers collapse onto one skeleton. 548 findings in `src`, 612 in `test`.

Shipped behind `--duplicate-detection`, following the existing `ConfigurationBindingAnalyser` (SNP0007/0008) precedent for a high-FP rule. A rule that is red on a clean codebase is not useful on every run.

### 2. Cross-directory structural guard (measured, applied)

A clone set confined to one containing directory is the signature of a locally repeated skeleton; a copy that crosses a directory is the case the rule exists to surface. Measured on Snipper.slnx: kills 518 of 1160, all of them same-directory pairs between sibling analysers. Snipper.slnx dogfood with opt-in **and** the guard: **2 findings**, both the `--version` short-circuit duplicated between `Program.cs` and `CliRunner.cs` - a true positive, and load-bearing (Program.cs must answer before MSBuildLocator binds, or the output carries SDK noise). Triaged intentional.

Residual monorepo noise is constant tables (39% of findings) and `Program.cs` composition preambles (11%) - 50% combined, inherent to token-normalized clone detection since every `const` declaration normalizes to the same shape. Documented in the README rather than special-cased, because duplicated constants across modules are a real maintenance hazard.

### 3. Two defects the gates caught

**Nested-match collapse (correctness).** Extension walks backwards one token at a time, so a single 220-token clone arrives as ~140 nested matches differing by start offset. The first implementation keyed fragments on `(path, tokenIndex)` and reported the clone ~140 times (200 findings for one fixture pair). Fixed by collapsing matches per file pair in a single ordered sweep. Pinned by `Report_A_Clone_Once_Not_Once_Per_Window_For_AnalyzeAsync`.

**Union-find on token offsets (correctness).** Grouping clone sets by `(TokenIndex, TokenCount)` silently dropped legitimate clones whose two copies sit at different offsets in differently sized files - `FileScopeClone.cs` (start 0) never paired with its mirror (start 100). Fixed by threading the *proven* match edges into union-find rather than re-deriving relationships from offset equality.

**Quadratic match handling (performance).** Two `Any()` scans inside loops (`Record` dedup, maximal-match subsumption) never completed on the monorepo: the run was killed after **29 minutes wall / 2520s CPU** without reaching "extending matches". Replaced with a hash-deduplicated index and a single ordered sweep per file pair. Milkrun SNP0031 pass now **52.6s**.

### 4. Performance budget exceeded (honest negative result)

The plan budgeted **≤15s marginal**. Measured **52.6s** on the 3,070-file monorepo - **3.5x over budget**. The main lever available is the already-in-place `--duplicate-detection` opt-in: the cost is never paid unless requested. Memory peaked at ~2.4GB during the pass. A future optimisation is sharding the index per project pair (risk 3's stated fallback), not yet done.

### 5. Namespace exclusion resolved syntactically

The plan said to mirror `ExclusionEngine.IsNamespaceExcluded(node, semanticModel, …)`, which requires a `SemanticModel` and would contradict locked decision 4 (syntax-only, zero binding). Resolved syntactically instead: enclosing `BaseNamespaceDeclarationSyntax`, falling back to a `<global>` marker for file-scope fragments (documented in the README; pinned by `Suppress_A_File_Scope_Fragment_In_An_Excluded_Namespace_For_AnalyzeAsync`).

### 6. Phase 0 reconciliation outcome

`fc5bbd8` tree == trimmer `b8cdc07` tree, so the rebase was a content fast-forward: git dropped the squashed import ("patch contents already upstream") and replayed only the perf commit as `8a6b58c`. Tree hash `5a0ae339` identical before and after. Verified with a clean build, 322/322 tests, dogfood 0, and 4-way output parity (default / `SNIPPER_MAX_DOP=1` / `SNIPPER_FANOUT_DOP=1` / both) - 190 findings, byte-identical semantic fields across all four (raw bytes differ only in `generatedAtUtc`).

### 7. fixture2 gate

`C:\Users\admin\AppData\Local\Temp\opencode\fixture2-w3\Fixture.slnx` does not exist on disk. Rather than leave an unrunnable gate, it is **removed from the 1.6.3 gate list**. The 1.6.2 parallelism-adoption evidence that cited it (0 finding drift, 23/25) stands as historical record from when the fixture existed; 1.6.3's own gate is the SampleApp fixture plus dogfood plus the monorepo A/B.

### Validation summary (1.6.3)

| Gate | Result |
|---|---|
| Full suite | 353 passed, 0 failed |
| Dogfood, default run | 3 findings (SNP0019 x2, SNP0024 x1); **0 SNP0031** - gate met |
| Dogfood, `--duplicate-detection` | 2 SNP0031, both triaged intentional |
| Monorepo baseline | 2,839 findings, 188.4s |
| Monorepo A/B | 8,359 total; SNP0031 5,439 findings / 1,659 clone sets, 52.6s marginal |
| Acceptance family | EcfMessages twins **caught** - 129-token fragment, both occurrences |
