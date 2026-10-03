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
dotnet pack src/Snipper -c Release
dotnet tool install --global Snipper --add-source src/Snipper/bin/Release
```

Update / uninstall:

```shell
dotnet tool update --global Snipper
dotnet tool uninstall --global Snipper
```

## Usage

```shell
snipper <path-to-solution-or-project> [output-file] [--format json|sarif] [--baseline <path>] [--certainty-tier <tier>] [--exclude-namespaces <list>] [--config-analysis] [--duplicate-detection] [--audit-suppressions]
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
| `--duplicate-detection` | Opt in to duplicate-fragment detection (SNP0031). **Off by default**: token-normalized clone detection flags structurally uniform code, which is duplication by design in most codebases (every workspace analyser shares one project/document loop). Costs ~53s on a 3,000-file monorepo, so it is never paid unless asked for. |
| `--exclude-namespaces <list>` | Suppress findings in the given namespaces (exact match plus sub-namespaces). Repeatable; each occurrence may be a comma-separated list. Code in excluded namespaces still counts as usage evidence — references from it keep other members alive. Applies to SNP0001/0002/0005/0006/0008/0009/0010/0019/0020/0021/0022/0023/0024/0025/0026/0027/0028/0029/0030/0031; assembly-level rules (SNP0003/0004) and JSON keys (SNP0007) have no namespace concept. SNP0031 resolves the namespace syntactically through the enclosing namespace declaration; for a fragment at file scope, exclude it with the special name `<global>`. Excluded findings are still recorded in the baseline, so removing an exclusion later does not resurface them as new. |
| `--audit-suppressions` | Report what your suppressions are actually hiding, per channel, and flag any that have gone stale. Adds a `suppression` section to the JSON report (omitted entirely when the flag is absent). **Off by default**, and free when you have no suppressions: measured at 0.0s on `Snipper.slnx` with no `snipper.json`, and 0.8s (~10% marginal) with an exclusion configured — the shadow passes reuse the memoized compilations and symbol indexes, so they cost far less than a second full analysis. See [Suppression audit](#suppression-audit). |

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Analysis completed (and report written, if requested). |
| `1` | Usage error: missing/invalid arguments, missing target, or unopenable solution. |
| `2` | Report could not be written to disk. |

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
| SNP0031 | Advisory (~40%) | Duplicated code fragments across files and projects: Type-1 exact and Type-2 rename-only clones of ≥60 normalized tokens spanning ≥4 lines. Detection is syntax-only (no semantic model is bound). **Opt-in via `--duplicate-detection`.** Clones confined to one directory are suppressed — sibling files sharing one skeleton are duplication by design, not copy-paste. Intra-file duplication is not reported. Constant tables and entry-point composition preambles dominate the output by nature: every `const` declaration normalizes to the same token shape. |

Framework entry points are excluded automatically: ASP.NET Core controllers, MediatR/MassTransit/Quartz handlers, hosted services, xUnit facts/theories, `IAsyncLifetime` fixtures and `[CollectionDefinition]` types, `[ModuleInitializer]` methods, entry-point (`Main`) containing types, source-generated members (`[LoggerMessage]`, `[GeneratedRegex]`), DI-registered services, members whose interface contracts have callers, interface implementations and override-chain members (polymorphic dispatch), and types whose names are spelled in string literals or configuration JSON (plugin loading by name). Framework-dispatched contracts are recognised by simple name — health checks, hosted services, exception handlers, FusionCache serializers, OpenApi transformers, Swashbuckle filters/examples (`IDocumentFilter`/`IOperationFilter`/`ISchemaFilter`/`IExamplesProvider`), xUnit serialization/test-case orderers, the MVC filter family (`IActionFilter`, `IOrderedFilter`, and the exception/result/resource/authorization twins), and MediatR pipeline behaviours — their implementations are framework-instantiated, so the type and its contract members are evidence. Types discovered by reflection assembly scans (`IsSubclassOf` / `IsAssignableFrom` plugin loading) are likewise treated as framework-instantiated. Package findings are additionally suppressed when the reference roots a transitive subtree the project actually uses (removal would break compilation).

## Configuration file

Snipper discovers `snipper.json` by walking up from the target solution/project directory (first file wins). Schema `"version": 1`:

```json
{
  "version": 1,
  "rules": { "SNP0010": "off", "SNP0018": "advisory" },
  "exclude": {
    "namespaces": ["Company.Generated"],
    "paths": ["**/Generated/**", "src/Legacy/**"]
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

## Development

```shell
dotnet build Snipper.slnx
dotnet test Snipper.slnx
```

- `src/Snipper` — the tool itself (analysers under `Analysis/`, CLI/reporting under `Cli/`).
- `test/Snipper.Tests` — xUnit + FluentAssertions suite against a fixture codebase under `TestAssets/SampleApp`.
