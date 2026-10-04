# CI integration

How to run Snipper in a pipeline, and how to choose a gate that a large monorepo can actually live
with. For the meaning of each option see the [usage guide](usage.md); for what each rule means see
the [rule catalogue](../README.md#rules).

This document assumes a large repository with substantial pre-existing debt. That is the normal case,
and it is the case most CI documentation gets wrong — a gate that is red on day one gets disabled on
day two, and a disabled gate is worse than no gate because it looks like coverage.

---

## Contents

1. [The one thing to get right](#the-one-thing-to-get-right)
2. [Choosing a gate: four stages](#choosing-a-gate-four-stages)
3. [Adopting a baseline](#adopting-a-baseline)
4. [Gating on the report](#gating-on-the-report)
5. [The entropy budget](#the-entropy-budget)
6. [Suppression hygiene in CI](#suppression-hygiene-in-ci)
7. [Layering configuration](#layering-configuration)
8. [Sharding a large monorepo](#sharding-a-large-monorepo)
9. [Pipeline recipes](#pipeline-recipes)
10. [Monorepo pitfalls](#monorepo-pitfalls)

---

## The one thing to get right

**Snipper exits `0` when it finds things.** There is no `--fail-on` flag. Exit codes are:

| Code | Meaning |
| --- | --- |
| `0` | Analysis ran. Findings may or may not exist. |
| `1` | Usage error. |
| `2` | Report could not be written. |
| `3` | Entropy budget exceeded. |

So `snipper ... && echo ok` in a pipeline is a check that Snipper ran, not that the code is clean. And
`if [ $? -eq 0 ]` will never fail on findings.

There are exactly two ways to build a real gate:

1. **Gate on the report** — run Snipper, then evaluate the JSON with `jq`, PowerShell, or similar.
   Full control over which rules and tiers count, and the only approach that supports
   "fail only on this rule in this directory".
2. **Use `--entropy-budget`** — Snipper's own gate. Fails on the *rate* of newly introduced findings
   rather than their absolute count. This is the only native gate and it is genuinely the better model
   for a legacy repo, because it is impossible to bankrupt.

Recommended: use both. Entropy budget as the blocking signal on the default pipeline, report gating
for the rules you actually care about.

---

## Choosing a gate: four stages

Adopt these in order. Do not skip to stage 3.

### Stage 1 — Observe (week 1)

No gate. Produce a report and publish it.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json --format json
```

Goals: find out how big the problem is, how long the run takes on CI hardware, and which rules are
going to be noisy. Nothing here blocks anyone.

### Stage 2 — Baseline gate (week 2)

Record existing findings as accepted, then fail only on new ones.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json --baseline .snipper/baseline.json
```

The build fails if the report contains findings; the baseline absorbs everything already known. This
is the stage that converts Snipper from "a report nobody reads" into an active ratchet.

### Stage 3 — Certainty gate (week 3+)

Narrow what counts. `--certainty-tier` sets a floor and is safe to change at any time — the baseline
always tracks the full finding set, so tier changes never churn it.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json \
  --baseline .snipper/baseline.json \
  --certainty-tier high
```

Sensible progression for a large codebase: start at `moderate`, move to `high` once the `moderate`
noise is baselined or fixed. `advisory` (the default floor) admits the duplication and tightening
rules, which are leads rather than defect claims.

### Stage 4 — Entropy gate (once the data is trustworthy)

Add the rate gate. See [The entropy budget](#the-entropy-budget).

---

## Adopting a baseline

### First run

Run on the default branch, commit the baseline, and read the result before trusting it.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json \
  --baseline .snipper/baseline.json \
  --audit-suppressions
```

Expect a large baseline file. That is correct — it is the pre-existing debt you have agreed to
tolerate.

**Expect the baseline to grow when you first add suppressions.** Namespace exclusions and disabled
rules are fingerprinted from the suppression-independent finding set, precisely so a config toggle
cannot silently rewrite what the baseline records. The consequence is that excluding a namespace
*adds* its findings to the baseline on that run. This is a deliberate trade: correctness of the
baseline over convenience. Do not treat the growth as a bug, and do not "fix" it by regenerating the
baseline — you will lose the property that makes the baseline trustworthy.

### Keeping it honest

- **Never hand-edit the baseline.** Regenerate it deliberately, in its own commit, so the diff shows
  the debt you decided to accept.
- **Never regenerate to unblock a red build.** That is how a baseline becomes meaningless.
- **Review it like code.** It is a long list of accepted debt; diffs should be small and explicable.
- **Review shrink.** A baseline that only ever grows means nobody is fixing anything. Track the count
  per rule over time — the ledger gives you this for entropy, and the report gives it to you directly.

### Dirty working trees

Snipper warns when the tree is dirty and stamps the report with the commit SHA. For entropy this
matters: the baseline's commit is what "new" is measured against. A dirty tree means the SHA does not
describe the code being analysed, and the entropy status reports `DirtyWorkingTree` rather than
scoring. In CI the tree is usually clean; locally, commit or stash first.

---

## Gating on the report

### Fail on any finding

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json --baseline .snipper/baseline.json
if [ "$(jq '.findings | length' artifacts/snipper.json)" -gt 0 ]; then
  echo "Snipper reported new findings"
  jq -r '.findings[] | "\(.ruleId) \(.filePath):\(.lineNumber)"' artifacts/snipper.json
  exit 1
fi
```

### Fail on specific rules only

Usually what you want on a monorepo: gate on rules you trust absolutely, report the rest.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json --baseline .snipper/baseline.json

# Guaranteed-tier findings only: dead code, unread locals, unnecessary usings.
count=$(jq '[.findings[] | select(.certainty == "Guaranteed")] | length' artifacts/snipper.json)
[ "$count" -eq 0 ] || { jq -r '.findings[] | select(.certainty=="Guaranteed") | "\(.ruleId) \(.filePath):\(.lineNumber)"' artifacts/snipper.json; exit 1; }
```

### Fail per directory

Useful when one team owns a subtree and should not inherit everyone else's debt. Because
`filePath` in the report is relative to the target directory, a prefix match is stable across
machines.

```shell
# Only newly introduced findings under src/Payments may block.
count=$(jq '[.findings[] | select(.filePath | startswith("src/Payments/"))] | length' artifacts/snipper.json)
```

### Fail on a rule in a specific path

```shell
jq -e '[.findings[] | select(.ruleId == "SNP0032")] | length == 0' artifacts/snipper.json
```

SNP0032 is the one to gate on hardest if you enable clone drift — it is the only rule in the set that
reports *correctness* defects rather than smells, and it found a real bug in its own author's code on
the day it was written.

### PowerShell

```powershell
snipper .\MyMonorepo.slnx artifacts\snipper.json --baseline .snipper\baseline.json
$report = Get-Content artifacts\snipper.json -Raw | ConvertFrom-Json
$blocking = $report.findings | Where-Object { $_.certainty -in 'Guaranteed', 'High' }
if ($blocking) {
    $blocking | ForEach-Object { Write-Host "$($_.ruleId) $($_.filePath):$($_.lineNumber)" }
    exit 1
}
```

### Suppress the noisiest rules in the report rather than the run

If a rule is too noisy to gate on but you still want the data, filter in the pipeline instead of in
`snipper.json`. Keeping the finding in the report is more useful than removing it, because you can
still track the trend:

```shell
jq '{toolVersion, commitSha, findings: [.findings[] | select(.ruleId != "SNP0024")]}' \
  artifacts/snipper.json > artifacts/snipper.filtered.json
```

---

## The entropy budget

The entropy gate is the one thing Snipper can enforce by itself, and for a legacy monorepo it is the
most defensible gate available: it fails on the **rate** of newly introduced findings per kLOC, which
a team cannot bankrupt by fixing things slowly.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json \
  --baseline .snipper/baseline.json \
  --entropy-budget 1.5 \
  --entropy-ledger .snipper/entropy-ledger.json
```

- `--entropy-rate` **requires** `--baseline`. It errors rather than defaulting, because "new" needs a
  reference commit and a silent `0.00` would pass any budget.
- `--entropy-budget`, `--entropy-ledger` and `--entropy-min-lines` each imply `--entropy-rate`.
- A breach exits **3**, distinct from 1 (usage) and 2 (I/O), so a pipeline can tell a policy failure
  from a real one.
- `--entropy-ledger` is a committed JSON file. It enables **monthly aggregation**, so you get a trend
  rather than a single number, and only *scored* runs are recorded — a row with no rate would make the
  ledger look fuller than the data is.

Fail-open statuses are reported rather than fatal, and you should treat them as "no opinion", not
"pass":

| Status | Meaning |
| --- | --- |
| `BaselineSeeded` | First run; the baseline was created rather than compared. |
| `NoBaselineReference` | The baseline has no commit SHA to compare against. |
| `NotAGitRepository` | Outside a checkout — the CLI's other git features degrade, and so does this. |
| `DirtyWorkingTree` | The working tree does not match the commit SHA. |
| `UnchangedRange` | No code changed in range; nothing to score. |
| `BelowMinimumChange` | Fewer changed lines than `--entropy-min-lines`. |
| `Scored` | A real rate was computed. |

If you want the pipeline to notice that entropy is not being measured, assert on `status == "Scored"`
in your gating step rather than trusting exit 0.

Choosing a budget: run stage 1 for a few weeks and look at the monthly series in the ledger before
picking a number. A budget set below your current rate is a permanently red pipeline; set it slightly
above current rate and tighten as it improves.

---

## Suppression hygiene in CI

Suppressions are where static analysis goes to die. `--audit-suppressions` is the defence, and it is
worth wiring into CI permanently — it is cheap and it fails silently without attention.

```shell
snipper ./MyMonorepo.slnx artifacts/snipper.json \
  --baseline .snipper/baseline.json \
  --audit-suppressions
```

Two checks worth automating:

**Dead suppressions.** Every entry in `obsolete[]` is a suppression that matched nothing:

```shell
jq -e '.suppression.obsolete | length == 0' artifacts/snipper.json || {
  echo "::warning::Snipper suppressions matched nothing:"
  jq -r '.suppression.obsolete[] | "  \(.channel) \(.selector) — \(.reason)"' artifacts/snipper.json
}
```

This catches the most common failure mode: a path glob written with a repo-relative path, which
matches nothing at all and silently suppresses nothing. See [Path globs](usage.md#path-globs).

**Hidden debt.** `hiddenDebtPercent` tells you how much of the finding set is being suppressed:

```shell
jq -r '"hidden debt: \(.suppression.totals.hiddenDebtPercent)%"' artifacts/snipper.json
```

A percentage that climbs over time means the visible report is becoming increasingly flattering
relative to reality. Worth a quarterly review.

Note that the audit is report-only — **there is no audit exit code**. If you want it blocking you must
gate on the JSON as above.

---

## Layering configuration

There is exactly one `snipper.json` in effect: the **first one found walking up** from the target's
directory. Configs are not merged, and there is no `extends` mechanism.

This has two consequences that will bite a monorepo:

1. **A config in a subdirectory fully replaces the root config** for targets beneath it. Copy the root
   config rather than assuming inheritance.
2. **Changing the target path can change the rule set.** Analysing `src/Service/Service.csproj`
   instead of `MyMonorepo.slnx` may pick up a different config, with different rules and different
   exclusions. In CI, pin the target path explicitly and use the same one as developers do locally,
   or you will spend an afternoon chasing a discrepancy that is really a config discovery difference.

For per-team variation, use per-team config files and per-team target paths — or pass
`--exclude-namespaces` on the command line, which **unions** with whatever config was discovered and
is therefore the clean way to add a CI-specific exclusion without editing the committed config.

---

## Sharding a large monorepo

A monorepo large enough to need sharding will hit MSBuild workspace load time before it hits analysis
time. Splitting the target is the most effective scaling strategy available, and the analysis passes
are independent per target, so results are not degraded by splitting.

**Trade-offs:**

| Approach | Pros | Cons |
| --- | --- | --- |
| One run per solution folder | Parallelises across runners; smaller workspaces | Each run re-parses shared projects; duplicate findings on shared code; N reports to merge |
| One run per project | Maximum parallelism | Duplicate findings across projects; **loses cross-project rules** (SNP0003/4/11/12 need the full graph); N× workspace load |
| Whole solution, one runner | Correct and simple | Slowest |

**Do not shard per project if you care about the package- and project-graph rules.** SNP0003
(unreferenced package), SNP0004 (unreferenced project), SNP0011 (orphan project) and SNP0012
(redundant transitive package) all reason across project boundaries; per-project runs will either
lose them or contradict each other.

If you shard, shard by **solution folder**, keep every shard's target inside one config scope, and
deduplicate merged findings by `ruleId + filePath + lineNumber`. Expect overlap on shared code and
decide explicitly whether a finding in two shards is one problem or two.

---

## Pipeline recipes

### GitHub Actions

```yaml
name: snipper
on:
  pull_request:
  push:
    branches: [main]

jobs:
  snipper:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          # Clone-drift and entropy both need history and a clean tree.
          fetch-depth: 0

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore MyMonorepo.slnx

      - name: Analyse
        id: snipper
        run: |
          snipper ./MyMonorepo.slnx artifacts/snipper.sarif \
            --format sarif \
            --baseline .snipper/baseline.json \
            --certainty-tier high \
            --entropy-budget 1.5 \
            --entropy-ledger .snipper/entropy-ledger.json

      - name: Upload SARIF
        if: always()
        uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: artifacts/snipper.sarif

      - name: Summarise findings
        if: always()
        run: snipper ./MyMonorepo.slnx
```

Notes:

- `fetch-depth: 0` is **required** for clone drift and entropy. A shallow clone has no history and
  both features will degrade to silence, which looks like success.
- The SARIF upload needs `if: always()` or a failing run publishes nothing — the most common reason
  code-scanning results go missing.
- Publish a console summary with no output path as well; it is the fastest thing for a developer to
  read in the job log.

### Azure Pipelines

```yaml
steps:
  - checkout: self
    fetchDepth: 0

  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: '10.0.x'

  - script: dotnet restore MyMonorepo.slnx
    displayName: Restore

  - script: >
      snipper ./MyMonorepo.slnx $(Build.ArtifactStagingDirectory)/snipper.sarif
      --format sarif
      --baseline .snipper/baseline.json
      --certainty-tier high
    displayName: Snipper

  - task: PublishBuildArtifacts@1
    condition: always()
    inputs:
      PathtoPublish: $(Build.ArtifactStagingDirectory)

  - publish: $(Build.ArtifactStagingDirectory)
    artifact: snipper
    condition: always()
```

### GitLab CI

```yaml
snipper:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:10.0
  variables:
    GIT_DEPTH: 0
  script:
    - dotnet restore MyMonorepo.slnx
    - >
      snipper ./MyMonorepo.slnx artifacts/snipper.json
      --baseline .snipper/baseline.json
      --certainty-tier high
      --entropy-budget 1.5
      --entropy-ledger .snipper/entropy-ledger.json
    - |
      if [ "$(jq '.findings | length' artifacts/snipper.json)" -gt 0 ]; then
        jq -r '.findings[] | "\(.ruleId) \(.filePath):\(.lineNumber)"' artifacts/snipper.json
        exit 1
      fi
  artifacts:
    when: always
    paths: [artifacts/]
```

### Generic shell, tier + budget gate

```shell
#!/usr/bin/env bash
set -euo pipefail

target="${1:-./MyMonorepo.slnx}"
mkdir -p artifacts

# The entropy gate fails the build itself (exit 3).
snipper "$target" artifacts/snipper.json \
  --baseline .snipper/baseline.json \
  --certainty-tier high \
  --entropy-budget 1.5 \
  --entropy-ledger .snipper/entropy-ledger.json \
  --audit-suppressions

# The certainty gate is enforced here, because Snipper has no --fail-on.
jq -r '.findings[] | "\(.certainty)\t\(.ruleId)\t\(.filePath):\(.lineNumber)\t\(.title)"' \
  artifacts/snipper.json | column -t -s$'\t'

jq -e '[.findings[] | select(.certainty == "Guaranteed")] | length == 0' artifacts/snipper.json

# Warn, never block, on dead suppressions.
jq -r '.suppression.obsolete[]? | "warning: \(.channel) \(.selector) matched nothing"' \
  artifacts/snipper.json
```

### Local pre-commit / pre-push

Use the narrowest target and the highest tier — a pre-push hook on a monorepo should take seconds:

```shell
snipper ./src/MyService/MyService.csproj --certainty-tier guaranteed --format json
```

---

## Monorepo pitfalls

The things most likely to cost you time. Each is a real, verified behaviour, not a hypothetical.

1. **Findings never fail the build.** Exit 0 with findings. Gate on the report or use
   `--entropy-budget`.
2. **Shallow clones silently disable git features.** `--clone-drift` and entropy both degrade to
   silence outside full history. Set `fetch-depth: 0` / `GIT_DEPTH: 0`.
3. **Relative path globs match nothing.** Globs match absolute paths and are anchored. Write
   `**/src/Generated/**`, and verify with `--audit-suppressions`.
4. **Configs do not merge.** The nearest `snipper.json` wins outright. Narrowing the target path can
   change the rule set.
5. **Namespace exclusions cover all sub-namespaces.** Excluding `Company` silences everything.
   Exclude the most specific namespace that covers generated code.
6. **Namespace exclusions retain usage evidence.** You cannot hide a public API surface with one and
   still get unused-member findings for the rest of the repo.
7. **File-scope findings escape namespace exclusion.** Expect residual unused-`using` findings in
   excluded trees; add a path glob for those files. SNP0031 is the exception — use the `<global>`
   marker in the **config file** (the CLI flag rejects it).
8. **Namespace exclusions are not validated in the config file.** `9bad-name` and `has space` are
   accepted silently and match nothing. Only the CLI flag checks syntax.
9. **The baseline grows when you add suppressions.** By design. Do not regenerate it to hide the diff.
10. **Disabling some of a multi-rule analyser's rules does not disable the analyser.**
    `RedundancyAnalyser` owns five; disable all five or the runtime cost stays.
11. **Overlapping suppression channels make the audit lie.** A glob covering namespaced-away files
    reads as `suppressedCount: 0`.
12. **SARIF is pre-filtered.** Baseline filtering happens before serialisation, so the SARIF file
    contains no `baselineState` and cannot distinguish new from pre-existing findings.
13. **The audit has no exit code.** To make dead suppressions blocking, gate on the JSON.
14. **CPU count changes timings, not results.** Output is deterministic, but if you shard by target
    you must deduplicate across shards.

---

## See also

- [Usage guide](usage.md) — full option reference, filtering model, glob and namespace semantics.
- [Code architecture](code_architecture.md) - for contributors: how the tool loads code, runs analysers, and produces findings.
- [README](../README.md) — installation and the rule catalogue.
- [`plan_1_7_0.md`](plan_1_7_0.md) — the entropy gate, suppression audit, and clone-drift design
  rationale, with measurements.
