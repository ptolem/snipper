# Competitive Analysis — Snipper vs. the Codebase-Cleaning Ecosystem

**Date:** 2026-09-14 · **Updated:** 2026-10-06 · **Snipper version:** 1.7.3 (28 rule IDs: SNP0001–0013, 0018–0032; 19 analysers; 581 tests)

> **Read this first — the governance axis has moved.** This document was last substantively
> researched against Snipper 1.7.0, which predates the entire entropy-governance wave. Three of the
> seven items in §6 ("features no tool offers") have since **shipped** as 1.7.0: clone drift (SNP0032),
> entropy-as-a-rate with an enforced budget (`--entropy-rate` / `--entropy-budget` / `--entropy-ledger`),
> and the suppression audit (`--audit-suppressions`). §0 finding 3 — *"Snipper has no governance story
> at all"* — was true when written and is **false today**. §3's Matrix B and §4's gap list have been
> corrected in place. Competitor-side research was **not** re-verified in this pass: treat §2/§3 cells
> for *other* tools as accurate to 2026-10-03, and Snipper's own cells as current to 1.7.3.
**Scope:** features for *reducing the entropy of a large codebase* — dead code detection, redundancy/hygiene sweeps, dependency bloat, duplication, and the machinery that actually controls entropy: gating, prioritization, architecture, history, suppression, and agent consumption.

The previous revision compared five tools that all perform *single-snapshot static* analysis of one repository. That was the wrong axis. Large-codebase entropy is not primarily a detection problem — it is a **governance** problem. A tool that finds 20,000 issues and cannot say which 200 matter, or cannot prove the next thousand commits will not add another twenty thousand, does not reduce entropy. This revision therefore splits the comparison in two (§2 detection, §3 governance) and adds the vendors that actually compete on the second axis.

**Sources & evidence levels:**
- *Verified against vendor docs (fetched 2026-10-03):* CodeScene 7.5.x documentation and product pages (hotspots, change coupling, bus-factor simulation, refactoring recommendations, Goals/X-Ray/Delta Analysis, ACE auto-refactor language support); Qodana 2026.2 `.NET` and feature docs (`qodana-dotnet` vs **`qodana-cdnet` Community license**, baseline, quality gate, fresh/total coverage, CLEANUP/APPLY strategies, vulnerability checker, `.mailmap`, pricing); SonarQube Server/Cloud 2026.x docs (MQR mode, Clean as You Code, new-code definition and trends, automatic per-author issue assignment, AI CodeFix, MCP server, Gitar); jscpd documentation and benchmarks (Type-1/2/3/4 clone kinds, C# in the semantic-embedding language list, `--skipLocal`, `--blame`, clone baseline, MCP server, 159 MB / 3.4 s claim); PMD CPD documentation (including its explicit statement that keeping clones in sync is beyond current tools); ArchUnitNET / NetArchTest.Rules repositories; ReSharper 2026.2 inspection index; InspectCode CLT docs.
- *Verified 2026-09-14 (prior revision):* ReSharper "Remove Unused References", Rider 2023.1 NuGet-aware reference removal, dupFinder CLT, dotCover, SARIF-as-default in InspectCode since 2024.1.
- *Stable product knowledge (not re-verified this revision):* NDepend (coverage import, CQLinq, complexity, dependency graph), CodeMaat / ADAM (churn × complexity hotspots, change coupling), Stryker.NET (mutation testing), PoliCheck, Semgrep cross-file analysis, CodeQL, dotCover, SonarQube CPD.
- *Qualitative:* community sentiment and vendor benchmarking claims are marked as such. Vendor speed and accuracy claims are **theirs, not reproduced here**.

---

## 0. TL;DR — what changed, stated plainly

Three findings. Two were uncomfortable when written and are kept rather than softened; the third is
now **wrong**, and is corrected here rather than quietly deleted.

**1. Snipper's duplication engine is behind a free open-source tool on clone *kinds*.** jscpd ships Type-1, Type-2, **Type-3 near-miss**, and experimental **Type-4 semantic** clone detection (C# is in the embedding language list), plus `--skipLocal` (the same same-directory guard Snipper implemented for 1.6.3), `--blame`, SARIF, an MCP server, and a clone baseline with `--fail-on-new-clones` / `--baseline-from-ref`. Snipper's SNP0031 is Type-1/Type-2 only. On the duplication axis specifically, jscpd is ahead on clone kinds, and SNP0031 remains worth keeping for the Roslyn-native reason (it shares the solution graph, `snipper.json`, baseline, and SARIF pipeline rather than being a second tool in the pipeline). It should not be described as best-in-class.

  **Two corrections since first written.** *(a) The speed claim is withdrawn.* This revision reported SNP0031 at "52.6 s marginal on a 3,070-file monorepo — 3.5× over its own ≤15 s budget." Re-measuring on the same kind of repo (81 projects, ~3,000 files) with 1.7.3 did not reproduce it: a full run took 125–255 s and **the same configuration varied 138 s → 240 s on consecutive runs**, with the marginal cost of the flag sitting inside that noise. The honest figure, measured where it can be measured, is **+0.7 s** on a 53-commit repo (11.4 s → 12.1 s, 5-run average). The earlier number was real when taken but was never a controlled measurement. *(b) The duplication axis now has a half nobody else ships.* SNP0032 (clone drift) went out in 1.7.0 and 1.7.3 fixed a real soundness bug in it — the clone index is keyed on a 32-bit hash and the 60-token window was never verified, so unrelated files could be reported as duplicates (232 such findings on the monorepo). jscpd's `--blame` still yields authors and dates and no divergence analysis, and PMD CPD still documents keeping clones in sync as beyond its reach.

**2. ReSharper's dead-code engine is now free in CI.** `qodana-cdnet` (ReSharper-based) runs under a **Community license** — no Ultimate subscription — with baseline, quality gate, coverlet import, and Quick-Fix. The prior revision's "ReSharper: CLT free (IDE paid)" framing understated the threat. `UnusedMember.Global` — the SWEA whole-program dead-code analysis Snipper spent 1.4.0–1.6.2 building toward — is now reachable in a CI pipeline at no licence cost. Snipper's remaining edge on this axis is *dependency-hygiene depth* (SNP0011/0012/0013) and certainty tiers, not dead-code coverage.

**3. ~~Snipper has no governance story at all.~~ Corrected 2026-10-06: this was true of 1.7.0-onward at the time of writing, and Wave 4 then built the missing story.** The original text read: *"It has `--baseline`, which is a new-findings gate — genuinely useful, and parity with SonarQube's new-code gate is close. Beyond that it has no prioritization (no complexity, no churn), no architecture modelling, no history, no suppression accounting, and no agent surface."*

  As of 1.7.3, three of the six gaps in that sentence are closed:

  | Claimed gap | Status |
  | --- | --- |
  | "no history" | **Closed.** `--clone-drift` reads history in two batched git calls; `--entropy-rate` anchors a churn-normalized rate to the commit recorded in the baseline; `--entropy-ledger` keeps a committed per-month series. |
  | "no suppression accounting" | **Closed.** `--audit-suppressions` reports what each of the five suppression channels hides, with stale-detection at two confidence levels. No peer in §3 does this. |
  | "no prioritization" | **Partly closed, and this is the honest part.** Entropy *rate* normalizes by churn and can gate a build (`--entropy-budget`, exit `3`), but nothing **ranks** findings. There is no complexity, no hotspot ranking, no "which 200 of these 6,626 matter". Prioritization-as-a-gate is not prioritization-as-a-list. |

  Still genuinely absent: architecture modelling, an agent surface (no MCP server), per-author attribution, and a finding-ranking axis. The specific sentence *"Every tool in §3 that competes on entropy rate rather than entropy stock does so with data Snipper does not collect"* no longer holds — Snipper now collects it — but its replacement is narrower than the original claim implied: it collects **rate**, not **priority**.

**Where Snipper still leads** is unchanged and real, and one item has been added: SNP0011/0012/0013 (orphan projects, redundant transitive packages, framework-inbox packages), SNP0018 (obsolete dead code), the read-only CI-first shape with no mandatory build, the certainty-tier doctrine, and — new — **clone drift and the churn-normalized entropy budget**, which no peer in §3 ships. §5.

---

## 1. What this revision is for

The doc answers three questions, in order:

1. **§2 — Detection parity.** For each class of finding, who finds it? (Snipper's home turf; mostly closed.)
2. **§3 — Entropy governance.** Who can tell a maintainer *where* to spend effort, *prove* debt is not growing, and *stop* it growing? (Snipper is largely absent. This is where the opportunity is.)
3. **§6 — What nobody offers.** The features a large-codebase maintainer needs that no tool in either matrix provides, ranked, with each one's nearest neighbour named honestly so the novelty claim can be audited.

---

## 2. Matrix A — detection parity (findings)

✓ = strong/native · ~ = partial · — = absent. Rider shares ReSharper's engine → one column, now including Qodana's CI packaging of it.

| Capability | Snipper | ReSharper / Rider + Qodana | VS + Roslyn | NDepend | SonarQube | jscpd / PMD CPD |
|---|---|---|---|---|---|---|
| **Dead code** | | | | | | |
| Unused private members | ✓ SNP0001 | ✓ `UnusedMember.Local` | ✓ IDE0051/CA1823 | ✓ | ✓ S1144/S1068 | — |
| Unused internal/public symbols | ✓ SNP0005/0006 | ✓ `UnusedMember.Global` (SWEA) | — | ✓ | ~ (private-focused) | — |
| Unused locals / parameters | ✓ SNP0009/0010 | ✓ | ✓ IDE0059/IDE0060 | — | ✓ S1481/S1172 | — |
| Unreachable statements | ✓ SNP0002 | ✓ | ✓ CS0162 | — | ✓ | — |
| Obsolete zero-usage members | ✓ **SNP0018 (unique)** | — | — | — | — | — |
| Field written, never read / unassigned | ✓ SNP0021 (unassigned stays SNP0001/CS0649) | ✓ `NotAccessedField`, `UnassignedField` | ✓ IDE0052, CS0649 | — | ✓ S4487 | — |
| Event never invoked | ✓ SNP0030 | ✓ `EventNeverInvoked` | ~ | — | ~ | — |
| Return value never used / param-only-precondition / out discarded / nameof-only | ~ (SNP0021 covers two slices) | ✓ 4 inspections | — | ~ (CQLinq) | — | — |
| Hierarchy dead code | ✓ SNP0023 + SNP0027 | ✓ `VirtualMemberNeverOverridden`, `ClassWithVirtualMembersNeverInherited`, `UnusedMemberHierarchy`, `UnusedMemberInSuper` | — | ~ | ~ | — |
| **Redundancy sweeps** | | | | | | |
| Unused usings | ✓ SNP0019 (CS8019 + CS8933, incl. globals) | ✓ `RedundantUsingDirective` (+global) | ✓ IDE0005 | — | ✓ S1128 | — |
| Redundant casts / qualifiers / type args / default args | ~ SNP0022/0025/0026/0028 (all soundness-gated; rest not covered) | ✓ 103 "Redundancies in Code" | ~ IDE00xx subset | — | ~ | — |
| Empty ctor/dtor/namespace, redundant overload/override/initializer/partial | ~ SNP0029 (two slices) | ✓ | ~ | — | ~ | — |
| Commented-out code | ✓ SNP0020 | — | — | — | ✓ S125 | — |
| **Tightening (entropy prevention)** | | | | | | |
| can-be-static / readonly / sealed / private / internal / const / init-only / file-local | ~ SNP0024 (static, readonly, sealed, private) | ✓ `MemberCanBeMadeStatic`, `FieldCanBeMadeReadOnly`, `ClassCanBeSealed`, `MemberCanBePrivate/Internal/FileLocal`… | ~ CA1822, CA1852, IDE0044 | ~ | ~ | — |
| **Dependency hygiene** | | | | | | |
| Unreferenced `PackageReference` | ✓ SNP0003 | ✓ Rider (NuGet-aware); Qodana license audit | ✓ "Remove Unused References" | ~ | — | — |
| Unreferenced `ProjectReference` | ✓ SNP0004 | ✓ | ✓ | ✓ | — | — |
| Redundant transitive direct package (lock-file graph, version-aware) | ✓ **SNP0012 (unique)** | — | — | — | — | — |
| Framework-inbox package (`PackageOverrides.txt`) | ✓ **SNP0013 (unique)** | — | — | — | — | — |
| Orphan / detached projects (graph + filesystem sweep) | ✓ **SNP0011 (unique)** | — | — | ~ (unused assemblies) | — | — |
| Outdated / vulnerable / deprecated packages | — | ✓ Rider NuGet health; Qodana vulnerability checker | ✓ NuGet Audit | — | — | — |
| License compliance | — | ~ (Qodana, paid tiers) | — | — | ~ | — |
| **Cross-language assets** | | | | | | |
| Unused `.resx` keys; XAML/Razor symbol usage | — (tracked) | ✓ (XAML, Razor, ASP.NET, Resx in engine) | — | — | ~ | ~ (224 langs) |
| **Duplication** | | | | | | |
| Exact clones (Type-1) | ✓ SNP0031 | ✓ dupFinder CLT | — | ✓ | ✓ CPD | ✓ (default) |
| Renamed clones (Type-2) | ✓ SNP0031 | ✓ | — | ~ | ~ | ✓ `--ignore-identifiers` |
| Near-miss clones (Type-3) | — | ~ | — | ~ | — | ✓ `--max-gap-lines`, `--similarity` (AST, JS/TS) |
| Semantic clones (Type-4) | — | — | — | — | — | ~ experimental, `--semantic`; **C# in language list** |
| Same-directory clone suppression | ✓ SNP0031 (measured 1160→2) | — | — | — | — | ✓ `--skipLocal` |
| Clone **drift** (copies that should have been fixed together) | — | — | — | — | — | — (see §6.1; CPD docs concede it is unsolved) |
| **Runtime evidence** | | | | | | |
| Coverage-driven "never executed" code | — (tracked) | ✓ dotCover; Qodana fresh/total coverage | — | ✓ (imports coverage) | ~ | — |
| Dead code kept alive *only* by tests | — | — | — | ~ (derivable) | ~ | ~ (`--dead-code` confidence, JS/TS/Py/Rust) |
| **Complexity / size** | | | | | | |
| Cyclomatic / cognitive complexity, nesting | — | ✓ | ~ | ✓ | ✓ | ~ (`--complexity` ranking) |
| **Workflow** | | | | | | |
| Auto-fix / bulk cleanup | — (read-only tenet) | ✓ CleanupCode; Qodana CLEANUP/APPLY; Quick-Fix | ✓ Code Cleanup + `dotnet format` | — | ~ (AI CodeFix) | — |
| Safe-delete with usage preview | — | ~ | ~ | — | — | — |
| Severity/suppression config | ✓ `snipper.json` | ✓ `.editorconfig` + `.DotSettings` + `qodana.yaml` | ✓ `.editorconfig` | ✓ | ✓ quality profiles | ✓ `.jscpd.json` |
| CI baseline / new-code gate | ✓ `--baseline` | ✓ Qodana baseline + `--fail-threshold` | — | ✓ | ✓✓ (industry reference) | ✓ clone baseline, `--fail-on-new-clones` |
| Baseline without a committed file (`--baseline-from-ref`) | — (generate from a checkout) | ~ | — | — | ✓ reference branch | ✓ |
| SARIF output | ✓ | ✓ (default since 2024.1) | ~ | — | ✓ | ✓ |
| Design-time squiggles | — | ✓✓ | ✓✓ | ✓ | ✓ (SonarLint) | — |
| Git blame / authorship attribution | — | ~ | ~ | — | ✓ (auto-assigns new issues to author) | ✓ `--blame` |
| Agent / MCP surface | — | ~ (VS Code, Qodana Cloud API) | ~ (Copilot) | — | ✓ **MCP server**, Gitar, AC/DC | ✓ **MCP server**, agent skills, `ai` reporter |
| Free & open-source | ✓ | **✓ via `qodana-cdnet` Community** | ✓ | paid | community tier | ✓ |

**Matrix A verdict.** Detection parity on Snipper's home turf is genuinely good — competitive on dead code, ahead on redundancy, unique on dependency hygiene. Two concrete parity gaps remain: **clone kinds** (Type-3 absent; Type-4 experimental in jscpd) and **attribution** (`--blame`-style authorship, cheap). Both are in §7.

---

## 3. Matrix B — entropy governance (Snipper's cells updated to 1.7.3)

✓ = strong/native · ~ = partial · — = absent. **Snipper's column is current to 1.7.3; all other columns are as researched on 2026-10-03.**

| Capability | Snipper | ReSharper / Qodana | SonarQube | CodeScene | NDepend | ArchUnitNET / jQAssistant | jscpd |
|---|---|---|---|---|---|---|---|
| **Prioritization** | | | | | | | |
| Complexity metrics | — | ✓ | ✓ | ✓ Code Health™ | ✓ | — | ~ |
| Churn / hotspot (change frequency) | ~ (churn only as an entropy **denominator**; no per-file ranking) | — | ~ (SCM analysis) | ✓✓ core product | — | — | ~ (`--blame` dates) |
| Hotspot = churn × health, ranked | — | — | — | ✓✓ | ~ | — | — |
| Change coupling (files that change together) | — | — | — | ✓ | — | — | — |
| Bus-factor / offboarding risk | — | ~ (Qodana `.mailmap` counts contributors) | ~ | ✓ simulation | — | — | ~ (`--blame`) |
| **Gating / trend** | | | | | | | |
| New-findings gate | ✓ `--baseline` | ✓ baseline + fail-threshold | ✓✓ new-code quality gate | ✓ Delta Analysis / PR review | ✓ | ✓ (tests) | ✓ clone baseline |
| Per-author attribution of new issues | — | ~ | ✓ | ✓ | — | — | — |
| New-code trend over time | ✓ `--entropy-ledger` monthly aggregates | ✓ Insights | ✓ "Tracking new code trends" | ✓ | ~ | — | ~ |
| **Entropy as a rate, normalized by churn, with a budget** | **✓✓ `--entropy-rate` + `--entropy-budget` (exit 3) + `--entropy-ledger`** | — | — | ~ Goals (degradation alerting, complexity-based) | — | — | — |
| **Suppression / baseline integrity** | | | | | | | |
| User false-positive / won't-fix workflow | — | — (baseline only) | ✓ | ~ | — | — | — |
| Audit suppressions themselves (count, age, rule mix, what they hide) | **✓ `--audit-suppressions`** (incl. stale detection at two confidence levels) | — | — | — | — | — | — |
| **Duplication governance** | | | | | | | |
| One-sided fix in a clone set (drift / divergence) | **✓✓ SNP0032 `--clone-drift`** (High when fix-shaped) | — | — | ~ (duplication, adjacent) | — | — | — (authors/dates only) |
| **Architecture** | | | | | | | |
| Dependency-direction / layering rules | — | ~ (inspections + Rider diagrams) | ~ (dependency rules, cycles) | ✓ change-coupling map | ✓ dependency graph | ✓✓ **purpose-built** | — |
| Rules hand-authored by the maintainer | — | — | ~ | — | ~ | ✓✓ | — |
| Architecture **inferred** from history, then erosion reported | — | — | — | ~ (adjacent) | — | — | — |
| **Dead-code-specific governance** | | | | | | | |
| Dead code vs live coverage cross-check | — (tracked) | ✓ | ~ | — | ✓ | — | ~ |
| **Structural self-consistency** | | | | | | | |
| Analysed-region accounting (what the tool *cannot* see: `#if`, generated, excluded) | — | — | ~ (exclusions visible in UI) | — | — | — | — |
| **Agent consumption** | | | | | | | |
| MCP server / agent skills / token-efficient report | — | ~ | ✓ | ✓ (VS Code ext, CLI hooks, skills) | — | — | ✓ |

**Matrix B verdict, revised for 1.7.3.** Snipper no longer has "exactly one cell" in this matrix. It now has **five**, and three of them are cells no peer fills: the churn-normalized **entropy rate with an enforced budget** (no commercial tool in this matrix computes a rate; CodeScene's *Goals* is the closest and is complexity-based, commercial, and closed), the **suppression audit**, and **one-sided clone-drift detection**. What it still lacks is the *prioritization* axis — complexity metrics, hotspot ranking, and change coupling are all absent, and nothing tells a maintainer which 200 of 6,626 findings to act on. SonarQube's new-code gate plus automatic per-author assignment plus trend view remains the most *complete* governance story available as a product, and it remains a stock-and-author story rather than a rate story. The cells still marked "—" for Snipper are §6's remaining opportunity list.

---

## 4. Where Snipper is behind (explicit gap list)

*Items 3 and 4 were closed by Wave 4 and are marked rather than deleted. Re-measured numbers are from
an 81-project / ~3,000-file / 3,896-commit monorepo at 1.7.3.*

1. **Duplication: behind on clone kinds, ahead on divergence.** Type-3 near-miss absent, Type-4 experimental elsewhere. No longer a *speed* gap on the evidence available — the old 52.6 s marginal figure did not reproduce (§0.1) and the honest measured marginal is +0.7 s on a small repo. The remaining real gap is clone *kinds*, plus no `--blame` equivalent.
2. **No prioritization.** No complexity, no hotspot ranking, no change coupling. Churn is collected — but only as the **denominator** of an entropy rate, never as a ranking input. Consequence: Snipper cannot answer "which 200 of these 6,626 findings matter?", and cannot rank its own output. This is now the single largest governance gap, and it also blocks every hotspot-flavoured idea in §6.
3. ~~**No suppression accounting.**~~ **CLOSED in 1.7.0.** `--audit-suppressions` reports what each of the five channels hides, plus stale suppressions at two confidence levels. The design note that made it honest — two channels suppress *before* a finding exists, so an accurate count needs a shadow pass with suppressions lifted — is recorded in [`plan_1_7_0.md`](plan_1_7_0.md).
4. ~~**No history.**~~ **CLOSED in 1.7.0.** History is now read for clone drift (two batched git calls) and for the entropy rate's changed-line denominator. What is still missing is **delta mode** — every run is still a full re-analysis; there is no incremental mode. The original parenthetical ("188 s on the 3,070-file monorepo") is now 125–255 s, machine-load dependent.
5. **No agent surface.** jscpd and SonarQube both ship MCP servers; Snipper emits JSON/SARIF only. This is the cheapest gap on this list and the most strategically relevant — agent-mediated consumption is becoming the primary interface for this category.
6. **No architecture modelling.** Layering, cycles, and fitness functions are entirely absent (a deliberate non-goal to date, never explicitly argued).
7. **Attribution.** No `--blame` equivalent on findings.
8. **Free ReSharper in CI** (`qodana-cdnet`) means dead-code parity must be defended on certainty and dependency depth, not coverage.
9. **The duplication rules are opt-in and their false-positive rate is only one-repo deep.** On the monorepo, SNP0031 alone is ~4,200 of ~6,600 findings — about two thirds of the report. A first-800 sweep found the output dominated by *real but not worth removing* duplication rather than by wrong answers, which is a tuning problem rather than a soundness one. One repository is not a calibration.

---

## 5. Where Snipper leads

- **SNP0012** — redundant transitive packages via real lock-file graphs with version-range awareness. Nothing in the ecosystem does this; ReSharper/Rider/VS only flag *wholly unused* references, and now do so for free in CI.
- **SNP0013** — framework-inbox packages via `PackageOverrides.txt`. Unique.
- **SNP0011** — orphan + detached projects with plugin-loading string evidence. Unique; no tool sweeps for projects abandoned on disk.
- **SNP0018** — obsolete-member dead code. Unique.
- **Certainty tiers as a first-class axis.** Snipper's Guaranteed/High/Moderate/Advisory scale is *confidence the finding is correct*. SonarQube's MQR mode is *severity of impact on each software quality*; ReSharper's `.Global`/`.Local` split is *scope*. These are orthogonal axes and Sonar is the only peer that has made impact multi-dimensional — it has not made **confidence** a reported dimension. Pairs naturally with MQR into a 2-D severity × confidence matrix; nobody offers it.
- **CI-first shape** — read-only, no mandatory build (InspectCode builds by default), SARIF + JSON + baseline in a zero-dependency global tool. InspectCode only matched SARIF in 2024.1 and still has no baseline concept; Qodana adds one but needs Docker/native runner setup and a JetBrains account for anything beyond the free tier.
- **Compounding FP discipline.** Three FP-review rounds against a live ~3,000-file monorepo with strip-compile proofs, and a record of *upholding* challenged true positives rather than reflexively demoting rules — including upholding all four 1.6.2 findings and then **withdrawing one rule enhancement** (SNP0012's direct-transitive claim) after an isolated build proved it wrong. Most competitors report recall; Snipper documents soundness, including its own.
- **Clone drift / one-sided-fix detection (SNP0032).** §6.1, shipped 1.7.0. PMD CPD's documentation explicitly concedes that keeping clones in sync is beyond current tooling; jscpd's `--blame` gives authors and dates but no divergence analysis. No cell in §3 is filled by any peer.
- **Churn-normalized entropy rate with an enforced budget.** §6.2, shipped 1.7.0. `--entropy-budget` fails the build (exit `3`) on a rate, not a stock, and **fails open** — an unmeasurable rate is one of seven named statuses, never `0.00`, so a missing git checkout cannot break a pipeline. Nothing else in §3 normalizes by churn.
- **Suppression and baseline-integrity audit.** §6.3, shipped 1.7.0, with no nearest neighbour at all.
- **A baseline that means the same thing under every configuration.** Five suppression channels, and all five are proved churn-free: `--baseline` output is identical whether or not any suppression is configured. Most competitors' baselines silently change meaning when you tune them.

---

## 6. Features no tool offers

Ranked by expected impact on a large codebase's entropy trajectory. For each, the nearest existing neighbour is named so the novelty claim is auditable — several ideas that look original are **not**, and are marked as such.

**Status as of 1.7.3: the top three have shipped.** §6.1, §6.2 and §6.3 were written as proposals, implemented in Wave 4 (1.7.0), and are kept here as the record of what was claimed and what the implementation actually had to concede. §6.4–§6.7 remain open.

| § | Feature | Status |
| --- | --- | --- |
| 6.1 | Clone drift / inconsistent-fix detection | **SHIPPED** 1.7.0 (SNP0032); soundness fix 1.7.3 |
| 6.2 | Entropy as a rate, normalized by churn, with a budget | **SHIPPED** 1.7.0 |
| 6.3 | Suppression and baseline integrity audit | **SHIPPED** 1.7.0 |
| 6.4 | Inferred architecture and erosion deltas | open |
| 6.5 | Unanalysed-region accounting | open |
| 6.6 | Removal-safety evidence (proof, not auto-fix) | open |
| 6.7 | Lower-priority absences | open |

### 6.1 Clone drift / inconsistent-fix detection — *nearest neighbour: PMD CPD, which concedes it is unsolved*

PMD's CPD documentation says it plainly: *"failure to keep the code in sync may mean automated tools will no longer recognise these blocks as duplicates. This means the task of finding duplicates to keep them in sync when doing subsequent refactorings can no longer be entrusted to an automated tool… We thus advise developers to use CPD to help remove duplicates, not to help keep duplicates in sync."*

This is a vendor stating that the most valuable half of duplication management is unassisted. The mechanism is straightforward and reuses SNP0031's index: for each clone set, walk git history and identify **copies that received a change the others did not** — then rank by whether the divergence is a bug fix (one copy got the null guard, three did not) or benign drift. jscpd's `--blame` yields authors and dates but no divergence analysis. Nothing computes it.

Why this is first: it is the only item on this list that finds **correctness defects** rather than smells; it is the missing half of the feature Snipper just shipped; and it has unambiguous remediation (consolidate or delete the clone set). It also explains *why* duplication matters, which SNP0031 currently cannot.

**Implemented as 4C (SNP0032) with one correction the fixture forced:** the present-tense reading is impossible, because a copy that receives a fix stops being a clone and leaves the set - verified, since applying a one-sided guard changed which pair SNP0031 reported. The rule therefore reports the temporal *event* and is silent about a fix still in place; clone-set breakup is the follow-up. Caveat that must be stated in any plan: "one copy changed" is a heuristic, not proof. High-confidence subset — a change that looks like a defensive fix (`null`/guard/try/catch/exception/bounds) applied to one copy of a clone set, where the other copies are byte-identical to the pre-change text — is defensible; anything looser is a report of *possible* drift and belongs in a lower tier.

**Two further defects were found by a first-800 sweep of the monorepo after this shipped, both fixed in 1.7.2/1.7.3, and both are worth recording as a caution about claiming a feature is done.** 4C builds on SNP0031's clone sets, so a soundness bug *upstream* of it silently degraded 4C too:

- **1.7.2 — the upstream set was wrong in a way 4C inherited.** SNP0032 reported 90 of 302 findings as one-sided fixes on commits that *created* the file. A file's first appearance cannot leave a sibling untouched, because there was no sibling. The patch parser had no concept of a creation hunk; it now records one and 4C ignores it.
- **1.7.3 — and unsound in a way no test caught.** SNP0031's candidate index is keyed on a 32-bit hash of the 60-token window, and `Extend` seeded its forward scan *at* `WindowTokens` — so the window the key was computed from was never actually compared. A hash collision therefore produced a finding claiming two unrelated files were 60-token duplicates (an interpolated `ToString()` reported as a clone of `Substitute.For<Refit.IApiResponse>()`). 232 findings on the monorepo were this or this shape. `Extend` now proves the window before seeding, and the test suite brute-forces a real collision to prove it.

The generalisable lesson, and it is the reason this entry is still here rather than archived: a clone-detection rule's hardest correctness property is that **candidates are verified against the tokens the index stands for**. An 81-project monorepo found it; a 581-test suite with the fixture not compiling did not.

### 6.2 Entropy as a rate, normalized by churn, with an enforced budget — *nearest neighbours: SonarQube new-code trends, CodeScene Goals; neither computes a churn-normalized rate*

Every tool ships entropy **stock** (how many findings) or a **delta** (new findings since a reference). None ships a **rate** — findings added per unit of change — which is the number a maintainer is actually asked for in a quarterly engineering review, and the only one that is comparable across teams of different sizes and cadences.

Design: persist a per-commit time series of (findings added, findings resolved, lines changed, commit SHA). Emit `new_findings / kLOC changed` with a configurable budget that can fail CI. Snipper already stamps `commitSha` in its JSON report and already computes new-vs-baseline, so the increment is normalization plus a stored series. Cost: a small state file, no new analysis, no new semantic work. **Implemented as 4B with one measured correction:** the denominator is anchored to the commit recorded in the baseline file rather than to the pull request, because at per-PR granularity the rate is dominated by change size — holding quality constant at one new finding, it spans 200/kLOC at 5 changed lines to 0.5/kLOC at 2000, a 400x spread that would make any fixed budget a measure of PR size. The gate ships opt-in with no default budget.

This is deliberately the cheapest high-impact item on the list. It is also the only one that directly answers the maintainer's question — *are we actually controlling entropy, or just not looking at the stock?*

### 6.3 Suppression and baseline integrity audit — *nearest neighbour: none*

Every long-lived codebase accumulates `#pragma: disable`, `[SuppressMessage]`, `.editorconfig` severities, and baseline entries. This is entropy disguised as cleanliness, and no tool reports on it. A maintainer inheriting a repository needs: total suppressions and their age distribution; which rules dominate; **findings suppressed and nothing else**; suppressions that are now obsolete (the code they covered is gone); and the headline question — *what fraction of our "clean" status is suppression currently buying us?*

**Correction, 2026-10-03.** An earlier draft of this section claimed `snipper.json` is applied at report time for *every* channel, "so the analyser knows precisely which findings each exclusion removed". That is true of only three of the five channels. `exclude.namespaces` suppresses findings inside the analysers and `rules: "off"` for a sole-rule analyser prevents the analyser running at all, so for those two the analyser does **not** know what was removed — the findings never become objects. Measuring this is what produced the two non-obvious consequences: an honest audit needs an opt-in re-run with suppressions lifted, and those same two channels silently rewrote the baseline (measured −30 and −19 fingerprints on the SampleApp fixture). Both are now fixed — see [`plan_1_7_0.md`](plan_1_7_0.md).

Snipper remains unusually well-placed here, for a different and better reason than the one originally claimed: all five channels funnel through two auditable places — `FindingFilter` for the report-time three, `ExclusionEngine.IsNamespaceExcluded` plus the analyser-removal path for the other two — so a single shared classifier can attribute every channel without duplicating the filter's logic. **Implemented** as `--audit-suppressions`. This is a governance feature rather than a detection feature, and it remains the item most likely to be valuable to exactly the large-codebase maintainers this tool targets.

### 6.4 Inferred architecture and erosion deltas — *nearest neighbour: ArchUnitNET/NetArchTest/jQAssistant (hand-authored rules), CodeScene change coupling (behavioural, adjacent)*

Architecture erosion is the largest single source of long-run entropy in a large codebase, and the standard defence — fitness functions as executable tests — is **labour-intensive to author**, which is why most teams never do it. Nobody *infers* the architecture from dependency history, proposes candidate fitness functions, and then reports where the code has since violated them: *"Layer B took 14 new dependencies on Layer C since March, across 23 commits; here they are."*

Note the honest caveat: this is a **recommendation** engine, not a gate. Inferring intent from history is inferable, not provable, so output must be proposals with evidence, never findings.

### 6.5 Unanalysed-region accounting — *nearest neighbour: none*

Every static analyser silently skips `#if` regions, generated code, and excluded paths. The tool's own entropy measurement is therefore quietly incomplete, and it never says so. Nothing reports *"18% of your lines sit inside conditional-compilation regions; findings there are unmeasured"*; nothing finds branches that are dead because the condition is a compile-time constant; nothing finds feature flags that are now permanently on or off.

This is the cheapest item on the list and the best fit for Snipper specifically: `#if` regions are a **syntax-level** construct, so this needs no semantic model and fits the architecture that made SNP0031 cheap. It also raises trust — a tool that volunteers where it cannot see is more credible than one that presents partial coverage as total.

### 6.6 Removal-safety evidence (proof, not auto-fix) — *nearest neighbour: auto-fix everywhere; proof nowhere*

Auto-fix is broadly available: `dotnet format` and ReSharper CleanupCode, Qodana Quick-Fix with CLEANUP/APPLY strategies, SonarQube AI CodeFix (Claude Sonnet 4 / GPT-5.1 / Bedrock / self-hosted), CodeScene ACE. None of them emits **evidence that removal preserved behaviour**. Qodana's "CLEANUP" strategy is described as safe minor fixes; that is a claim, not an artifact.

Snipper can attack this without breaking its read-only tenet: a `--verify-removals` mode that applies Guaranteed-tier removals in a **scratch worktree**, recompiles, and emits a proof artifact — referenced-symbol set unchanged, public API surface unchanged, no new compiler warnings. It mutates nothing the user owns. Snipper already stamps `lineText` + `commitSha` on every finding specifically so consumers can verify a finding still matches before applying it, and an external agent previously applied 35/39 findings with zero breakage — that is the raw material for a real proof rather than a claim.

### 6.7 Also absent, lower priority

- **Cross-repo / org-level duplication and convention drift.** Every tool is per-repository. Large organisations cannot ask "did we implement this three times across services?" or "which of these four utilities is canonical?". Requires an org-scoped index — a service, which conflicts with Snipper's shape (§7).
- **Per-team entropy ledgers via CODEOWNERS.** SonarQube auto-assigns new issues to the introducing developer and CodeScene does team-coupling analysis; Qodana counts contributors via `.mailmap`. The missing piece is a deterministic, free, per-team burn-down ledger with an enforced budget. Organisationally high value; novelty is moderate, not high.
- **Test-suite strength on surviving code** (mutation testing, e.g. Stryker.NET). Nobody combines "is this code dead?" with "do the tests that keep it alive actually constrain it?". Combining them yields *dead code that cannot be deleted safely* and *live code held together by assertions that mean nothing* — both genuinely expensive entropy. Large scope; mutation testing is expensive by nature.
- **Semantic clone detection done properly.** jscpd has Type-4 experimentally; the 2026 literature is openly pessimistic ("current SOTA detectors remain vulnerable even to simple Type-2 and Type-3 transformations"). Not a differentiator to claim, and not a gap to close.

---

## 7. What the current shape rules out

Constrained to Snipper's zero-dependency, single-binary, read-only, per-repository shape, the following are **off the table** and are recorded as deliberate non-considerations rather than oversights:

- **MCP server / agent skills** (§4.5) — an MCP server is a long-running process or at minimum a second entry point; it is cheap in *code* but it changes the product from "one binary you run in CI" to "a service". **Recommended exception:** the `ai`/token-efficient reporter pattern (a compact flat clone list on stdout) delivers most of the value with none of the shape change, and is a ~1-day item.
- **Org-scoped cross-repo index** (§6.7) — requires shared state at organisation scope. Not compatible; do not attempt.
- **Hosting a time-series/dashboard** (§6.2, §6.7) — a rate needs somewhere to live. The mitigation is a **committed, diffable JSON state file** in the repository, which keeps the tool stateless and makes the ledger reviewable in PRs. That is a genuine design constraint, not an obstacle.
- **Any LLM dependency** — Sonar AI CodeFix and CodeScene ACE both take an external model. Snipper can stay model-free and emit the deterministic inputs (`lineText`, `commitSha`, clone sets, divergence evidence) that an agent needs to do the fixing. This is a *stronger* position than embedding a model, given the zero-dependency tenet, and it should be stated as a deliberate choice.

---

## 8. Candidate mapping

### 8.1 Committed — Wave 4 (see [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md))

Candidate IDs here are **ranked by value**; the roadmap's `4A/4B/4C` are **sequenced by implementation order** (cheapest first). The two schemes are deliberately in different orders, so the mapping is given rather than left implicit.

| # | Candidate | Roadmap | Gap | Certainty | Effort | Status | Notes |
|---|---|---|---|---|---|---|---|
| 14 | **Suppression / baseline integrity audit** (§6.3) | 4A | new | Guaranteed (it is a count of our own config) | S–M | **SHIPPED 1.7.0** | `--audit-suppressions`. Highest value-per-effort on this list. Two of five channels needed an opt-in shadow re-run — see the correction in §6.3. |
| 13 | **Entropy rate ledger** (§6.2) | 4B | new | — (metric, not a finding) | M | **SHIPPED 1.7.0** | Churn-normalized new-findings rate with an opt-in budget; committed JSON ledger; per-month aggregation. Introduces Snipper's first gate. The denominator is anchored to the baseline commit rather than to the pull request — measured, a per-PR denominator is dominated by commit size (400x spread). |
| 12 | **Clone drift / inconsistent-fix detection** (§6.1) | 4C | new | High (defensive-fix subset) / Advisory (possible drift) | L | **SHIPPED 1.7.0**; two defects fixed in 1.7.2 / 1.7.3 | Builds on SNP0031's index + git history — the missing half of that rule. Finds correctness defects, not smells. Needs *history* access (not just a commit SHA), which `GitMetadata` does not yet provide. **1.7.2:** a file-*creation* commit was being reported as a one-sided fix (90 of 302 findings on the monorepo) — a file's first appearance cannot leave a sibling untouched. **1.7.3:** SNP0031's clone index is keyed on a 32-bit hash and the 60-token window was never verified, so unrelated files could be reported as duplicates (232 findings removed). Both were found by a first-800 sweep of the monorepo, not by any test. |

### 8.2 Tracked, not committed

| # | Candidate | Gap | Certainty | Effort | Notes |
|---|---|---|---|---|---|
| 15 | **Unanalysed-region accounting** (§6.5) | new | Guaranteed | S | `#if`/feature-flag regions; constant-folded dead branches; permanently-on/off flags. Pure syntax — best architectural fit on the list. |
| 16 | **Clone kinds: Type-3 near-miss** (§2) | parity | Advisory | M | jscpd parity. Lower priority than 12 — Type-3 on normalized tokens re-introduces exactly the structural-skeleton noise that forced SNP0031 behind a flag. Measure before building. |
| 17 | **Finding attribution (`--blame` equivalent)** (§2) | parity | — | S | Author + last-touch date per finding. jscpd, Sonar and CodeScene all have it; Snipper does not. Prerequisite for per-team routing. |
| 18 | **Complexity + churn as prioritization only** (§4.2) | new | — (not findings) | M–L | Deliberately **not** new complexity rules (non-goal): import both signals purely to *rank existing findings*, which is what unblocks hotspot-flavoured work without violating the lint non-goal. |
| 19 | **Coverage evidence import** (carried) | Tier 3.10 | channel | M | `--coverage coverlet.xml`; adjusts certainty, never a standalone finding. Pairs with 6.7's dead-code-kept-alive-by-tests rule. |
| 20 | **Unused `.resx` keys / XAML-Razor evidence** (carried) | Tier 3.11 | Moderate | M–L | Generalise `AssemblyNameEvidenceScanner` to resource/view assets. |
| 21 | **Token-efficient `ai` reporter** (§7) | parity | — | S | jscpd `--reporters ai` parity; the cheap 80% of MCP. |
| 22 | **Removal-safety evidence (proof, not auto-fix)** (§6.6) | new | — (evidence, not a finding) | L | `--verify-removals` applies Guaranteed-tier removals in a scratch worktree, recompiles, and emits proof that the referenced-symbol set and public API surface are unchanged. Mutates nothing the user owns, so it respects the read-only tenet. Supersedes candidate 5 in spirit. |

### 8.3 Superseded / rejected

| # | Candidate | Status |
|---|---|---|
| 5 | `--fix` for Guaranteed rules | ❌ Rejected 2026-09-15 — read-only is a permanent tenet. Superseded in spirit by candidate **22** (removal-safety *evidence*, §6.6), which proves safety without mutating the user's tree. *(An earlier draft cited "candidate 16" here; 16 is Type-3 near-miss clone detection. Removal-safety had no candidate ID at all, and §6.6 is a section number, not one.)* |
| — | Outdated/vulnerable packages | Non-goal — NuGet Audit and Qodana own it. |
| — | Design-time IDE squiggles | Non-goal — ReSharper/Rider/SonarLint own it. |
| — | General lint/bug-risk rules | Non-goal — compiler warnings and NRE detection are Roslyn/ReSharper territory. |
| — | Auto-generated *complexity* rules | Non-goal — but see candidate 18, which imports complexity for ranking only. |
| — | Org-scoped index, hosted dashboard, LLM fix generation | Rejected by shape (§7). |
