# Competitive Analysis — Snipper vs. the Codebase-Cleaning Ecosystem

**Date:** 2026-09-14 · **Updated:** 2026-10-16 (market re-check, pass 3) · **Snipper version:** 1.16.0 (30 rule IDs: SNP0001–0013, 0018–0034; 21 analysers; 755 tests)

> **Three revisions are recorded here. All three correct claims this document made.**
>
> **2026-10-16, pass 3: the market moved under two locked decisions.** Ten days after pass 2, a
> re-check against vendor pages found that two "deliberate non-goals" have become table stakes:
>
> - **Architecture conformance is now free for C#.** SonarQube Server 2026.4 ships architecture
>   management in Developer, Enterprise and Data Center at no extra cost. It covers C#, Java, JS,
>   TS and Python. Users define an intended architecture as an allow-list, and Sonar reports
>   deviations and tangles as issues.
> - **Every peer now has an agent surface except Snipper.** CodeScene has a single-user MCP with a
>   pre-commit Code Health safeguard. NDepend's MCP server is open source. jscpd's MCP now lives
>   in its Rust binary. RoslynLens is a 34-tool Roslyn MCP server with `find_dead_code`. repowise
>   is AGPL and exposes ten MCP tools.
>
> Two more moves matter to the target audience: maintainers who oversee several large monorepos.
>
> - **Org-level health dashboards went mainstream.** GitHub Code Quality reached GA on 2026-07-20.
> - **Free .NET dead-code tools multiplied.**
>
> The result is §0.4 (must-haves), new §6.8–§6.13 (features nobody ships) and candidates 23–34.
>
> Verification status for pass 3, fetched 2026-10-16:
>
> - **SonarQube Server 2026.4**: what's-new page and architecture docs.
> - **GitHub Code Quality GA**: changelog.
> - **NDepend 2026.1.6**: what's-new page.
> - **CodeScene single-user MCP**: help-centre article.
> - **jscpd 5.4.1**: Open Collective updates.
> - **repowise 0.39.0**: PyPI.
> - **RoslynLens 1.4.0, DeadCode 1.0.2, UnusedSymbolAnalyzer 1.0.4, DeadSharp 0.2.0**: NuGet.
> - **Teamscale**: architecture page.
>
> Claims carried from search results without a fetch are marked *(search result, not verified)*.
>
> **2026-10-06, pass 1 — the governance axis.** This document was last substantively researched
> against Snipper 1.7.0, which predates the entropy-governance wave. Three of the seven items in §6
> ("features no tool offers") have since **shipped** as 1.7.0: clone drift (SNP0032), entropy-as-a-rate
> with an enforced budget, and the suppression audit. §0 finding 3 — *"Snipper has no governance story
> at all"* — was true when written and is **false today**. Matrix B and §4 were corrected in place.
>
> **2026-10-06, pass 2 — competitor research re-verified against live vendor sources.** Every
> load-bearing competitor claim was re-fetched. Seven claims were **wrong or materially incomplete**,
> and are corrected where they appear rather than in a changelog: `qodana-cdnet` does **not** have
> Quick-Fix and is flagged **Early Access**; SonarQube's AI CodeFix is **Enterprise/Data Center only**
> and its LLM options are Azure OpenAI / Bedrock / self-hosted, not the model names previously listed;
> SonarQube Server is at **2026.3**; jscpd has grown well past duplication detection (§0.1); and — the
> two that matter most — **§6.1's "nothing computes this" and §0.1's "no peer ships divergence
> detection" are both false.** See §6.1 for the correction and the narrower claim that replaces it.
>
> Verification status: **jscpd v5.3.0**, **Qodana 2026.2**, **SonarQube Server 2026.3** (2026.1 LTA),
> **CodeScene 7.5.12**, **PMD 7.14.x**, **ReSharper 2026.2**, all fetched 2026-10-06. Vendor speed and
> accuracy claims remain **theirs, not reproduced here**.
**Scope:** features for *reducing the entropy of a large codebase* — dead code detection, redundancy/hygiene sweeps, dependency bloat, duplication, and the machinery that actually controls entropy: gating, prioritization, architecture, history, suppression, and agent consumption.

The previous revision compared five tools that all perform *single-snapshot static* analysis of one repository. That was the wrong axis. Large-codebase entropy is not primarily a detection problem — it is a **governance** problem. A tool that finds 20,000 issues and cannot say which 200 matter, or cannot prove the next thousand commits will not add another twenty thousand, does not reduce entropy. This revision therefore splits the comparison in two (§2 detection, §3 governance) and adds the vendors that actually compete on the second axis.

**Sources & evidence levels:**

**Pass 3, fetched 2026-10-16. Each claim was checked against a vendor page.**

**SonarQube Server 2026.4.**
- Architecture management:
  - Editions: Developer, Enterprise and Data Center. It is not in Community Build.
  - Languages: C#, Java, JS, TS and Python.
  - The intended architecture is modelled in a UI editor. Any relationship not defined there is
    forbidden.
  - Deviations are raised as issues of two types: wrong dependency and wrong location.
  - Cycles ("tangles") are detected automatically.
  - The docs show no file-based way to define the model.
- The "Sonar way for Agentic AI" gate has supply-chain conditions for typosquatted or
  hallucinated packages.
- A quality-gate adherence dashboard flags "risky releases" that shipped despite a failing gate.
- Incremental taint analysis now covers C#: "up to 90% faster".
- The release page does not mention monorepos or portfolios.

**GitHub Code Quality GA (2026-07-20).**
- It is on Enterprise Cloud and Team, not on GHES.
- It costs $10 per active committer per month, plus usage-based AI and Actions compute.
- Org-level dashboards show maintainability and reliability across repositories, with org-wide
  enablement.
- Gates are enforced through rulesets with an evaluate mode.
- Cobertura coverage is shown on PRs.
- Enablement and findings have APIs.

**NDepend 2026.1.x.**
- The latest is 2026.1.6.
- The NDepend MCP Server is open source.
- A Maintainability Index (0–100) is computed at method, type, namespace and project level.
- New architecture rules: ND1412 "Enforcing Clean Architecture" and ND1410.

**CodeScene single-user MCP.**
- Tools: `code_health_score`, `code_health_review`, `pre_commit_code_health_safeguard`,
  `analyze_change_set` and `code_health_refactoring_business_case`.
- Enforcement is a workflow, not an automatic block.
- Hotspots, goals and ownership need the project-level licence.

**jscpd 5.4.1 (2026-10-09).**
- v5 is a Rust engine. The vendor reports a 24–37× speedup.
- The MCP server has been built into the binary since 5.0.16.
- `--health` arrived in 5.3.0, `--lsp` in 5.4.0, and `--similarity` plus **`--changed`** (only
  files you changed) in 5.4.1.

**repowise 0.39.0 (2026-08-05, AGPL-3.0).**
- 1–10 health score from 25 deterministic markers.
- Dead code reported by confidence tier, with cleanup-impact estimates.
- Ranked refactoring targets.
- Ten MCP tools.
- **Workspaces index several repos as one unit**, with cross-repo consumers and co-change.

**.NET dead-code tools on NuGet.**
- **RoslynLens 1.4.0** (2026-07-04): a stdio MCP server with 34 tools, including `find_dead_code`.
- **DeadCode 1.0.2** (2026-03-27): static analysis plus dotnet-trace, "categorizes methods by
  removal safety", and JSON output for LLM cleanup.
- **UnusedSymbolAnalyzer 1.0.4** (2026-07-10): a .NET 10 global tool that finds unreferenced
  public symbols.
- **DeadSharp 0.2.0** (2025-05-26): older than this pass assumed, and not new.

**Teamscale.**
- Boxes-and-arrows architecture with allowed, tolerated and denied policies. Everything else is
  denied.
- The page does not confirm C# support.

**Search results, not fetched:**
- SonarQube AI Code Assurance autodetection is reportedly unreliable. One unofficial post says it
  is deprecated.
- Teamscale test-gap analysis (changes × coverage × tickets).
- CodeScene's "AI readiness" framing.
- Engineering-intelligence platforms (Cortex, Exceeds, Connectory) that blend DORA metrics with
  quality.
- An arXiv study of 302,579 AI-authored commits: 17–29% introduce an issue, and 22.7% of those
  issues are never fixed.

- ***Re-verified against vendor docs, fetched 2026-10-06** (this pass; every load-bearing claim below was re-fetched and three claims were found wrong — see §0):* **jscpd v5.3.0** README and benchmark (Type-1/2/3/4 clone kinds; C# in the semantic-embedding list; `--skip-local`, `--skip-isolated`, `--blame`, clone baseline with `--fail-on-new-clones` / `--baseline-from-ref`; **new in this pass:** `--dead-code`, `--complexity`, `--summary`, `--health`, `--history`, `--dashboard`, `--lsp`, five agent skills, MCP server; 84 ms on a 547-file corpus; 10M+ npm downloads/month; bundled by GitHub Super Linter and MegaLinter, **powers Codacy's duplication engine**). **Qodana 2026.2 Help** — `.NET` page (2026-08-06) and Licenses page (2026-09-18): `qodana-dotnet` vs **`qodana-cdnet` Community licence**, the feature matrix (**Quick-Fix, Insights, License audit and SSO are *not* on cdnet**), cdnet's **Early Access** status, supported languages, Docker-only shipping, token requirements. **SonarQube Server 2026.3** docs (2026.1 LTA): AI CodeFix (**Enterprise/Data Center only**; providers are Sonar-hosted, **Azure OpenAI, AWS Bedrock, or a self-hosted gateway**), **MCP server** (all commercial editions), Gitar, the Agent Centric Development Cycle, MQR mode, new-code definition and trends, per-author assignment. **PMD 7.14.x CPD docs** — the "not to help keep duplicates in sync" passage **re-confirmed verbatim**. **CodeScene 7.5.12** docs: hotspots, change coupling, **X-Ray proximity analysis (copy-paste × change coupling)**, Goals (Planned Refactoring / Supervise / No Problem / Critical), Delta Analysis and its **absent-change-pattern** warning, off-boarding simulation, ACE, Complexity Trends, Parallel Development. **ReSharper / InspectCode**: `UnusedMember.Global` **confirmed** as a real solution-wide inspection id reachable from the CLI via `--swea`.
- *Verified 2026-10-03 (prior pass, superseded where re-verified above):* ReSharper 2026.2 inspection index; ArchUnitNET / NetArchTest.Rules; MQR mode and Clean as You Code; jscpd's then-current 159 MB / 3.4 s claim (**now withdrawn — no longer in the current README**).
- *Verified 2026-09-14 (first revision):* ReSharper "Remove Unused References", Rider 2023.1 NuGet-aware reference removal, dupFinder CLT, dotCover, SARIF-as-default in InspectCode since 2024.1.
- ***New in this pass — the novelty check that §6.1 failed:*** Krinke & Ragkhitwetsagul, *"A Study of Consistent and Inconsistent Changes to Code Clones"* (WCRE 2007); a 2024 arXiv study of git commit logs for clone inconsistency; `phpcpd-next` (inconsistent-clone detection, PHP); `dupehound` (June 2026, history + CI gate for agent-written duplication).
- *Stable product knowledge (**not** re-verified this revision):* NDepend (coverage import, CQLinq, complexity, dependency graph), CodeMaat / ADAM (churn × complexity hotspots, change coupling), Stryker.NET (mutation testing), PoliCheck, Semgrep cross-file analysis, CodeQL, dotCover.
- *Qualitative:* community sentiment and vendor benchmarking claims are marked as such. Vendor speed and accuracy claims are **theirs, not reproduced here**.

---

## 0. TL;DR — what changed, stated plainly

Four findings. Two were uncomfortable when written and are kept rather than softened. The third is
now **wrong**, and is corrected here rather than quietly deleted. The fourth (2026-10-16) is the
reason this revision exists.

**1. Snipper's duplication engine is behind a free open-source tool on clone *kinds* — and jscpd has since grown into a code-health platform.** jscpd ships Type-1, Type-2, **Type-3 near-miss**, and experimental **Type-4 semantic** clone detection (C# is in the embedding language list), plus `--skip-local` (the same same-directory guard Snipper implemented for 1.6.3), `--blame`, SARIF, an MCP server, and a clone baseline with `--fail-on-new-clones` / `--baseline-from-ref`. Snipper's SNP0031 is Type-1/Type-2 only. On the duplication axis specifically, jscpd is ahead on clone kinds, and SNP0031 remains worth keeping for the Roslyn-native reason (it shares the solution graph, `snipper.json`, baseline, and SARIF pipeline rather than being a second tool in the pipeline). It should not be described as best-in-class.

  **Three corrections since first written.** *(a) The speed claim is withdrawn.* This revision reported SNP0031 at "52.6 s marginal on a 3,070-file monorepo — 3.5× over its own ≤15 s budget." Re-measuring on the same kind of repo (81 projects, ~3,000 files) with 1.7.3 did not reproduce it: a full run took 125–255 s and **the same configuration varied 138 s → 240 s on consecutive runs**, with the marginal cost of the flag sitting inside that noise. The honest figure, measured where it can be measured, is **+0.7 s** on a 53-commit repo (11.4 s → 12.1 s, 5-run average). The earlier number was real when taken but was never a controlled measurement. *(b) The "159 MB in 3.4 s" benchmark is gone.* jscpd's published benchmark is now a 547-file / 150-format corpus at **84 ms**, with PMD CPD at 35.980 s on the same corpus. The old figure is not in the current README. *(c) jscpd is no longer "a duplication detector with extras" — it is a code-health platform, and it is embedded in commercial products.* Verified 2026-10-06 at v5.3.0 (6.3k stars, ~5,000 dependents, **10M+ npm downloads/month**): beyond clones it now ships `--dead-code` (unused files/exports/declarations/imports with cascading reachability and per-finding confidence — but **JavaScript, TypeScript and Python only, no C#**), `--complexity` ranking, `--summary` refactoring hotspots, `--health` (a size-aware 0-100 score with a grade, **calibrated on 42 open-source projects**, extensible via `--health-input`), `--history` (**duplication trend over git history**, with a sparkline, per-commit deltas, and "how far `--threshold` could be tightened"), `--dashboard`, `--skip-isolated` (ignore duplication between monorepo folders owned by different teams), an LSP server, **five installable agent skills**, and the MCP server. It is bundled by **GitHub Super Linter** (runs by default), **MegaLinter**, and — commercially — **it powers Codacy's duplication engine**.

  The strategic consequence is not "jscpd is faster". It is that *duplication detection is now a commodity input to somebody else's product*, so the duplication axis is a place Snipper should expect to be a second tool in a pipeline rather than the pipeline. What is **not** commodity is the half nobody ships — see the correction in §6.1, which invalidates the earlier version of this document's own claim.

**2. ReSharper's dead-code engine is now reachable in CI without an Ultimate subscription — but the free linter is Early Access and thinner than previously recorded here.** `qodana-cdnet` ("Qodana Community for .NET") is ReSharper-based and documented as "Available under licenses: **Community license**"; `UnusedMember.Global` — the SWEA whole-program dead-code analysis Snipper spent 1.4.0–1.6.2 building toward — is real and reachable, with baseline, quality gate and code coverage. The prior revision's "ReSharper: CLT free (IDE paid)" framing understated the threat, so that part stands.

  **Corrected 2026-10-06 against JetBrains' own docs** (Qodana 2026.2 Help, .NET page dated 2026-08-06, Licenses page 2026-09-18):

  | Claim previously made | Verified position |
  | --- | --- |
  | cdnet has "baseline, quality gate, coverlet import, and Quick-Fix" | Baseline ✔, quality gate ✔, code coverage ✔ — **Quick-Fix ✘**, along with Insights, License audit and SSO. The Quick-Fix claim was wrong; that is Qodana *paid*, and the `--apply-fixes` / CLEANUP-APPLY story is not available on the free .NET linter. |
  | implied parity with paid Qodana for .NET | **`qodana-cdnet` is "currently in Early Access, which means that it may not be reliable, may not work as intended, and may contain errors."** A material qualifier on a threat assessment. |
  | — | cdnet supports **C#, C/C++, VB.NET** only. The Rider-based `qodana-dotnet` additionally covers JavaScript, TypeScript and F#, but is **Ultimate/Ultimate Plus only**. |
  | — | cdnet ships as a **Docker image** (no native solution); `qodana-dotnet` ships both. A project token is **required** for `qodana-dotnet` and **optional** for `qodana-cdnet`. |

  Net: the threat is real but was overstated here in two specific ways. Snipper's dead-code edge must be defended on *certainty tiers* and *dependency-hygiene depth* (SNP0011/0012/0013), not on coverage — and it should not assume the free linter's inspection portfolio is stable, because JetBrains says in terms that it may not be.

**3. ~~Snipper has no governance story at all.~~ Corrected 2026-10-06: this was true of 1.7.0-onward at the time of writing, and Wave 4 then built the missing story.** The original text read: *"It has `--baseline`, which is a new-findings gate — genuinely useful, and parity with SonarQube's new-code gate is close. Beyond that it has no prioritization (no complexity, no churn), no architecture modelling, no history, no suppression accounting, and no agent surface."*

  As of 1.7.3, three of the six gaps in that sentence are closed:

  | Claimed gap | Status |
  | --- | --- |
  | "no history" | **Closed.** `--clone-drift` reads history in two batched git calls; `--entropy-rate` anchors a churn-normalized rate to the commit recorded in the baseline; `--entropy-ledger` keeps a committed per-month series. |
  | "no suppression accounting" | **Closed.** `--audit-suppressions` reports what each of the five suppression channels hides, with stale-detection at two confidence levels. No peer in §3 does this. |
  | "no prioritization" | **Partly closed, and this is the honest part.** Entropy *rate* normalizes by churn and can gate a build (`--entropy-budget`, exit `3`), but nothing **ranks** findings. There is no complexity, no hotspot ranking, no "which 200 of these 6,626 matter". Prioritization-as-a-gate is not prioritization-as-a-list. |

  Still genuinely absent: architecture modelling, an agent surface (no MCP server), per-author attribution, and a finding-ranking axis. The specific sentence *"Every tool in §3 that competes on entropy rate rather than entropy stock does so with data Snipper does not collect"* no longer holds — Snipper now collects it — but its replacement is narrower than the original claim implied: it collects **rate**, not **priority**.

**4. Two locked decisions are now competitive liabilities, and the audience that matters most has
moved. Added 2026-10-16.** Locked decision 2a in the roadmap rules out an MCP server and an
org-scoped index. Architecture has never been argued at all (§4.6). Ten days after pass 2 the market
looked like this:

- **SonarQube gives intended-architecture conformance to every paying C# customer.**
- **Every peer has an MCP surface:** CodeScene, NDepend, Sonar, jscpd, repowise and RoslynLens.
- **GitHub sells org-wide health dashboards and gates** at $10 per committer.

A principal engineer who oversees several large monorepos asks three questions:

1. *Which repo is worst, and is it getting worse?*
2. *Which 20 actions would most improve it, and whose are they?*
3. *Can my agents query it?*

Snipper answers none of them today. Its findings are the best evidence available for .NET, but it
does not rank them, does not route them, and does not roll them up.

**Must-haves for that audience, ranked.** Each stays inside the single-binary, read-only shape,
except where M1 is marked:

| # | Must-have | Who already has it | Shape fit |
| --- | --- | --- | --- |
| **M1** | **Agent surface: `snipper mcp`**, a stdio mode of the same binary, with tools such as `findings`, `explain`, `rank`, `ledger` and `verify_still_present` | CodeScene, NDepend, Sonar, jscpd, repowise, RoslynLens | Needs 2a amended. jscpd shows that a stdio MCP is a *mode*, not a service (§7). |
| **M2** | **Architecture rules over the project graph and namespaces**: an allow-list `architecture` block in `snipper.json`, plus cycle and layering findings | SonarQube 2026.4 (free for C# in Developer), NDepend ND1412, Teamscale, ArchUnitNET | ✓. Snipper already owns the project and package graph (SNP0004/0011/0012). A file in the repo is *better* than Sonar's UI editor, because it can be reviewed in a PR. |
| **M3** | **Finding ranking**: churn × complexity × certainty × fan-in, with `--rank` and a top-N actionable list | CodeScene (hotspots), jscpd `--summary`/`--health`, repowise refactoring targets, GitHub Code Quality scores | ✓. All inputs are local; churn is already read for `--entropy-rate`. |
| **M4** | **Ownership and attribution**: CODEOWNERS plus `git blame` on every finding, with per-team rollups and budgets | Sonar (assigns to author), CodeScene, jscpd `--blame` | ✓ |
| **M5** | **Multi-repo rollup without a server**: `snipper portfolio` merges N committed reports and ledgers into one comparable scorecard with trends | GitHub Code Quality org dashboards, Sonar portfolios (Enterprise), repowise workspaces | ✓. It reads committed artifacts and has no shared state (§7). |
| **M6** | **Change-scoped mode**: `--changed-since <ref>` reports only findings in changed files, plus the ledger delta | jscpd 5.4.1 `--changed`, Sonar new-code, CodeScene `analyze_change_set` | ✓. It is a filter over a full run, not incremental analysis. |

Parity items that are **not** must-haves: Type-3 clones (candidate 16) and coverage import
(candidate 19). Auto-fix and vulnerability scanning stay non-goals, and that position still holds.

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
| Cyclomatic / cognitive complexity, nesting | ✓ SNP0033 + **SNP0034** (1.14.0: nesting-weighted, SonarSource spec; both report, neither suppresses the other) | ✓ | ~ | ✓ | ✓ | ~ (`--complexity` ranking) |
| **Workflow** | | | | | | |
| Auto-fix / bulk cleanup | — (read-only tenet) | ✓ CleanupCode; Qodana CLEANUP/APPLY; Quick-Fix | ✓ Code Cleanup + `dotnet format` | — | ~ (AI CodeFix) | — |
| Safe-delete with usage preview | — | ~ | ~ | — | — | — |
| Severity/suppression config | ✓ `snipper.json` | ✓ `.editorconfig` + `.DotSettings` + `qodana.yaml` | ✓ `.editorconfig` | ✓ | ✓ quality profiles | ✓ `.jscpd.json` |
| CI baseline / new-code gate | ✓ `--baseline` | ✓ Qodana baseline + `--fail-threshold` | — | ✓ | ✓✓ (industry reference) | ✓ clone baseline, `--fail-on-new-clones` |
| Baseline without a committed file (`--baseline-from-ref`) | — (generate from a checkout) | ~ | — | — | ✓ reference branch | ✓ |
| SARIF output | ✓ | ✓ (default since 2024.1) | ~ | — | ✓ | ✓ |
| Design-time squiggles | — | ✓✓ | ✓✓ | ✓ | ✓ (SonarLint) | — |
| Git blame / authorship attribution | — | ~ | ~ | — | ✓ (auto-assigns new issues to author) | ✓ `--blame` |
| Agent / MCP surface | — **(the only column without one, 2026-10-16)** | ~ (VS Code, Qodana Cloud API) | ~ (Copilot); **RoslynLens** (third-party stdio MCP, 34 tools incl. `find_dead_code`) | **✓ open-source MCP server** (2026.1, verified 2026-10-16; was `—`) | ✓ **MCP server**, Gitar, AC/DC | ✓ **MCP server in the Rust binary** (stdio), agent skills, `ai` reporter |
| Change-scoped run (only changed files) | — (full run; `--baseline` gates new findings) | ~ | — | ~ | ✓ new code | ✓ **`--changed`** (5.4.1) |
| **Architecture** (added 2026-10-16) | | | | | | |
| Intended architecture, allow-list, deviations as issues | — | ~ | — | ✓ CQLinq, **ND1412 Clean Architecture** (2026.1) | **✓ 2026.4, Developer+ (C#/Java/JS/TS/Py)** | — |
| Dependency cycles / tangles | — | ~ (Rider diagrams) | — | ✓ | ✓ tangles, auto-detected | — |
| **Fleet** (added 2026-10-16) | | | | | | |
| Multi-repo / org rollup | — | ~ (Qodana Cloud) | — | — | ✓ portfolios (Enterprise) | — |
| Free & open-source | ✓ | **✓ via `qodana-cdnet` Community** | ✓ | paid | community tier | ✓ |

**Matrix A verdict.** Detection parity on Snipper's home turf is genuinely good: competitive on dead code, ahead on redundancy, unique on dependency hygiene. Two concrete parity gaps remain: **clone kinds** (Type-3 absent; Type-4 experimental in jscpd) and **attribution** (`--blame`-style authorship, cheap). Both are in §8.2.

**Updated 2026-10-16.** Pass 3 added three row groups, and Snipper is empty in all of them:
- **Agent surface.** Snipper is now the only column without an MCP.
- **Architecture.** Sonar ships it free for C#.
- **Fleet rollup.**

None of these is a detection gap. They are the reasons a lead who already trusts Snipper's findings
still cannot run an estate on it. See §0.4.

---

## 3. Matrix B — entropy governance (updated 2026-10-16 for 1.16.0)

✓ = strong/native · ~ = partial · — = absent. **Snipper's column is current to 1.16.0.** Cells
marked 2026-10-16 were re-checked in pass 3, and the GitHub Code Quality column was added then.
Other columns are as researched on 2026-10-03 and 2026-10-06.

| Capability | Snipper | ReSharper / Qodana | SonarQube | CodeScene | NDepend | ArchUnitNET / jQAssistant | jscpd | GitHub Code Quality |
|---|---|---|---|---|---|---|---| --- |
| **Prioritization** | | | | | | | |  |
| Complexity metrics | ~ SNP0033/0034 report over-threshold methods (1.13.0/1.14.0). They are findings, **not a ranking input** | ✓ | ✓ | ✓ Code Health™ | ✓ | — | **✓ `--complexity` (verified 2026-10-06; was `~`)** | ~ maintainability score per repo |
| Churn / hotspot (change frequency) | ~ (churn only as an entropy **denominator**; no per-file ranking) | — | ~ (SCM analysis) | ✓✓ core product | — | — | ~ (`--blame` dates, `--history` trend) | — |
| Hotspot = churn × health, ranked | — | — | — | ✓✓ | ~ | — | **~ `--summary` + `--health`** (separate passes; size-aware score calibrated on 42 OSS projects) | — |
| Change coupling (files that change together) | — | — | — | ✓✓ (+ absent-change-pattern detection) | — | — | — | — |
| Bus-factor / offboarding risk | — | ~ (Qodana `.mailmap` counts contributors) | ~ | ✓ off-boarding simulation | — | — | ~ (`--blame`) | — |
| **Gating / trend** | | | | | | | |  |
| New-findings gate | ✓ `--baseline` | ✓ baseline + fail-threshold | ✓✓ new-code quality gate | ✓ Delta Analysis / PR review | ✓ | ✓ (tests) | ✓ clone baseline (`--fail-on-new-clones`, `--baseline-from-ref`) | ✓ rulesets, evaluate mode |
| Per-author attribution of new issues | — | ~ | ✓ | ✓ | — | — | — | — |
| New-code trend over time | ✓ `--entropy-ledger` monthly aggregates | ✓ Insights *(Ultimate Plus)* | ✓ "Tracking new code trends" | ✓ Complexity Trends / Delivery Effectiveness | ~ | — | ~ `--history` (duplication only, not findings) | ~ org dashboard |
| **Entropy as a rate, normalized by churn, with a budget** | **✓✓ `--entropy-rate` + `--entropy-budget` (exit 3) + `--entropy-ledger`** | — | — | ~ Goals (degradation alerting, complexity-based) | — | — | — (closest: `--history` + `--threshold` tightening advice, but duplication-only and not a gate) | — |
| **Suppression / baseline integrity** | | | | | | | |  |
| User false-positive / won't-fix workflow | — | — (baseline only) | ✓ won't-fix | ✓ **Goals: No Problem** (suppresses, but keeps analysing in background) | — | — | — | ~ *(dismissal; not verified)* |
| Audit suppressions themselves (count, age, rule mix, what they hide) | **✓ `--audit-suppressions`** (incl. stale detection at two confidence levels) | — | — | ~ (Goal failures surface as gate breaks, not as an audit) | — | — | — | — |
| **Duplication governance** | | | | | | | |  |
| One-sided fix in a clone set (drift / divergence) | **✓✓ SNP0032 `--clone-drift`** (High when fix-shaped; names the commit) | — | — | **~ X-Ray proximity analysis: combines copy-paste detection *with* change coupling to say which clones matter** — closest peer; ranks, does not report the event | — | — | ~ (inconsistent-clone detection exists in `phpcpd-next`, PHP only) | — |
| **Architecture** | | | | | | | |  |
| Dependency-direction / layering rules | — | ~ (inspections + Rider diagrams) | **✓ 2026.4 architecture management: deviations as issues, tangles auto-detected, C#** (2026-10-16; was `~`) | ✓ change-coupling map | ✓ dependency graph; ND1412 Clean Architecture (2026.1) | ✓✓ **purpose-built** | — | — |
| Rules hand-authored by the maintainer | — | — | **✓ intended-architecture editor (UI, allow-list, undefined = forbidden; no file format)** (2026-10-16) | — | ~ | ✓✓ | — | — |
| Architecture **inferred** from history, then erosion reported | — | — | ~ (shows the *current* architecture; intended is hand-modelled) | ~ Architectural Analyses (adjacent) | — | — | — | — |
| **Dead-code-specific governance** | | | | | | | |  |
| Dead code vs live coverage cross-check | — (tracked) | ✓ (cdnet code coverage ✔) | ~ | ✓ coverage in PRs | ✓ | — | ~ (`--dead-code`, **JS/TS/Python only**) | ~ Cobertura on PRs (coverage only, no dead-code link) |
| **Structural self-consistency** | | | | | | | |  |
| Analysed-region accounting (what the tool *cannot* see: `#if`, generated, excluded) | — | — | ~ (exclusions visible in UI) | — | — | — | — | — |
| **Agent consumption** | | | | | | | |  |
| MCP server / agent skills / token-efficient report | — (token-efficient JSON reporter only) | ~ | **✓ MCP server** — *all commercial editions; Developer/Enterprise/Data Center* (verified 2026-10-06) | ✓ **single-user MCP** (`code_health_score`, `pre_commit_code_health_safeguard`, `analyze_change_set`) + ACE (2026-10-16) | **✓ open-source MCP server** (2026-10-16; was `—`) | — | ✓✓ MCP (in the Rust binary since 5.0.16) + **five agent skills** + `--ai` reporter (~79% fewer tokens) | ~ Copilot Autofix + APIs |
| **Fleet / estate (added 2026-10-16)** | | | | | | | | |
| Org / multi-repo rollup | — (strictly per-repo) | ~ (Qodana Cloud) | ✓ portfolios *(Enterprise)* | ✓ (hosted, multi-project) | — | — | ~ `--skip-isolated` (monorepo folders, not repos) | **✓ org dashboards, org-wide enablement** |
| Ownership routing (CODEOWNERS / team) | — | — | ~ (per-author) | ✓ team analyses | — | — | ~ `--blame` | ~ *(not verified)* |
| Change-scoped / PR analysis | ~ `--baseline` (new-vs-baseline over a full run) | ✓ | ✓ new-code + incremental C# taint (2026.4) | ✓ `analyze_change_set` | ~ | — | ✓ `--changed` (5.4.1) | ✓ PR analysis |
| AI/agent-authored code governance | — | — | ✓ "Sonar way for Agentic AI" gate (supply-chain conditions); AI Code Assurance *(autodetect reportedly unreliable; search result, not verified)* | ~ pre-commit safeguard for agent edits | — | — | — | ~ Copilot Autofix (fixes, does not govern) |
| Gate adherence over time | ✓ `--entropy-ledger` (rate per month) | — | ✓ quality-gate adherence dashboard, "risky releases" (2026.4) | ~ | — | — | — | — |

**Matrix B verdict, revised 2026-10-06 for 1.7.3.** Snipper no longer has "exactly one cell" in this matrix. It now has **five**, and the strongest of them — the churn-normalized **entropy rate with an enforced budget** — has **no cell filled by any peer**. That claim survived re-verification and is the most defensible line in this document: CodeScene's *Goals* is the closest commercial analogue and is complexity-based and closed; jscpd's new `--history` reads git history but is duplication-only and is not a gate; nothing normalizes findings-per-change and can fail a build on it.

Two cells moved against Snipper in this pass and are marked as such rather than quietly re-graded:

- **jscpd now has complexity metrics** (`--complexity`, plus a size-aware `--health` score calibrated on 42 open-source projects). Snipper's "no prioritization" gap is therefore *narrower* than previously written: a free tool now ranks files by complexity and by a health score. Snipper still cannot rank **findings**, which is the thing that matters for a 6,626-finding report, but the honest statement is no longer "no peer computes complexity".
- **CodeScene's X-Ray proximity analysis is the closest peer to SNP0032** and was under-credited. It combines copy-paste detection *with* change coupling to decide which clones matter, and Delta Analysis separately detects **broken temporal change patterns** — warning that a previously-coupled cluster stopped changing together, which "may also be a sign of omission and a potential bug", on a self-correcting basis. That is not the one-sided-fix event Snipper reports, but it is the same intuition applied to coupling rather than to clone text, and this document previously rated CodeScene `~ (duplication, adjacent)`.

The cells still marked "—" for Snipper are §6's remaining opportunity list. The largest is still prioritization-as-a-ranking, followed by architecture and an agent surface.

**Revised 2026-10-16, pass 3.** Several cells moved against Snipper:

- **SonarQube's architecture cells went from `~` to `✓`.**
- **NDepend's and CodeScene's agent cells became `✓`.**
- **A new fleet group shows Snipper empty on every row except gate adherence.**

The entropy-rate cell is still unfilled by any peer. Sonar's new gate-adherence dashboard is the
nearest neighbour yet: it shows how often releases ship despite a failing gate. That is a pass/fail
count per release, not a churn-normalized findings rate. The priority order also changed. For an
estate owner it is now:

1. Ranking plus ownership (M3 + M4), because without them a rollup is just a bigger stock number.
2. The agent surface (M1).
3. Architecture (M2).
4. The rollup itself (M5).

---

## 4. Where Snipper is behind (explicit gap list)

*Re-ranked 2026-10-16 for an estate owner: 2 (ranking), 5 (agent surface), 6 (architecture), 11
(above-repo view) and 7 (attribution) are the must-haves of §0.4. Numbers are kept stable so
existing references still resolve.* *Items 3 and 4 were closed by Wave 4 and are marked rather than deleted. Re-measured numbers are from
an 81-project / ~3,000-file / 3,896-commit monorepo at 1.7.3.*

1. **Duplication: behind on clone kinds; the novelty claim needed correcting.** Type-3 near-miss absent, Type-4 experimental elsewhere. No longer a *speed* gap on the evidence available — the old 52.6 s marginal figure did not reproduce (§0.1) and the honest measured marginal is +0.7 s on a small repo. The remaining real gaps are clone *kinds*, no `--blame` equivalent, and — newly — that jscpd's duplication engine now **powers Codacy**, so duplication is a commodity input to someone else's product.
2. **No prioritization — but narrower than previously written.** Snipper has no complexity, no hotspot ranking, no change coupling, and cannot rank its own output. Churn is collected, but only as the **denominator** of an entropy rate, never as a ranking input. Re-verification correction: jscpd now ships `--complexity` and a size-aware `--health` score, so "zero prioritization" is no longer a market-wide absence — it is *Snipper's* absence, against a field where a free tool ranks files. Consequence for us is unchanged and is now sharper: on a 6,626-finding report, the question a maintainer asks is "which 200 of these matter?", and nothing we ship answers it.

   **2026-10-16 update.**
   - **The complexity half now exists.** SNP0033/0034 shipped in 1.13.0/1.14.0, but as findings,
     not as a ranking input.
   - **Ranking is still absent, and the field moved again.**
     - repowise ships ranked refactoring targets "by graph position".
     - CodeScene's MCP exposes a refactoring business case.
     - jscpd has `--health`.

   This is M3 in §0.4.
3. ~~**No suppression accounting.**~~ **CLOSED in 1.7.0.** `--audit-suppressions` reports what each of the five channels hides, plus stale suppressions at two confidence levels. The design note that made it honest — two channels suppress *before* a finding exists, so an accurate count needs a shadow pass with suppressions lifted — is recorded in [`1_7_0_plan.md`](history/1_7_0_plan.md). Re-verified: still no peer in §3 offers it. CodeScene's Goals are the nearest thing, and they are a manual per-hotspot annotation, not an audit.
4. ~~**No history.**~~ **CLOSED in 1.7.0.** History is now read for clone drift (two batched git calls) and for the entropy rate's changed-line denominator. What is still missing is **delta mode** — every run is still a full re-analysis; there is no incremental mode. The original parenthetical ("188 s on the 3,070-file monorepo") is now 125–255 s, machine-load dependent.
5. **No agent surface. This gap widened again on 2026-10-16.** Snipper is now the **only** tool in
   either matrix without an MCP:
   - CodeScene has a single-user MCP whose `pre_commit_code_health_safeguard` is designed to check
     agent edits before commit.
   - NDepend open-sourced an MCP server.
   - RoslynLens, a free third-party Roslyn MCP, exposes `find_dead_code` to Claude Code over stdio.
     A team that wants "agents ask about dead C#" is reaching for that today, without Snipper's
     tiers or dependency hygiene.

   **No agent surface: the 2026-10-06 text, kept for the record.** jscpd now ships an MCP server **and five installable agent skills**; SonarQube ships an MCP server (all commercial editions) framed around an "Agent Centric Development Cycle"; CodeScene ships ACE, an AI refactoring agent in the IDE. Snipper emits JSON/SARIF only. This was already "the cheapest gap on this list and the most strategically relevant"; after re-verification it is the clearest one, because three competitors have moved from *none* to *multiple agent entry points* in the same window.
6. **No architecture modelling, and it is no longer defensible as a non-goal. Raised to the
   second-largest gap on 2026-10-16.** Layering, cycles and fitness functions are entirely absent.
   That was a deliberate non-goal to date, but it was never explicitly argued. It now has to be
   argued against the market leader:
   - SonarQube Server 2026.4 includes architecture management in Developer, Enterprise and Data
     Center for C#. It has an intended-architecture allow-list, deviations raised as issues, and
     cycles ("tangles") detected automatically.
   - NDepend 2026.1 added ND1412 "Enforcing Clean Architecture".

   Snipper has the raw material most tools lack: an evaluated project and package graph
   (SNP0004/0011/0012). It also has a place to store the rules where they can be reviewed in a PR,
   namely `snipper.json`. Sonar's model lives in a UI editor with no documented file format. See M2
   in §0.4 and §6.12.
7. **Attribution.** No `--blame` equivalent on findings. *Promoted to a must-have on 2026-10-16
   (M4).* For a lead with several monorepos, ownership is how work gets assigned, so it comes
   before any rollup.
8. **Free ReSharper in CI** (`qodana-cdnet`) means dead-code parity must be defended on certainty and dependency depth, not coverage. Corrected 2026-10-06: the free .NET linter is **Early Access** ("may not be reliable, may not work as intended") and has **no Quick-Fix**, Insights or License audit — so this threat is real but was overstated in the previous revision on both capability and stability.
9. **The duplication rules are opt-in and their false-positive rate is only one-repo deep.** On the monorepo, SNP0031 alone was ~4,200 of ~6,600 findings — about two thirds of the report. **Corrected 2026-10-09 by 1.9.0:** per-set reporting cut that to 1,092 of 4,083, so the volume is no longer the objection — one set can legitimately contain many copies. What survives is the underlying finding: the first-800 sweep found the *copies* dominated by *real but not worth removing* duplication rather than by wrong answers, which is a tuning problem, not a soundness one. One repository is not a calibration.
10. **The clone-drift lead is not a moat.** §6.1's correction: the concept dates to Krinke & Ragkhitwetsagul (WCRE 2007) and `phpcpd-next` ships inconsistent-clone detection in PHP. Snipper's defensible position is being the only .NET implementation with commit-level attribution and certainty tiers — not being first.
11. **No view above the repository.** *Added 2026-10-16.* Several products can show a lead every
    repo's health on one screen:
    - GitHub Code Quality GA: org dashboards, org-wide enablement, ruleset gates.
    - SonarQube portfolios (Enterprise).
    - repowise workspaces: several repos indexed as one unit, with cross-repo consumers.

    Snipper's ledger and report are per-repo. They are committed JSON, though, so a rollup needs
    only a reader, not a service (§7, M5).
12. **Free .NET dead-code tools multiplied.** *Added 2026-10-16.*
    - DeadCode (static plus dotnet-trace, "categorizes methods by removal safety", JSON for LLM
      cleanup).
    - UnusedSymbolAnalyzer (a .NET 10 global tool).
    - RoslynLens (MCP `find_dead_code`).
    - DeadSharp (2025).

    None found has certainty tiers, dependency hygiene, a baseline or governance. Each, though,
    makes "finds dead C#" a weaker headline. DeadCode's runtime-evidence plus removal-safety
    pairing is the one to watch, because it overlaps candidates 19 and 22.

---

## 5. Where Snipper leads

- **SNP0012** — redundant transitive packages via real lock-file graphs with version-range awareness. Nothing in the ecosystem does this; ReSharper/Rider/VS only flag *wholly unused* references, and now do so for free in CI.
- **SNP0013** — framework-inbox packages via `PackageOverrides.txt`. Unique.
- **SNP0011** — orphan + detached projects with plugin-loading string evidence. Unique; no tool sweeps for projects abandoned on disk.
- **SNP0018** — obsolete-member dead code. Unique.
- **Certainty tiers as a first-class axis.** Snipper's Guaranteed/High/Moderate/Advisory scale is *confidence the finding is correct*. SonarQube's MQR mode is *severity of impact on each software quality*; ReSharper's `.Global`/`.Local` split is *scope*. These are orthogonal axes and Sonar is the only peer that has made impact multi-dimensional — it has not made **confidence** a reported dimension. Pairs naturally with MQR into a 2-D severity × confidence matrix; nobody offers it.
- **CI-first shape** — read-only, no mandatory build (InspectCode builds by default), SARIF + JSON + baseline in a zero-dependency global tool. InspectCode only matched SARIF in 2024.1 and still has no baseline concept; Qodana adds one but needs Docker/native runner setup and a JetBrains account for anything beyond the free tier.
- **Compounding FP discipline.** Three FP-review rounds against a live ~3,000-file monorepo with strip-compile proofs, and a record of *upholding* challenged true positives rather than reflexively demoting rules — including upholding all four 1.6.2 findings and then **withdrawing one rule enhancement** (SNP0012's direct-transitive claim) after an isolated build proved it wrong. Most competitors report recall; Snipper documents soundness, including its own.
- **Clone drift / one-sided-fix detection (SNP0032).** §6.1, shipped 1.7.0. **Stated narrowly after a 2026-10-06 correction:** the concept is not novel (Krinke & Ragkhitwetsagul, WCRE 2007) and is not uniquely implemented (`phpcpd-next` flags inconsistent clones in PHP). What is still defensible is that this is the only **.NET** implementation found, that it is **temporal rather than static** — it names the commit and date, which static divergence cannot — and that it tiers by fix-shape. PMD CPD still documents keeping clones in sync as beyond its reach; CodeScene's X-Ray is the closest peer and *ranks* clones by change coupling rather than reporting the one-sided-fix event.
- **Churn-normalized entropy rate with an enforced budget.** §6.2, shipped 1.7.0. `--entropy-budget` fails the build (exit `3`) on a rate, not a stock, and **fails open** — an unmeasurable rate is one of seven named statuses, never `0.00`, so a missing git checkout cannot break a pipeline. **This survived re-verification as the one Matrix B cell no peer fills.** The nearest neighbour got closer (jscpd's `--history` reads duplication over git history and advises how far `--threshold` could be tightened) but is duplication-only and is not a gate; CodeScene Goals is complexity-based and closed.
- **Suppression and baseline-integrity audit.** §6.3, shipped 1.7.0, with no nearest neighbour at all. Still true after re-verification.
- **Checkout-independent fingerprints and a large-solution speed that suits CI. Added 2026-10-16.**
  - In 1.15.0, SNP0031 began naming its other copies by workspace-relative path. Baselines no longer
    depend on where the repository is checked out, which matters for a rollup across agents and
    repos (M5).
  - In 1.16.0, the reference monorepo (~1,900 C# documents, ~120 projects) went from **118 s to
    63 s** wall time with byte-identical reports.
  - No free .NET dead-code entrant documents its soundness or its run cost on a solution that size.
- **Still unique against the new free .NET dead-code tools. Re-checked 2026-10-16.** DeadCode,
  UnusedSymbolAnalyzer, RoslynLens and DeadSharp all find unused symbols. None found offers any of:
  - certainty tiers
  - SNP0011/0012/0013 dependency hygiene
  - a baseline, a suppression audit or an entropy rate

  DeadCode's removal-safety categories are the closest overlap, and they rest on runtime traces,
  not proof (§6.6).
- **A baseline that means the same thing under every configuration.** Five suppression channels, and all five are proved churn-free: `--baseline` output is identical whether or not any suppression is configured. Most competitors' baselines silently change meaning when you tune them.

---

## 6. Features no tool offers

Ranked by expected impact on a large codebase's entropy trajectory. For each, the nearest existing neighbour is named so the novelty claim is auditable — several ideas that look original are **not**, and are marked as such.

**Status as of 1.7.3: the top three have shipped.** §6.1, §6.2 and §6.3 were written as proposals, implemented in Wave 4 (1.7.0), and are kept here as the record of what was claimed and what the implementation actually had to concede. §6.4–§6.7 remain open.

**Added 2026-10-16: §6.8–§6.13.** These are written for one reader: the tech lead or principal
engineer who oversees several large monorepos. That reader needs to validate their health and hand
out actionable work. Each entry builds on data Snipper already computes and stays read-only,
deterministic and model-free. Each names its nearest neighbour, found on 2026-10-16. Most depend
on M3 (ranking) and M4 (ownership) in §0.4, so those come first.

| § | Feature | Status |
| --- | --- | --- |
| 6.1 | Clone drift / inconsistent-fix detection | **SHIPPED** 1.7.0 (SNP0032); soundness fix 1.7.3. **Novelty claim corrected 2026-10-06** — not first, not unique; see the correction box. |
| 6.2 | Entropy as a rate, normalized by churn, with a budget | **SHIPPED** 1.7.0. Nearest neighbour narrowed (jscpd `--history`); the "no peer can gate on it" claim verified. |
| 6.3 | Suppression and baseline integrity audit | **SHIPPED** 1.7.0. Nearest neighbour: still none. |
| 6.4 | Inferred architecture and erosion deltas | open. **Merged into §6.12 on 2026-10-16**, because SonarQube's hand-authored model made the hand-authored half table stakes (M2) |
| 6.5 | Unanalysed-region accounting | open — still no peer |
| 6.6 | Removal-safety evidence (proof, not auto-fix) | open — **the gap widened**: CodeScene now ships ACE, an in-IDE AI refactoring agent |
| 6.7 | Lower-priority absences | open — **widened**: jscpd shipped five agent skills and an MCP server |
| 6.8 | Deletion dividend: cascade-closed, owner-routed removal batches | **new 2026-10-16**. Nearest neighbours: jscpd cascading reachability (no C#, file-level), DeadCode removal-safety classification (per symbol, no cascade) |
| 6.9 | Agent-vs-human entropy rate | **new 2026-10-16**. Nearest neighbours: Sonar "Agentic AI" gate (a stricter gate, no rate), AI Code Assurance (tagging) |
| 6.10 | .NET estate currency and drift map | **new 2026-10-16**. Nearest neighbours: Renovate/Dependabot (per package), NuGet Audit (vulnerabilities only) |
| 6.11 | Test-only liveness | **new 2026-10-16**. Nearest neighbour: jscpd `--dead-code` confidence (no C#) |
| 6.12 | Inferred architecture, proposed as reviewable rules, with an erosion rate | **new 2026-10-16** (absorbs 6.4). Nearest neighbour: SonarQube intended architecture (hand-modelled in a UI, no inference, no rate) |
| 6.13 | Estate health brief: ranked actions, not a stock dashboard | **new 2026-10-16**. Nearest neighbours: GitHub Code Quality org dashboard, Sonar portfolios, repowise refactoring targets |

### 6.1 Clone drift / inconsistent-fix detection — *nearest neighbour: PMD CPD, which concedes it is unsolved*

PMD's CPD documentation says it plainly: *"failure to keep the code in sync may mean automated tools will no longer recognise these blocks as duplicates. This means the task of finding duplicates to keep them in sync when doing subsequent refactorings can no longer be entrusted to an automated tool… We thus advise developers to use CPD to help remove duplicates, not to help keep duplicates in sync."* **Re-verified 2026-10-06, verbatim and still present in the current PMD 7.14.x docs.** That much holds, and it is a useful signal — but it is a statement about CPD's own scope, not evidence that nobody else does it.

### ⚠ CORRECTION 2026-10-06 — this section's novelty claim is false, and was written without checking

This section originally closed: *"jscpd's `--blame` yields authors and dates but no divergence analysis. **Nothing computes it.**"* That is not true. Two things found on re-verification:

1. **The idea is 19 years old in the literature.** Krinke & Ragkhitwetsagul, *"A Study of Consistent and Inconsistent Changes to Code Clones"* (WCRE 2007) — the abstract: *"usually half of the changes to code clone groups are inconsistent changes"*, and it defines consistent/inconsistent clone groups over a Simian + diff pipeline. A 2024 arXiv study on git commit logs for clone inconsistency continues the same line. Citing PMD's concession as if the question were unasked was the error: PMD declining is a vendor's scope decision, not evidence of an empty field. **An earlier draft of this document did not cite Krinke at all, which is an embarrassing omission for a novelty claim.**
2. **At least one tool ships it.** `phpcpd-next` — a token-based PHP detector — states that its suffix-tree engine *"additionally flags inconsistent clones — near-miss copies that have diverged — which is where duplication tends to hide bugs (one copy patched, its sibling not)."* That is Snipper's thesis, in PHP, in production code. A second new entrant, `dupehound` (June 2026), targets duplication written by coding agents and ships `history` plus a CI `check` gate — history-aware, but not divergence.

**What survives, stated as narrowly as the evidence allows.** Snipper's SNP0032 is not novel as a *concept*, and it is not the only *implementation*. What is true is narrower and still worth something:

- It is the only **.NET/CI** implementation found, in a zero-dependency global tool that shares the solution graph, `snipper.json`, baseline and SARIF stream with the rest of the analysis — rather than a third-party detector bolted on.
- It is **temporal, not static.** phpcpd-next flags clones that *differ now*. Snipper reports the **event** — the commit that changed one copy without its siblings — and names the commit and the date. A static divergence check cannot answer "when did these diverge, or did the sibling ever catch up?"; that history is the actionable half, and it is what makes the finding attributable rather than merely descriptive.
- It ships **certainty tiers** (High only where the change reads as a defensive fix), which the PHP tool does not.

This is a much weaker claim than the original and it is the one the evidence supports. The honest strategic read: *the concept is not a moat; being the only .NET tool that does it, with the attribution a CI gate needs, is.* Do not build the roadmap on the novelty.

**Implementation record (unchanged, and still the reason the claim needed re-checking).** Implemented as 4C (SNP0032) with one correction the fixture forced: the present-tense reading is impossible, because a copy that receives a fix stops being a clone and leaves the set - verified, since applying a one-sided guard changed which pair SNP0031 reported. The rule therefore reports the temporal *event* and is silent about a fix still in place; clone-set breakup is the follow-up. Caveat that must be stated in any plan: "one copy changed" is a heuristic, not proof. High-confidence subset — a change that looks like a defensive fix (`null`/guard/try/catch/exception/bounds) applied to one copy of a clone set, where the other copies are byte-identical to the pre-change text — is defensible; anything looser is a report of *possible* drift and belongs in a lower tier.

**Two further defects were found by a first-800 sweep of the monorepo after this shipped, both fixed in 1.7.2/1.7.3, and both are worth recording as a caution about claiming a feature is done.** 4C builds on SNP0031's clone sets, so a soundness bug *upstream* of it silently degraded 4C too:

- **1.7.2 — the upstream set was wrong in a way 4C inherited.** SNP0032 reported 90 of 302 findings as one-sided fixes on commits that *created* the file. A file's first appearance cannot leave a sibling untouched, because there was no sibling. The patch parser had no concept of a creation hunk; it now records one and 4C ignores it.
- **1.7.3 — and unsound in a way no test caught.** SNP0031's candidate index is keyed on a 32-bit hash of the 60-token window, and `Extend` seeded its forward scan *at* `WindowTokens` — so the window the key was computed from was never actually compared. A hash collision therefore produced a finding claiming two unrelated files were 60-token duplicates (an interpolated `ToString()` reported as a clone of `Substitute.For<Refit.IApiResponse>()`). 232 findings on the monorepo were this or this shape. `Extend` now proves the window before seeding, and the test suite brute-forces a real collision to prove it.

The generalisable lesson, and it is the reason this entry is still here rather than archived: a clone-detection rule's hardest correctness property is that **candidates are verified against the tokens the index stands for**. An 81-project monorepo found it; a 581-test suite with the fixture not compiling did not.

### 6.2 Entropy as a rate, normalized by churn, with an enforced budget — *nearest neighbours: SonarQube new-code trends, CodeScene Goals, and (new) jscpd `--history`; none computes a churn-normalized findings rate that can gate a build*

Every tool ships entropy **stock** (how many findings) or a **delta** (new findings since a reference). The overwhelming majority ship neither a **rate** — findings added per unit of change — which is the number a maintainer is actually asked for in a quarterly engineering review, and the only one that is comparable across teams of different sizes and cadences.

Design: persist a per-commit time series of (findings added, findings resolved, lines changed, commit SHA). Emit `new_findings / kLOC changed` with a configurable budget that can fail CI. Snipper already stamps `commitSha` in its JSON report and already computes new-vs-baseline, so the increment is normalization plus a stored series. Cost: a small state file, no new analysis, no new semantic work. **Implemented as 4B with one measured correction:** the denominator is anchored to the commit recorded in the baseline file rather than to the pull request, because at per-PR granularity the rate is dominated by change size — holding quality constant at one new finding, it spans 200/kLOC at 5 changed lines to 0.5/kLOC at 2000, a 400x spread that would make any fixed budget a measure of PR size. The gate ships opt-in with no default budget.

**Nearest-neighbour update, 2026-10-06.** jscpd now reads git history for duplication (`--history v5.0.0..HEAD`), producing a sparkline, a per-commit table with the delta between points, the overall trend, and an explicit recommendation on **how far `--threshold` could be tightened**. That is a real move toward this idea and it is the nearest free-tool analogue yet. Three reasons the cell still stands: it is scoped to **duplication only**, not to findings across 28 rules; it produces a **trend and an advisory tightening suggestion, not a budget that fails a build**; and it has no persisted ledger — the range is given on each invocation, so a CI gate would need the committed-state file this design specifies. The claim that no peer normalizes findings-per-change *and* can fail a build on it is verified, not assumed.

This is deliberately the cheapest high-impact item on the list. It is also the only one that directly answers the maintainer's question — *are we actually controlling entropy, or just not looking at the stock?*

### 6.3 Suppression and baseline integrity audit — *nearest neighbour: none*

Every long-lived codebase accumulates `#pragma: disable`, `[SuppressMessage]`, `.editorconfig` severities, and baseline entries. This is entropy disguised as cleanliness, and no tool reports on it. A maintainer inheriting a repository needs: total suppressions and their age distribution; which rules dominate; **findings suppressed and nothing else**; suppressions that are now obsolete (the code they covered is gone); and the headline question — *what fraction of our "clean" status is suppression currently buying us?*

**Correction, 2026-10-03.** An earlier draft of this section claimed `snipper.json` is applied at report time for *every* channel, "so the analyser knows precisely which findings each exclusion removed". That is true of only three of the five channels. `exclude.namespaces` suppresses findings inside the analysers and `rules: "off"` for a sole-rule analyser prevents the analyser running at all, so for those two the analyser does **not** know what was removed — the findings never become objects. Measuring this is what produced the two non-obvious consequences: an honest audit needs an opt-in re-run with suppressions lifted, and those same two channels silently rewrote the baseline (measured −30 and −19 fingerprints on the SampleApp fixture). Both are now fixed — see [`1_7_0_plan.md`](history/1_7_0_plan.md).

Snipper remains unusually well-placed here, for a different and better reason than the one originally claimed: all five channels funnel through two auditable places — `FindingFilter` for the report-time three, `ExclusionEngine.IsNamespaceExcluded` plus the analyser-removal path for the other two — so a single shared classifier can attribute every channel without duplicating the filter's logic. **Implemented** as `--audit-suppressions`. This is a governance feature rather than a detection feature, and it remains the item most likely to be valuable to exactly the large-codebase maintainers this tool targets.

### 6.4 Inferred architecture and erosion deltas — *nearest neighbour: ArchUnitNET/NetArchTest/jQAssistant (hand-authored rules), CodeScene change coupling (behavioural, adjacent)*

> **Superseded 2026-10-16 by §6.12.** SonarQube Server 2026.4 made *hand-authored* architecture
> conformance a free feature for C# (M2). The *inferred* half below is still unshipped anywhere. It
> is carried forward in §6.12 with a concrete output: a proposed `architecture` block, plus an
> erosion rate in the ledger.

Architecture erosion is the largest single source of long-run entropy in a large codebase, and the standard defence — fitness functions as executable tests — is **labour-intensive to author**, which is why most teams never do it. Nobody *infers* the architecture from dependency history, proposes candidate fitness functions, and then reports where the code has since violated them: *"Layer B took 14 new dependencies on Layer C since March, across 23 commits; here they are."*

Note the honest caveat: this is a **recommendation** engine, not a gate. Inferring intent from history is inferable, not provable, so output must be proposals with evidence, never findings.

### 6.5 Unanalysed-region accounting — *nearest neighbour: none*

Every static analyser silently skips `#if` regions, generated code, and excluded paths. The tool's own entropy measurement is therefore quietly incomplete, and it never says so. Nothing reports *"18% of your lines sit inside conditional-compilation regions; findings there are unmeasured"*; nothing finds branches that are dead because the condition is a compile-time constant; nothing finds feature flags that are now permanently on or off.

This is the cheapest item on the list and the best fit for Snipper specifically: `#if` regions are a **syntax-level** construct, so this needs no semantic model and fits the architecture that made SNP0031 cheap. It also raises trust — a tool that volunteers where it cannot see is more credible than one that presents partial coverage as total.

### 6.6 Removal-safety evidence (proof, not auto-fix) — *nearest neighbour: auto-fix everywhere; proof nowhere*

Auto-fix is broadly available: `dotnet format` and ReSharper CleanupCode, Qodana Quick-Fix with CLEANUP/APPLY strategies, SonarQube AI CodeFix, CodeScene ACE. None of them emits **evidence that removal preserved behaviour**. Qodana's "CLEANUP" strategy is described as safe minor fixes; that is a claim, not an artifact.

**Corrected 2026-10-06.** This section previously named SonarQube's AI CodeFix models as "Claude Sonnet 4 / GPT-5.1 / Bedrock / self-hosted". The current SonarQube Server docs (2026.2 / 2026.3) describe the provider choice as **Sonar's hosted models, your own Azure OpenAI or AWS Bedrock model, or a self-hosted gateway** — the specific frontier-model names were not verifiable and should not have been asserted. Two further facts sharpen this section rather than soften it: AI CodeFix is **Enterprise and Data Center only**, and CodeScene **ACE** is now an AI refactoring agent that runs *in the IDE*, which is a new entrant in the auto-fix space this document previously described as dominated by vendor cleanup tooling.

Snipper can attack this without breaking its read-only tenet: a `--verify-removals` mode that applies Guaranteed-tier removals in a **scratch worktree**, recompiles, and emits a proof artifact — referenced-symbol set unchanged, public API surface unchanged, no new compiler warnings. It mutates nothing the user owns. Snipper already stamps `lineText` + `commitSha` on every finding specifically so consumers can verify a finding still matches before applying it, and an external agent previously applied 35/39 findings with zero breakage — that is the raw material for a real proof rather than a claim.

### 6.7 Also absent, lower priority

- **Cross-repo / org-level duplication and convention drift.** Every tool is per-repository. Large organisations cannot ask "did we implement this three times across services?" or "which of these four utilities is canonical?". Requires an org-scoped index — a service, which conflicts with Snipper's shape (§7). **Partly superseded 2026-10-16:** *convention* drift across repos (TFMs, package versions, build settings) needs no index, and is §6.10; a per-repo comparison across the estate is M5 (`snipper portfolio`). Cross-repo *code* duplication still needs an index and stays here.
- **Per-team entropy ledgers via CODEOWNERS.** SonarQube auto-assigns new issues to the introducing developer and CodeScene does team-coupling analysis; Qodana counts contributors via `.mailmap`. The missing piece is a deterministic, free, per-team burn-down ledger with an enforced budget. Organisationally high value; novelty is moderate, not high. **Promoted 2026-10-16** to must-have M4 (§0.4, candidate 26): ownership is now table stakes, and §6.8, §6.9 and §6.13 all route through it.
- **Test-suite strength on surviving code** (mutation testing, e.g. Stryker.NET). Nobody combines "is this code dead?" with "do the tests that keep it alive actually constrain it?". Combining them yields *dead code that cannot be deleted safely* and *live code held together by assertions that mean nothing* — both genuinely expensive entropy. Large scope; mutation testing is expensive by nature.
- **Semantic clone detection done properly.** jscpd has Type-4 experimentally; the 2026 literature is openly pessimistic ("current SOTA detectors remain vulnerable even to simple Type-2 and Type-3 transformations"). Not a differentiator to claim, and not a gap to close.

### 6.8 Deletion dividend: cascade-closed, owner-routed removal batches — *nearest neighbours: jscpd cascading reachability (JS/TS/Python, file-level); DeadCode on NuGet (removal-safety classification per symbol, no cascade)*

**The problem for an estate owner.** A run on a large monorepo returns a list of individual findings,
often thousands. The person who oversees the estate does not want a list. They want a short set of
pull requests they can ask teams to open this quarter, and the payoff of each.

**The gap.** Dead code is transitively closed, and no .NET tool follows the chain. Deleting an unused
public method can make the private helpers it called unused. That can make a `using` unused, a
package reference unused or redundant (SNP0003/0012), and a project orphaned (SNP0011). Every tool reports only
the first link. A team deletes it, re-runs the tool and finds the next link, one round at a time.

**What Snipper would emit.** A `removalBatches` section in the JSON report and in the brief (§6.13).
Each batch holds:
- the seed findings
- the closure: the symbols, usings, package references and project references that become dead once
  the seeds are gone
- the dividend: lines removed, packages retired, projects retired
- the owner, from M4
- a certainty equal to the weakest tier in the batch

Batches are ranked by dividend per reviewed line. The pitch to the reader is concrete: "these 12 PRs
retire 4 projects, 9 packages and 31k lines".

**How.** Treat the seed set as deleted. A reference that comes only from inside the set no longer
counts. Re-resolve only the symbols the set references, and repeat until nothing new drops out.
`ReferenceSearch` already returns reference locations, so this is a bounded second pass, not a
second full analysis.

**Honest limits.**
- The closure stops at Advisory-tier evidence (reflection, DI registration, string-based activation).
  A batch never claims more certainty than its weakest member.
- A batch is a plan, not a proof. Paired with §6.6 (`--verify-removals` in a scratch worktree), it
  becomes a proven plan. That pairing is the strongest single argument for building §6.6.

### 6.9 Agent-vs-human entropy rate — *nearest neighbours: SonarQube "Sonar way for Agentic AI" gate and AI code autodetection (a stricter gate on tagged projects; the autodetection is reported unreliable); Exceeds and Connectory (manual tagging, no rate, no gate)*

**The question.** Every principal engineer is now asked: "is the agent rollout making the codebase
worse?" Today they answer with anecdotes. The entropy agents are known for is exactly what Snipper
measures: copies instead of reuse, leftover helpers, and packages added and never used.

**The gap.** The ledger (§6.2) already computes new findings per churned kLOC between the baseline
and HEAD. Nothing splits that rate by who wrote the change.

**What Snipper would emit.** The same rate, with numerator and denominator split by commit
provenance, per rule family, for example "agent-attributed commits: 2.1 new findings per kLOC
churned; human commits: 0.8". An optional separate budget (`budget.agent`) lets a team hold agent
changes to a stricter rate.

**How provenance is determined.** Deterministically, from git, with no model:
- `Co-Authored-By:` trailers that name an agent (Claude, Copilot, Cursor, Devin and others)
- bot author identities (`[bot]@users.noreply.github.com`)
- a configurable list in `snipper.json`

A finding is attributed to commits through M4's blame data.

**Honest limits.**
- Provenance is what the trailers say. Untagged agent use and inline completions count as human. The
  agent figure is therefore a floor, and the report must say so.
- There is deliberately no heuristic detection of AI-written code. Sonar's autodetection is the
  cautionary example.

**A ready test bed.** Snipper's own history already carries `Co-Authored-By: Claude` trailers, so the
self-scan exercises this from the first commit.

### 6.10 .NET estate currency and drift map — *nearest neighbours: Renovate and Dependabot (per package, per repo, open PRs, no estate view); NuGet Audit (vulnerabilities only)*

**The questions.** These are the ones that cost estate owners weeks before every .NET LTS cutover:
- Which repos are still on an out-of-support TFM?
- How many versions of `Newtonsoft.Json` do we ship?
- Who bypasses Central Package Management?

No free tool answers them across repos without a hosted software-composition product.

**What Snipper would emit.** A per-repo inventory section, rolled up across repos by M5:
- **TFM fragmentation:** projects per target framework, with out-of-support TFMs flagged. The support
  dates live in a table that ships with each Snipper version.
- **Package version skew:** the same package at different resolved versions across projects in one
  repo, which is a binding risk, and across repos in the portfolio.
- **CPM adoption and drift:** whether `Directory.Packages.props` exists, which projects bypass it, and
  how many `VersionOverride`s there are.
- **Build-setting inconsistency:** `LangVersion`, `Nullable`, `TreatWarningsAsErrors`, `ImplicitUsings`
  and the `global.json` SDK pin, compared across projects.

**Deliberately excluded.** Vulnerabilities stay a non-goal (§8.3). So does "outdated against
nuget.org": the map compares the estate with itself and needs no network.

**How.** Snipper already evaluates every project through MSBuildWorkspace and reads
`project.assets.json` through NuGet.ProjectModel. Every input is an evaluated property or a
resolved version that is already in memory. The output is inventory, Guaranteed by construction. Only
skew might later become an Advisory finding.

### 6.11 Test-only liveness — *nearest neighbour: jscpd `--dead-code` confidence scoring (JS/TS/Python/Rust, no C#)*

**The gap.** In a large monorepo this is the commonest dead code that no dead-code tool reports: a
production member whose every reference sits in a test project. It is dead in production but kept
alive by its own tests, so every reachability tool sees it as used. Matrix A already lists it as
"derivable" for Snipper and absent elsewhere in .NET.

**What Snipper would emit.** A new rule, "kept alive only by tests". The finding names the test
methods that hold the member alive, so a removal batch (§6.8) deletes them together.

**Certainty: High, not Guaranteed.** The tests may be the specification of a public API that is
consumed outside the solution. The existing public-API and `InternalsVisibleTo` handling applies
unchanged.

**How.** `ProjectGraph` already knows `IsTestProject`. The query changes from "any reference?" to "any
reference outside test projects?". It can still stop at the first non-test reference, so live code
costs what it costs today. Only test-only members pay for enumerating all their references, and they
are few.

### 6.12 Inferred architecture, proposed as reviewable rules, with an erosion rate — *nearest neighbours: SonarQube intended architecture (hand-modelled in a UI, no inference, no rate); CodeScene change coupling (behavioural, not structural); NDepend dependency matrix (descriptive)*

**The adoption problem.** M2 makes Snipper check a hand-written `architecture` block. Nobody writes
one from scratch for a 120-project monorepo, and that is why conformance tools stay unadopted even
where they are free.

**What Snipper would emit.** `--propose-architecture` infers layers from two sources:
- the project-reference graph: strongly connected components, then topological levels
- namespace prefixes

It emits a proposed block in `snipper.json` format, with each rule's violation count today, for
example:
- "`*.Domain` must not reference `*.Infrastructure`: 0 violations." The rule already holds, so adopt
  it.
- "`*.Api` must not reference `*.Data`: 3 violations." The rule nearly holds, so adopt it with a
  baseline.

Rules that already hold are free wins: adopting them prevents future erosion at zero cleanup cost.

**The erosion rate.** Once rules are adopted, the ledger records new edges against them per period,
for example "Layer B took 14 new edges to C since the baseline". That turns architecture from a
one-off diagram into a trend with a budget.

**How.** Graph algorithms over data Snipper already holds (SNP0004/0011/0012 build the project graph),
with no model. Co-change from the git log the ledger already reads is an optional extra signal.
This absorbs §6.4.

### 6.13 Estate health brief: ranked actions, not a stock dashboard — *nearest neighbours: GitHub Code Quality org dashboard (per-repo scores, hosted, paid per committer); SonarQube portfolios (Enterprise, ratings and a PDF); repowise refactoring targets (per repo)*

**The gap.** Every competitor's estate view is a hosted dashboard of *stock*: a grade, a rating, a
count. A tech lead walking into a quarterly review needs *actions with names attached*.

**What Snipper would emit.** `--format brief`: one deterministic Markdown page per repo, or per estate
through M5, that renders in a GitHub or Azure DevOps PR comment, a wiki or an email. It has five
parts:
1. **Headline:** the ledger rate against the budget, and the trend since the last ledger point.
2. **Top 10 actions:** ranked by M3 and grouped into §6.8 batches, each with an owner (M4), an effort
   (lines touched) and a payoff.
3. **Since the last brief:** new and retired findings by team.
4. **Trust:** suppression debt (§6.3), unanalysed regions (§6.5) and projects that failed to load.
   A brief that says where it could not see is more credible than one that does not.
5. **Currency:** the §6.10 summary.

It uses no LLM. Two briefs diff cleanly, and the brief can be committed next to the ledger. That is
the shape answer to "no hosted dashboard" (§7): the dashboard is a file in git that goes through
review.

**Build order across §6.8–§6.13.** M4 first, then M3. Then §6.11, which is cheap and stands alone,
then §6.8 and §6.13. §6.10 is independent of all of them. §6.9 needs M4's blame data, and §6.12
needs M2.

---

## 7. What the current shape rules out

Constrained to Snipper's zero-dependency, single-binary, read-only, per-repository shape, the following are **off the table** and are recorded as deliberate non-considerations rather than oversights:

- **MCP server / agent skills** (§4.5) — an MCP server is a long-running process or at minimum a second entry point; it is cheap in *code* but it changes the product from "one binary you run in CI" to "a service". **Re-verified 2026-10-06: this is no longer a defensible non-consideration, it is a competitive gap.** jscpd ships an MCP server *plus five installable agent skills* from a self-contained binary, SonarQube ships an MCP server, and CodeScene ships ACE. jscpd's `--mcp` is the specific problem for this argument: it is a mode of the *same single binary*, so "an MCP server changes the product shape" is not actually true of a stdio server. The locked decision should be revisited rather than re-argued. **Recommended exception, still sanctioned:** the `ai`/token-efficient reporter pattern delivers most of the value with none of the shape change, and remains a ~1-day item — but it is now catching up rather than differentiating (§8.2 candidate 21). **2026-10-16: recommend reversing this clause of locked decision 2a for a stdio mode only.** CodeScene (a local single-user MCP) and NDepend (an open-source MCP server) joined jscpd, SonarQube, repowise and RoslynLens, which leaves Snipper the only tool in its peer set without an agent surface. `snipper mcp` would be a mode of the same binary over stdio. It opens no port, keeps the loaded workspace in memory for the session, persists nothing, stays read-only, and serves the same JSON the CLI writes. It is must-have M1 (§0.4, candidate 23) and subsumes candidate 21. A hosted or network MCP stays ruled out.
- **Org-scoped cross-repo index** (§6.7) — requires shared state at organisation scope. Not compatible; do not attempt. **Clarified 2026-10-16:** an org *index* service stays ruled out, but a cross-repo *rollup* does not need one. `snipper portfolio` (M5, candidate 27) reads reports and ledgers that each repository has already committed, and writes one comparable view. It is a pure function of files, holds no shared state and runs anywhere the files are, so it is shape-compatible. It is also what lets §6.10 and §6.13 answer estate-level questions.
- **Hosting a time-series/dashboard** (§6.2, §6.7) — a rate needs somewhere to live. The mitigation is a **committed, diffable JSON state file** in the repository, which keeps the tool stateless and makes the ledger reviewable in PRs. That is a genuine design constraint, not an obstacle.
- **Any LLM dependency** — Sonar AI CodeFix and CodeScene ACE both take an external model. Snipper can stay model-free and emit the deterministic inputs (`lineText`, `commitSha`, clone sets, divergence evidence) that an agent needs to do the fixing. This is a *stronger* position than embedding a model, given the zero-dependency tenet, and it should be stated as a deliberate choice.

---

## 8. Candidate mapping

### 8.1 Committed — Wave 4 (see [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md))

Candidate IDs here are **ranked by value**; the roadmap's `4A/4B/4C` are **sequenced by implementation order** (cheapest first). The two schemes are deliberately in different orders, so the mapping is given rather than left implicit.

| # | Candidate | Roadmap | Gap | Certainty | Effort | Status | Notes |
|---|---|---|---|---|---|---|---|
| 14 | **Suppression / baseline integrity audit** (§6.3) | 4A | new | Guaranteed (it is a count of our own config) | S–M | **SHIPPED 1.7.0** | `--audit-suppressions`. Highest value-per-effort on this list. Two of five channels needed an opt-in shadow re-run — see the correction in §6.3. |
| 13 | **Entropy rate ledger** (§6.2) | 4B | new | — (metric, not a finding) | M | **SHIPPED 1.7.0** | Churn-normalized new-findings rate with an opt-in budget; committed JSON ledger; per-month aggregation. Introduces Snipper's first gate. The denominator is anchored to the baseline commit rather than to the pull request — measured, a per-PR denominator is dominated by commit size (400x spread). **Re-verified 2026-10-06 as the one unfilled Matrix B cell**: jscpd's `--history` is the nearest free analogue but is duplication-only and advisory, not a gate. |
| 12 | **Clone drift / inconsistent-fix detection** (§6.1) | 4C | new | High (defensive-fix subset) / Advisory (possible drift) | L | **SHIPPED 1.7.0**; two defects fixed in 1.7.2 / 1.7.3 | Builds on SNP0031's index + git history. Finds correctness defects, not smells. **Novelty downgraded 2026-10-06:** Krinke & Ragkhitwetsagul (WCRE 2007) studied inconsistent clone changes; `phpcpd-next` ships inconsistent-clone detection for PHP; CodeScene X-Ray ranks clones by change coupling. Defensible claim is now "only .NET implementation, temporal rather than static, tiered by fix-shape". **1.7.2:** a file-*creation* commit was being reported as a one-sided fix (90 of 302 findings on the monorepo) — a file's first appearance cannot leave a sibling untouched. **1.7.3:** SNP0031's clone index is keyed on a 32-bit hash and the 60-token window was never verified, so unrelated files could be reported as duplicates (232 findings removed). Both were found by a first-800 sweep of the monorepo, not by any test. |

### 8.2 Tracked, not committed

| # | Candidate | Gap | Certainty | Effort | Notes |
|---|---|---|---|---|---|
| 15 | **Unanalysed-region accounting** (§6.5) | new | Guaranteed | S | `#if`/feature-flag regions; constant-folded dead branches; permanently-on/off flags. Pure syntax — best architectural fit on the list. |
| 16 | **Clone kinds: Type-3 near-miss** (§2) | parity | Advisory | M | jscpd parity. Lower priority than 12 — Type-3 on normalized tokens re-introduces exactly the structural-skeleton noise that forced SNP0031 behind a flag. Measure before building. |
| 17 | **Finding attribution (`--blame` equivalent)** (§2) | parity | — | S | **Promoted 2026-10-16 to M4 / candidate 26.** Author + last-touch date per finding. jscpd, Sonar and CodeScene all have it; Snipper does not. Prerequisite for per-team routing. |
| 18 | **Complexity + churn as prioritization only** (§4.2) | new | — (not findings) | M–L | **Promoted 2026-10-16 to M3 / candidate 25.** Deliberately **not** new complexity rules (non-goal): import both signals purely to *rank existing findings*, which is what unblocks hotspot-flavoured work without violating the lint non-goal. **Urgency raised 2026-10-06:** jscpd now ships `--complexity` and a size-aware `--health` score for free, so complexity is no longer a differentiator — ranking *findings* still is, and it remains the largest gap in Matrix B. |
| 19 | **Coverage evidence import** (carried) | Tier 3.10 | channel | M | `--coverage coverlet.xml`; adjusts certainty, never a standalone finding. Pairs with 6.7's dead-code-kept-alive-by-tests rule. **Note:** `qodana-cdnet` has code coverage on the free Community licence, so this is parity work, not differentiation. |
| 20 | **Unused `.resx` keys / XAML-Razor evidence** (carried) | Tier 3.11 | Moderate | M–L | Generalise `AssemblyNameEvidenceScanner` to resource/view assets. |
| 21 | **Token-efficient `ai` reporter** (§7) | parity | — | S | **Subsumed 2026-10-16 by M1 / candidate 23**, if 2a is amended. If it is not, this stays the fallback. **Clearly behind as of 2026-10-06.** jscpd ships an `ai` reporter (~79% fewer tokens than console), an MCP server and five installable agent skills; SonarQube ships an MCP server across all commercial editions; CodeScene ships ACE in-IDE. Still the cheapest item on this list, still sanctioned by locked decision 2a — but "the cheap 80% of MCP" now describes catching up, not leading. |
| 22 | **Removal-safety evidence (proof, not auto-fix)** (§6.6) | new | — (evidence, not a finding) | L | `--verify-removals` applies Guaranteed-tier removals in a scratch worktree, recompiles, and emits proof that the referenced-symbol set and public API surface are unchanged. Mutates nothing the user owns, so it respects the read-only tenet. **Gap widened 2026-10-06:** CodeScene ACE is an AI refactoring agent inside the IDE, so the "everyone auto-fixes, nobody proves" gap now has a much more capable competitor on the fix side — which raises the value of being the tool that says whether the fix was safe. |
| 23 | **M1: `snipper mcp` (stdio mode)** (§0.4, §7) | parity, must-have | — | M | Tools over the existing report: `findings`, `explain`, `rank`, `ledger`, `verify_still_present`. Every peer has an agent surface as of 2026-10-16. **Needs locked decision 2a amended**; subsumes 21. |
| 24 | **M2: architecture rules** (§0.4, §4.6) | parity, must-have | Guaranteed (project graph) / High (namespace edges) | M | An allow-list `architecture` block in `snipper.json`, plus cycles and layering. SonarQube 2026.4 gives this free for C#. Snipper's answer is a reviewable file rather than a UI model. Feeds §6.12. |
| 25 | **M3: finding ranking** (§0.4, §4.2) | parity, must-have | — (ordering, not findings) | M–L | Supersedes 18. Churn × complexity × certainty × fan-in; `--rank`; a top-N list. Inputs are already computed (SNP0033/0034, the ledger's churn). |
| 26 | **M4: ownership and attribution** (§0.4, §6.7) | parity, must-have | — | S–M | Supersedes 17 and the per-team ledger in §6.7. CODEOWNERS plus `git blame` per finding, with per-team rollups and budgets. The prerequisite for 27, 29, 30 and 34. |
| 27 | **M5: `snipper portfolio`** (§0.4, §7) | parity, must-have | — | M | Merges N committed reports and ledgers into one scorecard with trends. It reads files, so it is shape-compatible (§7). The answer to GitHub Code Quality org dashboards and Sonar portfolios. |
| 28 | **M6: `--changed-since <ref>`** (§0.4) | parity, must-have | — | S | Filters a full run to changed files and adds the ledger delta. It is not incremental analysis. The 1.16.0 speed makes the full run behind it affordable in CI. |
| 29 | **Deletion dividend** (§6.8) | new | Weakest member's tier | L | Cascade-closed, owner-routed removal batches ranked by lines, packages and projects retired. Pairs with 22 for proof. |
| 30 | **Agent-vs-human entropy rate** (§6.9) | new | — (metric) | S–M | Ledger split by commit provenance from git trailers and bot identities; optional `budget.agent`. Needs 26's blame. |
| 31 | **.NET estate currency and drift map** (§6.10) | new | Guaranteed (inventory) | M | TFM fragmentation, version skew, CPM drift, `VersionOverride` sprawl, build-setting inconsistency. Offline, no vulnerabilities. Rolls up through 27. |
| 32 | **Test-only liveness rule** (§6.11) | new | High | S | Production members referenced only from test projects. Cheapest new item; `ProjectGraph.IsTestProject` and `ReferenceSearch` already exist. |
| 33 | **Inferred architecture with an erosion rate** (§6.12) | new | — (proposals) | M–L | `--propose-architecture` emits a draft block for 24, with today's violation counts; the ledger tracks new edges against adopted rules. Absorbs §6.4. |
| 34 | **Estate health brief** (§6.13) | new | — (report format) | S–M | `--format brief`: deterministic Markdown with a headline rate, the top 10 actions with owners, the change since the last brief, trust and currency. Most valuable once 25 and 26 exist. |

### 8.3 Superseded / rejected

| # | Candidate | Status |
|---|---|---|
| 5 | `--fix` for Guaranteed rules | ❌ Rejected 2026-09-15 — read-only is a permanent tenet. Superseded in spirit by candidate **22** (removal-safety *evidence*, §6.6), which proves safety without mutating the user's tree. *(An earlier draft cited "candidate 16" here; 16 is Type-3 near-miss clone detection. Removal-safety had no candidate ID at all, and §6.6 is a section number, not one.)* |
| — | Outdated/vulnerable packages | Non-goal — NuGet Audit and Qodana own it. |
| — | Design-time IDE squiggles | Non-goal — ReSharper/Rider/SonarLint own it. |
| — | General lint/bug-risk rules | Non-goal — compiler warnings and NRE detection are Roslyn/ReSharper territory. |
| — | Auto-generated *complexity* rules | Non-goal — but see candidate 18, which imports complexity for ranking only. |
| — | Org-scoped index, hosted dashboard, LLM fix generation | Rejected by shape (§7). **Not rejected (2026-10-16):** a cross-repo rollup over committed files (candidate 27) and a committed Markdown brief (candidate 34). They meet the same needs without a service. |
