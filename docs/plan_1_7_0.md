# Plan — Snipper 1.7.0: Wave 4, story 4A (suppression / baseline integrity audit)

**Status: IMPLEMENTED for 4A and 4A-2** (2026-10-03). 395 tests green (was 353; +42). Dogfood back to the 1.6.3 baseline of 3 pre-existing findings, zero contributed by this change. Two of my own defects were caught by the dogfood run and fixed rather than suppressed — recorded in "What implementation changed". **The cost model below was wrong by roughly an order of magnitude and is corrected against measurement.**

**Objective:** answer the question a suppression audit exists to answer — *what fraction of our current "clean" status is suppression buying, and what is it hiding?* — for every suppression channel Snipper supports, and flag suppressions that have gone stale.

This is the first Wave 4 story and the first that adds **no new rules**. Wave 4 is a governance wave (`docs/Snipper-Feature-Parity-Roadmap.md`); adding a `FindingCategory` here would contradict that framing, so 4A is a **report**, not a finding.

Two corrections to the roadmap's 4A description, both established by measurement below: it is *not* "nothing new is analysed" (the two dominant channels require a shadow pass), and it does not cost ~2× analysis time (measured at ~10% marginal, because the expensive parts are memoized).

Repo constraints (standing): no dynamic; no exceptions for flow control; FrozenSet/FrozenDictionary; syntax pre-filters first; `ArgumentNullException.ThrowIfNull`; evidence ≠ findings; red phase first with fixture scenarios in NEW files; deterministic sorted output; dogfood stays 0 or every new own-finding is triaged. Zero new dependencies (locked decision: single self-contained binary). Read-only tenet holds — 4A writes only its own report section.

## Phase 0 — measured investigation (done, 2026-10-03)

### There are four suppression channels, and they do not all work the same way

| # | Channel | Config | Applied | Reaches baseline? | Counted today? |
|---|---|---|---|---|---|
| 1 | Certainty-tier filter | `--certainty-tier` | report time | no | yes (console) |
| 2 | Path globs | `exclude.paths` | report time | no | yes (console, aggregate) |
| 3 | Severity overrides | `rules.SNxxxx: "moderate"` | report time | no | **no** |
| 4 | Namespace exclusions | `exclude.namespaces` | **inside analysers** | **yes** | **no** |
| 5 | Disabled rules | `rules.SNxxxx: "off"` | **analyser removed pre-run** | **yes** | **no** |

Channels 2 and 3 go through `FindingFilter.Apply` (`src/Snipper/Cli/CliRunner.cs:315`). Channels 4 and 5 remove findings *before* they exist as objects, so they cannot be recovered from the normal finding set at all.

Channel 5 has a subtlety: `FindingFilter.IsAnalyserEnabled` (`src/Snipper/Cli/CliRunner.cs:255`) keeps an analyser if **any** of its rules is enabled. So `"SNP0006": "off"` removes nothing (that analyser also emits SNP0005) and is handled entirely by `FindingFilter` at report time. Only when a **sole-rule** analyser is fully disabled does pre-run removal actually happen — 9 of the 20 analysers are single-rule.

### Measured baseline churn (SampleApp fixture, `CoreLib.csproj`, 256 findings)

| Config | Baseline | Delta | Console accounting |
|---|---|---|---|
| none | 256 | — | — |
| `"SNP0024": "off"` (sole rule of `TighteningAnalyser`) | 226 | **−30** | none printed |
| `exclude.namespaces: ["CoreLib.Shadowing", "Excluded.Fake"]` | 237 | **−19** | none printed |
| `exclude.paths` / severity overrides | 256 | **0** | printed |

So the doc comments that promise "toggling config never churns the baseline" (`SnipperConfig.cs:9-11`, `FindingFilter.cs:7-8`, `CliRunner.cs:311-313`) are **accurate for the channels they name** (2 and 3) and silent about the ones that actually churn (4 and 5). Nothing warns the user.

### Impact is currently cosmetic — and becomes a gate failure the moment 4B lands

Checked rather than assumed: `WriteReport` returns `0` on success regardless of finding count (`CliRunner.cs:365`); only argument and IO errors return non-zero. **There is no gate.** New-vs-baseline findings are informational today and cannot break a build.

That bounds the severity correctly. Baseline churn today means report noise and a noisy diff on a committed baseline file — not a red build. But 4B introduces an enforced budget that *does* fail CI, and at that point a namespace exclusion added today becomes a wall of spurious "new" findings on untouched code the day someone removes it. **Fixing the churn is therefore a 4B prerequisite, not a 4A nicety** — see "Deferred".

### Feasibility notes that shaped the design

- All 24 namespace-exclusion call sites funnel through two `ExclusionEngine.IsNamespaceExcluded` overloads, so central instrumentation is cheap. But the symbol is skipped *before* a finding object is constructed, so **symbol counts are not finding counts** — instrumenting the exclusion would not produce an honest audit. Counting findings requires re-running the analysis with suppressions lifted.
- Consequently "accounting over data already in hand" holds only for channels 1–3. Channels 4 and 5 need a **shadow run**, which is why the audit is opt-in.

## Scope decision (maintainer-confirmed, **superseded — see the 4A-2 addendum below**)

**Audit only.** Add `--audit-suppressions`, which performs the shadow passes needed to count channels 4 and 5, and reports every channel. **Do not** change what the baseline file contains.

> **This decision was reversed on 2026-10-03.** The maintainer subsequently chose to fix the baseline churn (4A-2), which is exactly the alternative deferred in the next paragraph. The reasoning, and the approach that was considered and rejected, are in the addendum at the end of this document. Nothing about 4A itself changes — this section records what was decided *for 4A*, and why that was a defensible boundary at the time.

The alternative — computing fingerprints from a suppression-independent finding set so the baseline is churn-proof — is strictly better for gate stability, but it means a committed baseline accumulates fingerprints for code the team has explicitly excluded, and every existing user's baseline file would legitimately grow on first run. That is a semantics decision, not an implementation detail, so it is deferred rather than taken unilaterally.

## Design

### Cost model — predicted ~2×, measured ~10%

The original estimate was "worst case ~2× analysis time, opt-in". Measured on `Snipper.slnx`
(8.2 s analysis), timing the audit phase in-process:

| Config | Shadow pass | Audit phase | Marginal |
|---|---|---|---|
| none | skipped | **0.0 s** | 0% |
| `"SNP0024": "off"` (targeted, 1 sole-rule analyser) | yes | **0.5 s** | ~6% |
| `exclude.namespaces: ["Snipper.Models"]` (full re-run) | yes | **0.8 s** | ~10% |

**The 2× estimate was wrong, and the reason matters.** `AnalysisRunner.RunAsync` calls
`WarmCompilationsAsync`, and every solution-keyed index (`SolutionUsageIndex`,
`FrameworkEvidenceIndex`, `InheritanceGraph`, `ProjectPackageUsageCache`) is memoized in a
`ConditionalWeakTable`. The second pass therefore does not re-pay Roslyn compilation or
symbol-index construction — the genuinely expensive work is already done. What remains is
re-walking syntax trees, which is cheap.

Two consequences: the cost model in the roadmap needs no "this is expensive" warning, and
the passes could arguably be unconditional. They stay conditional anyway, because skipping
work that has no consumer is still worth doing and the flag remains a deliberate opt-in.

### Attribution must be exact, not approximate

A finding can be suppressed by *both* a disabled rule and a namespace. Splitting the channels from a single lifted pass would misattribute the overlap. So:

- `allFindings` — normal run (namespace exclusions applied, disabled analysers removed).
- `disabledRuleFindings` — **only** the removed analysers, namespace exclusions still applied. Precise and cheap.
- `unexcludedFindings` — all analysers, namespace exclusions lifted.

Then `unexcluded = allFindings ⊎ disabledRuleFindings ⊎ namespaceSuppressed`, and the namespace channel is the set difference matched by baseline fingerprint (rule + relative path + message — stable, line-number-free, already the project's identity notion). No overlap ambiguity, no extra pass.

Channels 2 and 3 are classified by re-running `FindingFilter`'s predicate over `allFindings` in classification mode rather than by diffing counts, so per-glob and per-rule attribution is exact.

### Report shape

New optional `suppression` object in the JSON report (omitted entirely when the flag is absent, so existing consumers are unaffected):

- `totals` — findings found, findings reported, findings suppressed, suppression ratio, and the headline: share of total debt that no human has ever looked at.
- `pathGlobs[]` — glob, suppressed count, breakdown by rule.
- `severityOverrides[]` — rule, from → to, affected count.
- `disabledRules[]` — rule, suppressed count.
- `namespaceExclusions[]` — namespace, suppressed count.
- `obsolete[]` — suppressions that no longer match anything.

`obsolete` carries a `confidence` field because the evidence differs by channel: an unmatched path glob or a rule override on a rule with zero findings is **certain**; a namespace exclusion that suppressed nothing is only **suspected**, because a namespace can legitimately be clean. Overstating this would make the audit cry wolf, which is how audits get ignored.

### Console

A compact table, because the JSON is for machines and the console is where a human decides whether to delete a suppression. Zero suppressions configured must say so in one line rather than printing an empty table.

## What implementation changed

Three things were wrong in the design above and were corrected against failing tests rather
than adjusted to fit them.

**1. A severity override can *cause* a suppression, and the tier floor must not take the blame.**
The plan assumed a downgrade only ever softened a finding. It is the opposite:
`CertaintyTier` is `Guaranteed=1 … Advisory=4` and the floor keeps `Certainty <= floor`, so a
`High → Advisory` override moves a finding *away* from a `Moderate` floor. High(2) clears
Moderate(3); Advisory(4) does not. Without a fix the audit reports "your tier floor hides 1
finding" when the user's override is what hid it, sending them to the wrong line of their
configuration. The drop is now credited to the override when the pre-override tier cleared the
floor and the post-override tier did not, and to the floor otherwise.

**2. Namespace existence has to be probed downward, not upward.** `ExclusionEngine` walks *up*
from a symbol's namespace looking for an excluded ancestor, so excluding `Acme.Domain` also
covers `Acme.Domain.Models`. The first probe walked up from the candidate, which only ever
finds exact declarations — it would have reported every live parent exclusion as stale the
moment its last direct child was renamed. Now each declared name is tested against the
candidate as a prefix.

**3. `FindingFilter.Apply` and the audit were separate implementations.** They would have
drifted, and the audit would then have confidently described behaviour the tool does not
have. `Apply` now delegates to a shared `Classify`, and a matrix test asserts the two agree
across every channel and precedence combination.

**Dogfood caught two of my own defects**, both fixed rather than suppressed: an unnecessary
`using System.Text.Json;` I added to `JsonReportSerializerContext`, and an unused
`droppedByPathGlob` parameter on `BuildObsolete`. Snipper found its author's new code on the
first audit run, which is the tenet working as intended.

## Verified behaviour (SampleApp `CoreLib.csproj`, 256 findings unconfigured)

Config: `SNP0024: off`, `SNP0018: advisory`, `exclude.namespaces: [CoreLib.Shadowing,
Does.Not.Exist]`, `exclude.paths: [**/NoSuchDir/**]`.

| Channel | Selector | Effect |
|---|---|---|
| DisabledRule | `SNP0024` | 30 hidden — reproduces the Phase 0 `-30` churn exactly |
| NamespaceExclusion | `CoreLib.Shadowing, Does.Not.Exist` | 5 hidden (aggregate) |
| SeverityOverride | `SNP0018` | 5 downgraded (`was Highx3, Moderatex2`) |
| PathGlob | `**/NoSuchDir/**` | 0 hidden, flagged **certain** stale (matches no file) |
| NamespaceExclusion | `Does.Not.Exist` | flagged **certain** stale (not declared) |

`findingsAnalysed 221 + findingsHiddenByShadow 35 = 256` — the audit reconstructs the
unconfigured debt exactly, which is the invariant that proves the shadow passes are correct.
`hiddenDebtPercent` = 14.

The most striking result is from Snipper's own repository: with `"SNP0024": "off"` the audit
reports **33% of analysed debt hidden** — a single suppressed `Advisory` finding is a third of
the repo's entire remaining finding set. That is exactly the ratio the audit exists to make
visible, on the codebase of the person who wrote it.

## Honest limits

- Namespace exclusions are reported as one aggregate entry. Per-namespace counts would need
  one shadow pass per namespace, and a finding's containing namespace is not recoverable from
  the finding itself. The `Detail` field says so rather than implying a split exists.
- A disabled rule with zero shadow findings is reported as stale-*candidate* (suspected), not
  proof of obsolescence — a rule can be genuinely clean. Only a path glob matching no file on
  disk, or an exclusion for an undeclared namespace, is reported as certain.
- Namespace existence is checked against namespaces declared in the analysed solution, so an
  exclusion for code in an unvisited project may be reported as suspected-stale.
- `--baseline` interaction is unchanged: the audit reports configuration suppression and does
  not count baseline-accepted findings as suppressed. Those are a separate, deliberate
  decision and 4B is where they get a rate.
- SARIF output is unchanged; the audit lands in the JSON report only.

## Exit criteria — status

| # | Criterion | Status |
|---|---|---|
| 1 | Full channel breakdown on SampleApp and on Snipper's own solution | **met** — table above, plus self-audit at 33% |
| 2 | The `-30` and `-19` churn deltas reproduced and attributed to the right channels | **met** — `-30` reproduced exactly; the `-19` case splits to 5 + 0 with a stale flag on the phantom namespace |
| 3 | Without the flag, output is byte-identical and no shadow pass runs | **met** — `TryGetProperty("suppression")` false; `shadowAnalysisRan` false; 0.0 s audit |
| 4 | Full suite green; dogfood clean or triaged | **met** — 385 pass; dogfood at the 3 pre-existing findings |
| 5 | Misleading contract comments corrected | **met** — `SnipperConfig`, `FindingFilter`, `CliRunner` all now state which channels churn the baseline |

## Deferred (not in this increment)

- **4A-2 — churn-proof baseline.** See the addendum below. **Maintainer decision taken
  2026-10-03: fix it.**
- **4B — entropy rate ledger.** Introduces the first real gate. Was blocked on 4A-2; that
  dependency is now discharged.
- **4C — clone drift detection.**
- **Per-namespace attribution** for the namespace channel, if the aggregate proves too coarse.

---

# Addendum — 4A-2: churn-proof baseline (DECIDED and IMPLEMENTED 2026-10-03)

**Status: IMPLEMENTED.** 395 tests green (was 385; +10). Churn measured at **0 across every
suppression channel**, and removing a suppression now resurfaces **0** new findings instead of
30 and 19. Red-phase verified: with the fix disabled, 6 of the 10 new tests fail.

**One defect found by dogfood, not by the tests:** an unused `using Snipper.Models;` in the new
test file — the second time this session Snipper flagged a using directive I had added. Fixed.

**One test-infrastructure defect found by the full suite, not by the new tests in isolation:**
`BaselineChurnShould` and `CliRunnerShould` each call `CliRunner.RunAsync`, and Spectre.Console's
`Status` is **process-wide exclusive** — two runs cannot overlap in one test host. Running
`BaselineChurnShould` alone passed; running it alongside `CliRunnerShould` failed 3 tests with
`InvalidOperationException: Trying to run one or more interactive functions concurrently`. Fixed
by putting both classes in a shared `[Collection("CliRuns")]`, matching the existing
`[Collection("SampleSolution")]` convention. Honest cost: suite wall-clock 20s → 36s, verified
stable across three consecutive runs.

## The decision

Maintainer chose to fix the churn. Two approaches were available; both make config toggles
unable to rewrite a baseline, and they differ in what they cost.

| | **(A) Baseline = suppression-independent** *(chosen)* | (B) Baseline = union of known and visible |
|---|---|---|
| Rule | Fingerprint every finding that **exists**, ignoring config. | Fingerprint visible findings, and **never remove** a fingerprint merely because it became suppressed. |
| Baseline grows on first run for excluded code? | **Yes** | No |
| Removing an exclusion surfaces that debt as new? | No — already accepted | Yes |
| Complexity | Simple; one source of truth | Two rules; must distinguish "suppressed" from "genuinely fixed" |

(B) is cheaper on paper and was seriously considered. It was rejected because (A) is what
Snipper **already does** for `exclude.paths`, severity overrides and `--certainty-tier`:
those apply after fingerprinting, so findings under an excluded path have always been in the
baseline. (A) extends that existing behaviour to the two channels that were inconsistent, and
changes nothing else. (B) would introduce a *new* semantic — "removing an exclusion reveals
debt" — which is a defensible design but is a behaviour change beyond the bug being fixed.

Accepted cost of (A), stated plainly: **a user's baseline file legitimately grows the first
time they run a version with this fix**, because it will contain fingerprints for code they
have excluded. That is the price of a baseline that means the same thing regardless of config.

## Design

The baseline needs the set of findings that exists with **no** suppression. 4A already
computes exactly that set for its namespace shadow pass — all analysers, `AnalysisExclusions.None`.
So this is a reuse, not a second mechanism:

```
needsSuppressionIndependentPass = baselinePath is not null
                                  && (excludedNamespaces.Count > 0
                                      || removedByDisabledRules.Count > 0)
```

Three properties fall out of that condition:

1. **No extra pass without a baseline.** Suppressions that nobody has baselined cannot churn
   a baseline.
2. **No extra pass for the churn-free channels.** `exclude.paths`, severity overrides and
   `--certainty-tier` apply after fingerprinting and are excluded from the condition by
   construction, so a user relying only on those never pays.
3. **No extra pass with no suppression config.** `allFindings` *is* the suppression-independent
   set, and the code says so rather than re-running to discover it.

The pass is shared with the audit when both are active: one shadow run, two consumers.

## Measured cost

On `Snipper.slnx` (8.2 s analysis), the full unexcluded pass costs **0.8 s (~10%)** —
the same figure 4A measured, because it is the same computation, and because
`WarmCompilationsAsync` plus the `ConditionalWeakTable`-memoized symbol indexes mean the second
pass does not re-pay compilation.

## Invariant

The property worth testing is not "the code runs" but **churn invariance**: for every
suppression channel, toggling it must leave the baseline fingerprint set unchanged. That is
tested directly, per channel, rather than inferred from the implementation.

## Measured result

Baseline fingerprint count on SampleApp `CoreLib.csproj`, every configuration:

| Configuration | Before 4A-2 | After 4A-2 |
|---|---|---|
| no config | 256 | 256 |
| `"SNP0024": "off"` (sole-rule analyser) | **226** (−30) | **256** |
| `exclude.namespaces: [CoreLib.Shadowing, Excluded.Fake]` | **237** (−19) | **256** |
| `exclude.paths: [**/Generated/**]` | 256 | 256 |
| `SNP0018: advisory` | 256 | 256 |
| all four channels combined | churned | **256** |
| back to no config | churned | **256** |

Spread across all seven configurations: **30 before, 0 after.**

And the symptom that motivated the fix — add a suppression, then remove it, on untouched code:

| Step | Before | After |
|---|---|---|
| seed baseline, no config | NEW=256 | NEW=256 |
| add `"SNP0024": "off"` (hides 30) | NEW=226 | NEW=226 |
| **remove it again** | **NEW=30** | **NEW=0** |
| re-add namespace exclusion (hides 5) | — | NEW=0 |
| **remove namespace exclusion** | **NEW=19** | **NEW=0** |

Suppression itself is unchanged: with the exclusion in place the report still shows 226 and 251
findings respectively. Only the baseline's dependence on configuration is gone.

## Exit criteria — status

| # | Criterion | Status |
|---|---|---|
| 1 | Baseline identical across all five channels | **met** — spread 0 over 7 configurations |
| 2 | Removing a suppression resurfaces nothing as new | **met** — 0, was 30 and 19 |
| 3 | Suppression still hides findings from the report | **met** — 226 and 251 visible while baselined |
| 4 | No extra analysis pass without a baseline, or for churn-free channels | **met** — `RequiresSuppressionIndependentFingerprints` unit-tested on all four combinations |
| 5 | Tests fail without the fix | **met** — 6 of 10 fail with the fix disabled |
| 6 | Full suite green and stable | **met** — 395 pass, three consecutive clean runs |



---

# 4B — Entropy rate ledger (Phase 0 + design, 2026-10-03)

**Status: DESIGN SETTLED, implementation starting.** Decisions below were maintainer-confirmed
after seeing the Phase 0 measurements. Nothing here is implemented yet; measured results are
recorded in "Measured result" once the code exists.

**Objective:** answer the question a maintainer is actually asked in a quarterly review — *are
we controlling entropy, or just not looking at the stock?* — as a churn-normalized **rate** with
a budget that can fail CI. No competitor in `competitive-analysis.md` §6.2 ships a rate; they
ship stock (SonarQube) or delta (CodeScene Goals).

Wave 4's first story with **no new findings and an exit code that can be non-zero for a reason
unrelated to an error.**

## Phase 0 — measured investigation

### The denominator has no default, and the obvious one is incoherent

`new_findings / kLOC changed` needs a diff against *something*. The roadmap specifies "per PR".
Measured on this repository's 39 commits, holding code quality constant at **exactly one new
finding**:

| Change size | Rate | 
|---|---|
| 5 lines | **200.0** /kLOC |
| 20 lines | 50.0 /kLOC |
| 50 lines | 20.0 /kLOC |
| 100 lines | 10.0 /kLOC |
| 251 lines (repo median) | 4.0 /kLOC |
| 2000 lines | **0.5** /kLOC |

A **400x spread** driven entirely by commit size. A fixed budget is therefore not a coherent
gate: a budget of 5/kLOC passes a 251-line PR carrying one finding (3.98) and fails a 5-line PR
carrying one finding (200). **The gate would be measuring pull-request size, not entropy.**

Change-size distribution over the same 39 commits:

| Threshold | Commits below | Share |
|---|---|---|
| 10 lines | 5 | 13% |
| 25 lines | 6 | 15% |
| 50 lines | 7 | 18% |
| 100 lines | 13 | 33% |
| 200 lines | 16 | 41% |

Median commit is 251 changed lines (~0.25 kLOC), so a single finding in a *typical* commit
already reads as ~4/kLOC.

### The fix: bind the denominator to the numerator, then floor it

The numerator is `newFindings` — findings absent from the recorded baseline. So the honest
denominator is **lines changed over exactly the range that baseline was taken over**. That range
is knowable if the baseline records the commit it was stamped at, which is a one-field,
backward-compatible schema addition (`BaselineFile.CommitSha`, optional).

This is strictly better than a calendar window:

- numerator and denominator provably cover the **same** range, so the ratio cannot be
  mis-specified by a mismatch between "since last run" and "last N days";
- no window parameter, and no `--entropy-window` flag;
- works on the first run, because the range comes from git rather than from accumulated history.

The small-denominator pathology is then handled by an explicit floor rather than by a window:
below `--entropy-min-lines` (default 50) the rate is computed, reported, and explicitly marked
**not scored**, and the gate does not engage. Long-run stability comes from the per-month
aggregate, which has a large denominator by construction.

### Git access already exists — and the docs are stale about it

`GitMetadata` has shelled out to git since **1.6.1** (FP-5) for `commitSha` and the dirty flag.
So the roadmap's and `competitive-analysis.md`'s claim that 4C "requires history access -> first
Snipper feature that reads git" is **already false**. 4B is the second, not the first. Both docs
need correcting.

Measured degradation cases this repository actually exhibits, both of which the design must
survive rather than crash on:

- `origin/HEAD` is **unset**, so default-branch auto-detection via `origin/HEAD` fails here.
- `git log --numstat` reports binary files as `-`, which must not be parsed as a number.

### Numerator caveats established by reading the code, not assumed

- **Seeding a baseline is not 256 new findings.** On a first run the baseline file does not
  exist, `known` is empty, and every finding classifies as new. Reporting that as a rate would be
  nonsense, so `BaselineSeeded` is a distinct non-scored status.
- **Baseline-accepted findings are not counted** (this was the open question carried from the
  roadmap). They are known, already-accepted debt; counting them would make the rate a measure of
  repo size. Because Snipper *rewrites* the baseline on every run (`CliRunner.cs:447`), the
  numerator is genuinely a per-run delta, not a cumulative one — which also closes the obvious
  gaming path of "adopt a baseline and never refresh it".
- **A vanished fingerprint is not necessarily a fix.** `resolvedFindings` is
  `known - current`, which also counts findings whose file was deleted or whose code was
  restructured into a different message. Reported as a count, not as a claim that anyone fixed
  anything.
- **There is no gate today.** `WriteReport` returns `0` on success regardless of finding count;
  only argument (1) and IO (2) errors return non-zero. Exit code **3** is therefore free for
  "budget exceeded", which lets CI distinguish a snipper failure from a policy failure.

## Decisions (maintainer-confirmed 2026-10-03)

| # | Question | Decision |
|---|---|---|
| 1 | How much gating ships in 1.7.0? | **Opt-in gate, no default budget.** The gate engages only when a budget is passed explicitly, so no existing build can break on upgrade. |
| 2 | Normalize by what? | **Range for the gate, with a floor; per-month for the stable trend.** Per-PR rate is reported for visibility but marked not-scored below the floor. |
| 3 | How much aggregation? | **Per-run + per-month.** Per-team CODEOWNERS attribution is deferred — it needs CODEOWNERS parsing plus per-finding ownership mapping, is M-L effort in its own right, and this repository has no CODEOWNERS to test against. |

Baseline-accepted findings counting was **not** escalated as a question, because reading the code
settled it (see the numerator caveats above).

## Design

### CLI surface

| Flag | Effect |
|---|---|
| `--entropy-rate` | Compute and report the rate. No gate, no ledger write. |
| `--entropy-budget <per-kloc>` | Engages the gate. Implies `--entropy-rate`. |
| `--entropy-ledger <path>` | Append this run to the committed JSON series. Implies `--entropy-rate`. |
| `--entropy-min-lines <n>` | Scoring floor, default 50. |

`--entropy-rate` **requires `--baseline`**, because "new" is defined relative to a recorded
baseline. Without one the request is a usage error (exit 1), not a silent zero — the same
treatment `--baseline` itself gets.

### Statuses — every non-scored case is named, not silently zero

A rate that quietly reports `0.00` when it could not be measured is worse than no rate, because
`0.00` passes a budget. Each of these is a distinct status and **none of them engages the gate**:

`Scored`, `BaselineSeeded`, `NoBaselineReference`, `BelowMinimumChange`, `UnchangedRange`,
`NotAGitRepository`, `DirtyWorkingTree`.

`DirtyWorkingTree` matters more than it looks: `commitSha` plus the existing dirty flag mean the
analysed bytes are not the commit, so a diff-derived denominator would be measuring a different
tree than the one that produced the numerator.

### Fail-open, deliberately

Every unmeasurable path degrades to "not scored, gate silent, reason printed". This is the
opposite of a normal gate and is intentional: a metric that can break a build when git is
missing, the tree is dirty, or a baseline predates this feature trains people to disable it.

### Ledger

Committed JSON, per locked decision 2a. Canonicalised **sorted by commit SHA** rather than
chronologically, so a re-run of the same commit produces a byte-identical file and PR diffs stay
reviewable; consumers sort by `recordedAtUtc` for a series. Append is
**replace-or-insert keyed on commit SHA**, so CI re-runs on one commit cannot duplicate entries.

Concurrency: because the gate never writes, a PR run computes without mutating shared state, and
only an explicitly requested `--entropy-ledger` write touches the file. Two branches writing the
ledger concurrently can still conflict; documented recommendation is to write from the default
branch only.

### Report shape

New optional `entropyRate` object, omitted entirely when not requested, so existing consumers are
unaffected: status, reason, numerator, resolved count, lines changed, baseline and head SHAs,
rate, floor, whether the gate engaged, and `monthly[]` aggregated from the ledger.

## Implementation (done 2026-10-03)

**Status: IMPLEMENTED.** 438 tests green (was 395; +43 in `EntropyRateShould.cs`). Dogfood back to
the 3 pre-existing findings, zero contributed. Red phase verified by three mutations of the
fail-open contract, each caught.

New: `EntropyRate.cs`, `EntropyLedger.cs`, `EntropyRateShould.cs`. Changed: `CliRunner.cs`,
`GitMetadata.cs`, `BaselineService.cs`, `JsonReportSerializerContext.cs`, `BaselineServiceShould.cs`.

### Measured cost

On `Snipper.slnx`, with the same analysis in both cases:

| Run | Wall clock |
|---|---|
| `--baseline` only | 7.0 s |
| `--baseline --entropy-rate` | 6.8 s |

**No measurable overhead**, and none is expected: the numerator is already computed for the
baseline and the denominator is two git calls (`rev-parse`, `status`, `diff --numstat`). There
is no second analysis pass, which is what makes 4B cheaper than 4A. The figure is inside run-to-
run noise, which is the honest way to report it rather than claiming a speedup.

### What the design forced that the plan did not predict

**A clean tree is now an operational requirement, and it is stricter than it looks.**
`git status --porcelain` reports *untracked* files, so a `snipper.baseline.json` that has not
been committed makes the tree dirty, which makes the rate permanently unscored. The same applies
to the ledger and to `bin/`/`obj/` if they are not ignored. In practice the baseline must be
committed — which is what locked decision 2a wanted anyway — and build output must be ignored, as
every .NET repo already does. This surfaced as three failing tests before it was understood; the
fixture now commits a `.gitignore` mirroring a real repository, and there is an end-to-end
`Refuse_To_Score_A_Dirty_Tree_End_To_End` test so the requirement is documented by a test rather
than by a comment.

### Dogfood found four defects in this change, all fixed rather than suppressed

| Rule | Site | Defect |
|---|---|---|
| SNP0006 | `BaselineService.cs` | `Load` became dead code once the caller moved to the record-returning overload. Deleted, and its two test callers updated. |
| SNP0019 | `EntropyLedger.cs:5` | Unnecessary `using System.Text.Json.Serialization;`. |
| SNP0024 | `EntropyLedger.cs:67` | `CurrentVersion` was public but only used inside its own type. Made private. |
| SNP0019 | `BaselineService.cs:3` | **Introduced by the fix above** — removing `ToFrozenSet` orphaned `using System.Collections.Frozen`. |

The third unnecessary-using finding in this session, all three of them mine. The last one is the
interesting one: fixing a dead-code finding created a new unused-using finding, which is a decent
argument for running the audit after each fix rather than once at the end.

### Red-phase verification

Each mutation was applied to the shipped source, tested, then reverted:

| Mutation | Caught by |
|---|---|
| Drop the `IsScored` guard from `Exceeds` | `Never_Engage_The_Gate_For_An_Unscored_Status(BelowMinimumChange)` |
| Defeat the scoring floor | `Refuse_To_Score_Below_The_Minimum_Change` + the theory |
| Score a seeded baseline | `Refuse_To_Score_When_The_Baseline_Was_Just_Seeded` + the theory + the CLI test |

Only the `BelowMinimumChange` case trips the first mutation, because it is the one unscored status
that still carries a non-null rate. That is correct — the `IsScored` guard is a second line of
defence behind the null rate, not the only one.

## Honest limits

- **The dirty-tree rule is strict by design and will refuse runs that a user may consider
  legitimate** — a mid-edit local run, or a repository that has not yet committed its baseline.
  Loosening it to ignore untracked files was rejected: an untracked `.cs` file changes findings,
  so it cannot be ignored safely.
- **The rate is only as stable as the range it covers.** Binding the denominator to the baseline
  commit removes the mismatch between numerator and denominator, but a single small commit still
  yields a noisy figure. That is what the floor and the monthly aggregate are for; neither
  removes the underlying sensitivity.
- **`resolvedFindings` is a count, not a repair record.** A fingerprint also vanishes when its
  file is deleted or its message is rewritten.
- **`linesChanged` excludes binary files** and honours `exclude.paths`, so the denominator covers
  a slightly narrower set than raw `git diff --numstat`. The two halves of the ratio do describe
  the same set of code, which is the property that matters.
- **The window options in Phase 0 are gone.** Because the range is anchored to the baseline
  commit, `--entropy-window` was dropped rather than shipped as a second, conflicting way to
  choose a denominator.
- **`git log --numstat` window behaviour was not validated beyond 30 days** — this repository's
  entire history is 18 days old, so all three candidate windows returned the same figure.
- **No per-team attribution.** Deferred by decision; CODEOWNERS parsing plus per-finding ownership
  is a separate story.
- **The ledger has no concurrency control.** Two branches writing it in the same PR will conflict.
  The gate deliberately never writes, which contains the blast radius to opt-in ledger runs.

## Exit criteria — status

| # | Criterion | Status |
|---|---|---|
| 1 | Rate computed from a real change range on a real repository | **met** — end-to-end test plus the self-run above |
| 2 | Gate cannot fire on an unmeasured rate | **met** — six-status theory, plus `BelowMinimumChange` carries a rate of 19,998 and still stays silent |
| 3 | Gate is opt-in and no default budget can break an existing build | **met** — `Not_Engage_The_Gate_Without_A_Budget`; no budget is ever applied unless passed |
| 4 | Budget failure distinguishable from a snipper failure | **met** — exit 3, distinct from 1 (usage) and 2 (IO) |
| 5 | Ledger is reviewable and idempotent | **met** — replace-or-insert by commit, sorted by commit for byte-stable diffs |
| 6 | No measurable analysis overhead | **met** — 7.0 s vs 6.8 s; no second pass |
| 7 | Section omitted entirely when not requested | **met** — `Omit_The_Entropy_Section_Entirely_Without_The_Flag` |
| 8 | Full suite green and stable; dogfood clean or triaged | **met** — 438 pass; dogfood at the 3 pre-existing findings |
| 9 | Every unmeasurable path named rather than reported as zero | **met** — 7 statuses, each with a printed reason |

## Deferred

- **Per-team entropy via CODEOWNERS.** Deferred by decision 3. Needs CODEOWNERS parsing plus
  mapping each finding to an owner, and this repository has no CODEOWNERS to test against.
- **A default budget.** Deliberately not shipped. Any default would fail existing builds on
  upgrade, and the ledger has no history yet from which to derive a defensible one.
- **Cross-repository comparison.** The ledger format supports it; nothing consumes it yet.
