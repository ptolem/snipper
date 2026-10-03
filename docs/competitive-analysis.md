# Competitive Analysis — Snipper vs. the Codebase-Cleaning Ecosystem

**Date:** 2026-09-14 · **Updated:** 2026-10-03 · **Snipper version:** 1.6.3 (27 rule IDs: SNP0001–0013, 0018–0031; 353 tests)
**Scope:** features for *reducing the entropy of a large codebase* — dead code detection, redundancy/hygiene sweeps, dependency bloat, duplication, and the machinery that actually controls entropy: gating, prioritization, architecture, history, suppression, and agent consumption.

The previous revision compared five tools that all perform *single-snapshot static* analysis of one repository. That was the wrong axis. Large-codebase entropy is not primarily a detection problem — it is a **governance** problem. A tool that finds 20,000 issues and cannot say which 200 matter, or cannot prove the next thousand commits will not add another twenty thousand, does not reduce entropy. This revision therefore splits the comparison in two (§2 detection, §3 governance) and adds the vendors that actually compete on the second axis.

**Sources & evidence levels:**
- *Verified against vendor docs (fetched 2026-10-03):* CodeScene 7.5.x documentation and product pages (hotspots, change coupling, bus-factor simulation, refactoring recommendations, Goals/X-Ray/Delta Analysis, ACE auto-refactor language support); Qodana 2026.2 `.NET` and feature docs (`qodana-dotnet` vs **`qodana-cdnet` Community license**, baseline, quality gate, fresh/total coverage, CLEANUP/APPLY strategies, vulnerability checker, `.mailmap`, pricing); SonarQube Server/Cloud 2026.x docs (MQR mode, Clean as You Code, new-code definition and trends, automatic per-author issue assignment, AI CodeFix, MCP server, Gitar); jscpd documentation and benchmarks (Type-1/2/3/4 clone kinds, C# in the semantic-embedding language list, `--skipLocal`, `--blame`, clone baseline, MCP server, 159 MB / 3.4 s claim); PMD CPD documentation (including its explicit statement that keeping clones in sync is beyond current tools); ArchUnitNET / NetArchTest.Rules repositories; ReSharper 2026.2 inspection index; InspectCode CLT docs.
- *Verified 2026-09-14 (prior revision):* ReSharper "Remove Unused References", Rider 2023.1 NuGet-aware reference removal, dupFinder CLT, dotCover, SARIF-as-default in InspectCode since 2024.1.
- *Stable product knowledge (not re-verified this revision):* NDepend (coverage import, CQLinq, complexity, dependency graph), CodeMaat / ADAM (churn × complexity hotspots, change coupling), Stryker.NET (mutation testing), PoliCheck, Semgrep cross-file analysis, CodeQL, dotCover, SonarQube CPD.
- *Qualitative:* community sentiment and vendor benchmarking claims are marked as such. Vendor speed and accuracy claims are **theirs, not reproduced here**.

---

## 0. TL;DR — what changed, stated plainly

Three findings from this revision are uncomfortable and are recorded here rather than softened.

**1. Snipper's duplication engine is now behind a free open-source tool, on both capability and speed.** jscpd ships Type-1, Type-2, **Type-3 near-miss**, and experimental **Type-4 semantic** clone detection (C# is in the embedding language list), plus `--skipLocal` (the same same-directory guard Snipper implemented for 1.6.3), `--blame`, SARIF, an MCP server, and a clone baseline with `--fail-on-new-clones` / `--baseline-from-ref`. Its native Rust engine advertises 159 MB scanned in 3.4 s. Snipper's SNP0031 is Type-1/Type-2 only and cost **52.6 s marginal on a 3,070-file monorepo — 3.5× over its own ≤15 s budget**. On the duplication axis specifically, jscpd is currently ahead of Snipper. SNP0031 remains worth keeping for the Roslyn-native reason (it shares the solution graph, `snipper.json`, baseline, and SARIF pipeline rather than being a second tool in the pipeline), but it should not be described as best-in-class.

**2. ReSharper's dead-code engine is now free in CI.** `qodana-cdnet` (ReSharper-based) runs under a **Community license** — no Ultimate subscription — with baseline, quality gate, coverlet import, and Quick-Fix. The prior revision's "ReSharper: CLT free (IDE paid)" framing understated the threat. `UnusedMember.Global` — the SWEA whole-program dead-code analysis Snipper spent 1.4.0–1.6.2 building toward — is now reachable in a CI pipeline at no licence cost. Snipper's remaining edge on this axis is *dependency-hygiene depth* (SNP0011/0012/0013) and certainty tiers, not dead-code coverage.

**3. Snipper has no governance story at all.** It has `--baseline`, which is a new-findings gate — genuinely useful, and parity with SonarQube's new-code gate is close. Beyond that it has no prioritization (no complexity, no churn), no architecture modelling, no history, no suppression accounting, and no agent surface. Every tool in §3 that competes on entropy *rate* rather than entropy *stock* does so with data Snipper does not collect.

**Where Snipper still leads** is unchanged and real: SNP0011/0012/0013 (orphan projects, redundant transitive packages, framework-inbox packages), SNP0018 (obsolete dead code), the read-only CI-first shape with no mandatory build, and the certainty-tier doctrine. §5.

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

## 3. Matrix B — entropy governance (the axis Snipper never competed on)

✓ = strong/native · ~ = partial · — = absent.

| Capability | Snipper | ReSharper / Qodana | SonarQube | CodeScene | NDepend | ArchUnitNET / jQAssistant | jscpd |
|---|---|---|---|---|---|---|---|
| **Prioritization** | | | | | | | |
| Complexity metrics | — | ✓ | ✓ | ✓ Code Health™ | ✓ | — | ~ |
| Churn / hotspot (change frequency) | — | — | ~ (SCM analysis) | ✓✓ core product | — | — | ~ (`--blame` dates) |
| Hotspot = churn × health, ranked | — | — | — | ✓✓ | ~ | — | — |
| Change coupling (files that change together) | — | — | — | ✓ | — | — | — |
| Bus-factor / offboarding risk | — | ~ (Qodana `.mailmap` counts contributors) | ~ | ✓ simulation | — | — | ~ (`--blame`) |
| **Gating / trend** | | | | | | | |
| New-findings gate | ✓ `--baseline` | ✓ baseline + fail-threshold | ✓✓ new-code quality gate | ✓ Delta Analysis / PR review | ✓ | ✓ (tests) | ✓ clone baseline |
| Per-author attribution of new issues | — | ~ | ✓ | ✓ | — | — | — |
| New-code trend over time | — (needs history) | ✓ Insights | ✓ "Tracking new code trends" | ✓ | ~ | — | ~ |
| **Entropy as a rate, normalized by churn, with a budget** | — | — | — | ~ Goals (degradation alerting, complexity-based) | — | — | — |
| **Suppression / baseline integrity** | | | | | | | |
| User false-positive / won't-fix workflow | — | — (baseline only) | ✓ | ~ | — | — | — |
| Audit suppressions themselves (count, age, rule mix, what they hide) | — | — | — | — | — | — | — |
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

**Matrix B verdict.** Snipper has exactly one cell in this matrix: the new-findings gate. CodeScene's *Goals* feature (mark a hotspot **Supervise** — "shouldn't grow worse" — and it fails the goal when the code degrades) is the single closest thing in the market to entropy governance, and it is commercial, complexity-metric-based, and closed. SonarQube's new-code gate plus automatic per-author assignment plus trend view is the most *complete* governance story available as a product, and it is a stock-and-author story, not a rate story. The cells marked "—" for Snipper are §6's opportunity list.

---

## 4. Where Snipper is behind (explicit gap list)

1. **Duplication: behind on clone kinds and badly behind on speed.** Type-3 absent; Type-4 experimental elsewhere. 52.6 s vs jscpd's advertised 3.4 s on a smaller corpus.
2. **Zero prioritization.** No complexity, no churn, no hotspots. Consequence: Snipper cannot answer "which 200 of these 2,839 findings matter?", and cannot rank its own output. This also blocks every hotspot-flavoured idea in §6.
3. **No suppression accounting.** Snipper *applies* suppressions at report time and therefore knows exactly which findings each one killed — and never says so. A user cannot audit how much of their "clean" status is suppression.
4. **No history.** Every run is a fresh full analysis (188 s on the 3,070-file monorepo). No delta mode, no rate, no trend.
5. **No agent surface.** jscpd and SonarQube both ship MCP servers; Snipper emits JSON/SARIF only. This is the cheapest gap on this list and the most strategically relevant — agent-mediated consumption is becoming the primary interface for this category.
6. **No architecture modelling.** Layering, cycles, and fitness functions are entirely absent (a deliberate non-goal to date, never explicitly argued).
7. **Attribution.** No `--blame` equivalent on findings.
8. **Free ReSharper in CI** (`qodana-cdnet`) means dead-code parity must be defended on certainty and dependency depth, not coverage.

---

## 5. Where Snipper leads

- **SNP0012** — redundant transitive packages via real lock-file graphs with version-range awareness. Nothing in the ecosystem does this; ReSharper/Rider/VS only flag *wholly unused* references, and now do so for free in CI.
- **SNP0013** — framework-inbox packages via `PackageOverrides.txt`. Unique.
- **SNP0011** — orphan + detached projects with plugin-loading string evidence. Unique; no tool sweeps for projects abandoned on disk.
- **SNP0018** — obsolete-member dead code. Unique.
- **Certainty tiers as a first-class axis.** Snipper's Guaranteed/High/Moderate/Advisory scale is *confidence the finding is correct*. SonarQube's MQR mode is *severity of impact on each software quality*; ReSharper's `.Global`/`.Local` split is *scope*. These are orthogonal axes and Sonar is the only peer that has made impact multi-dimensional — it has not made **confidence** a reported dimension. Pairs naturally with MQR into a 2-D severity × confidence matrix; nobody offers it.
- **CI-first shape** — read-only, no mandatory build (InspectCode builds by default), SARIF + JSON + baseline in a zero-dependency global tool. InspectCode only matched SARIF in 2024.1 and still has no baseline concept; Qodana adds one but needs Docker/native runner setup and a JetBrains account for anything beyond the free tier.
- **Compounding FP discipline.** Two FP-review rounds against a live 3,070-file monorepo with strip-compile proofs, and a record of *upholding* challenged true positives rather than reflexively demoting rules. Most competitors report recall; Snipper documents soundness.

---

## 6. Features no tool offers

Ranked by expected impact on a large codebase's entropy trajectory. For each, the nearest existing neighbour is named so the novelty claim is auditable — several ideas that look original are **not**, and are marked as such.

### 6.1 Clone drift / inconsistent-fix detection — *nearest neighbour: PMD CPD, which concedes it is unsolved*

PMD's CPD documentation says it plainly: *"failure to keep the code in sync may mean automated tools will no longer recognise these blocks as duplicates. This means the task of finding duplicates to keep them in sync when doing subsequent refactorings can no longer be entrusted to an automated tool… We thus advise developers to use CPD to help remove duplicates, not to help keep duplicates in sync."*

This is a vendor stating that the most valuable half of duplication management is unassisted. The mechanism is straightforward and reuses SNP0031's index: for each clone set, walk git history and identify **copies that received a change the others did not** — then rank by whether the divergence is a bug fix (one copy got the null guard, three did not) or benign drift. jscpd's `--blame` yields authors and dates but no divergence analysis. Nothing computes it.

Why this is first: it is the only item on this list that finds **correctness defects** rather than smells; it is the missing half of the feature Snipper just shipped; and it has unambiguous remediation (consolidate or delete the clone set). It also explains *why* duplication matters, which SNP0031 currently cannot.

Caveat that must be stated in any plan: "one copy changed" is a heuristic, not proof. High-confidence subset — a change that looks like a defensive fix (`null`/guard/try/catch/exception/bounds) applied to one copy of a clone set, where the other copies are byte-identical to the pre-change text — is defensible; anything looser is a report of *possible* drift and belongs in a lower tier.

### 6.2 Entropy as a rate, normalized by churn, with an enforced budget — *nearest neighbours: SonarQube new-code trends, CodeScene Goals; neither computes a churn-normalized rate*

Every tool ships entropy **stock** (how many findings) or a **delta** (new findings since a reference). None ships a **rate** — findings added per unit of change — which is the number a maintainer is actually asked for in a quarterly engineering review, and the only one that is comparable across teams of different sizes and cadences.

Design: persist a per-commit time series of (findings added, findings resolved, lines changed, commit SHA). Emit `new_findings / kLOC changed` per PR, per team, per month, with a configurable budget that fails CI. Snipper already stamps `commitSha` in its JSON report and already computes new-vs-baseline, so the increment is normalization plus a stored series. Cost: a small state file, no new analysis, no new semantic work.

This is deliberately the cheapest high-impact item on the list. It is also the only one that directly answers the maintainer's question — *are we actually controlling entropy, or just not looking at the stock?*

### 6.3 Suppression and baseline integrity audit — *nearest neighbour: none*

Every long-lived codebase accumulates `#pragma: disable`, `[SuppressMessage]`, `.editorconfig` severities, and baseline entries. This is entropy disguised as cleanliness, and no tool reports on it. A maintainer inheriting a repository needs: total suppressions and their age distribution; which rules dominate; **findings suppressed and nothing else**; suppressions that are now obsolete (the code they covered is gone); and the headline question — *what fraction of our "clean" status is suppression currently buying us?*

Snipper is unusually well-placed here: `snipper.json` is applied at report time, *after* baseline fingerprinting, so the analyser knows precisely which findings each exclusion removed and can attribute them. It is currently discarding that information. This is a governance feature rather than a detection feature, and it is the item most likely to be valuable to exactly the large-codebase maintainers this tool targets.

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

| # | Candidate | Gap | Certainty | Effort | Notes |
|---|---|---|---|---|---|
| 12 | **Clone drift / inconsistent-fix detection** (§6.1) | new | High (defensive-fix subset) / Advisory (possible drift) | L | Builds on SNP0031's index + git history. The missing half of the feature just shipped; finds correctness defects, not smells. Requires history access → first Snipper feature that reads git. |
| 13 | **Entropy rate ledger** (§6.2) | new | — (metric, not a finding) | M | Churn-normalized new-findings rate with an enforced budget; committed JSON state file; no new analysis. Answers "are we controlling entropy?" |
| 14 | **Suppression / baseline integrity audit** (§6.3) | new | Guaranteed (it is a count of our own config) | S–M | Snipper already knows which findings each exclusion killed and discards it. Highest value-per-effort on this list. |

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

### 8.3 Superseded / rejected

| # | Candidate | Status |
|---|---|---|
| 5 | `--fix` for Guaranteed rules | ❌ Rejected 2026-09-15 — read-only is a permanent tenet. Superseded in spirit by candidate 16 (removal-safety *evidence*, §6.6), which proves safety without mutating the user's tree. |
| — | Outdated/vulnerable packages | Non-goal — NuGet Audit and Qodana own it. |
| — | Design-time IDE squiggles | Non-goal — ReSharper/Rider/SonarLint own it. |
| — | General lint/bug-risk rules | Non-goal — compiler warnings and NRE detection are Roslyn/ReSharper territory. |
| — | Auto-generated *complexity* rules | Non-goal — but see candidate 18, which imports complexity for ranking only. |
| — | Org-scoped index, hosted dashboard, LLM fix generation | Rejected by shape (§7). |
