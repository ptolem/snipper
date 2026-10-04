# Plan — Snipper 1.7.0: Wave 4 (4A, 4A-2, 4B, 4C) + the perf/correctness increment

**Status: ALL FOUR STORIES IMPLEMENTED, UNRELEASED** (2026-10-04). **527 tests green** (353 at 1.6.3 → 469 at 4C → 520 after the `CliRunner` refactor → 527 with the perf increment). Dogfood at the 1.6.3 baseline of 3 pre-existing findings, **zero contributed** by any of this work. Eleven of my own defects were caught by dogfood runs, mutation testing, or A/B comparison and fixed rather than suppressed — recorded per story below.

**`<Version>` is still `1.6.3`, the work is uncommitted, and nothing is published.** Per this repo's convention (`plan_1_6_3.md` reserves "SHIPPED" for after pack + `dotnet tool update`), 1.7.0 is not shipped. See [What is left for 1.7.0](#what-is-left-for-170) for the release gate.

**Corrections to the original roadmap design, all established by measurement:** 4A was budgeted at ~2× analysis time and measured ~10%; 4B's per-pull-request denominator was measured to be dominated by commit size (a 400× spread) and replaced; 4C's present-tense framing was not expressible, because a copy that receives a fix *leaves* the clone set. **Two of my own performance hypotheses were also measured and rejected** — Server GC and disabling tiered JIT — see the perf section.

**Objective of 4A–4C:** answer the question a suppression audit exists to answer — *what fraction of our current "clean" status is suppression buying, and what is it hiding?* — for every suppression channel Snipper supports, flag stale suppressions, measure whether new-finding entropy is actually controlled, and detect clone sets that were fixed inconsistently.

This is a governance wave (`docs/Snipper-Feature-Parity-Roadmap.md`), so 4A is a **report**, not a finding. 4C is the one story that finds **correctness defects** rather than smells.

Two corrections to the roadmap's 4A description, both established by measurement below: it is *not* "nothing new is analysed" (the two dominant channels require a shadow pass), and it does not cost ~2× analysis time.

Repo constraints (standing): no dynamic; no exceptions for flow control; FrozenSet/FrozenDictionary; syntax pre-filters first; `ArgumentNullException.ThrowIfNull`; evidence ≠ findings; red phase first with fixture scenarios in NEW files; deterministic sorted output; dogfood stays 0 or every new own-finding is triaged. Zero new dependencies (locked decision: single self-contained binary). Read-only tenet holds — 4A writes only its own report section, 4C reads git history read-only and degrades gracefully outside a checkout.

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

# 4B — Entropy rate ledger (DESIGN → IMPLEMENTED, 2026-10-03)

**Status: IMPLEMENTED** (2026-10-03). 438 tests green, +43 in `EntropyRateShould.cs`. Red phase verified by three mutations of the fail-open contract, each caught. No measurable analysis overhead (7.0s vs 6.8s). Dogfood found four defects in this change, all fixed. Implementation, measured results, honest limits and exit criteria are below; the Phase 0 investigation that produced the design is retained because the correction it forced is the most important thing here.

Decisions below were maintainer-confirmed after seeing the Phase 0 measurements.

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

---

# 4C — Clone drift / inconsistent-fix detection (Phase 0 + design, 2026-10-03)

**Status: DESIGN SETTLED, implementation starting.** New rule **SNP0032**. Detect the temporal
one-sided fix: a commit that added a defensive construct to one copy of a clone set without
touching its siblings. Decisions confirmed by the maintainer after the Phase 0 findings below.

**Objective:** the missing half of SNP0031. Per `competitive-analysis.md` §6.1, this is the only
item on the Wave 4 list that finds **correctness defects** rather than smells, and the only one
whose nearest neighbour concedes it is unsolved — PMD CPD's own documentation says automated tools
cannot be entrusted with keeping duplicates in sync.

## Phase 0 — measured investigation

### The cost is two git calls, not one per clone set

The obvious implementation — `git log` per member path — is unusable. Measured on this repository:

| Operation | Cost |
|---|---|
| `git log --format=%H -- <path>` | **~90 ms** per call (process spawn) |
| `git show --unified=0 <sha> -- <path>` | ~167 ms per call |

At ~90 ms a call, per-member history lookups would dominate a run that already costs ~53 s on a
3,000-file monorepo. Batching changes the shape entirely:

| Batched form | Cost | Output |
|---|---|---|
| `git log --no-merges --format=%H --name-only` (whole repo, 1 call) | 100 ms | 19.2 KB |
| `git log -p --no-walk --unified=0` for 30 commits (1 call) | 174 ms | 954.9 KB |
| `git log -p --no-merges --unified=0 -- <4 member paths>` (1 call) | **94 ms** | **101.2 KB** |

So 4C's git cost is **O(1) process spawns** — one path-filtered patch call per *batch of member
paths*, not per member. Path filtering at the git level is what makes this work: 9x less output
than the unfiltered equivalent. Paths are chunked per invocation and commits are capped, with
truncation reported rather than silent.

### The detection problem, which is not a detail

**SNP0031 builds clone sets from current text, so its members are token-identical *now*.** A member
that received a fix is therefore *no longer identical* and has already dropped out of the set.
"One copy of a clone set received a fix the others did not" is not expressible in the present tense
at all — it only exists historically.

This rules out the naive reading of the roadmap's "identify copies that received a change the others
did not". Three framings were considered:

| Framing | Catches | Cost |
|---|---|---|
| **Temporal one-sided fix** *(chosen)* | The commit where a defensive fix landed on one member only. Reports which siblings still lack it today. | Flat — the plumbing measured above |
| Clone-set breakup | Regions identical at an earlier commit but different now. Catches "A fixed permanently, B stale permanently". | Scales with history depth; needs similarity search over history |
| Current-text clone sets + past commits | Nothing — a member that diverged is not in the set, so there is no set to attribute the divergence to. | n/a |

Clone-set breakup is the more satisfying signal and is the natural follow-up; it does not fit the
size 4C was budgeted at. The chosen framing has one honest limitation, recorded under "Honest limits":
**if the fix is still in place, the copies are no longer a clone set and 4C reports nothing about
them today.** It reports the *event*, not the standing state.

### The coordinate problem, and why the chosen framing dissolves it

A member's clone region is expressed in HEAD line numbers, but a patch's coordinates are the
*parent's*. Reconciling the two across a long history is where this design would otherwise go wrong.

The chosen framing sidesteps it entirely by **searching for the siblings' token stream in the
commit's pre-image**. If the fix landed on member `M` at commit `C`, then `C^:M` still contains the
original clone verbatim — because the siblings still hold it. So:

1. Take the siblings' current token stream for the region as the **search key**.
2. Find it inside `tokenize(git show C^:M)`. If absent, `M` was not a clone of them before `C`, so
   no drift claim is made.
3. The search yields pre-image line numbers, which are **exactly the coordinate system the patch's
   `-` side already uses**. No reconciliation needed.

The same search doubles as the High-tier proof required by §6.1 ("the other copies are byte-identical
to the pre-change text"): if the key is found in the pre-image, they provably were.

### Fixture scenario, verified end to end before designing against it

Seeded `SampleApp`, then applied a defensive fix to **one** member of a 2-member set:

```
fix commit 7838644  "fix: guard empty label in MirroredPair.Render"
  App/CloneFixturesMirrored.cs   @@ -11,0 +12,5 @@   (guard inserted)
  CoreLib/CloneFixtures.cs       (untouched)
```

`git show 7838644^:App/CloneFixturesMirrored.cs` returns 88 lines with no guard; the working copy has
5 guard lines at 12-16. The signal is present, unambiguous, and reachable with the plumbing above.

## Design

### Output

A finding, not a report section — the maintainer's decision, and the right one for a correctness
defect: a report section cannot fail CI and cannot appear in SARIF. New rule **SNP0032**,
`FindingCategory` reused from the duplicate family so tiering and baselines behave like any other rule.

### Certainty

| Tier | Condition |
|---|---|
| `High` | One-sided **and** defensive-fix-shaped **and** at least one sibling still lacks the construct today. |
| `Advisory` | One-sided and fix-shaped but every sibling now has it (resolved), or one-sided with no fix-shaped marker. |

The third clause is what makes the finding actionable rather than historical trivia: it is the
difference between "this codebase shipped inconsistent code" and "this happened once and was fixed".

### Defensive-fix markers

Conservative and token-based, per §6.1's `null`/guard/`try`/`catch`/exception/bounds list:
`null`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `??`, `?.`, `try`, `catch`, `finally`, `throw`,
`ArgumentNullException`, `ArgumentOutOfRangeException`, `NullReferenceException`, `Length`, `Count`.
One hit promotes a change to fix-shaped. Anything looser would manufacture High findings, which is
how a drift rule gets switched off.

### CLI

`--clone-drift`. **Implies `--duplicate-detection`** rather than erroring, because the two share one
shingling pass and asking users to pass two flags for one feature would be user-hostile. Outside a git
repository, or with no usable history, 4C emits nothing and says so — never a crash, consistent with
`GitMetadata`'s degrade-never-fail contract and the read-only tenet.

## Honest limits

- **A fix still in place is invisible to 4C today.** The copies have stopped being clones, so there
  is no set to attribute the divergence to. This is the cost of the chosen framing and is the reason
  clone-set breakup is the recommended follow-up.
- **Temporal, not standing.** The finding describes a commit. It reports which siblings still lack
  the construct now, but it is not proof that the missing construct was ever a live defect.
- **Commit cap.** History is bounded and chunked; a truncated walk is reported in the finding, never
  silently shortened.
- **"One copy changed" remains a heuristic.** §6.1's caveat applies unchanged: the High tier is
  defensible, the Advisory tier is a prompt to look, not a verdict.
- **`--merge` commits are skipped**, so drift introduced and reverted within a merge is missed.

### Correction to the design above, forced by building the fixture

The sibling-token search described in Phase 0 **does not work**, and building the scenario proved
why. Two findings, both from measurement rather than reasoning:

**1. A one-sided fix that is still in place destroys the clone set.** Applying the guard to
`App/CloneFixturesMirrored.cs` alone and committing it does not shrink the reported set — it
*changes which pair is reported*. The surviving set became `Compose` ↔ `SeedPairRenamed.Compose`
(207 tokens), an unrelated pair that was always a clone. The `Render` pair, which is the one the
fix touched, was gone from the report entirely. So 4C, iterating HEAD's clone sets, correctly
reports nothing about it: there is no longer a set to attribute the divergence to.

**2. The sibling-token search key is therefore wrong.** It searches for the siblings' *current*
tokens inside the commit's pre-image. But once the sibling has also been fixed, its current tokens
contain the guard, which the pre-image by definition lacks. The search fails on exactly the case
the feature exists to catch.

The substitution that does work is simpler and needs no search at all:

> **HEAD proves the copies are meant to be in sync** (SNP0031 proved token-identity over the
> region), **and the patch proves they were not, at commit C** (C changed one member's region and
> no sibling). Those two facts together are the finding. §6.1's High-tier clause — "the other
> copies are byte-identical to the pre-change text" — was written for a present-tense framing that
> cannot exist; HEAD-identity is the correct substitute for it here, and it is *stronger*, because
> it is a proof over the whole region rather than a single commit's parent.

### Scope narrowed to "the most recent change to each copy", deliberately

Reconciling a patch's pre-image coordinates with a HEAD-anchored region is where this design would
otherwise go wrong, and the honest fix is to refuse the ambiguity. 4C examines **the most recent
commit touching each copy of a clone set** — at most one candidate commit per member. For such a
commit the post-image *is* HEAD, so the hunk coordinates and the region coordinates are the same
system and no reconciliation is needed.

This costs recall: a one-sided fix followed by unrelated later edits to the same file is missed. It
buys exactness, a bounded and predictable cost (two git calls regardless of clone-set count), and a
scope that can be stated in one sentence. Widening it is a follow-up, not a redesign.

The detectable window, verified on the fixture — HEAD shows the pair as clones again, while C1
changed only one copy:

```
C1 78f0d905  "fix(A): guard empty label"        App/CloneFixturesMirrored.cs  @@ -12 +12,5 @@
C2 18174cfe  "fix(B): guard empty label (later)" CoreLib/CloneFixtures.cs      (the sibling catching up)
```

Between C1 and C2 the sibling shipped unguarded. That window is the finding, and it is reported with
both dates so a reader can see how long the copies were out of sync.

## Implementation (done 2026-10-03)

**Status: IMPLEMENTED.** **469 tests green** (was 438; +31 in `CloneDriftShould.cs`). Dogfood back to
the 3 pre-existing findings. Red phase verified by two mutations of the decision logic, each caught.
New rule **SNP0032**, flag `--clone-drift`.

New: `GitHistory.cs`, `GitProcess.cs`, `CloneDriftDetector.cs`, `CloneDriftShould.cs`.
Changed: `DuplicateFragmentAnalyser.cs`, `FindingCategory.cs`, `CliRunner.cs`, `GitMetadata.cs`.

### Measured cost

On `Snipper.slnx`, 9.6 s with `--duplicate-detection --clone-drift` against 7.0 s for a plain
baseline run. The shingling pass is **shared, not repeated** — 4C runs over the clone sets SNP0031
already proved in the same call, which is why the roadmap's "builds on SNP0031's index" was treated
as a requirement rather than a convenience. Enabling drift with duplicate detection already off
implies it rather than erroring.

### What the design above got wrong, and the four bugs that found it

Building the fixture before designing against it was the right call; it invalidated two of the
three mechanisms in the original design.

**1. The sibling-token search could never fire.** It searched for the siblings' *current* tokens
inside the commit's pre-image, but once the sibling is also fixed its tokens contain the guard the
pre-image lacks. Replaced by a simpler argument needing no search: HEAD proves the copies are meant
to be in sync, the patch proves they were not at commit C.

**2. A one-sided fix that is still in place destroys the clone set.** Applying the guard to one
member and committing it did not shrink the reported set — it *changed which pair was reported*.
The surviving set became `Compose` ↔ `SeedPairRenamed.Compose`, an unrelated pair. The pair the fix
touched was gone entirely. This is the honest limit of the temporal framing and it is why 4C reports
the *event* rather than the standing state.

**3. Git timestamps cannot order commits.** The first implementation compared `Date` to decide
whether a sibling was caught up later. Git timestamps have one-second resolution; the fixture's two
commits landed in the same second and the comparison silently failed. Replaced with `Order`, the
position in `git log`'s newest-first output. **Any commit-ordering logic must use log order, never
timestamps.**

**4. Embedded quoting silently broke every git lookup.** Paths were quoted by hand inside a
single command string, so git searched for a pathspec that literally included the quote characters
and returned nothing — with no error. Fixed by passing arguments through
`ProcessStartInfo.ArgumentList`. Hand-built command strings are a trap; the runtime's escaping is
strictly better than anything written by hand.

### The rule found a real defect in its own author, on the day it was written

The first dogfood run reported **SNP0032 High** on Snipper's own repository:

```
One-sided defensive fix in a clone set of 2 copies: commit 0f59d6d changed this copy
without touching GitHistory.cs:435
```

A true positive. `GitHistory.RunGit` duplicated `GitMetadata.RunGit`, and when the stderr-drain fix
was applied earlier in the session it was applied to only one of the two copies. That is precisely
the defect class SNP0032 exists to find, and it was found by the tool on its own author's code in the
same session. The fix was to delete the duplication rather than suppress the finding: `GitProcess.cs`
is now the only place Snipper shells out to git, with the incident recorded in its doc comment so a
future copy is not written by accident. Post-fix dogfood reports **zero** SNP0032 on this repository.

### Dogfood findings fixed, not suppressed

| Rule | Site | Defect |
|---|---|---|
| SNP0006 | `GitHistory.cs` | `ParseHunks` was public but had no caller in the solution. Removed; its three tests were rewritten against **real git output** rather than a canned patch string, which is a stronger test — a hand-written fixture only proves the parser agrees with the fixture. |
| SNP0019 | `GitHistory.cs` | `using System.Diagnostics;` orphaned by the extraction. |
| SNP0024 | `GitProcess.cs` | `DefaultTimeoutMilliseconds` was public but used only inside its own type. |

Final dogfood: **3 findings, all pre-existing** (`DuplicateFragmentAnalyser.cs:5`,
`DuplicateFragmentAnalyser.cs:109`, `FindingFilter.cs:125`) plus 2 SNP0031 clone sets that predate
this work. Nothing from Wave 4.

## Honest limits

- **A one-sided fix that is still in place is invisible to 4C.** The copies have stopped being
  clones, so there is no set to attribute the divergence to. This is the cost of the chosen framing
  and the reason clone-set breakup is the recommended follow-up.
- **Scope is the most recent commit touching each copy.** A one-sided fix followed by unrelated
  later edits to the same file is missed. Widening it is a follow-up, not a redesign.
- **Temporal, not standing.** The finding describes a commit. It reports whether the sibling caught
  up and when, but it is not proof the missing construct was ever a live defect.
- **The fix-shaped test is loose by design.** Substring, case-insensitive; `null` matches
  `Nullable`. This only ever promotes a change to fix-shaped, and it is gated by the one-sided
  requirement, which is the real filter.
- **`--merge` commits are skipped**, so drift introduced and reverted within a merge is missed.
- **Outside a git repository 4C emits nothing and says nothing.** It degrades like `GitMetadata`
  rather than failing.
- **Renames are not followed.** A commit that renamed a copy is attributed to the old path.
- **Not validated on a large repository.** All measurements are from a 39-commit repo and the
  SampleApp fixture. The commit cap and path chunking exist but their limits were not exercised.

## Exit criteria — status

| # | Criterion | Status |
|---|---|---|
| 1 | Detects a one-sided defensive fix in a real clone set | **met** — end-to-end test plus the self-detection above |
| 2 | Silent when the change was applied to every copy | **met** — `Report_No_Drift_When_Both_Copies_Were_Fixed_In_One_Commit` |
| 3 | Silent for a change outside the cloned region | **met** — `Report_No_Drift_For_An_Unrelated_Later_Edit` |
| 4 | Reports the earlier link of a chain, not both | **met** — mutation 2; a catch-up commit is a resolution, not new drift |
| 5 | Degrades outside a git repository | **met** — `Say_Nothing_Rather_Than_Failing_Outside_A_Git_Repository` |
| 6 | Reuses SNP0031's shingling pass | **met** — one pass, `BuildFindings` returns the sets it proved |
| 7 | Git cost independent of clone-set count | **met** — one call per 256-path batch |
| 8 | Tests fail without the logic | **met** — two mutations, 3 and 1 failures respectively |
| 9 | Full suite green; dogfood clean or triaged | **met** — 469 pass; dogfood at the 3 pre-existing findings |

## Deferred

- **Clone-set breakup** — regions identical at an earlier commit but different now. The signal that
  catches "A permanently fixed, B permanently stale". Needs similarity search over history; L+xlarge.
- **Per-team attribution** and widening the commit scope beyond the most recent change per copy.

---

# 4D - Performance and correctness increment (IMPLEMENTED 2026-10-04)

Not a roadmap story. Added after 4C because two things surfaced that had to be fixed before
a 1.7.0 cut: a **reproducible crash on real multi-project solutions**, and a measured
performance profile that showed the planned work was aimed at the wrong phase.

## The crash (the reason this section exists)

Benchmarking candidate targets found that **2 of 7 real solutions on the machine aborted**:

```
System.ArgumentException: An item with the same key has already been added.
Key: C:\pp\cardanosharp-wallet\CardanoSharp.Wallet\CardanoSharp.Wallet.csproj
   at UnreferencedPackageAnalyser.AnalyzeAsync(...) line 41
```

`UnreferencedPackageAnalyser` keyed a `ToFrozenDictionary` on `project.FilePath`. **A csproj
path is not unique inside a `Solution`**: a multi-targeted project surfaces once per TFM, and
a project reached through two referencing paths can appear twice. The throw happened inside
the analyser fan-out, so it took the whole run down rather than degrading to a missed finding.
`Mintsafe.sln` crashed because it pulls in the CardanoSharp projects.

Fixed by making the mapping one-to-many. The referenced-assembly test now unions every TFM
instance rather than picking one arbitrarily — which is what `ProjectPackageUsageCache` already
does with `UsedAssemblyNames`, so the fix aligns the two rather than inventing a policy.

**Swept for the same class of bug.** All 24 `ToDictionary` / `ToFrozenDictionary` sites were
audited; the other path-keyed ones (`ProjectGraph`, `InheritanceGraph`) use indexer assignment
or `TryGetValue` and are already duplicate-tolerant. One genuine site.

**Also fixed a correctness gap in the parallelism contract:** `AssemblyNameEvidenceScanner`
constructed `new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }`
inline, so the documented `SNIPPER_MAX_DOP=1` "revert to sequential" escape hatch silently
did not apply to that scan. Routed through `AnalysisParallelism.CreateOptions`.

## Where the time actually goes — the finding that reordered the work

The plan's implicit assumption was that analysis is the cost. Measured phase split:

| Target | total wall | analysis fan-out | MSBuild load + output | load share |
|---|---|---|---|---|
| Peckr.sln (8 proj) | 5.41 s | 1.70 s | 3.71 s | **69%** |
| AzureBusDepot.sln (4 proj) | 4.04 s | 1.40 s | 2.64 s | **65%** |
| Cscli.sln (3 proj) | 9.25 s | 4.80 s | 4.45 s | **48%** |
| Snipper.csproj (1 proj) | 7.29 s | 4.50 s | 2.79 s | 38% |

`MSBuildWorkspace.OpenSolutionAsync` is single-threaded per project and everything downstream
needs its `Solution`, so its cost cannot overlap with analysis. Measured CPU/wall is **1.7–3.0×
on a 16-core box** — roughly 80% of the machine idle.

**The biggest remaining lever is the load phase, not the analysers.** That work was explicitly
deferred (see [Deferred](#deferred-for-170)) because `OpenSolutionAsync` is Roslyn's design-time
build with no supported parallelism knob.

The analyser fan-out, by contrast, is already well-tuned and was left alone: `AnalysisRunner`
pre-warms compilations sequentially (33 s → 4.6 s when that was removed), and all four shared
indexes are correctly memoized on the `Solution` instance. There is no `solution.With*` call
anywhere in `src/`, so those caches genuinely hit once.

## What changed, and what it measured

Every figure is an **interleaved A/B** — two binaries alternating run-by-run so machine drift
cancels. This matters: a real, verified improvement measured **3.8% slower** when compared
non-interleaved, and one pair of runs of identical binaries differed by **>2×** from machine load
alone. CPU is quoted alongside wall throughout because CPU is the far less noisy signal.

| Change | Kind | Effect |
|---|---|---|
| `TieredPGO=false` in `Snipper.csproj` | config | **−2.9% to −11.1% wall, −11.6% to −18.6% CPU**, all six targets |
| `FrameworkEvidenceIndex` receiver text | allocation | `memberAccess.Expression.ToString()` ran for **every** invocation; the result is only read for 7 method names out of ~4,500. ~99.9% of those strings were built and discarded. Now gated on the identifier first. |
| `FrameworkEvidenceIndex` attribute names | allocation | `AttributeNameText` removes 1–2 allocations per attribute per document, **while deliberately keeping the `ToString()` fallback for qualified names** (see [Invariant 20](#invariants-added-by-this-increment)). |
| `SolutionUsageIndex` interning | allocation | Every `SimpleNameSyntax` hashed its identifier 3×. A `names.Add` guard collapses it to 1× on the hottest loop in the tool. |
| `CliRunner` fingerprint memo | redundancy | `ComputeFingerprint` ran up to 3× per finding across the audit and baseline blocks; one reference-keyed memo now serves all five call sites. |
| `ExclusionEngine` entry-point memo | redundancy | `IsFrameworkEntryPointType` was O(members in the type) **per candidate symbol** — a 50-method type with 40 candidates paid ~2,000 `GetAttributes()` bindings where 50 suffice. |
| `DocumentIdentifierIndex` memo | redundancy | Two analysers built the same per-document index independently. |
| `RedundancyAnalyser` walk fusion | redundancy | Five full `DescendantNodes()` traversals per document → one walk into five buckets. |
| `ProjectFileReader` cache | redundancy | Five call sites each parsed the same csproj with `XDocument.Load(..., SetLineInfo)`. Invalidated by last-write time. |

### Cumulative effect

| Target | wall | CPU |
|---|---|---|
| CardanoSharp.sln | **−18.1%** | **−25.3%** |
| Cscli.sln | −13.3% | −20.4% |
| Peckr.sln | −12.8% | −23.4% |
| Snipper.csproj | −10.5% | −20.3% |
| Mintsafe.sln | −10.2% | −16.4% |
| AzureBusDepot.sln | −2.2% | −7.8% |

Verified output-neutral: the `findings` array is **byte-identical on all six targets**.

### Two hypotheses measured and rejected

Recorded so they are not relitigated:

- **Server GC** — neutral to *worse* (Cscli 7.91 → 8.79 s). Startup cost dominates a
  single-shot process.
- **`TieredCompilation=0`** — **+27% worse**. Disabling tiered JIT entirely is far too expensive
  for a ~7-second run. Tiered compilation stays **on**.

### Honest gaps in the perf work

- **The fingerprint memo is reasoned, not measured.** It only runs under `--baseline` /
  `--audit-suppressions`, which the harness does not exercise by default. A targeted interleaved
  A/B on the two largest findings sets gave −3.7%, +2.3%, −5.0% — i.e. noise. It is kept because
  it strictly removes duplicate work, but **do not quote a number for it**.
- **No monorepo-scale target existed locally.** The largest real solution available was 119 `.cs`
  files. The per-symbol and per-node memos measured as **neutral on every real target** and only
  paid off (**−2.9% wall / −3.0% CPU**) on a generated 40-project / 800-file / 172k-line target.
  Small-target benchmarking **under-reports this class of change** — that is why they are kept.
- `TieredPGO=false` ships in `runtimeconfig.json` and therefore applies to every consumer.
  Validated on one 16-core x64 box only; re-measure on ARM or older x86 before trusting it broadly.

## Invariants added by this increment

Numbered to continue §"Invariants you must not break" in `docs/code_architecture.md`:

19. A cache keyed on a csproj path must be invalidated by last-write time.
20. Do not "optimise" a name comparison by returning a bare identifier.
21. A project path is not unique in a `Solution`.

**Invariant 20 is a trap worth stating explicitly.** The obvious allocation fix for
`attribute.Name.ToString()` is `SimpleNameOf`, which is already in that file. Using it would have
been a **silent behaviour change**: callers compare the *whole rendered string*, so
`[Newtonsoft.Json.JsonSerializable]` deliberately does **not** match `[JsonSerializable]`. Returning
the bare identifier would have started accepting it. `AttributeNameText` keeps the qualified-name
fallback.

## Test-coverage gaps this increment closed

Both new caches shipped with **zero** invalidation coverage. Two mutations were applied to check:

| Mutation | Before | After |
|---|---|---|
| `ProjectFileReader` cache never expires | **34/34 project-file tests green** | 2 of 4 new tests fail |
| `UnreferencedPackageAnalyser` duplicate-key throw | crashes | 3 of 3 new tests fail |

`ProjectFileReaderShould` and `DuplicateProjectPathShould` exist because the existing suite could
not see either failure. **A cache without an invalidation test is a cache that will silently rot.**

## Harness

`test/Fixtures/Measure-Performance.ps1` — timing and interleaved A/B, with a canonical
`findings` hash as the output-equivalence oracle. Documented in its own header, including the two
methodology mistakes it exists to prevent (non-interleaved comparison; using Snipper-on-Snipper
as the oracle).

---

# What is left for 1.7.0

Everything above is **implemented and verified but unreleased**. `<Version>` is `1.6.3`.

## Release gate

| # | Step | State |
|---|---|---|
| 1 | Version bump `1.6.3` → `1.7.0` in `src/Snipper/Snipper.csproj` | **done** |
| 2 | `dotnet pack` | **done** — `Snipper.1.7.0.nupkg`, 11.34 MB, README + `TieredPGO: false` verified inside |
| 3 | Global tool install | **done** — 1.7.0 installed from the packed nupkg |
| 4 | Self-run against the *installed* tool | **done** — version, exit codes, JSON schema, dogfood, and packaged-vs-local byte parity |
| 5 | Monorepo A/B on the 4C High tier | **BLOCKED — see below** |
| 6 | Commit | **done** (4 commits) + this release commit |

Packaged-artifact verification worth recording: the installed tool and the local Release build produce a
**byte-identical** `findings` array on a real 3-project solution (hash `44ECC0D24FB21294`, 19 findings), so
the packaging step introduces no behavioural difference.

## BLOCKER found during release validation — SNP0031 does not terminate at scale

The monorepo A/B (gate 5) cannot be run, because the duplicate/clone pass does not complete on a
**182-file** solution. Measured on `CardanoSharp.Wallet.sln`, same machine, assets warm:

| Run | Result |
|---|---|
| plain (installed 1.7.0) | **10.6 s**, 809 findings, all analysers ≤ 4.5 s |
| `--duplicate-detection --clone-drift` | **did not finish in 20 minutes** |

The log localises it precisely: every other analyser completes in ≤4.5 s, and the last line emitted is
`DuplicateFragmentAnalyser: shingling 209 files` followed by `extending matches`. It never returns.

**Root cause.** `DuplicateFragmentAnalyser.FindFragments` compares **every pair** of locations in each
60-token shingle bucket (`for i` / `for j`), and each surviving pair calls `Extend`, which itself walks
tokens forward and backward. That is O(K²·L) in the size K of the largest bucket. There is no cap on
bucket size and no cancellation check inside the inner pair loop. A codebase with heavy boilerplate —
exactly what generated DTO/entity layers produce — puts thousands of locations in a single bucket, and
the pair loop becomes billions of operations.

This is the same cost model the roadmap already flagged for SNP0031 (52.6 s marginal on a 3,070-file
monorepo, 3.5× over its own ≤15 s budget). The release run shows the problem is worse than "over budget":
on this target it does not finish at all.

**Why this blocks gate 5 rather than merely qualifying it.** 4C's clone sets are produced by SNP0031, so
`--clone-drift` cannot be exercised on a large repository until this is addressed. The Wave 4 exit
criterion for 4C therefore cannot be evaluated, and it stays **unmet**.

**Not fixed in this release, deliberately.** The obvious mitigation is to keep only one location per path
per bucket before pairing (same-path pairs are already skipped, so bucket size becomes bounded by the file
count). That is a small change, but it alters which alignments are discovered, so it is a detection-
semantics decision rather than a release-mechanics one, and it is the user's call rather than a silent
late fix. SNP0031 and `--clone-drift` remain **opt-in**, and the opt-in default means no existing pipeline
is affected.

**Also found during validation, unrelated to 4C.** Snipper surfaces a raw unhandled
`System.Xml.XmlDocument` stack trace when handed a malformed `.slnx` (hit by hand-writing one). There is
no try/catch around `OpenSolutionAsync`, so bad input produces a crash dump instead of a diagnostic. Worth
a follow-up; it is a robustness gap against the "degrade gracefully" tenet, not a regression from this work.

## Open items that gate or qualify the release

**4C's High tier has never run against a large repository.** The roadmap's own Wave 4 exit
criterion — "4C's High tier produces zero false positives on the monorepo or drops a tier" — is
**not met**. It is verified on the SampleApp fixture and on the author's own repository only. This
is the single most important thing left, because 4C is the one story that emits *correctness*
findings rather than smells, and an unproven High tier is a trust risk.

**Monorepo-scale performance is unvalidated for everything in this document.** Every local target
is ≤119 files. The 1.4.2 precedent applies: the user's monorepo A/B is the acceptance test, and
per locked decision on that wave, **any SNP0005/0006 finding increase blocks**.

**Also open, carried forward from the individual stories:**

- 4A-2's baseline now legitimately **grows on first run** to include findings for excluded code.
  Needs a release-note line, because users will see their baseline file change on upgrade.
- `--entropy-budget` ships **opt-in with no default budget**, deliberately: any default would fail
  existing builds on upgrade. Documented in `docs/usage.md`.
- Clone-set breakup (4C follow-up, L+xlarge) — regions identical at an earlier commit but different
  now. Unstarted, and the reason permanently diverged pairs are still deferred.
- Per-team CODEOWNERS attribution (4B follow-up) — deferred.

## Not in 1.7.0

Governance candidates deliberately held back are listed in
`docs/Snipper-Feature-Parity-Roadmap.md` §"Added 2026-10-03". The first of them —
**unanalysed-region accounting** — is explicitly flagged there as "the first thing to add" to
this wave, so it is the natural 1.7.1 candidate.

## Deferred for 1.7.0

- **The MSBuild load phase.** 38–69% of wall clock on real multi-project solutions, and the
  largest remaining lever. `MSBuildWorkspace.OpenSolutionAsync` is Roslyn's design-time build;
  there is no supported parallelism knob, so any attempt carries real risk. Deliberately not
  attempted, and documented as the next investigation rather than quietly forgotten.

## Suggested commit split

The working tree mixes four separable concerns. A single commit would be unreviewable; splitting
on these lines gives four independently revertable changes:

1. **4C** — `CloneDriftDetector`, `GitHistory`, `GitProcess`, `DuplicateFragmentAnalyser`,
   `FindingCategory`, `GitMetadata`, `EntropyRateShould`, `CloneDriftShould`, docs.
2. **`CliRunner` decomposition** — the six extracted `Cli/` types, `CommandLineParserShould`,
   `ShadowPassShould`, `JsonReportSerializerContext`, `ReportSchemaShould`. Behaviour-neutral.
3. **The crash fix** — `UnreferencedPackageAnalyser`, `AssemblyNameEvidenceScanner`,
   `DuplicateProjectPathShould`. Independently revertable and worth shipping on its own merits.
4. **The perf increment** — the seven `Analysis/` files, `Snipper.csproj`,
   `ProjectFileReaderShould`, and the `code_architecture.md` perf section. Output-neutral,
   which the harness's findings hash is what proves.