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
snipper <path-to-solution-or-project> [output-file] [--format json|sarif] [--baseline <path>] [--certainty-tier <tier>] [--exclude-namespaces <list>] [--config-analysis]
```

| Argument / Flag | Description |
| --- | --- |
| `<path>` | Target `.sln`, `.slnx`, or `.csproj`. |
| `[output-file]` | Optional report destination. Directory is created if missing. |
| `--format json\|sarif` | Report format (default `json`). SARIF 2.1.0 integrates with GitHub Advanced Security, Azure DevOps, and other SAST consumers. |
| `--baseline <path>` | Baseline file for CI adoption: previously recorded findings are suppressed, only **new** findings are reported, and the baseline is refreshed in place. |
| `--certainty-tier <tier>` | Minimum certainty to report: `guaranteed`, `high`, `moderate`, or `advisory`. E.g. `--certainty-tier high` shows only Guaranteed and High findings. Applies to the terminal table and report file; the baseline always tracks the full finding set so switching tiers never churns it. |
| `--version`, `-v` | Print the tool version and exit. |
| `--config-analysis` | Opt in to configuration binding analysis (SNP0007/SNP0008). **Off by default**: indirect binding through referenced libraries and framework conventions makes its false-positive rate too high for default runs. |
| `--exclude-namespaces <list>` | Suppress findings in the given namespaces (exact match plus sub-namespaces). Repeatable; each occurrence may be a comma-separated list. Code in excluded namespaces still counts as usage evidence — references from it keep other members alive. Applies to SNP0001/0002/0005/0006/0008/0009/0010/0019/0020/0021/0022/0023/0024/0025/0026; assembly-level rules (SNP0003/0004) and JSON keys (SNP0007) have no namespace concept. Note: the baseline refreshes in place, so excluded findings drop out of it and resurface as new if the exclusion is removed later. |

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
| SNP0003 | High (~90%) | `PackageReference` contributing assemblies with zero used symbols. Analyser/build-only packages are skipped. |
| SNP0004 | High (~90%) | `ProjectReference` with zero used symbols from the referenced project. |
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
| SNP0019 | Guaranteed (100%) | Using directives the compiler itself proved unnecessary: unused ordinary and `global using` directives (CS8019), and ordinary usings duplicating a global one (CS8933). |
| SNP0020 | Advisory (~50%) | Blocks of commented-out code (≥2 comment lines that look like code). Doc comments, license headers, URLs, and TODO/FIXME markers are excluded. |
| SNP0021 | High (~90%) | Private fields that are written but never read. Compound assignment, `++`/`--`, and `ref` count as reads; `out` counts as a write. Demoted to Moderate when a serialization attribute (`[JsonInclude]`, `[DataMember]`, `[XmlElement]`, `[JsonProperty]`) is present. Zero-reference fields stay with SNP0001. |
| SNP0022 | High (~90%) | Invocation arguments identical to the parameter's default value. Trailing positional literals only (primitives/string/enum/null); removal is verified by speculative re-binding, so overload traps are never flagged. Caller-info parameters (`[CallerMemberName]` etc.), `params`, named arguments, and conditional-access (`?.`) invocations are excluded; object creation is not covered yet. |
| SNP0025 | High (~90%) | Explicit method type arguments that type inference would infer identically (verified by speculative re-binding — overload rebinds and inference failures are never flagged). Nullable-annotated, `dynamic`, and conditional-access (`?.`) invocations are excluded. |
| SNP0023 | Moderate (~70%) | Hierarchy dead code: virtual members nothing overrides (speculative extension points) and classes declaring virtuals that nothing inherits. Class-level findings suppress member-level ones; overrides, interface implementations, and zero-reference types are excluded. Demoted to Advisory on exported surfaces and friend assemblies; type names spelled in strings/JSON (plugin loading) suppress. |
| SNP0024 | Advisory (~50%) | Tightening invitations: methods that bind no instance state and can be `static` (attributed/virtual/interface methods excluded), fields written only in the constructor that can be `readonly` (`ref`/compound writes exclude; zero-read fields stay with SNP0001/SNP0021), internal classes with no derived types that can be `sealed`. |
| SNP0026 | High (~90%) | Casts the type system proves redundant: identity conversions where the operand is already of the target type (removal cannot change overload resolution). Upcasts, numeric, boxing/unboxing, and user-defined conversions are never flagged. |

Framework entry points are excluded automatically: ASP.NET Core controllers, MediatR/MassTransit/Quartz handlers, hosted services, xUnit facts/theories, `IAsyncLifetime` fixtures and `[CollectionDefinition]` types, `[ModuleInitializer]` methods, entry-point (`Main`) containing types, source-generated members (`[LoggerMessage]`, `[GeneratedRegex]`), DI-registered services, members whose interface contracts have callers, interface implementations and override-chain members (polymorphic dispatch), and types whose names are spelled in string literals or configuration JSON (plugin loading by name). Package findings are additionally suppressed when the reference roots a transitive subtree the project actually uses (removal would break compilation).

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

Config applies at report time, after baseline fingerprinting — toggling it never churns your baseline. Malformed files and unknown entries degrade to warnings, never failures.

## Development

```shell
dotnet build Snipper.slnx
dotnet test Snipper.slnx
```

- `src/Snipper` — the tool itself (analysers under `Analysis/`, CLI/reporting under `Cli/`).
- `test/Snipper.Tests` — xUnit + FluentAssertions suite against a fixture codebase under `TestAssets/SampleApp`.
