# Snipper Feature-Parity Roadmap (Waves 1–4)

**Date:** 2026-09-15 · **Updated:** 2026-10-06 · **Baseline:** Snipper 1.1.2 (15 rules, 98 tests) · **Source analysis:** [`competitive-analysis.md`](competitive-analysis.md)
**Current release:** **1.7.4** — 28 rule IDs (SNP0001–0013, 0018–0032), 19 analysers, **590 tests**. Waves 1–3 and Wave 4 (4A/4A-2/4B/4C) are all shipped; 1.7.1–1.7.4 were a correctness pass over Wave 4's output, found by reviewing real findings rather than by tests. The open backlog is in [Next up](#next-up--the-17x-false-positive-backlog). **This document is current.** Every plan, wave spec, FP review and retrospective it cites is superseded and lives in [`history/`](history/README.md), version-prefixed — including several that record a no-go or a reversal.
**Status:** Wave 1 SHIPPED 2026-09-17 as 1.2.0 (16 rules, 127 tests). Wave 2 COMPLETE 2026-09-18 — 2A as 1.3.0, 2B as 1.3.1 (19 rules, 158 tests). Wave 3 SHIPPED 2026-09-18 as 1.4.0 (22 rules, 188 tests) — roadmap complete through the committed waves. 1.4.1 (2026-09-18): hotfix — invocation speculation guarded against conditional access (`?.`), fixing an SNP0022/SNP0025 crash (Roslyn speculative binder NRE on `MemberBindingExpression`). 1.4.2 (2026-09-18, 192 tests): perf wave — shared `SolutionReferenceIndex` (one semantic harvest replacing per-candidate `FindReferencesAsync` storms in SNP0005/0006 incl. both rescue passes, and absorbing SNP0003/0004's usage sweep), `UnreachableCodeGate` syntax pre-filter for flow analysis (SNP0002/0009), SNP0019 single-pass per-project diagnostics. Monorepo validation pending user A/B (doctrine gate: SNP0005/0006 reduction OK, increase blocks; other rules count-identical). 1.4.3 (2026-09-18, 195 tests): hotfix — the index gained the unconfirmed-name evidence tier (broken bindings / dynamic receivers), replicating FindReferencesAsync's candidate-location doctrine; fixes the SNP0005/0006 false-positive flood reported on the user's monorepo (interface-dispatched domain entities). Monorepo A/B remains the acceptance test. 1.4.4 (2026-09-18, 191 tests): perf-wave index REVERTED — the monorepo gate was breached (SNP0005/0006 findings 1,532 → 3,769 despite both doctrine fixes; root cause unsolved, hypotheses in [`1_4_4_perf_wave_retrospective.md`](history/1_4_4_perf_wave_retrospective.md)). SNP0005/0006 and SNP0009's slow path back to `FindReferencesAsync`/`SymbolReferenceQuery`, `SymbolUsageCollector` restored. Kept: `UnreachableCodeGate` (SNP0002/0009 flow pre-filter, monorepo SNP0002 47.1s → 24.2s), SNP0019 single-pass diagnostics (14.2s → 6.4s), 1.4.1 `?.` guard. 1.5.0 (2026-09-19, 209 tests): **Wave 4 framework evidence** ([`1_5_0_phase3_wave4.md`](history/1_5_0_phase3_wave4.md)) — SNP0005/0006 learn to recognise framework-invoked members: `FrameworkEvidenceIndex` (STJ `[JsonSerializable]` contexts, serializer call sites, Refit signatures, `[FromBody]` binding, GraphQL response contracts → transitive DTO closure; DI/framework registration shapes → contract implementations; middleware + FluentValidation conventions; member-level serialization attributes) plus record-protocol `Deconstruct` exemption. Monorepo A/B: SNP0006 1,515 → 1,090 (−28%), all other rules count-identical (SNP0023 0 → 2 = parity with 1.4.1/1.4.3; the 1.4.4 "0" was a stale-report artifact). 1.5.1 (2026-09-19, 213 tests): Wave 4 course-correction — plain DI registration is no longer evidence (calls through a registered contract are ordinary visible references; 1.5.0 wrongly suppressed uncalled contract members on registered types, e.g. `CartRepository.GetActiveCart`). Registration-shape evidence replaced by framework-dispatched CONTRACT evidence (`IHealthCheck`, `IFusionCacheSerializer`, `IHostedService`, `IExceptionHandler`, OpenAPI filters/transformers), which also catches shapes registration-scanning missed (instance registrations, `SchemaFilter<T>`). Monorepo A/B vs 1.5.0: +36 restored true positives, −5 additional genuine FPs. 1.5.2 (2026-09-19, 217 tests): correctness + perf patch per the approved gap-filler/perf plan. (a) Snipper's own report JSONs no longer feed string-name evidence (content-sniff `"ruleId"` in `AssemblyNameEvidenceScanner`) — monorepo SNP0023 0 → 3, all three previously hidden by stale reports (the two attribute classes **plus `MilkrunDiscount`**, whose name a 1.4.4-era report spelled in unrelated finding paths — mechanism triple-confirmed). (b) `[JsonInclude]` private members are SNP0001 evidence (`FrameworkEvidenceIndex.HasMemberSerializationAttribute`). (c) `DiRegistrationScanner` syntax-first — semantic models only for registration-bearing documents. (d) Redundancy `?.`-guard per-tree memoized pre-clear (same accept/reject set). (e) **Parallelism adopted behind the revertible `SNIPPER_MAX_DOP` switch (default on):** per-document binding (`FrameworkEvidenceIndex`, `DiRegistrationScanner` pass 1) and — via the user-approved second spike — per-candidate `FindReferencesAsync` in SNP0005/0006 (two-phase restructure: sequential candidate enumeration, parallel searches, sorted output). Adoption gate: **0 finding drift on fixture2 (both modes, 23/25) and on the monorepo (2,323 findings, sequential vs parallel); monorepo SNP0005/0006 164.4s → 57.8s (−65%), total 309.9s → 211.3s (−32%)** — same-build back-to-back Debug runs. All other monorepo rules count-identical to 1.5.1. 1.6.0 (2026-09-19, 238 tests, 26 rule IDs): **Wave 5 gap-filler sweep** ([`1_6_0_phase3_wave5.md`](history/1_6_0_phase3_wave5.md)) — SNP0026 upcast variant (rebind-gated whole arguments + fixed-target contexts), SNP0024 can-be-private (locality proof over containing-type spans), SNP0028 redundant qualifiers (`this.` + qualified type names, speculation-gated), SNP0029 empty ctor/dtor, SNP0027 unused member hierarchy (override families with no external caller), SNP0030 event-never-invoked. Monorepo A/B vs 1.5.2: all pre-existing rules count-identical; +73 net findings across the new rules (21 can-be-private, 49 SNP0028, 2 SNP0029, 1 upcast; SNP0027/0030 zero on this codebase), all sampled findings explainable. Wave-time bug the A/B caught: SNP0027 family roots initially walked into metadata (empty finding path) — fixed pre-ship. 1.6.1 (2026-09-20, 252 tests): **external FP-review hardening** ([`1_6_1_fp_review.md`](history/1_6_1_fp_review.md)) — an apply-and-build-verify pass over 461 Guaranteed/High monorepo findings found 52 FPs/stale, 47 in one category. Fixed: SNP0003/SNP0004 now compute the **transitive consumer closure** (hub references flow downstream by default; `PrivateAssets="all"` blocks it) and SNP0004's **upstream exclusive flow** (removal evicts the reference's transitive project/package flow); SNP0026 suppresses casts on natural-type-less operands (collection expressions — the parenthesis-inherits-converted-type hole); SNP0019 calls out verbatim duplicates in the message; reports are stamped with `commitSha` + per-finding `lineText` (SARIF `snippet`) for mechanical drift checks. One claim disproven with an isolated repro: the three SNP0025 "FPs" are true positives (lambda output-inference + unique-interface inference both strip cleanly) — pinned in the fixture against future regression. Monorepo A/B vs 1.6.0: SNP0003 31 → 16, SNP0004 16 → 10, all three proven FP chains suppressed, remainders sampled-explainable. 1.6.2 (2026-09-20, 297 tests): **framework-evidence round 2** ([`1_6_2_fp_review.md`](history/1_6_2_fp_review.md)) - Advisory-tier FP review of the 1.6.1 monorepo report. Fixed: the 1.5.1 OpenApi transformer contract names were wrong (never matched; real names `IOpenApi*Transformer` + Swashbuckle `IDocumentFilter`/`IOperationFilter`); contract list gained Swashbuckle `IExamplesProvider`, xUnit (`IXunitSerializable`/`ITestCaseOrderer`/`IXunitTestCaseOrderer`), the MVC filter family, and MediatR pipeline middleware (request/notification handlers stay OUT - test-callable); framework-dispatched contract implementations are now evidence at the TYPE level too (scan-instantiated implementations carry no registration reference); new reflection plugin-by-scan evidence (`IsSubclassOf`/`IsAssignableFrom` bases → derived types, type-level only, simple-name closure). Two root-cause bugs: SNP0020 reported the trivia ANCHOR's location instead of the block's own (every finding misplaced; the "double emission" was two distinct blocks pinned to one method brace), and SNP0005/0006 proved applied attribute classes unreferenced because applications omit the `Attribute` suffix (usage-index short-spelling union). SNP0023 excludes `System.Attribute`-derived classes (terminal by convention). Same-tree monorepo A/B vs 1.6.1: SNP0006 1,125 → 1,085 (-40, all classified), SNP0023 3 → 1, SNP0020 12 → 12 (locations corrected), all other rules count-identical. 1.6.3 (2026-10-03, 353 tests): **SNP0031 duplicate-fragment detection shipped** ([`1_6_3_plan.md`](history/1_6_3_plan.md)) — Advisory, opt-in behind `--duplicate-detection`, syntax-only Type-1/Type-2 clone detection; opt-in + cross-directory guard + maximal-match collapse took dogfood from 1,160 findings to 2 (both triaged). Monorepo: 5,439 findings / 1,659 clone sets at 52.6 s marginal — **3.5× over the planned ≤15 s budget**, recorded honestly. Phase 0 prerequisite first: `pp\snipper` was reconciled onto trimmer's lineage (perf commit replayed as `8a6b58c`, tree hash unchanged, 4-way output parity verified), so 1.6.3 builds on real history. Wave 4 (below) was added 2026-10-03 from a competitive re-analysis that split detection parity from **entropy governance** — see [`competitive-analysis.md`](competitive-analysis.md) §3 and §6.**Wave 4 UPDATE 2026-10-05:** all of 4A, 4A-2, 4B and 4C are **SHIPPED as 1.7.0** (565 tests green; `<Version>` `1.7.0`, packed and installed), together with a performance/correctness increment that fixed a reproducible crash on 2 of 7 real solutions - see the 4D note below and [`1_7_0_plan.md`](history/1_7_0_plan.md) for the release gate. **4C's High tier was validated on the 2,763-file monorepo on 2026-10-05** (gate 5): it runs deterministically, fits its performance budget, and satisfies the Wave 4 exit criterion on its second branch - 29 High findings demoted to Advisory, 111 -> 82 - after four High-tier presentation defects were found and fixed. See [`1_7_0_plan.md`](history/1_7_0_plan.md) gate 5. **1.7.1 / 1.7.2 / 1.7.3 (SHIPPED 2026-10-05 → 2026-10-06)** — a correctness pass over Wave 4, not new features. All three defects (E1 clone-window verification, E2 file-creation drift, F1 SNP0019 double-report) and one withdrawn claim (F4) came from reviewing real findings on the owner's monorepo; **none was found by a test**, and the suite was green against a fixture that had never compiled. 6,893 → 6,625 findings on that monorepo. See the three sections below and [Next up](#next-up--the-17x-false-positive-backlog).

## Locked decisions

1. **Read-only forever.** Snipper never mutates source. No `snipper fix`, no auto-cleanup — detection and reporting only. This is a permanent design tenet, not a deferral; it supersedes the `--fix` candidate in `competitive-analysis.md` §5. ReSharper/VS own the *removal* workflow; Snipper owns CI-grade *detection*.
2. **Waves 1–3 committed** (through 1.4.0). Duplicates engine, coverage import, `.resx`/XAML remain tracked candidates, re-evaluated after 1.4.0 dogfooding. *(Amended 2026-09-19: duplicates shipped 1.6.3; coverage import and `.resx`/XAML still open. **Amended 2026-10-03:** Wave 4 committed — the first wave not driven by rule parity. The 2026-10-03 competitive re-analysis showed detection parity is largely closed while **entropy governance** has exactly one Snipper cell, so Wave 4 targets governance, not new rules.)*
2a. **Shape discipline extends to the governance wave** *(added 2026-10-03)* — Wave 4 stays inside the zero-dependency, single-binary, read-only, per-repository shape. Consequences, recorded so they are not relitigated: no MCP server (a token-efficient `ai` reporter is the sanctioned substitute), no org-scoped cross-repo index, no hosted dashboard (the rate ledger persists to a **committed, diffable JSON file** in the repo so it stays reviewable in PRs), and **no LLM dependency** — Snipper stays model-free and emits the deterministic inputs (`lineText`, `commitSha`, clone sets, divergence evidence) an agent needs to do the fixing. Wave 4 is also the first wave permitted to read git history; that access must stay read-only and must degrade gracefully outside a checkout.
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
| 3A | **Hierarchy dead code** ✅ | SNP0023, Moderate | Virtual member never overridden + class with virtuals never inherited. Built on the new shared `InheritanceGraph` index — the "SNP0018 override graph" assumed here turned out not to exist (SNP0018 uses per-symbol checks; corrected in [`1_4_0_phase3_wave3.md`](history/1_4_0_phase3_wave3.md)). String-name reflection evidence suppresses (SNP0006 demotion pattern). | M |
| 3B | **Tightening** ✅ | SNP0024, Advisory (default) | One analyser, three sub-checks: member-can-be-static (CA1822 parity), field-can-be-readonly (IDE0044 — reuses the SNP0021 reference machinery via the extracted `FieldReferenceMap`), internal-class-can-be-sealed (CA1852). Promotable via `snipper.json`. | M |
| 3C | **Redundancy sweep, part 2** ✅ | SNP0026, High | Redundant cast, narrowed to identity conversions in v1 (spike-proven: `IsIdentity` discriminates every non-flag shape); the upcast variant needs a rebind gate and stays deferred. | M |

## Wave 4 → 1.7.0 — "Entropy governance" (COMMITTED 2026-10-03)

Not a rule wave. The 2026-10-03 competitive re-analysis ([`competitive-analysis.md`](competitive-analysis.md) §3, §6) found detection parity largely closed and **entropy governance** almost entirely absent — Snipper had one cell (the new-findings gate) against SonarQube's new-code gate + per-author assignment + trends, and CodeScene's hotspots + change coupling + degradation Goals. These three stories are the features we found that **no tool in either matrix offers**. Per locked decision 2a, all three stay inside the current shape.

| # | Story | Kind | Design | Effort | Status |
|---|---|---|---|---|---|
| 4A | **Suppression / baseline integrity audit** | report | First, because it is cheapest and Snipper already computes it. `snipper.json` exclusions are applied at report time *after* baseline fingerprinting, so the analyser already knows exactly which findings each exclusion killed — and currently discards that. Emit: suppression count by rule and by age; findings suppressed **and nothing else**; obsolete suppressions (target code gone); and the headline ratio — what fraction of current "clean" status suppression is buying. Nothing new is analysed; this is accounting over data already in hand. **⚠ Both this sentence and the one above it were disproved by measurement — see the three findings below the table.** | S–M | **SHIPPED in 1.7.0** 2026-10-05, see [`1_7_0_plan.md`](history/1_7_0_plan.md) |
| 4B | **Entropy rate ledger** | metric + gate | Normalize new findings by churn and persist a series. `new_findings / kLOC changed`, against a configurable budget that fails CI. State lives in a **committed JSON file** so the ledger is reviewable in a PR (locked decision 2a) — no service, no dashboard. Answers the question a maintainer is actually asked quarterly: *are we controlling entropy, or just not looking at the stock?* **⚠ Three corrections, all measured — see [`1_7_0_plan.md`](history/1_7_0_plan.md):** (1) "per PR" is not viable as a gate: holding quality constant at one new finding, the per-change rate spans **200/kLOC at 5 changed lines to 0.5/kLOC at 2000** — a 400x spread from commit size alone — so the denominator is anchored to the commit the baseline was stamped at, which binds it to exactly the range the numerator came from; (2) per-team CODEOWNERS attribution is deferred, not shipped; (3) the gate is **opt-in with no default budget**, because any default would fail existing builds on upgrade. Per-month aggregation ships instead and has a large denominator by construction. | M | **SHIPPED in 1.7.0** 2026-10-05, see [`1_7_0_plan.md`](history/1_7_0_plan.md) |
| 4C | **Clone drift / inconsistent-fix detection** | findings | The missing half of SNP0031, and the only item on this list that finds **correctness defects** rather than smells. PMD's CPD docs concede this is beyond current tools ("we advise using CPD to help remove duplicates, not to help keep duplicates in sync"); jscpd's `--blame` gives authors but no divergence. Reuse the SNP0031 index: per clone set, walk history and surface copies that received a change the others did not. **Tier discipline matters more here than anywhere else** — only a change that looks like a defensive fix (`null`/guard/`try`/exception/bounds) applied to one copy while the others remain byte-identical to the pre-change text is High; anything looser is Advisory "possible drift". Degrades gracefully outside a checkout. **⚠ Three corrections, all measured - see [`1_7_0_plan.md`](history/1_7_0_plan.md):** (1) `GitMetadata` has read git since **1.6.1**, so this is not the first; what is new is reading *history*. (2) "Copies that received a change the others did not" is not expressible in the present tense, because **a copy that receives a fix stops being a clone and leaves the set** - verified on the fixture, where applying a one-sided guard changed *which pair* SNP0031 reported. 4C therefore reports the temporal **event** (commit X fixed copy A; the sibling was not brought in line until commit Y) and is silent about a fix that is still in place. That is the honest limit, and clone-set breakup is the follow-up. (3) Scope is the most recent commit touching each copy, so hunk and region coordinates are the same system and no reconciliation is needed. Tier discipline as specified above was kept and is pinned by tests. | L | **SHIPPED in 1.7.0** 2026-10-05, see [`1_7_0_plan.md`](history/1_7_0_plan.md) |

**Exit criteria:** 4A ships and reports on Snipper's own repository ✅ (it reports 33% of debt hidden under a one-rule suppression); 4A-2 makes the baseline churn-proof under every channel ✅; 4B's rate is measured against a plausible budget before any gate is enabled ✅ for the *opt-in* gate — **no default budget ships**, and per-team CODEOWNERS attribution is deferred; 4C's High tier produces zero false positives on the monorepo or drops a tier - **met 2026-10-05 on the second branch** (29 High findings demoted to Advisory, 111 -> 82); all three documented in README and `competitive-analysis.md` §8.1 ✅ (4A, 4B and 4C).

### 1.7.1 released 2026-10-05 — unopenable targets, and an SNP0031 tuning pass measured as a no-go

A patch release carrying one behavioural fix and one documented non-change. Full detail, measurements
and the reasoning are in [1_7_1_plan.md](history/1_7_1_plan.md).

**A malformed .slnx crashed the process.** The open-path filter was a closed type list and
System.Xml.XmlException derives from SystemException, not IOException, so it matched nothing.
SolutionPersistence throws it from an XmlDocument.Load in the serializer's reader constructor and
nothing upstream wraps. A hand-written malformed .slnx produced Unhandled exception... and exit
-532462766 instead of the documented 1. A .slnx that is well-formed XML with an invalid schema
failed the same way via SolutionException. Now both exit 1 with a diagnostic, and a latent second
bug on the same line is fixed too: neither the path nor the exception message was Markup.Escaped,
and an XmlException message ends [at line 3, position 12], which Spectre parses as markup.

**SNP0031's 4,373 findings on the monorepo were investigated and left alone.** 1.7.0 recorded that
the count "wants its own tuning pass"; that pass was run and it measured as a no-go. There is no
trimmable tail — the line distribution peaks at 10-13 lines, only 8% of findings sit at the 60-token
window floor, and zero are in generated code. Every threshold that shrinks the count deletes
representative findings rather than noise. The real driver is that the rule emits one finding per
occurrence rather than per clone set (134,675 occurrences behind 4,373 findings); switching to per-set
reporting would cut roughly 73% but changes the rule's contract, and with it baseline-churn and entropy
rate semantics. Deferred to its own plan rather than smuggled into a patch release.

### 1.7.2 and 1.7.3 released 2026-10-06 - false positives found by a first-800 sweep of the monorepo

Two patch releases from one investigation. Full detail and measurements in
[`1_7_1_false_positives_investigation.md`](history/1_7_1_false_positives_investigation.md) (the sweep),
[`1_7_2_plan.md`](history/1_7_2_plan.md) and [`1_7_3_plan.md`](history/1_7_3_plan.md).

**1.7.1's "no-go" was half wrong, and finding out why was the point.** The sweep read the first 800
findings of the monorepo report against source: **265 false positives of 800** (33%), of which 482
were real. Two engine-level defects accounted for 75.

- **1.7.2 - SNP0032 was blaming file creations.** 90 of 302 findings said a commit that *created* a
  file was a "one-sided defensive fix" - the merge that *introduced* a clone set, reported as drift
  within it. Now skipped, keyed on git's `new file mode` header rather than `OldCount`, because a
  guard inserted above a cloned block has the identical hunk header and is the shape the rule exists
  to report. **High 82 -> 29.**
- **1.7.2 - SNP0019 reported one directive twice.** Roslyn emits CS8019 *and* CS8933 on the same
  `using` when it duplicates a global; both were surfaced, and because the messages differ both
  hashed differently into the baseline. **250 -> 188**, with all 62 surviving CS8933 findings
  byte-identical.
- **1.7.3 - SNP0031 never verified the window it was handed.** `Extend` seeded its forward scan at
  `WindowTokens`, leaving offsets `[0,60)` uncompared, so a FNV-1a/32 collision became a 60-token
  clone. Caught in the wild: an interpolated `ToString()` reported as a clone of
  `Substitute.For<Refit.IApiResponse>()`. **232 removed, 0 added.** This also revived a dead
  `length < WindowTokens` guard and falsified a code comment that claimed collisions "cost time and
  never correctness".
- **1.7.3 - the SampleApp fixture had never compiled.** 8 distinct defects / 11 diagnostics,
  invisible because Snipper reports findings rather than requiring a clean build. `FixtureBuildShould`
  now pins it. Repairing it needed a public `InternalFixtureBridge` rather than `InternalsVisibleTo`,
  because a friend assembly correctly demotes every SNP0005/SNP0023 on the project from Moderate to
  Advisory - which broke five tests until reverted.

**Two proposals were withdrawn on evidence, not shipped.** SNP0012 (29 findings) looked like the same
class of defect until an isolated repro showed removing the direct `Serilog` reference still compiles:
the findings are true, and the rule is correctly tiered Moderate. And the investigation's own E1
evidence example was mis-attributed - the DI-registration family it used is a *normalisation* problem
(every registration normalises to `ID . ID < ID , ID > ( ) ;`), not a hash collision. Both corrections
are recorded in place.

**What this changes for the roadmap.** The 1.7.1 SNP0031 no-go stands for *thresholds* - there is
genuinely no trimmable tail - but the count it measured (4,373) included ~232 collisions and a
large real-but-worthless bootstrap/const-table family. Per-set reporting is still the right lever for
the latter, and it still needs its own plan.

### 1.7.4 released 2026-10-06 - namespace exclusion could not reach file-scope code

Detail in [`1_7_4_plan.md`](history/1_7_4_plan.md). One defect and one withdrawn claim, both from
reviewing real findings on the owner's monorepo.

- **1.7.4 - `--exclude-namespaces` silently did nothing for files with no namespace.** A
  top-level-statements `Program.cs` and a file of global usings declare no namespace, so
  `ExclusionEngine` had nothing to match: the symbol path returned `false` for any global-namespace
  symbol, and the syntax path resolved through an enclosing *type* declaration that file-scope code
  does not have. Excluding `Milkrun.Integration.MockingService` still produced 3 findings from that
  project. The fix promotes the `<global>` marker that SNP0031 already used internally
  (`DuplicateFragmentAnalyser`) to a shared `AnalysisExclusions` sentinel honoured by every
  namespace-aware rule, and accepts it on the command line — it had been config-file-only, purely
  because that path validated namespace syntax and the config file did not.
- **1.7.4 - the same change had a near-miss worth recording.** A file-scoped `namespace N;` is a
  *sibling* of its file-level `using` directives, not an ancestor, so the obvious ancestor walk
  classifies them as global. Shipping that would have made `<global>` suppress usings in every
  namespaced file in the solution. `Not_Suppress_Usings_In_A_Namespaced_File_When_Only_The_Global_Marker_Is_Excluded`
  pins it.

**A claimed second defect was withdrawn before any code was written.** The review also suspected
SNP0019 of judging global usings per-file. A fixture proved otherwise: CS8019 is evaluated over the
whole compilation, and a global using consumed only by a *different* file is correctly not reported.
Snipper surfaces the compiler's verdict unchanged and was never wrong. The suspicion was an artefact
of a test harness that matched flagged namespaces against project-wide type *names* and so counted
four projects' own `LogEventIds` as usage. Recorded in [`1_7_4_plan.md`](history/1_7_4_plan.md)
because the cheap fix for it — gating CS8019 behind a project-wide re-check — would have suppressed
true positives.

### 4D added 2026-10-04 — performance and correctness increment (not a roadmap story)

Surfaced while validating 4C for release, and shipped in the same 1.7.0 because two of its
findings gate the cut. Full detail, measurements and honest gaps in
[`1_7_0_plan.md`](history/1_7_0_plan.md) §"4D".

- **A crash fix that outranks most of the roadmap.** `UnreferencedPackageAnalyser` keyed a frozen
  dictionary on `project.FilePath`, which is **not unique in a `Solution`** (multi-targeted projects
  appear once per TFM). It threw `ArgumentException` inside the analyser fan-out and aborted the
  whole run. **2 of 7 real solutions on the test machine reproduced it.** A project-path keying bug
  is a monorepo-shaped bug, and this class did not appear anywhere in the fixtures.
- **A correctness gap in the parallelism contract:** `AssemblyNameEvidenceScanner` bypassed
  `AnalysisParallelism`, so the advertised `SNIPPER_MAX_DOP=1` escape hatch did not apply to it.
- **The perf work aimed at the wrong phase.** Measured phase split on real solutions: the
  `MSBuildWorkspace` design-time load is **38–69% of wall clock**, against a fan-out that is
  already well-tuned. CPU/wall is 1.7–3.0× on 16 cores. Cumulative effect of the work that *was*
  worth doing: **−2.9% to −18.1% wall, −7.8% to −25.3% CPU** across six targets, with the
  `findings` array byte-identical on all six.
- **Two hypotheses measured and rejected** so they are not retried: Server GC (worse — startup
  dominates a single-shot process) and `TieredCompilation=0` (+27% worse).
- **Methodology note, because it changes how future perf claims must be made:** non-interleaved
  before/after comparison is not evidence on this class of machine — one verified improvement
  measured 3.8% *slower* non-interleaved, and identical binaries differed >2× under load.
  `test/Fixtures/Measure-Performance.ps1` now ships to do interleaved A/B plus a `findings`-hash
  equivalence check.
- **Scale caveat:** no monorepo-scale target existed locally (largest real solution: 119 `.cs`
  files). The per-symbol and per-node memos measured *neutral* on every real target and only paid
  off (−2.9% wall / −3.0% CPU) on a generated 800-file target. Small-target benchmarking
  under-reports that class of change.
**Sequencing note:** 4A before 4B before 4C. 4A is a few days and validates the governance framing; 4C is the largest and the most likely to need its tiers revised after dogfooding, exactly as SNP0031 did.

### 4A implemented — three findings that changed the design

> **Release state:** 4A, 4A-2, 4B and 4C **SHIPPED as 1.7.0** on 2026-10-05. `<Version>` is now `1.7.4` (1.7.1 exit-code fix, 1.7.2 clone-drift + SNP0019 correctness, 1.7.3 clone-window soundness, 1.7.4 namespace-exclusion reach; 590 tests). `Snipper.1.7.4.nupkg` is packed. **Not yet installed as a global tool** — `dotnet tool update --global Snipper` has not been run for 1.7.4, so the installed tool still reports `1.7.3` and this release does not yet meet the repo's own convention (`1_6_3_plan.md` uses "SHIPPED" only after pack + `dotnet tool update`). There is no git tag and no NuGet publish pipeline here, so a release is a commit plus a pack and a tool install.

Recorded because two of them contradict this roadmap's own 4A row, which is left above as originally written:

1. **"Nothing new is analysed" was wrong.** `exclude.namespaces` and `rules: "off"` for a sole-rule analyser suppress findings *before* they exist as objects, so they cannot be counted from the normal finding set. 4A needs an opt-in shadow re-run (`--audit-suppressions`). The roadmap's cost warning also proved wrong by ~an order of magnitude: measured **0.8s on an 8.2s analysis (~10%)**, not the ~2× budgeted, because the shadow pass reuses memoized compilations and symbol indexes.
2. **The baseline-churn contract was half-true.** `exclude.paths` and severity overrides genuinely never churn the baseline; `exclude.namespaces` and sole-rule `off` entries did, and nothing said so. Measured −30 and −19 fingerprints on the SampleApp fixture. Harmless today (there is no gate), but it would have become a real CI failure the moment 4B enforced a budget. **Fixed by 4A-2** — see below.
3. **No gate exists.** `WriteReport` returns `0` regardless of finding count; only argument and IO errors return non-zero. The roadmap describes 4B as failing CI, which is new behaviour to build rather than an existing capability to reuse.

The audit's own verdict on Snipper's repository: disabling one rule that emits a single `Advisory` finding hides **33%** of everything the tool finds there.

### 4A-2 implemented — churn-proof baseline

**4B's blocking prerequisite is discharged.** The baseline is now fingerprinted from the suppression-independent set, so no `snipper.json` channel can rewrite it. Maintainer decision: approach (A) over (B) — the reasoning and the rejected alternative are recorded in [`1_7_0_plan.md`](history/1_7_0_plan.md).

| | Before | After |
|---|---|---|
| Baseline spread across 7 configurations | **30 fingerprints** | **0** |
| Re-add then remove `"SNP0024": "off"` | **30 findings resurface as new** | **0** |
| Re-add then remove a namespace exclusion | **19 findings resurface as new** | **0** |

Cost is bounded by construction: the extra pass runs only when a baseline **and** a churn-prone channel are both present, so a user with no baseline, or one relying only on `exclude.paths` / severity overrides / `--certainty-tier`, never pays for it. Measured ~10% marginal. Accepted cost: **a baseline file legitimately grows on first run** to include findings for excluded code.

**4B is now unblocked.** Its remaining prerequisite is that it introduces the first real gate, so its budget semantics need deciding up front — specifically whether the rate counts findings a baseline has already accepted.

## Tracked candidates (post-1.4.0, not committed)

- **Parallel semantic binding** — ~~spike GO 2026-09-18~~ **ADOPTED 2026-09-19 as 1.5.2** behind the revertible `SNIPPER_MAX_DOP` switch (default on): per-document binding (`FrameworkEvidenceIndex`, `DiRegistrationScanner` pass 1) and per-candidate `FindReferencesAsync` in SNP0005/0006. The workspace-level caveat was retired by a second user-approved spike: **2,323 findings, 0 drift, monorepo sequential vs parallel; SNP0005/0006 164.4s → 57.8s, total −32%**. Suite runs the parallel path by default; `SNIPPER_MAX_DOP=1` suite pass verified.
- **Duplicate detection** — token-shingle engine (normalised token streams, 60-token windows), cross-project. L. **SHIPPED 1.6.3 as SNP0031, opt-in** ([`1_6_3_plan.md`](history/1_6_3_plan.md)): Advisory, syntax-only Type-1 + Type-2 clone detection, spike-gated thresholds; ships as a normal finding rule (the 2026 "separate report section" note predates `snipper.json`/baseline). Opt-in behind `--duplicate-detection` (same precedent as SNP0007/0008): unguarded it reported 1,160 findings on Snipper's own solution, because every workspace analyser shares one structural skeleton. Cross-directory guard + maximal-match collapse bring that to 2. Monorepo: 5,439 findings / 1,659 clone sets, 52.6s.
- **Coverage evidence import** — `--coverage coverlet.xml` (cobertura); corroborating channel that adjusts certainty, never a standalone finding. M.
- **Unused `.resx` keys / XAML-Razor evidence** — generalise `AssemblyNameEvidenceScanner`. M–L.

### Added 2026-10-03 — governance candidates, deliberately NOT committed

These came out of the same re-analysis as Wave 4 but are held back. Reasoning for each is in [`competitive-analysis.md`](competitive-analysis.md) §6.4–§6.7 and §8.2; the common thread is that each needs either data Snipper does not yet collect or a judgement call that should not be made in the same wave as 4A–4C.

- **Unanalysed-region accounting** — report what the tool *cannot* see: `#if`/feature-flag regions silently skipped, constant-folded dead branches, permanently-on/off flags. Pure syntax, so the best architectural fit on the list, and it raises trust by volunteering blind spots. Candidate 15, S. *Uncommitted only because Wave 4 is already three stories — this is the first thing to add to it.*
- **Inferred architecture + erosion deltas** ("Layer B took 14 new dependencies on Layer C since March, across 23 commits"). Nobody infers architecture from history and reports erosion; ArchUnitNET/NetArchTest require hand-authored rules, which is why most teams never write them. Output must be **proposals with evidence, never findings** — intent is inferable, not provable. Candidate 4, M–L.
- **Removal-safety evidence (proof, not auto-fix)** — `--verify-removals` applies Guaranteed-tier removals in a scratch worktree, recompiles, and emits proof that the referenced-symbol set and public API surface are unchanged. Mutates nothing the user owns, so it respects locked decision 1 and supersedes the rejected `--fix` candidate in spirit. Candidate 22, L.
- **Per-team entropy ledgers via CODEOWNERS** — Sonar auto-assigns new issues to authors, CodeScene does team coupling, Qodana counts contributors via `.mailmap`; the deterministic free per-team burn-down ledger is the missing piece. Depends on 4B. Candidate 6.7, M.
- **Clone kinds: Type-3 near-miss** — jscpd parity gap. **Deliberately last**, not first: Type-3 over normalized tokens re-introduces exactly the structural-skeleton noise that forced SNP0031 behind `--duplicate-detection`. Spike and measure before building. Candidate 16, M.
- **Finding attribution (`--blame` equivalent)** — author + last-touch date per finding; jscpd, Sonar and CodeScene all have it. Prerequisite for per-team routing. Candidate 17, S.
- **Complexity + churn as prioritization only** — Snipper has no complexity or churn, so it cannot rank its own output or do anything hotspot-flavoured. Import both **purely to rank existing findings**; deliberately not new complexity rules (locked decision 6). Candidate 18, M–L.
- **Token-efficient `ai` reporter** — jscpd `--reporters ai` parity; delivers most of the MCP value at none of the shape cost (locked decision 2a). Candidate 21, S.

## Next up — the 1.7.x false-positive backlog

Wave 4 is shipped and 1.7.1–1.7.3 fixed the three **correctness** defects (E1, E2, F1) plus one
withdrawn claim (F4). What remains is a set of **tuning** classes from the same sweep. They are ranked
here, not in [`competitive-analysis.md`], because the sweep measured them on a real 81-project /
~3,000-file monorepo and that measurement is the input. Full evidence in
[`1_7_1_false_positives_investigation.md`](history/1_7_1_false_positives_investigation.md).

| Class | Rule | What is wrong | Findings affected | Status |
| --- | --- | --- | --- | --- |
| **F2** | SNP0006 | A DTO property populated by a serializer is reported as a zero-reference public member. No wire-format evidence channel exists. | 49 of 70 (70%) | **Open — largest single class.** Needs serializer-shape evidence, not a suppression. |
| **F3** | SNP0018 | `[Obsolete]` members that are **live serialisation contracts** — the contract is alive, the type is not. | 48 of 49 (98%) | **Open.** Shares F2's missing evidence channel; build once, use twice. |
| **F5** | SNP0032 | The fix-shape marker matcher promotes non-defensive text to `High` (≈17 cases). The matcher is deliberately loose in the safe direction, so this is a precision problem, not a soundness one. | ~17 | **Open.** Lower tier first, tighten second. |
| **F6** | several | Four remaining single-finding classes, each needing its own evidence channel. | 4 | **Open.** Individually trivial; collectively a pattern (see below). |
| **Normalisation** | SNP0031 | The study's largest *misattribution*: the DI-registration family is not a hash collision at all, it is over-aggressive token normalisation collapsing unrelated registrations into one shape. | — | **Open, and reclassified.** 1.7.3 fixed the collision bug; this is a different problem that the earlier draft conflated with it. |
| **Per-set reporting** | SNP0031 | Output is per-fragment, so one 30-fragment skeleton reads as 30 findings and a threshold table reads as hundreds. 1.7.1's no-go on tuning was correct *and* its premise was half wrong — the 4,373 included ~232 collisions that should never have been reported. | ~4,200 total | **Open — the largest lever.** Report clone sets with member counts rather than fragments. |
| **Clone-set breakup** | SNP0032 | A one-sided fix still in place removes the copies from the clone set, so 4C is silent about the case it exists to find. | — | **Open.** The documented blind spot since 1.7.0. |

**The pattern worth naming:** F2, F3 and F6 are all the same defect — *a rule has no channel for
evidence that only exists outside the compiler's model* (serialiser contracts, JSON shape, framework
dispatch). F1 was the one case where the evidence already existed and the rule simply did not consult
it. That is a cheaper class of fix, and it is where a new rule should look first.

**Sequencing note:** per-set reporting is the highest-value item and is independent of the evidence
work. F5 can ship in isolation at low risk. F2/F3 should ship together or not at all, since they need
the same channel. Normalisation should not be attempted before per-set reporting lands, because the
two interact — collapsing fewer skeletons is a different change from reporting them once.

---

## Next steps

0. **1.6.3 duplicate detection (SHIPPED)** — the last tracked candidate from the original Tier-3 list: [`1_6_3_plan.md`](history/1_6_3_plan.md) (token-shingle engine, SNP0031 Advisory, opt-in via `--duplicate-detection`, spike-gated). Remaining tracked candidates are coverage import and `.resx`/XAML evidence.

0d. **1.7.1 / 1.7.2 / 1.7.3 (SHIPPED 2026-10-05 → 2026-10-06)** — see [Next up](#next-up--the-17x-false-positive-backlog) above for what is still open, and [`1_7_1_plan.md`](history/1_7_1_plan.md) / [`1_7_2_plan.md`](history/1_7_2_plan.md) / [`1_7_3_plan.md`](history/1_7_3_plan.md) for each release. Measured effect of the three releases on the owner's monorepo, same tree and same flags: **6,893 → 6,625 findings** (SNP0031 −232 with **0** added and 0 files growing, SNP0032 −36). 581 tests green.

0e. **A methodological debt this wave created.** All three correctness defects (E1, E2, F1) were found by *reading findings on a real repository*, and none of them by a test. Meanwhile the 581-test suite was green throughout, against a `SampleApp` fixture that had never compiled. The next wave should decide deliberately whether per-repository finding review is a standing part of the release process — [`1_7_3_plan.md`](history/1_7_3_plan.md) argues it is, and the evidence supports that.

0a. **Wave 4 "Entropy governance" → 1.7.0 (COMMITTED 2026-10-03)** — 4A suppression/baseline integrity audit, 4B entropy rate ledger, 4C clone drift detection. Sequence 4A → 4B → 4C; see the Wave 4 table above and [`competitive-analysis.md`](competitive-analysis.md) §6.1–§6.3. Dogfood protocol per locked decision 2a: git-history access is read-only and must degrade gracefully outside a checkout.
  - **4A SHIPPED in 1.7.0** ([`1_7_0_plan.md`](history/1_7_0_plan.md)): `--audit-suppressions` reports per-channel suppression totals and stale suppressions. Baseline-churn accounting documented truthfully. **4A-2 SHIPPED in 1.7.0**: the baseline is now churn-proof under every suppression channel (395 tests green), which discharged 4B's prerequisite. **4B SHIPPED in 1.7.0**: `--entropy-rate` with an opt-in `--entropy-budget` gate (exit 3), a committed `--entropy-ledger`, and per-month aggregation (438 tests green). No measurable analysis overhead. **4C SHIPPED in 1.7.0**: `--clone-drift` emits SNP0032, High for a one-sided defensive fix and Advisory for other one-sided change. **565 tests green** after the `CliRunner` decomposition, the 4D perf/correctness increment, the determinism fixes and the 4C High-tier corrections. It found a real defect in its own author's code on the day it was written - a duplicated git runner where the stderr fix had been applied to one copy only. Gate 5 on the owner's 2,763-file monorepo then found four High-tier presentation defects, all fixed; High 111 -> 82.

0c. **Two uncomfortable competitive facts to carry into 1.7.0 planning** (from the 2026-10-03 re-analysis, [`competitive-analysis.md`](competitive-analysis.md) §0):
   - **jscpd is ahead of SNP0031 on clone *kinds*** — it ships Type-1/2/3 plus experimental Type-4 with C# in the embedding language list, `--skipLocal`, `--blame`, an MCP server and a clone baseline. SNP0031 is kept for the shared-solution-graph reason (one tool, one baseline, one SARIF stream), **not** because it is best-in-class. Type-3 parity is therefore tracked last, not first (see Tracked candidates). **Amended 2026-10-06: the original "and speed" claim is withdrawn** — it rested on a 52.6 s marginal figure that does not reproduce. Re-measured at 1.7.3, the same configuration on an ~3,000-file monorepo varied 138 s → 240 s between consecutive runs and the flag's marginal cost sat inside that noise; the honest controlled figure is **+0.7 s** on a small repo. See [`competitive-analysis.md`](competitive-analysis.md) §0.1.
   - **ReSharper's dead-code engine is free in CI** — `qodana-cdnet` runs under a Community licence with baseline, quality gate, coverlet import and Quick-Fix. Snipper's dead-code edge must now be defended on *certainty tiers* and *dependency-hygiene depth* (SNP0011/0012/0013), not on coverage.

0b. ~~REVERT the perf-wave index~~ — **executed 2026-09-18, shipped as 1.4.4** (see [`1_4_4_perf_wave_retrospective.md`](history/1_4_4_perf_wave_retrospective.md)). Remaining: the retrospective's kept-knowledge section records the reusable findings and the unsolved root-cause hypotheses for any future index retry.

0b. ~~Wave 4 framework evidence~~ — **shipped 2026-09-19 as 1.5.0** ([`1_5_0_phase3_wave4.md`](history/1_5_0_phase3_wave4.md)). SNP0006 on the monorepo −28% with rule-parity everywhere else. Follow-up candidates from the same trust theme: [JsonInclude] private members (SNP0001 evidence), SNP0023 external-hook evidence, stale report files in the analysis root feeding string-name evidence (consider excluding `output*.json` / snipper report files from `AssemblyNameEvidenceScanner` — mechanism confirmed twice: the 1.4.4 and 1.5.1 runs each suppressed real SNP0023 findings because a previous report JSON containing those names sat in the solution root), and the remaining closed-world items (test builders etc. — true positives, no action).

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
   - **1.6.0 (Wave 5, gap-filler rules — all five committed):** ~~SNP0026 upcast variant~~ / ~~SNP0024 can-be-private~~ / ~~SNP0028 redundant qualifiers~~ / ~~SNP0029 empty ctor-dtor~~ / ~~SNP0027 hierarchy member-level~~ / ~~SNP0030 event-never-invoked~~ — **all shipped 2026-09-19** ([`1_6_0_phase3_wave5.md`](history/1_6_0_phase3_wave5.md)).
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
