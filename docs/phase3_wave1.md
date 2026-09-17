# Phase 3 — Wave 1 Team Spec (1A config, 1B SNP0019, 1C SNP0020)

**Target release:** 1.2.0 · **Roadmap:** [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md) · **Baseline:** 1.1.3 (98 tests)
**Status:** APPROVED. Implement in merge order: **1A → 1B → 1C → cross-cutting → release.**

## Spike results (CS8019 — resolved 2026-09-15)

In-memory `CSharpCompilation` (Roslyn 5.9), `SemanticModel.GetDiagnostics()`:

- **CS8019 "Unnecessary using directive" fires for ordinary unused usings ✓ and for unused `global using` directives ✓** (Hidden severity). No fallback analysis needed — SNP0019 covers both from day one.
- **CS8933 "using directive appeared previously as global using"** fires on the *ordinary* using that duplicates a global one (deterministic: global directives are processed first). Same semantic family ("this using contributes nothing") → included in SNP0019 as a companion diagnostic.
- Compiler Hidden diagnostics never affect builds; Snipper surfaces them as Guaranteed findings.

## Working agreements (carried)

Red-green fixture-first; new fixture scenarios in NEW files (never `DeadCode.cs`); syntax pre-filters before semantic calls; `FrozenSet`/`FrozenDictionary`; no `dynamic`; no exceptions for flow control; sequential semantic binding; UK spelling "Analyser" (MSBuild literal `"Analyzer"` in `ProjectFileReader` stays US). Test conventions: xUnit + FluentAssertions, Contain/NotContain on names (never counts except the SNP0002 `ContainSingle` anchor), `[Collection("SampleSolution")]` for workspace tests.

---

## Story 1A — `snipper.json` configuration

### Design

**File discovery:** from the target's directory, walk up toward the filesystem root; first `snipper.json` wins. Missing file → defaults (no config). Grey console note when a config is loaded.

**Schema (`"version": 1`, additive-only evolution):**

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

- `rules`: per-rule override. Values: `off` | `advisory` | `moderate` | `high` | `guaranteed` (case-insensitive). Unknown rule ids / unknown severities → console warning, entry ignored (tolerant).
- `exclude.namespaces`: unioned with `--exclude-namespaces` (additive; no conflict possible in v1).
- `exclude.paths`: glob patterns matched against each finding's full path (normalised to `/`, case-insensitive). **Findings are filtered, usage evidence is never filtered** — applied at report time alongside the certainty-tier filter and AFTER baseline fingerprinting (baseline tracks the raw finding set; toggling config never churns the baseline file — same philosophy as the existing tier filter).

**Rule→analyser mapping:** `IWorkspaceAnalyser` gains `IReadOnlyCollection<string> RuleIds { get; }` (one line per analyser; `UnreferencedPackageAnalyser` → SNP0003+SNP0004, `UnusedNonPrivateMemberAnalyser` → SNP0005+SNP0006, `ConfigurationBindingAnalyser` → SNP0007+SNP0008, the rest single-rule). An analyser whose *entire* rule set is `off` is skipped entirely (saves its runtime, not just its output).

### New files / changes (src)

| File | Content |
|---|---|
| `Cli/SnipperConfig.cs` | `SnipperConfig` record (`Version`, `FrozenDictionary<string,CertaintyTier?> RuleOverrides` (`null` = off), `FrozenSet<string> ExcludedNamespaces`, `FrozenSet<string> ExcludedPathGlobs`, `static SnipperConfig Empty`). |
| `Cli/SnipperConfigLoader.cs` | `Load(targetPath, out SnipperConfig?, warnings)`: discover + parse via `JsonReportSerializerContext` (source-gen, add entries); malformed JSON / unreadable / unsupported `version` → warning + Empty. `off` decoded as `null` override. |
| `Cli/GlobPattern.cs` | glob → regex (`**` → `.*`, `*` → `[^/]*`, `?` → `.`), compiled+cached; `IsMatch(normalizedPath)`. |
| `Cli/FindingFilter.cs` | Pure static: `Apply(findings, config)` — drop `off` rules, apply severity overrides, drop path-glob matches. `IsAnalyserEnabled(ruleIds, config)`. |
| `Cli/CliRunner.cs` | Load config early; union namespaces into `AnalysisExclusions`; skip disabled analysers; `FindingFilter.Apply` at report time (after baseline, with the tier filter); grey notes. |

### Test matrix (`SnipperConfigLoaderShould`, `GlobPatternShould`, `FindingFilterShould` — pure, no workspace)

1. Missing file → Empty, no warnings.
2. Malformed JSON → Empty + warning, no throw.
3. `version: 99` → Empty + warning.
4. Valid config parses: rule override + `off` + namespaces + globs; walk-up discovery finds parent-dir config.
5. Unknown rule id / unknown severity → warning, entry ignored, rest applies.
6. Glob: `**/Generated/**` matches nested path; `src/*.cs` does not cross directories; `?` single char; case-insensitive.
7. Filter: `off` rule dropped; severity overridden; globbed finding dropped; non-matching untouched; ordering preserved.
8. `IsAnalyserEnabled`: all-off → false; one rule live → true.

---

## Story 1B — SNP0019 Unused using directives (Guaranteed)

### Design

`UnusedUsingDirectiveAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0019"]`. New `FindingCategory.UnusedUsingDirective = 14`.

Per document (skip `ShouldSkipDocument`): `semanticModel.GetDiagnostics(ct)` → diagnostics with `Id is "CS8019" or "CS8933"`. For each: locate `UsingDirectiveSyntax` at the diagnostic span for a specific message — `Using directive 'System.Text' is unnecessary (never used).` / `'System' duplicates a global using directive.`; fall back to the compiler message when the node shape is unexpected. Namespace-exclusion via the existing node-based overload. **Certainty: Guaranteed** (compiler-computed). No analysers-parallelism needed — diagnostics come with the model.

### Fixture (new files)

- `CoreLib/UnusedUsings.cs`: `using System.Collections.Generic;` (used — `List<int>`), `using System.Text;` (CS8019), `using System.Xml.Linq;` (CS8019), `using System;` (CS8933 — duplicates the ImplicitUsings generated global using). Namespace names chosen to avoid the generated ImplicitUsings set.
- `App/GlobalUsings.cs`: `global using System.Text;` (unused across App → CS8019). *Not* `System.Linq` et al. — those are in the SDK's generated set and would produce non-deterministic CS8933 attribution.

### Test matrix (`UnusedUsingDirectiveAnalyserShould`, `[Collection("SampleSolution")]`)

1. Flag CS8019 usings: findings in `UnusedUsings.cs` at the `System.Text` and `System.Xml.Linq` lines, Certainty Guaranteed.
2. Flag CS8933 duplicate: finding at the `using System;` line.
3. Flag unused global using: finding in `App/GlobalUsings.cs`.
4. Not flag the used `System.Collections.Generic` using.
5. Not flag anything in files with no unused usings (spot: `DeadCode.cs`).

---

## Story 1C — SNP0020 Commented-out code (Advisory)

### Design

`CommentedCodeAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0020"]`. New `FindingCategory.CommentedOutCode = 15`.

Per document (skip `ShouldSkipDocument`), syntax-only over `root.DescendantTrivia()`:

1. Collect `SingleLineCommentTrivia` runs (consecutive lines, only EOL between) and `MultiLineCommentTrivia` blocks. `///` doc comments are `DocumentationCommentTrivia` — a different kind, naturally excluded.
2. Candidate = run of ≥2 comment *lines* (multi-line blocks: ≥2 text lines after stripping `/* */ *`).
3. **Code-likeness:** a non-empty line is code-like when it contains `;`, `{`, or `}`, or starts with a keyword (`if|for|foreach|while|return|var|using|switch|try|catch|else|do|throw|new|await|public|private|internal|protected|static`). Lines containing `://` (URLs) count as prose. Flag when ≥60% of non-empty lines are code-like.
4. **Skip blocks:** first line within the file's first 5 lines containing `copyright`/`licensed under` (license headers, case-insensitive); first non-empty line starting with `TODO`/`FIXME`/`HACK`/`NOTE` markers.
5. Finding at block start: `"<n> line(s) of commented-out code."`, Advisory. Namespace exclusion via node-based overload (trivia → owning token → enclosing type).

### Fixture (new file)

`CoreLib/CommentedOut.cs`: one 4-line commented-out block (`// var total = …; // if (…) // { // return …;`) → flagged; one prose block (plain sentence + `// TODO: …` + a `https://…` line) → not flagged.

### Test matrix (`CommentedCodeAnalyserShould`, `[Collection("SampleSolution")]`)

1. Flag the code block: SNP0020 in `CommentedOut.cs`, Advisory.
2. Not flag the prose/TODO/URL block (single finding in that file).
3. Not flag prose comments elsewhere: no SNP0020 in `DeadCode.cs`, `Worker.cs`.

---

## Cross-cutting (bundled)

1. **fixture2 generator:** `test/Fixtures/Generate-Fixture2.ps1` — checked-in script that materialises the 18-finding regression solution (App/Core/Orphan + appsettings scenarios) under a given path; documents the expected 18 default / 20 `--config-analysis` counts in a header comment. Replaces the silently-rotted `%TEMP%` fixture.
2. **Dogfood gate:** self-run must stay 0 findings after all three stories (new rules: assert no SNP0019/SNP0020 on Snipper's own source — any finding is a bug in the rule, fix before release).
3. README rules-table rows SNP0019/SNP0020 + `snipper.json` section; `competitive-analysis.md` §5 check-offs (items 1–3 → shipped).

## Release

Bump 1.2.0 → `dotnet pack` → `dotnet tool update` → `snipper --version` → self-run verification → summarise.

## Judgement calls (pre-approved)

- CS8933 rides inside SNP0019 rather than its own rule id — same semantic family, same tier, one less rule to configure.
- Path globs filter findings only; a per-rule CLI flag is NOT added in v1 (config file is the mechanism; CLI stays small).
- Analyser-level `RuleIds` lives on the interface (not a parallel registry) — one source of truth, one line per analyser.
- SNP0020 stays sequential (KISS); parallelism was "allowed" by the roadmap, not required — candidates are trivia-cheap.
