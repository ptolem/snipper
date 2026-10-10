# Snipper

Roslyn-based dead-code and unreferenced-dependency analyser for .NET solutions. Snipper loads a `.sln`, `.slnx`, or `.csproj` through `MSBuildWorkspace`, runs certainty-tiered analysis, and reports what can be safely trimmed — read-only, never mutating your code.

## Prerequisites

- **.NET 10 SDK** (or newer) installed on the machine. Snipper binds to the locally installed SDK via `MSBuildLocator` and requires it to evaluate and compile target projects.

## Install

From NuGet.org (once published):

```shell
dotnet tool install --global Snipper
```

From a local build:

```shell
dotnet pack src/Snipper -c Release -o artifacts
dotnet tool install --global Snipper --add-source artifacts --version <version>
```

Pass `--version` explicitly: without it the installer picks the highest version found across *all*
configured sources, which is not necessarily the one you just built.

Update / uninstall:

```shell
dotnet tool update --global Snipper
dotnet tool uninstall --global Snipper
```

## Usage

```shell
snipper <path-to-solution-or-project> [output-file] [--format json|sarif] [--baseline <path>] [--certainty-tier <tier>] [--exclude-namespaces <list>] [--config-analysis] [--duplicate-detection] [--clone-drift] [--audit-suppressions] [--entropy-rate] [--entropy-budget <per-kloc>] [--entropy-ledger <path>] [--entropy-min-lines <n>] [--version]
```

| Argument / Flag | Description |
| --- | --- |
| `<path>` | Target `.sln`, `.slnx`, or `.csproj`. |
| `[output-file]` | Optional report destination. Directory is created if missing. The JSON report stamps the tool version, generation time, and the analysed commit SHA (when the target is a git checkout — a dirty tree prints a warning); every finding carries `lineText`, the trimmed source line it points at, so consumers can verify a finding still matches before applying it. SARIF reports carry the same text as the region snippet. |
| `--format json\|sarif` | Report format (default `json`). SARIF 2.1.0 integrates with GitHub Advanced Security, Azure DevOps, and other SAST consumers. |
| `--baseline <path>` | Baseline file for CI adoption: previously recorded findings are suppressed, only **new** findings are reported, and the baseline is refreshed in place. Independent of every `snipper.json` channel — see [Configuration file](#configuration-file). |
| `--certainty-tier <tier>` | Minimum certainty to report: `guaranteed`, `high`, `moderate`, or `advisory`. E.g. `--certainty-tier high` shows only Guaranteed and High findings. Applies to the terminal table and report file; the baseline always tracks the full finding set so switching tiers never churns it. |
| `--version`, `-v` | Print the tool version and exit. |
| `--config-analysis` | Opt in to configuration binding analysis (SNP0007/SNP0008). **Off by default**: indirect binding through referenced libraries and framework conventions makes its false-positive rate too high for default runs. |
| `--duplicate-detection` | Opt in to duplicate-fragment detection (SNP0031). **Off by default**: token-normalized clone detection flags structurally uniform code, which is duplication by design in most codebases (every workspace analyser shares one project/document loop). Cheap relative to semantic analysis — **+0.7s** on this repository (11.4s → 12.1s, 5-run average). On an 81-project / ~3,000-file monorepo the whole run takes 125-255s depending on machine load, with the marginal cost sitting inside run-to-run noise, so it is never paid unless asked for. |
| `--clone-drift` | Report the **temporal one-sided fix** in a clone set: a commit that changed one copy of a duplicated region without touching its siblings. Emits `SNP0032` — `High` when the change reads as a defensive fix (`null`/guard/`try`/`catch`/bounds), `Advisory` otherwise. **Implies `--duplicate-detection`** and reuses its shingling pass, so there is no second analysis. Costs no extra analysis either: history is read in two batched git calls regardless of how many clone sets exist. **Off by default**, and silent outside a git repository. See [Clone drift](#clone-drift). |
| `--exclude-namespaces <list>` | Suppress findings in the given namespaces (exact match plus sub-namespaces). Repeatable; each occurrence may be a comma-separated list. Code in excluded namespaces still counts as usage evidence — references from it keep other members alive. Applies to SNP0001/0002/0005/0006/0008/0009/0010/0019/0020/0021/0022/0023/0024/0025/0026/0027/0028/0029/0030/0031; assembly-level rules (SNP0003/0004) and JSON keys (SNP0007) have no namespace concept. **`<global>`** excludes file-scope code — files declaring no namespace, such as a top-level-statements `Program.cs` or a file of global usings — which no namespace name can match. Excluded findings are still recorded in the baseline, so removing an exclusion later does not resurface them as new. |
| `--audit-suppressions` | Report what your suppressions are actually hiding, per channel, and flag any that have gone stale. Adds a `suppression` section to the JSON report (omitted entirely when the flag is absent). **Off by default**, and free when you have no suppressions: measured at 0.0s on `Snipper.slnx` with no `snipper.json`, and 0.8s (~10% marginal) with an exclusion configured — the shadow passes reuse the memoized compilations and symbol indexes, so they cost far less than a second full analysis. See [Suppression audit](#suppression-audit). |
| `--entropy-rate` | Report findings added per kLOC changed, normalized by churn, plus a month-over-month history. Requires `--baseline`. Adds an `entropyRate` section to the JSON report (omitted entirely when the flag is absent). **Off by default**, and costs no measurable analysis time (7.0s vs 6.8s on `Snipper.slnx`): the numerator is already computed for the baseline and the denominator is two git calls. See [Entropy rate](#entropy-rate). |
| `--entropy-budget <per-kloc>` | Turn `--entropy-rate` into a gate: exit `3` when a **scored** rate exceeds the budget. **Opt-in, with no default** — no budget is ever applied unless you pass one, so upgrading cannot break an existing build. Fails *open*: a rate that could not be measured never fails the run. |
| `--entropy-ledger <path>` | Append each scored run to a committed JSON ledger, and include per-month aggregates in the report. Implies `--entropy-rate`. Recommended to write from your default branch only; two branches writing the same ledger will conflict. |
| `--entropy-min-lines <n>` | Scoring floor in changed lines (default `50`). Below it the rate is reported but marked **not scored**, so the gate stays silent — a 5-line change carrying one finding would otherwise read as 200/kLOC. |

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Analysis completed (and report written, if requested). |
| `1` | Usage error: missing/invalid arguments, missing target, or a target that could not be opened — including a malformed `.slnx` or a solution whose XML is well-formed but schema-invalid. |
| `2` | Report could not be written to disk. |
| `3` | Entropy budget exceeded (`--entropy-budget`). Distinct from `1` and `2` so a pipeline can tell a policy failure from a tool failure. |

### Examples

```shell
# Terminal report
snipper ./MyMonorepo.slnx

# SARIF for CI upload
snipper ./MyMonorepo.slnx results/snipper.sarif --format sarif

# CI gate: fail review only on newly introduced dead code
snipper ./MyMonorepo.slnx --baseline .snipper-baseline.json
```

## Rules

| Rule | Certainty | What it flags |
| --- | --- | --- |
| SNP0001 | Guaranteed (100%) | Private fields/properties/methods/local functions with zero references. |
| SNP0002 | Guaranteed (100%) | Statements that can never execute (after unconditional `return`/`throw`/etc.). |
| SNP0003 | High (~90%) | `PackageReference` contributing assemblies with zero used symbols — in the declaring project **and in every project it flows to transitively** (hub references are load-bearing for their consumers; `PrivateAssets="all"` blocks the flow). Its exclusive transitive package subtree counts too. Analyser/build-only packages are skipped. |
| SNP0004 | High (~90%) | `ProjectReference` with zero used symbols from the referenced project, its transitive project/package flow, or any downstream consumer. Removing a reference evicts its flow — only flows unreachable by other paths count as evidence. |
| SNP0005 | Moderate (~70%) | `internal` types/members with zero solution-wide references (Advisory if `InternalsVisibleTo` or DI-registered). |
| SNP0006 | Moderate/Advisory | `public` members with zero references; demoted on exported API surfaces and DI-registered types. |
| SNP0007 | Advisory (~50%) | `appsettings*.json` keys not mapped to any options-bound property. Framework sections (`Logging:*`, `ConnectionStrings:*`, ...) are ignored. **Opt-in via `--config-analysis`.** |
| SNP0008 | Advisory (~50%) | Options POCO properties never set in any discovered settings file. **Opt-in via `--config-analysis`.** |
| SNP0009 | Guaranteed (100%) | Local variables, out-vars, deconstruction elements, and pattern variables that are never read. `using` declarations, `ref` locals, and discards are excluded. |
| SNP0010 | Moderate (~70%) | Parameters of private methods/local functions that are never used. Methods referenced as method groups (events, delegates), partial methods, and explicit interface implementations are excluded. |
| SNP0011 | Moderate (~70%) | Projects nothing references: loaded projects with no inbound `ProjectReference` (not entry points/test projects) plus `.csproj` files on disk detached from the workspace. Assembly-name string/JSON evidence (plugin loading) suppresses findings. |
| SNP0012 | Moderate (~70%) | Direct `PackageReference` already supplied transitively by another direct reference at an equal or higher required version on every target framework. |
| SNP0013 | High (~90%) | `PackageReference` that ships inside the project's own shared framework (per the targeting pack's `PackageOverrides.txt`) at a version greater than or equal to the declared one. |
| SNP0018 | High/Moderate | `[Obsolete]` types/members with zero references. High for non-public members and `error: true`; Moderate for public. Overrides and interface implementations are excluded. |
| SNP0019 | Guaranteed (100%) | Using directives the compiler itself proved unnecessary: unused ordinary and `global using` directives (CS8019), and ordinary usings duplicating a global one (CS8933). A verbatim duplicate is called out as such in the message — exactly the flagged occurrence is safe to remove. |
| SNP0020 | Advisory (~50%) | Blocks of commented-out code (≥2 comment lines that look like code). Doc comments, license headers, URLs, and TODO/FIXME markers are excluded. |
| SNP0021 | High (~90%) | Private fields that are written but never read. Compound assignment, `++`/`--`, and `ref` count as reads; `out` counts as a write. Demoted to Moderate when a serialization attribute (`[JsonInclude]`, `[DataMember]`, `[XmlElement]`, `[JsonProperty]`) is present. Zero-reference fields stay with SNP0001. |
| SNP0022 | High (~90%) | Invocation arguments identical to the parameter's default value. Trailing positional literals only (primitives/string/enum/null); removal is verified by speculative re-binding, so overload traps are never flagged. Caller-info parameters (`[CallerMemberName]` etc.), `params`, named arguments, and conditional-access (`?.`) invocations are excluded; object creation is not covered yet. |
| SNP0025 | High (~90%) | Explicit method type arguments that type inference would infer identically (verified by speculative re-binding — overload rebinds and inference failures are never flagged). Nullable-annotated, `dynamic`, and conditional-access (`?.`) invocations are excluded. |
| SNP0023 | Moderate (~70%) | Hierarchy dead code: virtual members nothing overrides (speculative extension points) and classes declaring virtuals that nothing inherits. Class-level findings suppress member-level ones; overrides, interface implementations, zero-reference types, and attribute classes (terminal by convention) are excluded. Demoted to Advisory on exported surfaces and friend assemblies; type names spelled in strings/JSON (plugin loading) suppress. |
| SNP0027 | Moderate (~70%) | Unused member hierarchy: an override family (root + every override reaching it) whose references all stay inside the family's own declarations — the chain keeps itself alive with no external caller. One finding per family at the root; abstract links, interface implementations, and unconfirmed references suppress. |
| SNP0024 | Advisory (~50%) | Tightening invitations: methods that bind no instance state and can be `static` (attributed/virtual/interface methods excluded), fields written only in the constructor that can be `readonly` (`ref`/compound writes exclude; zero-read fields stay with SNP0001/SNP0021), internal classes with no derived types that can be `sealed`, public/internal members on non-exported types referenced only within their containing type that can be `private` (virtuals, attributed members, contract implementations, and framework-evidence members excluded). |
| SNP0026 | High (~90%) | Casts proven redundant: identity conversions where the operand is already of the target type (removal cannot change overload resolution), and implicit-reference upcasts in provably safe positions — whole arguments passing an overload-rebind gate, or fixed-target contexts with an exact type match (explicitly typed declaration/assignment, matching return). Casts on operands with no natural type (collection expressions, target-typed `new`, lambdas, null/default literals) are structural and never flagged, nor are `var` contexts, nested-expression casts, numeric, boxing/unboxing, or user-defined conversions. |
| SNP0028 | High (~90%) | Redundant qualification: `this.` where nothing shadows the member, and qualified type names whose bare form binds identically — both verified by speculative re-binding. Using directives, alias-qualified names, and conditional-access contexts are excluded. |
| SNP0029 | High (~90%) | Empty type members: a public parameterless empty constructor on a non-abstract class (its only constructor — the compiler synthesizes an identical one), and empty destructors (finalization overhead with no work). Private/static constructors, structs, records, initializers, and attributes are excluded. |
| SNP0030 | Advisory (~50%) | Field-like events that are never raised: subscribers (`+=`/`-=`) alone never count as usage. Custom add/remove accessors, interface events and implementations, virtual/override families, and attributed events are excluded. |
| SNP0031 | Advisory (~40%) | Duplicated code fragments across files and projects: Type-1 exact and Type-2 rename-only clones of ≥60 normalized tokens spanning ≥4 lines. Detection is syntax-only (no semantic model is bound), and the 60-token window is **verified token-by-token** before a clone is confirmed — the index is keyed on a 32-bit hash, so verification is what stops two unrelated files from colliding into a finding (fixed in 1.7.3). **Reports one finding per clone set since 1.9.0**, with the remaining copies attached as `relatedLocations`, so a 257-copy set is one finding rather than 257. **One location per file since 1.10.0**, so the copy count equals the file count and a single region is never counted as many copies. **Opt-in via `--duplicate-detection`.** Clones confined to one directory are suppressed — sibling files sharing one skeleton are duplication by design, not copy-paste. Intra-file duplication is not reported. Constant tables and entry-point composition preambles dominate the output by nature: every `const` declaration normalizes to the same token shape. |
| SNP0032 | High/Advisory | **Temporal one-sided fix** in a clone set: a commit that changed one copy of a duplicated region without touching its siblings. `High` when the change reads as a defensive fix (`null`/guard/`try`/`catch`/bounds), `Advisory` otherwise. A commit that *creates* the file is never drift (1.7.2). A commit that brought a copy back into line is reported as a resolution, not new drift. Merge commits are skipped; renames are not followed. **Opt-in via `--clone-drift`**, which implies `--duplicate-detection`. Silent outside a git repository. |

Framework entry points are excluded automatically: ASP.NET Core controllers, MediatR/MassTransit/Quartz handlers, hosted services, xUnit facts/theories, `IAsyncLifetime` fixtures and `[CollectionDefinition]` types, `[ModuleInitializer]` methods, entry-point (`Main`) containing types, source-generated members (`[LoggerMessage]`, `[GeneratedRegex]`), DI-registered services, members whose interface contracts have callers, interface implementations and override-chain members (polymorphic dispatch), and types whose names are spelled in string literals or configuration JSON (plugin loading by name). Framework-dispatched contracts are recognised by simple name — health checks, hosted services, exception handlers, FusionCache serializers, OpenApi transformers, Swashbuckle filters/examples (`IDocumentFilter`/`IOperationFilter`/`ISchemaFilter`/`IExamplesProvider`), xUnit serialization/test-case orderers, the MVC filter family (`IActionFilter`, `IOrderedFilter`, and the exception/result/resource/authorization twins), and MediatR pipeline behaviours — their implementations are framework-instantiated, so the type and its contract members are evidence. Types discovered by reflection assembly scans (`IsSubclassOf` / `IsAssignableFrom` plugin loading) are likewise treated as framework-instantiated. Package findings are additionally suppressed when the reference roots a transitive subtree the project actually uses (removal would break compilation).

## Documentation

| Document | Covers |
| --- | --- |
| [Usage guide](docs/usage.md) | Every option in depth, certainty tiers, the four suppression channels, glob and namespace semantics, the analyser→rule catalogue, report schemas, performance knobs, and known limitations. |
| [Code architecture](docs/code_architecture.md) | For contributors: C4 context/container and class diagrams, a full run sequence, how code is loaded through Roslyn, the analyser contract and shared-index pattern, the filtering order, 24 invariants, and a checklist for adding a rule. |
| [CI integration](docs/ci-integration.md) | Gating strategies for a large monorepo, baseline adoption, report-gating recipes with `jq`/PowerShell, the entropy budget, suppression hygiene, sharding, and ready-to-use GitHub Actions / Azure Pipelines / GitLab CI definitions. |
| [Feature parity roadmap](docs/Snipper-Feature-Parity-Roadmap.md) | Wave-by-wave delivery history and what is planned next, with the reasoning kept rather than rewritten. |
| [Competitive analysis](docs/competitive-analysis.md) | Snipper against the dead-code / duplication / entropy-governance ecosystem, including where it is genuinely behind. |
| [Documentation history](docs/history/README.md) | Superseded plans, wave specs, FP reviews and retrospectives, version-prefixed. Several exist specifically to record no-gos and reversals. |

Release plans and investigations are kept as historical record rather than edited into prose:
[`1_7_3_plan.md`](docs/history/1_7_3_plan.md) (clone-window verification + a fixture that had never compiled),
[`1_7_2_plan.md`](docs/history/1_7_2_plan.md) (clone drift no longer blaming file creations, SNP0019 dedupe),
[`1_7_1_plan.md`](docs/history/1_7_1_plan.md) (unopenable targets exit `1`; an SNP0031 tuning pass measured as a
no-go), and
[`1_7_1_false_positives_investigation.md`](docs/history/1_7_1_false_positives_investigation.md) — the
first-800 sweep of an 81-project monorepo that produced 1.7.2 and 1.7.3, **including two places
where the investigation's own conclusions were wrong** and are corrected in place.

## Current release

`1.7.4` — 28 rule IDs, 19 analysers, 590 tests. Behaviour changes since `1.7.0`:

| Version | Change |
| --- | --- |
| `1.7.1` | An unopenable target — malformed `.slnx`, or one whose XML is schema-invalid — exits `1` with a message instead of crashing. |
| `1.7.2` | SNP0032 no longer reports a commit that *created* the file as a one-sided fix. SNP0019 reports one directive once, not twice for CS8019 + CS8933. |
| `1.7.3` | SNP0031 verifies all 60 window tokens before confirming a clone, so a 32-bit hash collision cannot report two unrelated files as duplicates. The `SampleApp` test fixture had never compiled (8 defects) and now is asserted to. |
| `1.7.4` | `--exclude-namespaces` reaches file-scope code via the shared `<global>` marker: a top-level-statements `Program.cs` or a file of global usings declares no namespace, so no namespace name could match it and its findings were never suppressed. `<global>` was previously SNP0031-only and config-file-only; it now works for every namespace-aware rule and on the CLI. |

`1.9.0` — 29 rule IDs, 20 analysers, 610 tests. **Behaviour changes since `1.8.0`:**

| Version | Change |
| --- | --- |
| `1.8.0` | SNP0033 (classical cyclomatic complexity) under a new `Complexity` category with `--max-complexity`. |
| `1.9.0` | **SNP0031 reports one finding per clone set, not one per copy**, with the other copies as `relatedLocations` (native in both JSON and SARIF). On the reference monorepo 4,375 findings become 1,092 — −45% of the whole report — while every one of the 4,375 copies is still reported. Consumers with an existing SNP0031 baseline must re-baseline once. [`1_9_0_plan.md`](docs/history/1_9_0_plan.md) |

| `1.10.0` | **SNP0031 reports one location per file**, so a set's copy count equals its file count. A set previously claimed more copies than it had places - 1,210 of 4,379 (27.6%) were a second-or-later fragment from a file already present, and 93 sets carried a self-contradictory message. Copies 4,379 -> 3,169 on the reference monorepo; every other rule unchanged. | **Re-baseline** - both counts in the message changed, so a consumer upgrading across 1.9.0 and 1.10.0 re-baselines once. |

| `1.12.0` | **SNP0032 marker precision (backlog F5).** The fix-shape matcher promoted non-defensive text to High - a `null` initialiser, a property named `Count`, a metric named `…-requests.count`, a 1.5 KB JSON fixture, and a method *declaring* `IsNullOrEmpty`. Markers are now shapes; comments and literals come off first; an added line that pre-existed on the removed side is a rearrangement, not a fix. **High 22 -> 9, 0 findings removed**, every other rule byte-identical. The 1.7.1 proposal would have missed its own headline case and demoted a real `!= null` guard. Requires a re-baseline - 13 findings change fingerprint because the tier is in the message. [1_12_0_plan.md](docs/history/1_12_0_plan.md) |
| `1.11.0` | **Wire-contract evidence for SNP0006/SNP0018 (backlog F2 + F3).** Minimal-API typed-result returns seed the DTO closure; the closure now walks base types, so a leaf's inherited response properties are reached. Plus enum ordinal contracts and extension-holder rescue. **SNP0018 49 -> 1** (that 1 is a verified true positive), **SNP0006 1,647 -> 1,558**, plus 3 SNP0024 nobody asked for. **140 suppressed, 0 new**, every other rule byte-identical. Also fixes a cross-compilation symbol-identity bug that had left the closure largely inert on large solutions. Requires a re-baseline. [1_11_0_plan.md](docs/history/1_11_0_plan.md) |

## Configuration file

Snipper discovers `snipper.json` by walking up from the target solution/project directory (first file wins). Schema `"version": 1`:

```json
{
  "version": 1,
  "rules": { "SNP0010": "off", "SNP0018": "advisory" },
  "exclude": {
    "namespaces": ["Company.Generated"],
    "paths": ["**/Generated/**", "**/Legacy/**"]
  }
}
```

- `rules`: per-rule `off` or a severity override (`advisory`/`moderate`/`high`/`guaranteed`). An analyser whose rules are all `off` never runs.
- `exclude.namespaces`: unioned with `--exclude-namespaces`; suppresses findings, never usage evidence.
- `exclude.paths`: glob patterns (`**`, `*`, `?`) matched against finding paths — findings are filtered, evidence is retained.

Malformed files and unknown entries degrade to warnings, never failures.

**No suppression channel can churn your baseline.** Every configuration produces an identical
baseline, so adding or removing a suppression never turns untouched code into a wall of "new"
findings. Two mechanisms get there:

| Channel | How it is kept churn-free |
| --- | --- |
| `exclude.paths`, `rules` severity overrides, `--certainty-tier` | Applied at report time, **after** fingerprinting. |
| `exclude.namespaces`, `rules: "off"` | These suppress findings *before* they exist — namespace exclusions run inside the analysers, and a sole-rule analyser is never run at all. So when either is configured **and** you use `--baseline`, Snipper fingerprints the suppression-independent set: every finding that exists, ignoring config. Costs one extra analysis pass (~10% marginal, measured). |

Two consequences worth stating plainly:

- **Your baseline file legitimately grows the first time you run a version with this fix**, because it now also records findings for code you have excluded. That is the price of a baseline that means the same thing regardless of configuration.
- **Findings you suppress stay out of the report.** They are baselined, not reported — suppression still works exactly as before.

Run `--audit-suppressions` to see what those suppressions are hiding.

## Suppression audit

`--audit-suppressions` answers the question suppressions exist to raise: *what fraction of our "clean" status is suppression buying, and what is it hiding?*

```shell
snipper ./MyMonorepo.slnx report.json --audit-suppressions
```

Suppressions work through five channels, and they do not all work the same way. Two of them remove findings before those findings exist, so an honest count needs the tool to re-run analysis with suppressions lifted — that shadow run is why the flag is opt-in, and why it is skipped entirely when there is nothing to audit.

The JSON report gains a `suppression` section (omitted completely when the flag is absent, so existing consumers see byte-identical output):

```json
{
  "suppression": {
    "shadowAnalysisRan": true,
    "totals": {
      "findingsAnalysed": 221,
      "findingsHiddenByShadow": 35,
      "findingsDropped": 0,
      "findingsDowngraded": 5,
      "findingsAfterSuppression": 221,
      "hiddenDebtPercent": 14
    },
    "disabledRules": [{ "channel": "DisabledRule", "selector": "SNP0024", "suppressedCount": 30, "ruleIds": ["SNP0024"] }],
    "namespaceExclusions": [{ "channel": "NamespaceExclusion", "selector": "CoreLib.Shadowing, Does.Not.Exist", "suppressedCount": 5, "detail": "aggregate over 2 namespace(s); per-namespace split not available" }],
    "pathGlobs": [{ "channel": "PathGlob", "selector": "**/NoSuchDir/**", "suppressedCount": 0 }],
    "severityOverrides": [{ "selector": "SNP0018", "downgradedCount": 5, "detail": "-> Advisory (was Highx3, Moderatex2)" }],
    "obsolete": [
      { "channel": "NamespaceExclusion", "selector": "Does.Not.Exist", "confidence": "Certain", "reason": "namespace is not declared anywhere in the analysed solution" },
      { "channel": "PathGlob", "selector": "**/NoSuchDir/**", "confidence": "Certain", "reason": "matches no file in the analysed tree" }
    ]
  }
}
```

`hiddenDebtPercent` is the headline: `(dropped + hidden-before-analysis) / total debt`. On Snipper's own repository, disabling a single rule that emits one `Advisory` finding reports **33%** — one suppressed finding is a third of everything the tool finds there.

**Stale suppressions** are flagged with a confidence level, because the evidence genuinely differs:

- `Certain` — the target provably does not exist: a glob matching no file in the tree, or an exclusion for an undeclared namespace. Safe to delete.
- `Suspected` — the suppression matched nothing this run, but that can be legitimate. A rule that produced no findings may simply be clean; a glob over clean generated code is doing its job. Review, don't auto-delete.

Two details worth knowing. A severity override can *cause* a suppression rather than soften one: `CertaintyTier` runs `Guaranteed=1 … Advisory=4` and the floor keeps `Certainty <= floor`, so a `High → Advisory` override pushes a finding *past* a `Moderate` floor. The audit credits the drop to the override rather than the floor. And namespace exclusions are reported as a single aggregate — per-namespace counts would need one shadow pass per namespace — with the `detail` field saying so rather than implying a split exists.

## Entropy rate

`--entropy-rate` answers the question a maintainer is asked in a quarterly review: *are we controlling entropy, or just not looking at the stock?* Every tool ships entropy **stock** (how many findings) or a **delta** (new since a reference). This ships a **rate** - findings added per kLOC changed - which is the only one of the three that is comparable across teams of different sizes and cadences.

```shell
snipper ./MyMonorepo.slnx report.json --baseline snipper.baseline.json --entropy-rate
```

### Why the denominator is not "this pull request"

The obvious definition - new findings divided by lines changed in the PR - does not survive contact with real numbers. Holding code quality constant at **exactly one new finding**:

| Change size | Rate |
| --- | --- |
| 5 lines | **200.0** /kLOC |
| 50 lines | 20.0 /kLOC |
| 251 lines (median commit on Snipper's own repo) | 4.0 /kLOC |
| 2000 lines | **0.5** /kLOC |

That is a **400x spread driven entirely by commit size**. A fixed budget would therefore be a measure of pull-request size, not of entropy: a budget of 5/kLOC passes a 251-line PR carrying one finding and fails a 5-line PR carrying one finding.

So the denominator is anchored to the commit your baseline was stamped at. Snipper records that commit in the baseline file, and the changed-line count is taken over exactly the range the recorded findings came from - numerator and denominator provably cover the same span. Small ranges are handled by an explicit floor (`--entropy-min-lines`, default 50) rather than being averaged away: below it the rate is reported but marked **not scored**, and the gate stays silent. For a stable trend, use the per-month aggregate, whose denominator is large by construction.

### The gate fails open, on purpose

`--entropy-budget <per-kloc>` exits `3` when a **scored** rate exceeds the budget. Every other case is silent, because a metric that can break a build when git is missing trains people to switch it off. A rate that could not be measured is never `0.00`; it is one of seven named statuses, each with a printed reason:

| Status | Meaning |
| --- | --- |
| `Scored` | Both halves measured; the rate is meaningful. |
| `BaselineSeeded` | No baseline existed, so every finding counted as new. Re-run to score. |
| `NoBaselineReference` | The baseline predates commit stamping. Re-baseline to score. |
| `NotAGitRepository` | Not a git repository, or the revisions could not be read. |
| `DirtyWorkingTree` | Uncommitted changes mean the analysed tree is not the analysed commit. |
| `UnchangedRange` | The baseline commit is HEAD; nothing changed. |
| `BelowMinimumChange` | Under the scoring floor. The rate is still shown. |

**There is no default budget.** Upgrading cannot break an existing build, and the ledger has no history yet from which to derive a defensible default.

### The operational requirement this creates

Because a dirty tree is refused, **your baseline must be committed** and build output must be gitignored, or the rate can never be scored locally. That is the intent of a committed baseline anyway, but it is a real constraint rather than an implementation detail.

### Ledger

`--entropy-ledger <path>` appends each *scored* run to a committed JSON file - reviewable in a pull request, no service and no dashboard. Entries are replace-or-insert keyed on commit SHA, so a CI re-run cannot duplicate a row, and the file is sorted by commit rather than chronologically so a re-run produces a byte-identical file and diffs stay clean. The gate never writes; only an explicit `--entropy-ledger` run does. Write from your default branch, since two branches writing the same ledger will conflict.

Only scored runs are recorded - a run that seeded a baseline or sat below the floor has no rate, and rows of nulls would make the ledger look fuller than the data is.

```json
{
  "entropyRate": {
    "status": "Scored",
    "scored": true,
    "newFindings": 12,
    "resolvedFindings": 4,
    "linesChanged": 1840,
    "baselineCommitSha": "25b9a6a...",
    "headCommitSha": "853625b...",
    "minimumLines": 50,
    "findingsPerKloc": 6.52,
    "budget": 10.0,
    "budgetExceeded": false,
    "monthly": [
      { "month": "2026-09", "newFindings": 88, "resolvedFindings": 31, "linesChanged": 21400, "findingsPerKloc": 4.11 }
    ]
  }
}
```

`resolvedFindings` is a count, not a repair record: a fingerprint also disappears when its file is deleted or its message is rewritten.

Not included: per-team attribution via CODEOWNERS. It needs CODEOWNERS parsing plus mapping each finding to an owner, and is deferred rather than half-shipped.

The `entropyRate` section lands in the **JSON** report only, as with the suppression audit. With `--format sarif` the console table and the budget gate still work, but the SARIF file carries no entropy section.
## Clone drift

`--clone-drift` is the missing half of duplicate detection. SNP0031 tells you two regions are identical; 4C tells you when one copy of such a region was changed and the others were not.

```shell
snipper ./MyMonorepo.slnx report.json --clone-drift
```

PMD's CPD documentation concedes why this is worth building: *"failure to keep the code in sync may mean automated tools will no longer recognise these blocks as duplicates... we thus advise developers to use CPD to help remove duplicates, not to help keep duplicates in sync."* A vendor stating the valuable half of duplication management is unassisted. jscpd's `--blame` yields authors and dates but no divergence analysis. Nothing computes it.

### What it actually reports, and the subtlety that shapes it

SNP0031 builds clone sets from **current** text, so its members are token-identical *now*. The consequence is not obvious: **a copy that receives a fix stops being identical, and has already dropped out of the set.** So "one copy of a clone set got a fix" cannot be expressed in the present tense at all — it only exists historically.

4C therefore reports the **event**, not the standing state:

```
SNP0032  High  One-sided defensive fix in a clone set of 2 copies: commit a1592e4
        ("fix(A): guard empty label", 2026-10-03) changed this copy without touching
        CloneFixtures.cs:1; an equivalent change reached the sibling later (62f48e4 on 2026-10-03).
```

Read that as: copy A got a guard; copy B did not, until a later commit. **The second half of the message is the actionable part** — it dates the window in which the copies were out of sync, or tells you no equivalent change ever arrived. A commit that *brought a copy into line* is reported as a resolution, not as new drift, so one inconsistency yields one finding rather than one per link in the chain.

**The honest limit:** if the one-sided fix is still in place, the copies are no longer a clone set and 4C says nothing about them today. Detecting that case needs clone-set *breakup* search over history, which is the documented follow-up.

### Tiers

| Tier | Condition |
| --- | --- |
| `High` | One-sided, and the change reads as a defensive fix. |
| `Advisory` | One-sided, with no fix-shaped marker: real drift, but not evidence of a defect. |

**A commit that creates the file is not drift.** In 1.7.1, 90 of 302 findings on an 81-project
monorepo said a commit that *created* a file was a one-sided fix — a file's first appearance cannot
leave a sibling untouched, because there was no sibling to begin with. Since `1.7.2` the patch parser
records whether a hunk is a file creation and the detector ignores those hunks. A sibling added in a
*later* commit still counts as drift, because that genuinely is one copy of a region existing without
the other.

Tier discipline matters more here than anywhere else in the tool. "One copy changed" is a heuristic, not proof, so the High tier is gated on the change actually looking like a fix, behind the one-sided requirement, which is the real filter.

**The marker test stopped being loose in `1.12.0`.** It used to match bare words (`null`, `Length`, `Count`, `try`) anywhere in the added patch text, which meant a `string? url = null;` initialiser, a property declared `public int Count`, a metric named `"...-requests.count"`, a 1.5 KB JSON fixture and a method *declaring* `IsNullOrEmpty` all reached High. It now requires a **shape** - `!= null`, `is null`, `?.`, `??`, or a bounds name followed by a comparison - strips comments and string literals before matching, and treats an added line whose text already existed on the removed side as a rearrangement rather than a contribution. On the reference monorepo that took High from 22 to 9 with **no finding removed**: the drift is still reported, it just stops asserting a defect. `if (list.Count > 0)` still qualifies, because it is a real bounds guard.

### Cost and behaviour

Enabling drift costs **no extra analysis**. The clone sets SNP0031 already proved in the same pass are handed to the detector, and history is read in **two batched git calls** regardless of how many clone sets exist — per-path `git log` costs ~90 ms of process spawn on Windows, which would have made per-member lookups unusable.

`--clone-drift` implies `--duplicate-detection` rather than erroring, since the two share one shingling pass. Outside a git repository it emits nothing and fails nothing. Merge commits are skipped, and renames are not followed.

### It found a bug in its own author

The first dogfood run reported SNP0032 `High` on Snipper's own repository: the new git runner duplicated an existing one, and a stderr-drain fix applied earlier in the session had landed in only one of the two copies. The fix was to delete the duplication — `GitProcess.cs` is now the sole place Snipper shells out to git — rather than suppress the finding. Post-fix, Snipper reports zero clone drift on itself.

## Development

```shell
dotnet build Snipper.slnx
dotnet test Snipper.slnx
```

- `src/Snipper` — the tool itself (analysers under `Analysis/`, CLI/reporting under `Cli/`).
- `test/Snipper.Tests` — xUnit + FluentAssertions suite against a fixture codebase under `TestAssets/SampleApp`.
