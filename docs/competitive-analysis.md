# Competitive Analysis — Snipper vs. the .NET Code-Analysis Ecosystem

**Date:** 2026-09-14 · **Updated:** 2026-09-18 · **Snipper version:** 1.3.0 (17 rules: SNP0001–0013, 0018–0021)
**Scope:** features for *reducing the entropy of a large codebase* — dead code detection, redundancy/hygiene sweeps, dependency bloat, duplication, and the removal workflow (fix automation, gating, suppression).

**Sources & evidence levels:**
- *Verified against vendor docs (fetched 2026-09-14):* ReSharper 2026.2 inspection index (1,078 configurable C# inspections + 1,269 error inspections, incl. the full "Redundancies in Code" (103) and "Redundancies in Symbol Declarations" (51) categories), ReSharper "Remove Unused References" docs (project/assembly references, preview dialog, deletes redundant usings alongside), InspectCode CLT docs (SARIF default since 2024.1, builds solution by default, no baseline concept, `--include/--exclude`, `.editorconfig` severity config).
- *Stable product knowledge:* VS/Roslyn analysers (IDE/CA rules, `dotnet format`), NDepend (dead-code rule family + coverage import), SonarQube (rule IDs, new-code quality gate), Rider 2023.1 NuGet-aware reference removal, dupFinder CLT, dotCover.
- *Qualitative (community sentiment, YouTrack voting):* marked as such where used.

---

## 1. Capability matrix

✓ = strong/native · ~ = partial · — = absent. Rider shares ReSharper's engine → one column.

| Capability | Snipper | ReSharper / Rider | VS + Roslyn | NDepend | SonarQube |
|---|---|---|---|---|---|
| **Dead code** | | | | | |
| Unused private members | ✓ SNP0001 | ✓ `UnusedMember.Local` | ✓ IDE0051/CA1823 | ✓ | ✓ S1144/S1068 |
| Unused internal/public symbols | ✓ SNP0005/0006 | ✓ `UnusedType/Member.Global` (SWEA) | — | ✓ | ~ (private-focused) |
| Unused locals / parameters | ✓ SNP0009/0010 | ✓ | ✓ IDE0059/IDE0060 | — | ✓ S1481/S1172 |
| Unreachable statements | ✓ SNP0002 | ✓ | ✓ CS0162 | — | ✓ |
| Obsolete zero-usage members | ✓ **SNP0018 (unique)** | — | — | — | — |
| Field written, never read / unassigned | ✓ SNP0021 (write-only; unassigned stays SNP0001/CS0649) | ✓ `NotAccessedField`, `UnassignedField` | ✓ IDE0052, CS0649 | — | ✓ S4487 |
| Event never invoked | — | ✓ `EventNeverInvoked` | ~ | — | ~ |
| Return value never used / param-only-precondition / out always discarded / nameof-only | ~ (SNP0021 covers the nameof-only and out-only field slices) | ✓ `UnusedMethodReturnValue`, `ParameterOnlyUsedForPreconditionCheck`, `OutParameterValueIsAlwaysDiscarded`, `EntityNameCapturedOnly` | — | ~ (CQLinq) | — |
| Hierarchy dead code (virtual never overridden, class never inherited, member only via overrides/base) | — | ✓ `VirtualMemberNeverOverridden`, `ClassWithVirtualMembersNeverInherited`, `UnusedMemberHierarchy`, `UnusedMemberInSuper` | — | ~ | — |
| **Redundancy sweeps** | | | | | |
| Unused usings | ✓ SNP0019 (CS8019 + CS8933, incl. global usings) | ✓ `RedundantUsingDirective` (+global) | ✓ IDE0005 | — | ✓ S1128 |
| Redundant casts / qualifiers / type args / default args / etc. | — | ✓ 103 inspections | ~ IDE00xx subset | — | ~ |
| Empty ctor/destructor/namespace, redundant overload/override/initializer/partial | — | ✓ | ~ | — | ~ |
| Commented-out code | ✓ SNP0020 | — | — | — | ✓ S125 |
| **Tightening (entropy prevention)** | | | | | |
| can-be-static / readonly / sealed / private / internal / const / init-only / file-local | — | ✓ `MemberCanBeMadeStatic`, `FieldCanBeMadeReadOnly`, `ClassCanBeSealed`, `MemberCanBePrivate/Internal/FileLocal`… | ~ CA1822, CA1852, IDE0044 | ~ | ~ |
| **Dependency hygiene** | | | | | |
| Unreferenced `PackageReference` | ✓ SNP0003 | ✓ Rider 2023.1+ (NuGet-aware); ReSharper: project/assembly refs | ✓ "Remove Unused References" | ~ | — |
| Unreferenced `ProjectReference` | ✓ SNP0004 | ✓ | ✓ | ✓ | — |
| Redundant transitive direct package (lock-file graph, version-aware) | ✓ **SNP0012 (unique)** | — | — | — | — |
| Framework-inbox package (`PackageOverrides.txt`) | ✓ **SNP0013 (unique)** | — | — | — | — |
| Orphan / detached projects (graph + filesystem sweep) | ✓ **SNP0011 (unique)** | — | — | ~ (unused assemblies) | — |
| Outdated / vulnerable / deprecated packages | — | ✓ Rider NuGet health | ✓ NuGet Audit | — | — |
| **Cross-language assets** | | | | | |
| Unused `.resx` keys; XAML/Razor symbol usage; unused CSS/JS | — | ✓ (XAML, Razor, ASP.NET, Resx in engine) | — | — | ~ |
| **Duplication** | | | | | |
| Duplicate code blocks | — | ✓ dupFinder CLT (free) + TeamCity | — | ✓ | ✓ (CPD) |
| **Runtime evidence** | | | | | |
| Coverage-driven "never executed" code | — | ✓ dotCover | — | ✓ (imports coverage) | ~ |
| **Workflow** | | | | | |
| Auto-fix / bulk cleanup | — | ✓ quick-fixes + CleanupCode CLT | ✓ Code Cleanup + `dotnet format` | — | ~ |
| Safe-delete with usage preview | — | ✓ | ~ | — | — |
| Severity/suppression config (.editorconfig etc.) | ✓ `snipper.json` (per-rule severity/off, namespace + path/glob exclusions) | ✓ .editorconfig + .DotSettings | ✓ .editorconfig | ✓ | ✓ |
| CI baseline / new-code gate | ✓ `--baseline` | ~ (severity threshold) | — | ✓ baseline diff | ✓✓ (industry reference) |
| SARIF output | ✓ | ✓ (CLT default since 2024.1) | ~ | — | ✓ |
| Design-time squiggles | — | ✓✓ | ✓✓ | ✓ | ✓ (SonarLint) |
| Free & open-source | ✓ | CLT free (IDE paid) | ✓ | paid | community tier |

---

## 2. Gaps ranked by developer value

### Tier 1 — cheap for Snipper, very high perceived value (fits the syntax-first architecture)

1. **Unused usings (IDE0005 parity).** ✅ **Shipped 1.2.0 as SNP0019** — via compiler-diagnostic surfacing (CS8019 incl. global usings, CS8933 duplicates), Guaranteed tier.
2. **Severity/suppression configuration.** ✅ **Shipped 1.2.0** — `snipper.json` with per-rule severity/off, namespace exclusions, and path globs, applied at report time so baselines never churn.
3. **Commented-out code (Sonar S125 parity).** ✅ **Shipped 1.2.0 as SNP0020** — comment-trivia code-likeness heuristic, Advisory tier.

### Tier 2 — the biggest functional gaps developers notice

4. **Read-vs-write analysis.** ✅ **Shipped 1.3.0 as SNP0021** — write-only private fields at High tier via syntax-role read/write classification at reference locations; unassigned fields deliberately remain SNP0001/CS0649.
5. **Auto-fix mode.** ❌ **Rejected 2026-09-15** — read-only is a permanent design tenet; ReSharper/VS own the removal workflow, Snipper owns CI-grade detection.
6. **Redundancy sweep.** ⏸ **Speculation gates spike-proven 2026-09-18; implementation deferred pending review** — split into per-pattern rules (SNP0022 default-value argument, SNP0025 method type arguments, SNP0026 cast in Wave 3).

### Tier 3 — differentiating but harder

7. **Hierarchy dead code** (`VirtualMemberNeverOverridden`, `ClassWithVirtualMembersNeverInherited`). ReSharper-exclusive; extremely valuable for library pruning. Snipper already computes override relationships for SNP0018 — a Moderate-tier rule waiting to happen.
8. **Tightening rules** (can-be-static/readonly/sealed/private). Entropy *prevention*; CA1822/CA1852 parity.
9. **Duplicate detection.** dupFinder-style token hashing; a separate engine but monorepo gold.
10. **Runtime/coverage evidence.** Import coverlet output as a certainty channel (coverage ≠ usage, but strong corroborating evidence — NDepend's model).
11. **Cross-language reach.** Unused `.resx` keys; XAML/Razor name evidence (the `AssemblyNameEvidenceScanner` pattern generalises here).

---

## 3. What developers value most

Evidence-weighted; qualitative items marked.

1. **Design-time "unused symbol" detection with solution-wide scope.** JetBrains ships solution-wide analysis *on by default* in Rider despite its cost — unused-symbol greying is the feature users most associate with "the IDE keeps my codebase clean." *(Qualitative)* community threads on "why keep ReSharper" consistently name dead-code detection a top reason.
2. **Fix-in-place and bulk cleanup.** Detection without a one-click fix is considered table stakes; JetBrains investing in a free `CleanupCode` CLT signals CI-side demand for *automated* removal, not just reports.
3. **Remove unused usings.** IDE0005 + "on save" cleanup is among the most-used hygiene features in VS; universally enabled in `.editorconfig` cleanup profiles.
4. **Remove unused references.** *(Qualitative)* the Rider YouTrack request for NuGet-aware unused-reference removal was among its most-voted issues for years before shipping as a headline 2023.1 feature. This validates Snipper's dependency-hygiene axis — and note **no one has caught up to SNP0012/0013 yet**.
5. **New-code gating.** SonarQube's flagship value proposition: teams don't want 4,000 legacy findings, they want "no *new* entropy." Snipper's `--baseline` is the right primitive; parity here is already good.
6. **Confidence signalling.** ReSharper's `.Global`/`.Local` inspection split and per-rule severities solve the same trust problem as Snipper's certainty tiers — the market has validated tiered-confidence design.

---

## 4. Where Snipper already leads

- **SNP0012** — redundant transitive packages via real lock-file graphs with version-range awareness. Nothing in the ecosystem does this; ReSharper/Rider/VS only flag *wholly unused* references.
- **SNP0013** — framework-inbox packages via `PackageOverrides.txt`. Unique.
- **SNP0011** — orphan + detached projects with plugin-loading string evidence. Unique; no tool sweeps for projects abandoned on disk.
- **SNP0018** — obsolete-member dead code. Unique.
- **CI-first shape** — read-only, fast (no mandatory build; InspectCode builds by default), SARIF + JSON + baseline in a zero-dependency global tool. InspectCode only matched SARIF in 2024.1 and still has no baseline concept.

**Bottom line (2026-09-18):** Snipper owns the *dependency-hygiene* quadrant outright and, as of 1.3.0, has closed the daily-visible parity gaps — unused usings (SNP0019), commented-out code (SNP0020), suppression config (`snipper.json`), and read/write flow analysis (SNP0021). The remaining deltas are deliberate or scheduled: auto-fix is rejected (read-only tenet), the redundancy sweep is spike-proven and deferred pending review (SNP0022/0025/0026), and hierarchy/tightening rules are committed for Wave 3 (SNP0023/0024).

---

## 5. Phase 3 candidate mapping

> **Superseded 2026-09-15 by [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md)**, which commits Waves 1–3 (1.2.0–1.4.0). Notable delta: candidate #5 (`--fix`) is **rejected** — Snipper is read-only forever by design.

Gap → candidate rule, in suggested implementation order (Tier 1 first). IDs provisional.

| # | Candidate | Gap closed | Certainty | Effort | Notes |
|---|---|---|---|---|---|
| 1 | **SNP0019 Unused using directives** ✅ shipped 1.2.0 | Tier 1.1 | Guaranteed | S | Shipped as compiler-diagnostic surfacing (CS8019 incl. global usings, CS8933 duplicates) after the spike proved coverage — even simpler than the original design. |
| 2 | **Suppression & severity config** (`snipper.json`, per-rule severity, path/glob exclusions) ✅ shipped 1.2.0 | Tier 1.2 | — | M | Walk-up discovery, report-time application (baseline-stable), analysers skip when fully disabled. |
| 3 | **SNP0020 Commented-out code** ✅ shipped 1.2.0 | Tier 1.3 | Advisory | S | Comment-trivia code-likeness heuristic; doc comments/license/URLs/TODO markers excluded. |
| 4 | **SNP0021 Field assigned, never read** ✅ shipped 1.3.0 | Tier 2.4 | High | M | Syntax-role read/write classification at reference locations (compound/`++`/`ref` = read, `out`/simple assignment = write); serialization attributes demote to Moderate. Unassigned fields deliberately stay with SNP0001/CS0649 — no separate variant. |
| 5 | **`--fix` for Guaranteed rules** (dry-run diff first; SNP0019 + SNP0002 initially) | Tier 2.5 | — | L | Keep read-only default; explicit opt-in; idempotent. |
| 6 | **SNP0022 Redundant code sweep** (casts, type args, default args, qualifiers) | Tier 2.6 | High | M–L | Amended 2026-09-18: split into per-pattern rules — SNP0022 (default-value argument), SNP0025 (method type arguments), SNP0026 (cast, Wave 3) — each speculation-gated (spike-proven). Implementation deferred pending review. |
| 7 | **SNP0023 Virtual member never overridden / class never inherited** | Tier 3.7 | Moderate | M | Reuse SNP0018 override graph; suppress on entry-point/reflection evidence like SNP0006. |
| 8 | **SNP0024 Tightening** (can-be-static, can-be-readonly, can-be-sealed) | Tier 3.8 | Moderate/Advisory | M | Entropy prevention; Advisory default to avoid CI noise. |
| 9 | **Duplicate detection engine** (token-hash, cross-project) | Tier 3.9 | Advisory | L | Separate engine; dupFinder parity; likely new report section, not a finding rule. |
| 10 | **Coverage evidence import** (`--coverage coverlet.xml` → certainty adjust) | Tier 3.10 | (channel) | M | Downgrade/upgrade certainty; never a standalone finding. |
| 11 | **Unused `.resx` keys; XAML/Razor evidence** | Tier 3.11 | Moderate | M–L | Generalise `AssemblyNameEvidenceScanner` to resource/view assets. |

*Non-goals (deliberate):* design-time IDE integration, general lint/bug-risk inspections (compiler warnings, NRE detection — ReSharper/Roslyn territory), outdated/vulnerable package reporting (NuGet Audit / `dotnet list package` already own this).
