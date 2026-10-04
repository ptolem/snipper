# Snipper usage guide

Reference for running Snipper, the meaning of every option, and how findings are filtered. For
pipeline recipes and gating strategies see [CI integration](ci-integration.md). For what each rule
*means* see the [rule catalogue in the README](../README.md#rules); this document covers *how to run
and filter it*.

Every behaviour here was verified against the running tool on this repository (Snipper 1.7.0,
Wave 4 working tree). Where something is a limitation rather than a feature, it says so.

---

## Contents

1. [Execution model](#execution-model)
2. [Command synopsis](#command-synopsis)
3. [Exit codes](#exit-codes)
4. [Options reference](#options-reference)
5. [Certainty tiers](#certainty-tiers)
6. [Filtering and suppression](#filtering-and-suppression)
7. [The `snipper.json` config file](#the-snipperjson-config-file)
8. [Path globs](#path-globs)
9. [Namespace exclusions](#namespace-exclusions)
10. [Analyser catalogue](#analyser-catalogue)
11. [Baselines](#baselines)
12. [Report formats](#report-formats)
13. [Performance and scaling](#performance-and-scaling)
14. [Known limitations and rough edges](#known-limitations-and-rough-edges)

---

## Execution model

Snipper loads a solution or project through `MSBuildWorkspace`, then runs a fixed set of analysers
over the loaded solution. It is:

- **Read-only.** Snipper never writes to the analysed tree. The only files it writes are the report
  and the entropy ledger, both at paths you name.
- **Single-invocation.** One process, one pass. There is no daemon, no watch mode, and no incremental
  analysis — every run re-analyses everything from scratch.
- **Semantic where it matters.** Most rules bind a Roslyn semantic model; the duplication rules
  (SNP0031/SNP0032) are deliberately syntax-only.
- **Deterministic.** Findings are emitted through order-independent containers and sorted before
  output, so two runs on the same commit produce byte-identical reports.

Design-time build diagnostics (for example NuGet vulnerability audit warnings during load) are
**non-fatal**: the project still loads and is analysed. Each distinct message is printed once and
duplicates are summarised.

---

## Command synopsis

```
snipper <path-to-solution-or-project> [output-file] [options]
```

- The **target path** is required and must be positional — it is `args[0]`.
- The **output file** is optional and positional. It may appear anywhere after the target, but only
  the first bare (non-flag) argument becomes the output path. A second bare argument is an
  `Unexpected argument` usage error (exit 1).
- Flag names are matched **case-insensitively**. Flag *values* are parsed case-insensitively too
  (`--certainty-t-tier HIGH` works), with one deliberate exception: `--entropy-budget` is parsed with
  invariant culture, because a CI-facing number must not change meaning with the machine's locale.
- Output directories are created if missing.

If the target path is omitted, Snipper prints the synopsis and exits 1.

---

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Analysis completed. **Also returned when there are findings.** |
| `1` | Usage error — missing target, malformed flag value, unknown argument, or `--entropy-rate` without `--baseline`. |
| `2` | The report could not be written (I/O error). |
| `3` | The entropy budget was exceeded (`--entropy-budget`). |

**The single most important thing to know about the exit code: findings never fail the build.**
There is no `--fail-on` option. Exit 0 means "the analysis ran", not "the code is clean". A pipeline
that wants to gate on findings must either gate on the JSON/SARIF report or use the one native gate,
`--entropy-budget`.

A real failure outranks a policy failure: if the report cannot be written, you get `2` rather than
`3`, so CI sees "Snipper could not write the report" rather than a budget breach it cannot act on.

---

## Options reference

### Target and output

| Option | Value | Notes |
| --- | --- | --- |
| `<target>` | path to `.sln`, `.slnx`, or `.csproj` | Required, positional, must exist. |
| `[output-file]` | path | Optional, positional. Directory created if missing. Omit it for console-only output. |
| `--format` | `json` \| `sarif` | Default `json`. |

### Reporting and filtering

| Option | Value | Notes |
| --- | --- | --- |
| `--certainty-tier` | `guaranteed` \| `high` \| `moderate` \| `advisory` | Minimum certainty to report. `--certainty-tier high` shows Guaranteed + High. |
| `--exclude-namespaces` | comma-separated list | Repeatable. **Unions** with the config file's namespace list rather than replacing it. Malformed entries warn and are skipped. |
| `--audit-suppressions` | — | Report what each suppression is hiding. See [Filtering and suppression](#filtering-and-suppression). |

### Analyser selection

| Option | Value | Notes |
| --- | --- | --- |
| `--config-analysis` | — | Enables SNP0007 + SNP0008 (configuration/options binding). |
| `--duplicate-detection` | — | Enables SNP0031 (duplicated fragments). |
| `--clone-drift` | — | Enables SNP0032 (clone drift). **Implies `--duplicate-detection`** — they share one shingling pass, so asking for two flags to get one feature would be user-hostile. |

### Baselines and entropy (4B)

| Option | Value | Notes |
| --- | --- | --- |
| `--baseline` | path | Read, filter to new findings, refresh in place. |
| `--entropy-rate` | — | Requires `--baseline`. Measures findings per kLOC introduced since the baseline commit. |
| `--entropy-budget` | findings/kLOC | **Implies `--entropy-rate`.** Must be non-negative. Breach exits `3`. |
| `--entropy-ledger` | path | **Implies `--entropy-rate`.** Committed JSON history enabling monthly aggregation. |
| `--entropy-min-lines` | integer | **Implies `--entropy-rate`.** Floor on the denominator, so a 3-line change cannot post a flattering rate. |

`--entropy-rate` deliberately **errors** without `--baseline` rather than defaulting: "new" is defined
relative to a recorded baseline, and a silent `0.00` would be indistinguishable from a genuinely
clean change and would pass any budget.

Fail-open statuses (reported, not fatal): `BaselineSeeded`, `NoBaselineReference`,
`NotAGitRepository`, `DirtyWorkingTree`, `UnchangedRange`, `BelowMinimumChange`, `Scored`.

The `entropyRate` section appears in the **JSON report only**. With `--format sarif` the console
table and budget gate still work, but the SARIF file carries no entropy section.

### Version

| Option | Notes |
| --- | --- |
| `--version`, `-v` | Print the tool version and exit `0` immediately, before MSBuild locator registration. |

### Options that do not exist

Being explicit, because these are the natural next guesses and none of them are implemented:

- **No `--fail-on`, `--fail-threshold`, or `--max-findings`.** Gate on the report instead.
- **No `--exclude-rules` / `--disable-rule` CLI flag.** Rule suppression is config-file only.
- **No rule-group or preset names** (`--rules unused`, or similar). Disable individual rule ids.
- **No `--include`/`--exclude-project` or project filter.** Scope a run by passing a narrower target.
- **No `--fail-on-audit` or audit exit code.** The audit is report-only today.

---

## Certainty tiers

Four tiers, ordered. `--certainty-tier` sets a floor; SARIF maps them to levels.

| Tier | Meaning | SARIF level |
| --- | --- | --- |
| `Guaranteed` | Compiler-equivalent or semantically proven. Removing the code cannot change behaviour. | `error` |
| `High` | Very likely correct, with a small, characterised set of exclusions. | `warning` |
| `Moderate` | Likely correct; a documented demotion path applies to edge cases. | `warning` |
| `Advisory` | A lead worth a human look, not a defect claim. | `note` |

Notes that matter in practice:

- Certainty is a property of the **rule plus its evidence**, not of the category. A single rule can
  emit two tiers — SNP0005 is Moderate but Advisory when `InternalsVisibleTo` or DI registration is
  present; SNP0021 is High but Moderate on a serialization-attributed field.
- `--certainty-tier` applies to the terminal table **and** the report file.
- The baseline always tracks the **full** finding set, so switching tiers never churns the baseline
  file. This is deliberate and is what makes tier changes safe to make mid-adoption.
- Severity overrides in `snipper.json` can move a finding in **either** direction, including
  promoting an Advisory finding to `guaranteed` so a native gate sees it.

---

## Filtering and suppression

Snipper has four suppression channels. They are not equivalent, and the distinction decides
performance, baseline behaviour, and whether they show up in the audit.

### The two classes

**Class 1 — removes findings before they exist.** Cheap (the analyser never runs, or the finding is
never constructed) and invisible to the report-time filter.

| Channel | Where | Cost |
| --- | --- | --- |
| Namespace exclusion | `exclude.namespaces` / `--exclude-namespaces` | Analyser skips the symbol. |
| Whole-analyser disable | every rule id of an analyser set to `off` | **The analyser never runs.** |

**Class 2 — removes findings at report time.** The analysis cost is already paid.

| Channel | Where | Cost |
| --- | --- | --- |
| Rule off | `rules: { "SNPxxxx": "off" }` | Full analysis, then dropped. Unless *all* of an analyser's rules are off, in which case it becomes Class 1. |
| Path glob | `exclude.paths` | Full analysis, then dropped. |
| Severity override | `rules: { "SNPxxxx": "advisory" }` | Full analysis, then rewritten. |

### Precedence

Evaluated in this order; first match wins:

```
namespace exclusion        (upstream — before findings exist)
  └─> disabled rule        ┐
  └─> path glob            ├─ FindingFilter.Classify, in this order
  └─> severity override    ┘
```

`FindingFilter.Classify` is shared by the report filter and the suppression audit, so the two can
never disagree about why a finding vanished.

**Consequence worth knowing:** because namespace exclusions run upstream, a path glob that covers the
same files will report `suppressedCount: 0` and be flagged `obsolete` — the findings were already
gone. Overlapping channels produce audit rows that look like dead configuration.

### Baseline safety

No suppression channel can rewrite a baseline. Path globs and severity overrides apply *after*
fingerprinting, and as of 4A-2 namespace exclusions and disabled rules are fingerprinted from the
suppression-independent finding set. **The accepted cost:** a baseline legitimately grows on its
first run to include findings for newly excluded code.

### The audit (`--audit-suppressions`)

Adds a `suppression` section to the JSON report and a table to the console. It exists so that
"why is this not failing?" is answerable without reading the config by hand.

```json
"suppression": {
  "totals": {
    "findingsAnalysed": 2,
    "findingsHiddenByShadow": 1,
    "findingsDropped": 0,
    "findingsDowngraded": 0,
    "findingsAfterSuppression": 2,
    "hiddenDebtPercent": 33
  },
  "pathGlobs": [ { "channel": "PathGlob", "selector": "**/FindingFilter.cs",
                   "suppressedCount": 0, "downgradedCount": 0,
                   "ruleIds": [], "detail": "" } ],
  "severityOverrides": [ { "channel": "SeverityOverride", "selector": "SNP0031",
                           "suppressedCount": 0, "downgradedCount": 0,
                           "ruleIds": [], "detail": "-> Advisory (was )" } ],
  "disabledRules": [],
  "namespaceExclusions": [ { "channel": "NamespaceExclusion", "selector": "Snipper.Cli",
                             "suppressedCount": 1, "downgradedCount": 0, "ruleIds": [],
                             "detail": "aggregate over 1 namespace(s); per-namespace split not available" } ],
  "obsolete": [ { "channel": "SeverityOverride", "selector": "SNP0031",
                  "confidence": "Suspected", "reason": "no finding of this rule was downgraded" } ],
  "shadowAnalysisRan": true
}
```

Fields worth acting on:

| Field | Use it to |
| --- | --- |
| `obsolete[]` | **Find dead suppressions.** An entry means the suppression matched nothing. For a path glob this usually means the glob is wrong (see [Path globs](#path-globs)); for a namespace exclusion it usually means the namespace is renamed or the code moved. This is the single most useful CI check for suppression hygiene. |
| `hiddenDebtPercent` | Portion of findings hidden by suppression. A high number means your visible report is flattering. |
| `findingsHiddenByShadow` | Findings that exist but are suppressed — measured by the 4A-2 shadow pass. |
| `suppressedCount` / `downgradedCount` | Per-channel attribution, so you can attribute debt to a specific glob. |
| `shadowAnalysisRan` | Whether the shadow pass ran. It only runs when there is a baseline *and* a churn-prone suppression, so `false` is normal and not an error. |

The audit costs nothing for a user with no suppressions, and the console prints a single
"no active suppression is hiding anything" line in that case.

Namespace exclusions are reported in **aggregate** — one row for all of them, with no per-namespace
split. If you need per-namespace attribution, run once per namespace, or read
`hiddenDebtPercent` while bisecting.

---

## The `snipper.json` config file

```json
{
  "version": 1,
  "rules": {
    "SNP0010": "off",
    "SNP0018": "advisory",
    "SNP0031": "moderate"
  },
  "exclude": {
    "namespaces": ["Company.Generated", "Company.Contracts.Internal"],
    "paths": ["**/Generated/**", "**/Migrations/**"]
  }
}
```

### Discovery

Snipper walks **up** from the target's directory looking for `snipper.json`; the **first file found
wins**.

Two consequences that matter for a monorepo:

- **Configs are not merged.** A `snipper.json` in `src/Service/` completely replaces one at the repo
  root for a target inside `src/Service/`. There is no layering or inheritance.
- **Passing a narrower target picks a different config.** Analysing `src/Service/Service.csproj`
  instead of `MyMonorepo.slnx` can silently change your rule set. In CI, always pass the same target
  path as locally, or you will chase phantom differences.

### Schema

| Key | Type | Meaning |
| --- | --- | --- |
| `version` | int | Must be `1`. Any other value warns and applies defaults. |
| `rules` | object | Rule id → `"off"` or a certainty tier name. |
| `exclude.namespaces` | string[] | Namespace prefixes; see [Namespace exclusions](#namespace-exclusions). |
| `exclude.paths` | string[] | Globs matched against absolute file paths; see [Path globs](#path-globs). |

### Tolerance

The loader is deliberately tolerant. **None of these are fatal**, and each produces a warning:

- No `snipper.json` anywhere → defaults.
- Malformed JSON, unreadable file → warning, defaults.
- Unsupported `version` → warning, defaults.
- Unknown rule id (not `SNP` + ≥4 chars) → warning, that entry skipped.
- Unknown severity value → warning, that entry skipped.

This is the right call for a tool in a shared pipeline — a typo degrades to "less filtering", not to
a red build — but it does mean **a misspelled rule id silently does nothing**. The `obsolete[]` audit
array is how you detect it.

Rule ids are case-insensitive and normalised to upper case. `exclude.namespaces` and `exclude.paths`
are compared with ordinal (case-**sensitive**) semantics.

---

## Path globs

Globs are translated to a case-insensitive regex anchored at both ends (`^…$`) and matched against
the finding's file path after backslashes are normalised to `/`.

| Pattern | Regex fragment | Meaning |
| --- | --- | --- |
| `**/` | `(?:.*/)?` | Zero or more directories. Optional, so `**/x` also matches a bare `x`. |
| `**` | `.*` | Any characters, including `/`. |
| `*` | `[^/]*` | Any characters **except** `/`. Does not cross directories. |
| `?` | `[^/]` | Exactly one non-`/` character. |
| anything else | escaped | Literal, case-insensitive. |

### ⚠ Globs match absolute paths — always lead with `**/`

Globs are matched against the analyser's **absolute** file path, not the repo-relative path that
appears in the JSON report. Because the regex is anchored, a repo-relative glob matches nothing.

Verified on this repository:

| Config | Result |
| --- | --- |
| *(no config)* | 3 findings: `DuplicateFragmentAnalyser.cs:5`, `DuplicateFragmentAnalyser.cs:109`, `FindingFilter.cs:125` |
| `"paths": ["**/DuplicateFragment*.cs"]` | 1 finding: `FindingFilter.cs:125` ✅ both suppressed |
| `"paths": ["src/Snipper/Analysis/DuplicateFragmentAnalyser.cs"]` | 3 findings — **glob silently did nothing** ❌ |

So the README's example entry `"src/Legacy/**"` does not work as written; it needs to be
`"**/src/Legacy/**"` or `"**/Legacy/**"`. This is a documentation defect worth fixing separately, and
a plausible candidate for hardening (auto-prepend `**/` when a pattern has no leading wildcard).

**How to catch it:** run with `--audit-suppressions`. A dead glob appears in `pathGlobs` with
`suppressedCount: 0` *and* in `obsolete[]`. This is the fastest way to validate a glob you just
wrote, and worth wiring into CI permanently.

### Cost

Path globs are Class 2 — the finding is fully analysed, then dropped. They do not save analysis
time. If the goal is performance on a monorepo, prefer namespace exclusions, which skip work.

---

## Namespace exclusions

Available as `--exclude-namespaces` and as `exclude.namespaces` in the config. The two **union**: a
namespace passed on the command line is added to the config's set, not substituted for it. Verified —
config `Snipper.Cli` plus `--exclude-namespaces Snipper.Analysis` suppressed findings in both.

Console output states the semantics explicitly:

```
Excluding namespaces (findings suppressed, usage evidence retained): Company.Generated, ...
```

### Matching: exact, plus every sub-namespace

The namespace name is matched, then progressively truncated at each `.` boundary, all the way to the
root namespace. Excluding `Company.Domain.Types` therefore excludes:

- `Company.Domain.Types`
- `Company.Domain.Types.Internal`
- `Company.Domain.Types.Internal.Impl`
- …and so on to any depth.

There is no wildcard and no way to exclude only one level. **Excluding a broad namespace silences
everything beneath it**, so `Company` as an entry is almost never what you want. Prefer the most
specific namespace that covers the generated or vendored code.

### Findings are suppressed; usage evidence is retained

This is the most important semantic in the whole feature. Excluding `Company.Generated` stops that
code producing findings, but code in it **still counts as a reference** for the
reference-checking rules (SNP0001–SNP0006 and friends).

The consequence for a monorepo: you cannot use namespace exclusion to hide a public API surface and
then expect unused-member findings for the rest of the repo. The excluded assembly's types are still
evidence that the members are used.

### ⚠ Findings with no enclosing type escape namespace exclusion

Namespace exclusion for symbol-less findings (unused usings, unreachable statements, unread locals)
is resolved through the **enclosing type declaration**. A file-level `using` directive has no
enclosing type, so it is not covered.

Verified: excluding `Snipper.Analysis` removed `DuplicateFragmentAnalyser.cs:109` (SNP0024) but left
`DuplicateFragmentAnalyser.cs:5` (SNP0019, unused using) in place.

If you exclude a large generated tree, expect residual SNP0019/SNP0020/SNP0002 findings at file
scope. Add a path glob for those files — accepting that the analysis cost is still paid.

**SNP0031 is the exception.** The duplication rule resolves its namespace syntactically through the
enclosing *namespace* declaration, and supports a special marker for fragments at file scope:

```json
{ "version": 1, "exclude": { "namespaces": ["<global>"] } }
```

`<global>` suppresses file-scope clone fragments. It works **only through the config file** — the
CLI flag's syntax check rejects it (see below).

### ⚠ The CLI validates namespace syntax; the config file does not

`--exclude-namespaces` checks each entry: every dot-separated segment must start with a letter or
`_` and contain only letters, digits, and `_`. Anything else warns and is skipped.

The config file applies **no syntax validation at all**. Verified — all three of these are accepted
silently:

| Config entry | Result |
| --- | --- |
| `["<global>"]` | accepted |
| `["9bad-name"]` | accepted (starts with a digit) |
| `["has space"]` | accepted |

Two consequences:

- **`<global>` is config-only.** `--exclude-namespaces "<global>"` prints
  `Warning: ignoring malformed namespace '<global>'.` and excludes nothing.
- **A typo in the config is silent.** Since a misspelled namespace matches nothing rather than
  erroring, it looks exactly like a working exclusion that happens to have no findings. The
  `obsolete[]` audit array is the only way to detect it.

### Cost

Class 1 — the analyser skips the symbol rather than discarding a finished finding. For
monorepo-scale generated code this is the difference between a minutes-long run and an unusable one.

---

## Analyser catalogue

19 analysers, 28 rules. 17 run by default; 2 are opt-in.

`RuleIds` is the authoritative mapping — this is the same property the tool uses to decide whether an
analyser is worth running, so "disable every rule id in this row" is exactly equivalent to "skip this
analyser".

| Analyser | Rules | Default certainty | Always runs |
| --- | --- | --- | --- |
| `UnreachableCodeAnalyser` | SNP0002 | Guaranteed | yes |
| `UnusedPrivateMemberAnalyser` | SNP0001 | Guaranteed | yes |
| `UnusedUsingDirectiveAnalyser` | SNP0019 | Guaranteed | yes |
| `UnusedLocalVariableAnalyser` | SNP0009 | Guaranteed | yes |
| `UnreferencedPackageAnalyser` | SNP0003, SNP0004 | High | yes |
| `FrameworkInboxPackageAnalyser` | SNP0013 | High | yes |
| `WriteOnlyFieldAnalyser` | SNP0021 | High, Moderate | yes |
| `RedundancyAnalyser` | SNP0022, SNP0025, SNP0026, SNP0028, SNP0029 | High | yes |
| `UnusedNonPrivateMemberAnalyser` | SNP0005, SNP0006 | Moderate, Advisory | yes |
| `UnusedParameterAnalyser` | SNP0010 | Moderate | yes |
| `OrphanProjectAnalyser` | SNP0011 | Moderate | yes |
| `RedundantTransitivePackageAnalyser` | SNP0012 | Moderate | yes |
| `ObsoleteMemberAnalyser` | SNP0018 | High, Moderate | yes |
| `HierarchyDeadCodeAnalyser` | SNP0023, SNP0027 | Moderate, Advisory | yes |
| `TighteningAnalyser` | SNP0024 | Advisory | yes |
| `CommentedCodeAnalyser` | SNP0020 | Advisory | yes |
| `EventNeverInvokedAnalyser` | SNP0030 | Advisory | yes |
| `ConfigurationBindingAnalyser` | SNP0007, SNP0008 | Advisory | **no** — `--config-analysis` |
| `DuplicateFragmentAnalyser` | SNP0031 (Advisory), SNP0032 (High/Advisory) | Advisory, High/Advisory | **no** — `--duplicate-detection` / `--clone-drift` |

Notes:

- **`RedundancyAnalyser` owns five rules.** Disabling "the redundancy analyser" means turning off
  SNP0022, SNP0025, SNP0026, SNP0028 *and* SNP0029. Missing one leaves the analyser running and the
  cost unchanged while you think you disabled it — the `obsolete[]` audit will not catch this, because
  a partially-disabled analyser is not a dead suppression. Check the console analyser list, or
  disable all five.
- **Opt-in rules can be config-disabled too**, in which case the analyser is skipped entirely and the
  flag becomes unnecessary. Disabling SNP0031 in the config is strictly better than remembering to
  omit `--duplicate-detection` on every invocation.
- **SNP0014–SNP0017 are not used.** Rule ids are stable strings, not a dense enumeration; gaps are
  reserved and will not appear.

### Excluding a group of analysers

There is no group syntax. The mechanism is to set every rule id of the target analysers to `off`:

```json
{
  "version": 1,
  "rules": {
    "SNP0022": "off", "SNP0025": "off", "SNP0026": "off",
    "SNP0028": "off", "SNP0029": "off"
  }
}
```

That is a legitimate first move on a large monorepo — it is exactly what Class 1 whole-analyser
disable is for, and it is the only configuration change that actually reduces runtime. But treat it
as a staged decision, not a permanent one: the `RedundancyAnalyser` rules are all High certainty and
all removal-safe, so they are poor candidates for permanent exclusion.

---

## Baselines

`--baseline <path>` is the adoption mechanism for an existing codebase:

1. Read the baseline file.
2. Fingerprint every finding and suppress those already recorded.
3. Report only new findings.
4. Refresh the baseline in place with the current full set.

Properties that make it safe to adopt on a live repo:

- **Independent of every `snipper.json` channel.** Baseline and config suppression are orthogonal;
  a finding hidden by a glob is still tracked by the baseline (see [Baseline safety](#baseline-safety)).
- **Tier-independent.** The baseline records the full finding set, so changing `--certainty-tier` does
  not churn it.
- **Fingerprints are suppression-independent** (4A-2), so a config toggle cannot silently rewrite
  what the baseline records.

The JSON report stamps `commitSha` when the target is a git checkout, and warns on a dirty tree. The
entropy features use that SHA to compute "new since commit X".

The baseline is the only thing standing between a large legacy codebase and a permanently red
pipeline. Adopt it before adding any gate.

---

## Report formats

### JSON (default)

```json
{
  "toolVersion": "1.7.0",
  "commitSha": "0f59d6d...",
  "generatedAtUtc": "2026-10-03T12:34:56Z",
  "findings": [
    {
      "ruleId": "SNP0024",
      "title": "Method 'CollectFiles' does not use instance state and can be static.",
      "message": "...",
      "certainty": "Advisory",
      "category": "Tightening",
      "filePath": "src/Snipper/Analysis/DuplicateFragmentAnalyser.cs",
      "lineNumber": 109,
      "characterOffset": 8,
      "lineText": "    private static IEnumerable<string> CollectFiles(..."
    }
  ],
  "suppression": { "...": "present with --audit-suppressions" },
  "entropyRate": { "...": "present with --entropy-rate" }
}
```

`suppression` and `entropyRate` are optional sections, present only when the corresponding option was
used. Every finding carries `lineText`, so a consumer can verify a finding still matches before
applying it — useful for a gate that auto-fixes.

`filePath` in the report is **relative to the target's directory** (forward slashes), whereas globs
match the **absolute** path. That asymmetry is the source of the glob footgun above.

### SARIF 2.1.0

`--format sarif` emits SARIF 2.1.0 for GitHub Advanced Security, Azure DevOps, and other SAST
consumers.

- Level mapping: `Guaranteed → error`, `High → warning`, `Moderate → warning`, `Advisory → note`.
- Each result carries `properties.certainty` and `properties.category` in addition to its level, so
  you can gate on certainty inside a consumer that only sees SARIF levels.
- The source line is embedded as the region snippet.
- **No `baselineState`.** Baseline filtering happens before serialisation, so SARIF contains only the
  findings that survived it. The SARIF file therefore does not distinguish new from pre-existing — it
  is already filtered. Upload the JSON alongside if you need the distinction.
- **No suppression or entropy section.** Both are JSON-only.

---

## Performance and scaling

### Tuning knobs

| Variable | Default | Purpose |
| --- | --- | --- |
| `SNIPPER_MAX_DOP` | `Environment.ProcessorCount` | Width of a single pass's inner loops (documents, candidates, reference searches). Set to `1` to force sequential execution. |
| `SNIPPER_FANOUT_DOP` | `Environment.ProcessorCount` | Width of the outer analyser fan-out. |

Set both to `1` as the first thing to try when diagnosing a hang or a suspected concurrency problem —
it trades speed for determinism of behaviour, and it is the documented revert path.

**Do not widen the fan-out beyond core count on a loaded CI agent.** Every analyser already saturates
the machine with its own inner loops, so the two nest. Measured on 16 cores against the sample app
(190 findings, identical output at every width): fan-out 1 → 6.9 s, 4 → 5.4 s, 8 → 4.8 s, 16 → 4.6 s.
The naive full-width fan-out *without* the compilation pre-warm measured 33 s — 17 analysers
discovering the same cold compilations at once and serialising on Roslyn's compilation tracker. Snipper
pre-warms compilations for this reason.

### What costs what

| Change | Cost |
| --- | --- |
| Namespace exclusion | **Reduces** work (analyser skips the symbol). |
| Whole-analyser disable | **Eliminates** that analyser's work. |
| Rule off / path glob / severity override | **No** saving — the analysis is already done. |
| `--config-analysis` | Adds settings-file parsing and options-binding resolution. |
| `--duplicate-detection` | One syntax-only shingling pass. Cheap relative to semantic analysis. |
| `--clone-drift` | **No extra analysis.** Reuses SNP0031's clone sets and reads history in two batched git calls, regardless of clone-set count. |

Measured on this repository (39 commits, `Snipper.slnx`): 9.6 s with
`--duplicate-detection --clone-drift` against 7.0 s for a plain baseline run.

The per-member git cost is why 4C batches. A per-path `git log` costs ~90 ms of process spawn on
Windows, which would make per-clone-member lookups unusable on a monorepo with thousands of clone
sets. The whole-repo `git log --name-only` is 100 ms / 19.2 KB, and a batched 30-commit patch fetch is
174 ms / 954.9 KB.

### Scaling to a large monorepo

All of the following are unmeasured at monorepo scale — see [Known limitations](#known-limitations-and-rough-edges).

- **Pass the `.slnx`/`.sln`, not a project**, or you will analyse a fraction of the repo and may pick
  up a different `snipper.json`.
- **Shard by target path.** Analysing each project or solution folder separately parallelises across
  CI runners. The trade-offs are in [CI integration](ci-integration.md#sharding-a-large-monorepo).
- **Baseline first.** Without it, a legacy repo produces a report too large to be actionable and a
  gate that is always red.
- **Disable whole analysers you have decided not to adopt** — the only change that reduces runtime.
- **Exclude generated trees by namespace**, not by path glob, if the generated code has a consistent
  namespace. Path globs pay full analysis cost.

---

## Known limitations and rough edges

Verified against the running tool. Listed so they are not discovered the hard way.

1. **Findings never fail the build.** Exit 0 with findings is normal. No `--fail-on` exists.
2. **Relative path globs silently match nothing.** Globs match absolute paths and are anchored. Write
   `**/src/Generated/**`. Detect dead globs with `--audit-suppressions` and check `obsolete[]`.
3. **Namespace exclusion covers sub-namespaces to any depth,** with no wildcard. Excluding `Company`
   silences the entire codebase.
4. **File-scope findings escape namespace exclusion,** because the lookup goes through the enclosing
   type declaration. SNP0031 is the exception and accepts a special `<global>` marker — config file
   only, since the CLI's syntax check rejects it.
5. **Namespace exclusion retains usage evidence,** so it cannot be used to hide a public API surface
   from the unused-member rules.
6. **The CLI validates namespace syntax; the config file does not.** `--exclude-namespaces` rejects
   `9bad-name`, `has space` and `<global>`; the same entries in `exclude.namespaces` are accepted
   silently and match nothing.
7. **Configs do not merge.** The nearest ancestor `snipper.json` wins outright; narrowing the target
   path can change the rule set.
8. **A misspelled rule id is a warning, not an error,** and does nothing. Audit `obsolete[]` catches it.
9. **Overlapping channels mislead the audit.** A glob covering namespaced-away files reads as
   `suppressedCount: 0` and is flagged obsolete.
10. **Audit namespace rows are aggregate only.** No per-namespace split.
11. **A partially disabled multi-rule analyser still runs.** Disable every rule id, or the cost stays.
12. **The audit's severity-override `detail` string shows an empty original tier** (`"-> Advisory
    (was )"`). Cosmetic, but it means the audit does not tell you what a finding was before you
    overrode it.
13. **Baseline and globs interact asymmetrically.** The baseline grows on first run to include
    findings for newly excluded code, by design.
14. **SARIF carries no baseline state and no entropy or suppression sections.** JSON-only for those.
15. **SNP0031/SNP0032 have not been validated on a large repository.** Everything measured so far is a
    39-commit repo and the SampleApp fixture.
16. **SNP0032 has a known blind spot.** A one-sided fix that is still present removes the copies from
    the clone set, so there is nothing to attribute it to. Clone-set breakup search is the documented
    follow-up.
17. **`--merge` commits are skipped by 4C,** and renames are not followed.

---

## See also

- [CI integration](ci-integration.md) — pipeline recipes, gating strategies, monorepo sharding.
- [Code architecture](code_architecture.md) - for contributors: how the tool loads code, runs analysers, and produces findings.
- [README](../README.md) — installation, quick start, and the rule catalogue.
- [`plan_1_7_0.md`](plan_1_7_0.md) — design rationale and measurements for the 1.7.0 wave.
- [Feature parity roadmap](Snipper-Feature-Parity-Roadmap.md) — what is planned next and why.
