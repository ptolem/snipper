# Snipper Feature-Parity Roadmap (Waves 1–3)

**Date:** 2026-09-15 · **Baseline:** Snipper 1.1.2 (15 rules, 98 tests) · **Source analysis:** [`competitive-analysis.md`](competitive-analysis.md)
**Status:** Wave 1 SHIPPED 2026-09-17 as 1.2.0 (16 rules, 127 tests). Wave 2 story 2A SHIPPED 2026-09-18 as 1.3.0 (17 rules, 141 tests); 2B deferred pending review — spec: [`phase3_wave2.md`](phase3_wave2.md).

## Locked decisions

1. **Read-only forever.** Snipper never mutates source. No `snipper fix`, no auto-cleanup — detection and reporting only. This is a permanent design tenet, not a deferral; it supersedes the `--fix` candidate in `competitive-analysis.md` §5. ReSharper/VS own the *removal* workflow; Snipper owns CI-grade *detection*.
2. **Waves 1–3 committed** (through 1.4.0). Duplicates engine, coverage import, `.resx`/XAML remain tracked candidates, re-evaluated after 1.4.0 dogfooding.
3. **One release per wave** — 1.2.0, 1.3.0, 1.4.0, each a coherent dogfooded milestone.
4. **Per-pattern redundancy rule IDs** *(amended 2026-09-18)* — the redundancy sweep does NOT share one SNP0022 id: SNP0022 = redundant default-value argument, SNP0025 = redundant method type arguments, SNP0026 = redundant cast. Independently toggleable via `snipper.json`; SNP0023/0024 stay reserved for Wave 3. 2B implementation is deferred pending user review — 1.3.0 ships 2A only.

## Guiding principles

1. **Config before noise.** No Advisory/Moderate rule ships until users can tune or disable rules (Wave 1A exists for this reason).
2. **Red-green fixture-first.** Every story starts with failing fixture tests (SampleApp; never touch `DeadCode.cs` — SNP0002 anchor at line 21).
3. **Tier honesty.** Guaranteed = compiler-level certainty only; heuristics start Advisory/Moderate and earn promotion through dogfooding evidence.
4. **Evidence ≠ findings.** Generated/external/excluded code feeds usage evidence but never produces finding locations (unchanged).
5. **Performance discipline.** Syntax pre-filters before any semantic call; sequential semantic binding (`ConcurrentBuild=false`); parallelism only for syntax-only work; per-wave perf budget report.
6. **Non-goals stay non-goals:** IDE squiggles, general lint/bug-risk rules, package vulnerability reporting.

---

## Wave 1 → 1.2.0 — "Daily-visible parity"

The three features developers see every day in ReSharper/Sonar, all cheap in Snipper's architecture.

| # | Story | Rule / Tier | Design | Effort |
|---|---|---|---|---|
| 1A | **Config & suppression file** (`snipper.json`) | — | Walk-up discovery from the solution directory. Schema (`"version": 1`): `{"rules": {"SNP0010": "off" \| "advisory" \| "moderate" \| "high" \| "guaranteed"}, "exclude": {"namespaces": [...], "paths": ["**/Generated/**"]}}`. Extends `AnalysisExclusions`; CLI flags win on conflict. Path globs filter *findings*, never *evidence*. | M |
| 1B | **Unused using directives** | SNP0019, Guaranteed | Surface compiler-computed **CS8019** from `SemanticModel.GetDiagnostics()` — compiler-grade soundness for free. **Spike first:** verify CS8019 also fires for unnecessary `global using`; if not, globals are skipped in v1 (documented). | S |
| 1C | **Commented-out code** | SNP0020, Advisory | Syntax-trivia scan (parallelisable, zero semantic cost): ≥2 consecutive comment lines (or a block comment) where ≥60% of lines tokenize as code (`; { } =` density + statement-parse probe). Exclusions: `///` doc comments, license/copyright headers, TODO/FIXME/HACK/NOTE, URLs. | S |

**Cross-cutting (bundled into Wave 1):**
- Rebuild the corrupted `%TEMP%` regression fixture (fixture2) as a **checked-in generator script** under `test/` so it cannot silently rot again.
- Dogfood gate: self-run on `Snipper.slnx` stays at 0 findings, or every new finding is individually triaged (the 1.1.1/1.1.2 protocol).

**Exit criteria:** all three stories merged, full suite green, `snipper.json` demonstrably silences every existing rule, README + `competitive-analysis.md` updated.

## Wave 2 → 1.3.0 — "Semantic depth I"

| # | Story | Rule / Tier | Design | Effort |
|---|---|---|---|---|
| 2A | **Field written, never read** (IDE0052 parity) | SNP0021, High | Read/write classification in usage collection: assignment target / `++`/`--` / `out` = write; `ref` = read+write; everything else = read. Flag fields with ≥1 write and 0 reads. Demote to Moderate on serialization attributes (`[JsonInclude]`, `[DataMember]`, `[XmlElement]`). Never-assigned fields deliberately excluded — CS0649 is the compiler's job. Starts one tier lower if dogfooding shows noise; promoted after a wave of evidence. | M |
| 2B | **Redundancy sweep, part 1** *(deferred — see locked decision 4)* | SNP0022 / SNP0025, High | Per-pattern rules, each shipped only when individually soundness-proven. (1) redundant default-value argument (metadata default vs literal + speculative rebinding check — spike-proven 2026-09-18); (2) redundant method type arguments when inference yields identical (speculation-gated — spike-proven). | L |

## Wave 3 → 1.4.0 — "Semantic depth II"

| # | Story | Rule / Tier | Design | Effort |
|---|---|---|---|---|
| 3A | **Hierarchy dead code** | SNP0023, Moderate | Virtual member never overridden + class with virtuals never inherited (ReSharper-exclusive today). Reuses the override graph built for SNP0018; string-name reflection evidence suppresses (SNP0006 demotion pattern). | M |
| 3B | **Tightening** | SNP0024, Advisory (default) | One analyser, three sub-checks: member-can-be-static (CA1822 parity), field-can-be-readonly (IDE0044), internal-class-can-be-sealed (CA1852). Advisory default to protect CI; promotable via `snipper.json`. | M |
| 3C | **Redundancy sweep, part 2** *(if deferred)* | SNP0026, High | Redundant cast sub-rule (types match + no user-defined conversion involved). | M |

## Tracked candidates (post-1.4.0, not committed)

- **Duplicate detection** — token-shingle engine (normalised token streams, ~60-token windows), cross-project, separate report section. L.
- **Coverage evidence import** — `--coverage coverlet.xml` (cobertura); corroborating channel that adjusts certainty, never a standalone finding. M.
- **Unused `.resx` keys / XAML-Razor evidence** — generalise `AssemblyNameEvidenceScanner`. M–L.

## Per-story working agreement (all waves)

1. Fixture scenarios added to SampleApp in **new files** (never `DeadCode.cs`), using locally-defined framework stand-ins where name-matching applies (the `FactAttribute` pattern).
2. Red phase must demonstrate the exact expected failures before any engine change; green phase is the minimal change that flips them.
3. Every new rule registered in `CliRunner`, documented in the README rules table, and cross-checked off in `competitive-analysis.md` §5.
4. Version bump → pack → `dotnet tool update` → self-run verification at the end of each wave.

## Risk register

1. **CS8019 spike fails for global usings** → SNP0019 v1 ships without globals (documented); fallback is namespace-usage analysis (M effort).
2. **Read/write classification FPs** (reflection writes, `unsafe`, interceptors) → SNP0021 drops to Moderate until evidence accumulates; config file (1A) is the shock absorber.
3. **Redundancy sub-rule soundness** — a wrong "redundant" claim costs more trust than ten missed ones; any sub-rule whose speculation proof isn't clean defers rather than ships.
4. **Config schema churn** → `"version": 1` from day one; additive changes only within a major version.
