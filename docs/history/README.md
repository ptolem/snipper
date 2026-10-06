# Documentation history

Superseded documents, kept as a record rather than deleted. Nothing in this folder is
maintained; each file describes the state of the world at the version it is named for.

**Read the current documentation instead:**

| Document | Covers |
| --- | --- |
| [README](../../README.md) | Install, usage, the rule catalogue, and what changed in each release. |
| [Usage guide](../usage.md) | Every option in depth, filtering semantics, performance, known limitations. |
| [CI integration](../ci-integration.md) | Gating strategies, baselines, entropy budget, pipeline recipes. |
| [Code architecture](../code_architecture.md) | For contributors: run sequence, invariants, adding a rule. |
| [Competitive analysis](../competitive-analysis.md) | Snipper against the ecosystem, including where it is behind. |
| [Feature parity roadmap](../Snipper-Feature-Parity-Roadmap.md) | Delivery history and the open backlog. |

## Naming convention

`<version>_<subject>.md`, version first, underscores rather than dots — so files sort by
release and the affected version is visible without opening anything:

```
1_7_3_plan.md          the plan for release 1.7.3
1_6_1_fp_review.md     the false-positive review for 1.6.1
1_4_4_perf_wave_retrospective.md
```

This convention was applied on 2026-10-06. Files were previously named `plan_1_7_3.md`,
`fp_review_1_6_1.md`, `phase3_wave4.md` and `perf_wave_retrospective.md`; the version was
mid-name or absent. `git mv` was used throughout so history follows the rename.

**Two files carry a judgement call in their name**, flagged here rather than buried:

- `1_4_4_perf_wave_retrospective.md` — the document's own title says "1.4.2/1.4.3", which is
  when the perf wave *shipped*. It is filed under **1.4.4**, the release in which its verdict
  (the revert) shipped, because the retrospective is a record of that outcome. `1_4_2_` would
  also be defensible.
- `1_3_0_phase3_wave2.md` — this wave shipped **two** releases: 2A as 1.3.0 and 2B as 1.3.1.
  Filed under the primary target, 1.3.0.

## Contents

### Release plans

| Version | Document | What it covers |
| --- | --- | --- |
| 1.6.2 | [`1_6_2_plan.md`](1_6_2_plan.md) | Framework-evidence round 2; contract names, Swashbuckle/xUnit/MVC/MediatR contracts. |
| 1.6.3 | [`1_6_3_plan.md`](1_6_3_plan.md) | The token-shingle duplicate-detection engine (SNP0031), threshold spike and rationale. |
| 1.7.0 | [`1_7_0_plan.md`](1_7_0_plan.md) | The entropy-governance wave: 4A suppression audit, 4A-2 churn-proof baseline, 4B entropy rate, 4C clone drift, 4D perf. **Largest document here (~1,370 lines).** |
| 1.7.1 | [`1_7_1_plan.md`](1_7_1_plan.md) | Unopenable targets exit `1`; an SNP0031 tuning pass measured as a no-go. |
| 1.7.2 | [`1_7_2_plan.md`](1_7_2_plan.md) | Clone drift no longer blaming file creations; SNP0019 dedupe; one enhancement withdrawn. |
| 1.7.3 | [`1_7_3_plan.md`](1_7_3_plan.md) | Clone-window verification, and a test fixture that had never compiled. |
| 1.7.4 | [`1_7_4_plan.md`](1_7_4_plan.md) | Namespace exclusion reaching file-scope code via the shared `<global>` marker; **and a suspected second defect withdrawn before any code was written.** |
| 1.7.5 | [`1_7_5_plan.md`](1_7_5_plan.md) | Data-structure and allocation audit, measured on the 3,864-file MILKRUN monorepo. **Contains an extrapolation caught by measurement, and three suggested fixes rejected against the real API.** |

### Specs, by wave

| Version | Document | What it covers |
| --- | --- | --- |
| 1.1.0 | [`1_1_0_phase2_analysers.md`](1_1_0_phase2_analysers.md) | SNP0011/0012/0013/0018 team spec. |
| 1.2.0 | [`1_2_0_phase3_wave1.md`](1_2_0_phase3_wave1.md) | 1A config channels, 1B SNP0019, 1C SNP0020. |
| 1.3.0 / 1.3.1 | [`1_3_0_phase3_wave2.md`](1_3_0_phase3_wave2.md) | 2A SNP0021; 2B design sketch only, deferred. |
| 1.4.0 | [`1_4_0_phase3_wave3.md`](1_4_0_phase3_wave3.md) | 3A SNP0023, 3B SNP0024, 3C SNP0026. |
| 1.5.0 | [`1_5_0_phase3_wave4.md`](1_5_0_phase3_wave4.md) | Framework evidence for SNP0005/0006 false-positive reduction. |
| 1.6.0 | [`1_6_0_phase3_wave5.md`](1_6_0_phase3_wave5.md) | Gap-filler sweep: SNP0026 variant, SNP0024 can-be-private, SNP0027/0028/0029/0030. |

### Reviews and retrospectives

| Version | Document | What it covers |
| --- | --- | --- |
| 1.4.4 | [`1_4_4_perf_wave_retrospective.md`](1_4_4_perf_wave_retrospective.md) | The `SolutionReferenceIndex` gate breach and its revert. Read this before trusting any perf claim. |
| 1.6.1 | [`1_6_1_fp_review.md`](1_6_1_fp_review.md) | False-positive review against the monorepo. |
| 1.6.2 | [`1_6_2_fp_review.md`](1_6_2_fp_review.md) | Second FP round; **upheld all four challenged findings as true positives.** |
| 1.7.1 | [`1_7_1_false_positives_investigation.md`](1_7_1_false_positives_investigation.md) | The first-800 sweep of an 81-project monorepo that produced 1.7.2 and 1.7.3. **Contains two conclusions corrected in place** — a hash-collision finding that was really a normalisation problem, and a tally that did not add up. |

## A note on how these documents were written

Several are kept specifically because they record a **no-go or a reversal**, which is the part
of a project's history that gets quietly rewritten:

- `1_4_4_perf_wave_retrospective.md` — a shipped optimisation that broke a gate and was reverted.
- `1_7_1_plan.md` — an SNP0031 tuning pass that measured as unnecessary.
- `1_6_2_fp_review.md` — challenged findings upheld rather than demoted.
- `1_7_2_plan.md` — a planned enhancement withdrawn after an isolated build disproved it.
- `1_7_4_plan.md` — a suspected SNP0019 defect disproved by a two-file fixture before any code was
  written; the planned fix would have suppressed 103 true positives.
- `1_7_5_plan.md` — an audit whose allocation estimates were extrapolated from an 800-file target to
  3,900 files and overstated by roughly an order of magnitude when finally measured on a real
  monorepo. Read this before quoting any allocation figure from the other documents.
- `1_7_1_false_positives_investigation.md` — a study whose own headline conclusions were wrong,
  corrected in place rather than deleted.

If you are about to change behaviour and one of these documents disagrees with the current code,
the current code is right and the document is history. Where they conflict, that is worth
understanding rather than resolving by editing the old file.
