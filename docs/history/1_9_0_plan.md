# 1.9.0 — SNP0031 per-set reporting

**Status:** implemented, measured, released.

## The defect

SNP0031 reported **one finding per copy** of a clone set. A skeleton copied 30 times was 30
findings, so a single duplication could dominate a threshold table and a report could read as
thousands of issues while holding a few hundred distinct problems. On the owner's monorepo the rule
was the largest contributor to the report by an order of magnitude, and almost all of that volume
was one duplication counted repeatedly.

## The change

**One finding per clone set**, anchored at the first surviving member in the set's own
`(path, token index)` order, so the anchor is stable across runs.

The copies are not discarded — they become **related locations** on that one finding:

```
70-token block (10 lines) duplicated 257 time(s) across 13 file(s).
```

This is SARIF's native `location.relatedLocations`, so the format gained a first-class concept
rather than a workaround. JSON gains an optional `relatedLocations` array on the finding entry, and
— following the convention `ReportSchemaShould` already pins for `lineText` — it is **omitted**
entirely for every other rule, so existing consumers see no change.

One consistency fix came with it. A set reduced to a single member by exclusions previously emitted
that member as a finding while `CloneSet` refused to record it (`reported.Count >= 2`). Both now use
the same floor, so a lone surviving copy is not reported at all: one copy has no sibling, which is
what made it a set in the first place.

## Measured on the owner's monorepo (MILKRUN, `--duplicate-detection`)

| | Before | After |
|---|---|---|
| SNP0031 findings | **4,375** | **1,092** |
| Total report | 7,366 | **4,083** |
| Copies accounted for | 4,375 | **4,375** |

**−3,283 SNP0031 findings, −75%; −45% of the whole report.** The critical line is the last one: the
1,092 findings carry exactly 4,375 copies between them, so **not one copy stopped being reported**.
The change reorganises the output; it does not hide anything.

Every other rule was byte-identical before and after:

| | Before | After |
|---|---|---|
| SNP0006 | 1,657 | 1,657 |
| SNP0024 | 703 | 703 |
| SNP0019 | 172 | 172 |
| SNP0020 | 86 | 86 |
| SNP0009 | 58 | 58 |
| SNP0033 | 43 | 43 |
| *(and 15 others)* | unchanged | unchanged |

## What the collapse looks like

The largest sets, which were previously one finding per copy:

| Copies | Now |
|---|---|
| 257 | 1 finding |
| 155 | 1 finding |
| 137 | 1 finding |
| 97 | 1 finding |
| 66 | 1 finding |

The 257-copy case is the defect in miniature: a 10-line, 70-token block repeated 257 times — once
across 13 files. That alone was 257 rows of the report. It is now one row that says so.

## Baseline consequences

SNP0031 findings are fingerprints over rule + relative path + **message**, and the message now
carries the *set* size rather than a per-copy description. Two consequences, both intended:

- **Churn drops.** Before, one set of 257 copies held 257 fingerprints that all died together if
  the set changed. Now it holds one.
- **A set's fingerprint moves when its size changes.** Adding one copy to a set rewrites its count in
  the message, so the old entry goes stale and the new one reads as new. This is the same
  trade-off SNP0033 makes and the same resolution: the message must be self-describing, because a
  finding you cannot triage without re-running the tool is not worth having.

Consumers with an existing SNP0031 baseline should expect it to re-baseline once. That is the intended
migration, and it is a one-time cost against a permanent 75% reduction.

## Ordering note

The roadmap required per-set reporting to land **before** SNP0031 normalisation work, because
collapsing fewer skeletons is a different change from reporting them once. This release satisfies
that ordering; the normalisation misattribution (over-aggressive token collapsing unrelated DI
registrations into one shape) is now unblocked and remains open.

## Tests

`DuplicateFragmentAnalyserShould` gained `Report_One_Finding_Per_Set_Not_One_Per_Copy_For_AnalyzeAsync`
and a `FilesOf(finding)` helper that reads anchor plus related locations, because with per-set
reporting "was this file reported" is a question about the whole set, not about `FilePath`.

Two existing assertions were rewritten rather than deleted, and both revealed that the invariant they
encoded was wrong:

- uniqueness is **not** per file — one file can legitimately anchor several distinct clone sets;
- uniqueness is **not** per line — two different fragments can begin on the same line.

The real invariant is uniqueness of the anchor **span** `(file, line, characterOffset)`, which is what
"one clone, not one per window" actually means. Worth recording: both wrong invariants passed
review and failed only when executed against real data.