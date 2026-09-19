# Snipper Feature-Parity Roadmap (Waves 1–3)

**Date:** 2026-09-15 · **Baseline:** Snipper 1.1.2 (15 rules, 98 tests) · **Source analysis:** [`competitive-analysis.md`](competitive-analysis.md)
**Status:** Wave 1 SHIPPED 2026-09-17 as 1.2.0 (16 rules, 127 tests). Wave 2 COMPLETE 2026-09-18 — 2A as 1.3.0, 2B as 1.3.1 (19 rules, 158 tests). Wave 3 SHIPPED 2026-09-18 as 1.4.0 (22 rules, 188 tests) — roadmap complete through the committed waves. 1.4.1 (2026-09-18): hotfix — invocation speculation guarded against conditional access (`?.`), fixing an SNP0022/SNP0025 crash (Roslyn speculative binder NRE on `MemberBindingExpression`). 1.4.2 (2026-09-18, 192 tests): perf wave — shared `SolutionReferenceIndex` (one semantic harvest replacing per-candidate `FindReferencesAsync` storms in SNP0005/0006 incl. both rescue passes, and absorbing SNP0003/0004's usage sweep), `UnreachableCodeGate` syntax pre-filter for flow analysis (SNP0002/0009), SNP0019 single-pass per-project diagnostics. Monorepo validation pending user A/B (doctrine gate: SNP0005/0006 reduction OK, increase blocks; other rules count-identical). 1.4.3 (2026-09-18, 195 tests): hotfix — the index gained the unconfirmed-name evidence tier (broken bindings / dynamic receivers), replicating FindReferencesAsync's candidate-location doctrine; fixes the SNP0005/0006 false-positive flood reported on the user's monorepo (interface-dispatched domain entities). Monorepo A/B remains the acceptance test. 1.4.4 (2026-09-18, 191 tests): perf-wave index REVERTED — the monorepo gate was breached (SNP0005/0006 findings 1,532 → 3,769 despite both doctrine fixes; root cause unsolved, hypotheses in [`perf_wave_retrospective.md`](perf_wave_retrospective.md)). SNP0005/0006 and SNP0009's slow path back to `FindReferencesAsync`/`SymbolReferenceQuery`, `SymbolUsageCollector` restored. Kept: `UnreachableCodeGate` (SNP0002/0009 flow pre-filter, monorepo SNP0002 47.1s → 24.2s), SNP0019 single-pass diagnostics (14.2s → 6.4s), 1.4.1 `?.` guard. 1.5.0 (2026-09-19, 209 tests): **Wave 4 framework evidence** ([`phase3_wave4.md`](phase3_wave4.md)) — SNP0005/0006 learn to recognise framework-invoked members: `FrameworkEvidenceIndex` (STJ `[JsonSerializable]` contexts, serializer call sites, Refit signatures, `[FromBody]` binding, GraphQL response contracts → transitive DTO closure; DI/framework registration shapes → contract implementations; middleware + FluentValidation conventions; member-level serialization attributes) plus record-protocol `Deconstruct` exemption. Monorepo A/B: SNP0006 1,515 → 1,090 (−28%), all other rules count-identical (SNP0023 0 → 2 = parity with 1.4.1/1.4.3; the 1.4.4 "0" was a stale-report artifact). 1.5.1 (2026-09-19, 213 tests): Wave 4 course-correction — plain DI registration is no longer evidence (calls through a registered contract are ordinary visible references; 1.5.0 wrongly suppressed uncalled contract members on registered types, e.g. `CartRepository.GetActiveCart`). Registration-shape evidence replaced by framework-dispatched CONTRACT evidence (`IHealthCheck`, `IFusionCacheSerializer`, `IHostedService`, `IExceptionHandler`, OpenAPI filters/transformers), which also catches shapes registration-scanning missed (instance registrations, `SchemaFilter<T>`). Monorepo A/B vs 1.5.0: +36 restored true positives, −5 additional genuine FPs. 1.5.2 (2026-09-19, 217 tests): correctness + perf patch per the approved gap-filler/perf plan. (a) Snipper's own report JSONs no longer feed string-name evidence (content-sniff `"ruleId"` in `AssemblyNameEvidenceScanner`) — monorepo SNP0023 0 → 3, all three previously hidden by stale reports (the two attribute classes **plus `MilkrunDiscount`**, whose name a 1.4.4-era report spelled in unrelated finding paths — mechanism triple-confirmed). (b) `[JsonInclude]` private members are SNP0001 evidence (`FrameworkEvidenceIndex.HasMemberSerializationAttribute`). (c) `DiRegistrationScanner` syntax-first — semantic models only for registration-bearing documents. (d) Redundancy `?.`-guard per-tree memoized pre-clear (same accept/reject set). (e) **Parallelism adopted behind the revertible `SNIPPER_MAX_DOP` switch (default on):** per-document binding (`FrameworkEvidenceIndex`, `DiRegistrationScanner` pass 1) and — via the user-approved second spike — per-candidate `FindReferencesAsync` in SNP0005/0006 (two-phase restructure: sequential candidate enumeration, parallel searches, sorted output). Adoption gate: **0 finding drift on fixture2 (both modes, 23/25) and on the monorepo (2,323 findings, sequential vs parallel); monorepo SNP0005/0006 164.4s → 57.8s (−65%), total 309.9s → 211.3s (−32%)** — same-build back-to-back Debug runs. All other monorepo rules count-identical to 1.5.1.

## Locked decisions

1. **Read-only forever.** Snipper never mutates source. No `snipper fix`, no auto-cleanup — detection and reporting only. This is a permanent design tenet, not a deferral; it supersedes the `--fix` candidate in `competitive-analysis.md` §5. ReSharper/VS own the *removal* workflow; Snipper owns CI-grade *detection*.
2. **Waves 1–3 committed** (through 1.4.0). Duplicates engine, coverage import, `.resx`/XAML remain tracked candidates, re-evaluated after 1.4.0 dogfooding.
3. **One release per wave** — 1.2.0, 1.3.0, 1.4.0, each a coherent dogfooded milestone.
4. **Per-pattern redundancy rule IDs** *(amended 2026-09-18)* — the redundancy sweep does NOT share one SNP0022 id: SNP0022 = redundant default-value argument, SNP0025 = redundant method type arguments, SNP0026 = redundant cast. Independently toggleable via `snipper.json`; SNP0023/0024 stay reserved for Wave 3. 2B approved 2026-09-18 after user review; ships as 1.3.1 (1.3.0 shipped 2A only).

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

## Wave 3 → 1.4.0 — "Semantic depth II" ✅ SHIPPED 2026-09-18

| # | Story | Rule / Tier | Design | Effort |
|---|---|---|---|---|
| 3A | **Hierarchy dead code** ✅ | SNP0023, Moderate | Virtual member never overridden + class with virtuals never inherited. Built on the new shared `InheritanceGraph` index — the "SNP0018 override graph" assumed here turned out not to exist (SNP0018 uses per-symbol checks; corrected in [`phase3_wave3.md`](phase3_wave3.md)). String-name reflection evidence suppresses (SNP0006 demotion pattern). | M |
| 3B | **Tightening** ✅ | SNP0024, Advisory (default) | One analyser, three sub-checks: member-can-be-static (CA1822 parity), field-can-be-readonly (IDE0044 — reuses the SNP0021 reference machinery via the extracted `FieldReferenceMap`), internal-class-can-be-sealed (CA1852). Promotable via `snipper.json`. | M |
| 3C | **Redundancy sweep, part 2** ✅ | SNP0026, High | Redundant cast, narrowed to identity conversions in v1 (spike-proven: `IsIdentity` discriminates every non-flag shape); the upcast variant needs a rebind gate and stays deferred. | M |

## Tracked candidates (post-1.4.0, not committed)

- **Parallel semantic binding** — ~~spike GO 2026-09-18~~ **ADOPTED 2026-09-19 as 1.5.2** behind the revertible `SNIPPER_MAX_DOP` switch (default on): per-document binding (`FrameworkEvidenceIndex`, `DiRegistrationScanner` pass 1) and per-candidate `FindReferencesAsync` in SNP0005/0006. The workspace-level caveat was retired by a second user-approved spike: **2,323 findings, 0 drift, monorepo sequential vs parallel; SNP0005/0006 164.4s → 57.8s, total −32%**. Suite runs the parallel path by default; `SNIPPER_MAX_DOP=1` suite pass verified.
- **Duplicate detection** — token-shingle engine (normalised token streams, ~60-token windows), cross-project, separate report section. L.
- **Coverage evidence import** — `--coverage coverlet.xml` (cobertura); corroborating channel that adjusts certainty, never a standalone finding. M.
- **Unused `.resx` keys / XAML-Razor evidence** — generalise `AssemblyNameEvidenceScanner`. M–L.

## Next steps (as of 1.4.2, 2026-09-18)

0. ~~REVERT the perf-wave index~~ — **executed 2026-09-18, shipped as 1.4.4** (see [`perf_wave_retrospective.md`](perf_wave_retrospective.md)). Remaining: the retrospective's kept-knowledge section records the reusable findings and the unsolved root-cause hypotheses for any future index retry.

0b. ~~Wave 4 framework evidence~~ — **shipped 2026-09-19 as 1.5.0** ([`phase3_wave4.md`](phase3_wave4.md)). SNP0006 on the monorepo −28% with rule-parity everywhere else. Follow-up candidates from the same trust theme: [JsonInclude] private members (SNP0001 evidence), SNP0023 external-hook evidence, stale report files in the analysis root feeding string-name evidence (consider excluding `output*.json` / snipper report files from `AssemblyNameEvidenceScanner` — mechanism confirmed twice: the 1.4.4 and 1.5.1 runs each suppressed real SNP0023 findings because a previous report JSON containing those names sat in the solution root), and the remaining closed-world items (test builders etc. — true positives, no action).

1. **Monorepo A/B validation of 1.4.2** — user re-runs their large monorepo and reports per-analyser timings + finding counts. Gate (user-approved): SNP0005/0006 reduction acceptable (over-approximation doctrine — each delta needs a defensible "used" explanation); **any SNP0005/0006 increase blocks**; all other rules count-identical. Baseline from the user's 1.4.1 run (total 224.7s):

   | Analyser | Time | Findings | 1.4.2 expectation |
   |---|---|---|---|
   | UnusedNonPrivateMember (SNP0005/0006) | 105.1s | 1,532 | ~20–25s (index harvest) |
   | UnreachableCode (SNP0002) | 47.1s | 0 | <5s (syntax gate) |
   | UnusedLocalVariable (SNP0009) | 28.4s | 56 | <5s + index (gate + index) |
   | UnreferencedPackage (SNP0003/0004) | 17.9s | 47 | ~0 marginal (shared harvest) |
   | UnusedUsingDirective (SNP0019) | 14.2s | 207 | ~2s (single-pass diagnostics) |
   | Everything else | ≤2.5s each | 19, 8, 49, 0, 34, 0, 12, 8, 72, 3, 670 | unchanged |
   | **Total** | **224.7s** | | **~60–80s** |

2. **Parallel-binding adoption decision (1.4.3 candidate)** — spike verdict GO (see Tracked candidates). If the user approves: parallelize the `SolutionReferenceIndex` harvest + per-document analyser sweeps behind a revertible degree-of-parallelism switch; validate full suite + dogfood + fixture2 + monorepo A/B. Expected: harvest (the post-1.4.2 bottleneck) drops ~10×.

3. ~~Wave 4 direction — PENDING USER DECISION~~ — **decided 2026-09-19: gap-filler sweep + quick perf wins** (all four recommendations user-approved). Packaging:
   - **1.5.2 (patch, zero intentional finding deltas except SNP0023):** (a) snipper-report exclusion in `AssemblyNameEvidenceScanner` (content-sniff `"ruleId"`; the twice-confirmed self-suppression fix — SNP0023 returns to a stable 2 on the monorepo); (b) `[JsonInclude]` private members → SNP0001 evidence via the existing `FrameworkEvidenceIndex` member-attribute check; (c) `DiRegistrationScanner` syntax-first (semantic models only for registration-bearing documents) + parallel syntax pass; (d) redundancy `?.`-guard cheapening (per-document pre-clear — same accept/reject set, no subtree scan for clean documents); (e) parallel per-document binding behind a revertible switch (`SNIPPER_MAX_DOP`, default on — the 2026-09-18 spike verdict: 24,301 bindings, 0 drift, 12.5×); (f) **FindReferencesAsync parallelism spike** (user-approved; 0-drift gate: order-normalized findings identical on Snipper.slnx + fixture2, then monorepo A/B count-identical; NO-GO → stay sequential, documented).
   - **1.6.0 (Wave 5, gap-filler rules — all five committed):** SNP0026 upcast variant (High, rebind-gated); SNP0024 can-be-private sub-check (Advisory — extends the existing bundle, user-approved); SNP0028 redundant qualifiers / SNP0029 empty ctor-destructor (High, per-pattern IDs per locked decision 4); SNP0027 hierarchy member-level — override family with no external caller (Moderate, on `InheritanceGraph`); SNP0030 event-never-invoked (Advisory).
   - **Verified OUT during planning (monorepo evidence):** MediatR contracts (100+ `IRequestHandler` impls, zero `Handle` findings — tests call handlers directly; adding the contract would only hide future dead handlers), options binding (SNP0007/0008 already cross-references; binder-written-never-read = true dead config), controllers (none exist), can-be-const/init-only/file-local (niche), return-value-never-used (FP-prone), out-always-discarded (niche), SNP0023 attribute-class hook evidence (2 findings, no complaint).

   **Explicit non-goals (standing):** auto-fix (read-only tenet), outdated/vulnerable packages (NuGet Audit owns), IDE squiggles, general lint/bug-risk rules.

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
