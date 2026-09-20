# Plan — Snipper 1.6.3: duplicate detection (token-shingle engine)

**Status: DRAFT for approval.** Source: roadmap tracked candidate ("Duplicate detection — token-shingle engine (normalised token streams, ~60-token windows), cross-project. L") + competitive-analysis Tier 3 #9 (dupFinder parity — "a separate engine but monorepo gold").

**Objective:** a new syntax-only engine that flags duplicated code fragments (Type-1 exact and Type-2 rename-only clones) across the whole solution, including cross-project copies — the last uncommitted Tier-3 capability gap.

**Real acceptance families (milkrun, known from the 1.6.x FP reviews):**
- `LocalHostedService.cs` — six near-identical copies across consumer projects.
- `EcfEventMessageBase` + payload/message family — duplicated between `Milkrun.Integration.MockingService` and `Milkrun.Orders.FulfilmentUpdatesConsumer`.

Repo constraints (standing): no dynamic; no exceptions for flow control; FrozenSet/FrozenDictionary; syntax pre-filters first; `ArgumentNullException.ThrowIfNull`; evidence ≠ findings (generated/external/excluded code never produces finding locations); red phase first with fixture scenarios in NEW files; deterministic sorted output; dogfood stays 0 or every new own-finding is triaged (the 1.1.1/1.1.2 protocol). Release process: bump `<Version>` → commit → pack → `dotnet tool update --global --add-source` → `snipper --version` → self-run expect 0. Validation gates: full suite, dogfood 0 on Snipper.slnx, fixture2 pins 23/25 (C:\Users\admin\AppData\Local\Temp\opencode\fixture2-w3\Fixture.slnx), milkrun A/B.

## Locked decisions

1. **New rule SNP0031 "Duplicate Code Fragment", Advisory tier — a normal finding rule, NOT a separate report section.** The 2026-09-14 "separate report section" note predates `snipper.json` and `--baseline`; a normal rule gets the config off-switch/severity override, baseline fingerprinting, SARIF output, and `--certainty-tier` filtering for free. One finding per occurrence; the message carries the occurrence count and the first other location. New `FindingCategory.DuplicateFragment` (= 23, next free after `RedundantQualifier` = 22).
2. **Type-1 + Type-2 clones in v1** — token normalization abstracts identifiers and literals, so rename-only clones are caught. Type-3 (fuzzy/gapped clones) is out of scope (different algorithm class — CPD-style gapped matching or AST edit distance; revisit after v1 evidence).
3. **Syntax-only engine, zero semantic binding.** Tokens come from the parsed syntax trees the workspace already has; the engine is parallel by construction (`AnalysisParallelism.CreateOptions`, the revertible `SNIPPER_MAX_DOP` contract) and adds no binding time — fits the performance doctrine by design.
4. **Cross-project by design; same-path self-matches suppressed.** Locations are keyed by file PATH, not document — linked/shared files and multi-TFM documents contribute one copy (the perf-retrospective H3 lesson: project duplication must not split identity).
5. **Spike-gated thresholds.** The window size and line floor ship only after a count-only monorepo spike shows the signal dominates the boilerplate (see "Spike" below — the 1.4.x spike discipline).

## Engine design — `src\Snipper\Analysis\DuplicateFragmentAnalyser.cs` + `TokenShingleIndex.cs`

### Tokenization (normalised token stream, per file)
- `root.DescendantTokens()` — trivia/comments never enter the stream (tokens only). Generated/external documents skipped via `ExclusionEngine.ShouldSkipDocument`.
- **Excluded subtrees:** `UsingDirectiveSyntax` and `ExternAliasDirectiveSyntax` — milkrun's near-identical `GlobalUsings.cs` files across ~180 projects would otherwise dominate every clone set. (Namespace declarations stay; their body content is the signal.)
- **Normalization map:** identifier tokens → `ID`; string/character/numeric literal tokens (incl. interpolated-string text tokens) → `STR`/`CHR`/`NUM`; keywords and punctuation → verbatim text; directive tokens skipped.
- Stream entries carry (normalised text, path, line) for mapping back to locations.

### Shingling + matching
- Sliding window **W = 60 tokens** (roadmap default; one tuning knob, `WindowTokens`), FNV-1a hash over the window's normalised texts.
- Index: `Dictionary<int hash, List<(string Path, int TokenIndex, int Line)>>` across ALL files (parallel fill, then freeze). Same (path, tokenIndex) recorded once (decision 4).
- **Match extension:** for each hash bucket with ≥2 distinct locations, extend runs forward/backward while normalised tokens match (CPD-style maximal runs); require ≥60 tokens AND ≥4 spanned lines AND non-overlapping source spans between occurrences.
- **Clone sets:** union-find over matched pairs → groups of ≥2 occurrences; one finding per occurrence.

### Finding shape
`SNP0031`, Advisory: *"62-token fragment (7 lines) duplicated 3 time(s); first other occurrence at src\Other\LocalHostedService.cs:5."* Location = the fragment's own first line. Namespace exclusions apply like other member-level rules. Baseline note (documented): the embedded other-location churns when that copy moves — the old baseline entry suppresses, the moved copy appears as new; acceptable at Advisory.

### Guards (noise control)
- ≥60 tokens AND ≥4 lines (kills single-line/minified matches).
- Using/extern subtrees excluded (above).
- Generated/external/`exclude.paths` documents never seed; `exclude.namespaces` filters findings.
- Identical attribute blocks / auto-property clusters are typically <60 tokens — the threshold is the guard; DTO-class clones that EXCEED it (the EcfMessages twins) are the intended catch, not noise.

### Performance budget
~2M LOC monorepo ≈ 12–15M tokens ≈ 15M windows; FNV into int keys + bucket lists; extension pass touches colliding buckets only (rare). Target ≤15s marginal (SNP0019's 6.4s is the reference point); measure in the spike and report per-wave (guiding principle 5).

## Spike (go/no-go, BEFORE the rule ships)
Count-only monorepo run (console summary, no findings): window count, colliding buckets, raw clone-set count at W=60, top 20 sets by size. **GO** when the known families (LocalHostedService ×6, EcfMessages twins) appear AND the raw count is triageable (order tens, not thousands). **Retune** (W=80, line floor 6, or excluding runs that are pure auto-property blocks) when boilerplate dominates. Outcome recorded in this doc before implementation.

## Work items (red-green fixture-first; fixture scenarios in NEW files, never `DeadCode.cs`)

### 1. Token stream + shingle index
`TokenShingleIndex`: tokenization with the normalization map, using/extern exclusion, window hashing, path-keyed locations. Unit-testable without a workspace (feed it parsed trees). Fixture unit pins: normalization table (identifiers/literals abstracted, keywords verbatim), using exclusion, same-path dedup.

### 2. Match extension + clone sets + analyser
`DuplicateFragmentAnalyser : IWorkspaceAnalyser` (RuleIds `["SNP0031"]`, takes `AnalysisExclusions?`), deterministic sorted output. Fixture (new files): planted ≥60-token cross-project clone pair (CoreLib + App), one exact and one identifier-renamed (both flagged); a divergent control (<60 matching tokens — not flagged); a short control (identical 20-token helper — not flagged); an identical-using-lists control (not flagged). Assert rule id, occurrence count, and both locations.

### 3. Registration + docs
Register in `CliRunner` (analyser list), README rules-table row, `--exclude-namespaces` rule list gains SNP0031, competitive-analysis §1 matrix row (Duplicate code blocks: ✓ SNP0031) + §5 candidate #9 checked off, roadmap tracked-candidate marked shipped, status line entry.

### 4. Validation
Full suite; dogfood 0 (own-codebase triage: the two cast evaluators are structurally similar by design — if flagged at the shipped threshold, extract the shared skeleton or retune, per the dogfood protocol); fixture2 pins 23/25 — if the fixture2 codebase plants duplicates, the pin updates deliberately (triaged + documented); monorepo run: report counts, spot-check the known families, sample-verify 10 sets by hand.

### 5. Ship
Version 1.6.3 → commit → pack → install → `snipper --version` → self-run 0.

## Risks
1. **Boilerplate dominance** (options/POCO/registration families) → spike gate + threshold retune pre-ship; `snipper.json` off-switch is the shock absorber post-ship.
2. **Baseline churn** when a referenced copy moves (message embeds its location) → documented Advisory-tier behavior; alternative (occurrence-count-only message) rejected — location is what makes the finding actionable.
3. **Memory on very large solutions** (int-key buckets) → measured in spike; fallback: shard the index per project pair.
4. **Multi-TFM/linked-file self-matches** → path-keyed dedup from day one (decision 4), fixture-pinned.

## Explicit non-goals (v1)
Type-3/fuzzy clones; clone REFACTORING suggestions (read-only tenet); per-file duplication percentage metrics (Sonar territory); HTML/diff visualisation.
