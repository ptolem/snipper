# Phase 3 — Wave 2 Team Spec (2A SNP0021; 2B design-sketch only, DEFERRED)

**Target release:** 1.3.0 (2A) / 1.3.1 (2B) · **Roadmap:** [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md) · **Baseline:** 1.2.0 (16 rules, 127 tests)
**Status:** 2A SHIPPED 2026-09-18 as 1.3.0 (17 rules, 141 tests). 2B SHIPPED 2026-09-18 as 1.3.1 (19 rules, 158 tests). Merge order was: **2A → cross-cutting → 1.3.0 → 2B → cross-cutting → 1.3.1.**

## Spike results (speculation APIs — resolved 2026-09-18)

In-memory `CSharpCompilation` (Roslyn 5.9), `SemanticModel.GetSpeculativeSymbolInfo(position, rewrittenInvocation, SpeculativeBindingOption.BindAsExpression)` on a syntax-rewritten (never-inserted) invocation node:

- **A1 — rebinding trap is detectable:** `Foo(int a, int b = 2)` + `Foo(int a)` overloads; `Foo(5, 2)` binds `Foo(int, int)`; speculatively removing the `2` rebinds to `Foo(int)`. `SymbolEqualityComparer.Default` reports the symbols different.
- **A2 — safe removal is provable:** with no competing overload, the speculatively-rebound symbol compares **equal** to the original.
- **A3 — literal-vs-default check:** `IParameterSymbol.HasExplicitDefaultValue` / `ExplicitDefaultValue` + `SemanticModel.GetConstantValue(argument)` reliably compares the literal to the metadata default (`2` vs `3` mismatch detected).
- **B1 — redundant type args provable:** `Echo<int>(5)` with type list stripped speculatively binds the **identical constructed symbol** (`SymbolEqualityComparer.Default` on the constructed `Echo<int>(int)`, inferred type argument `int` matches).
- **B2 — inference-change trap detectable:** with a competing non-generic `Echo(int)`, stripping `<int>` rebinds to it — symbols differ.

**Conclusion:** both SNP0022 and SNP0025 are implementable with a **speculation gate**: flag only when the value/binding preconditions hold *and* the speculatively re-bound symbol equals the original under `SymbolEqualityComparer.Default`. Spike probe was deleted after capture.

## Working agreements (carried)

Red-green fixture-first; new fixture scenarios in NEW files (never `DeadCode.cs`); syntax pre-filters before semantic calls; `FrozenSet`/`FrozenDictionary`; no `dynamic`; no exceptions for flow control; sequential semantic binding (`ConcurrentBuild=false`); locally-defined attribute stand-ins where name-matching applies. Test conventions: xUnit + FluentAssertions, Contain/NotContain on names (never counts except the SNP0002 `ContainSingle` anchor), `[Collection("SampleSolution")]` for workspace tests.

---

## Story 2A — SNP0021 Field written, never read (High)

### Design

`WriteOnlyFieldAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0021"]`. New `FindingCategory.WriteOnlyField = 16`.

**Boundary with SNP0001 (locked):** zero references → SNP0001 (Guaranteed). SNP0021 requires **≥1 write reference and 0 read references**. A declarator initializer (`private int _x = 5;`) is *not* a reference — such fields remain SNP0001's and SNP0021 never double-reports.

**Flow** (mirrors `UnusedPrivateMemberAnalyser`, same cost profile):

1. Per document (skip `ShouldSkipDocument`): syntax gate — `VariableDeclaratorSyntax` under a `FieldDeclarationSyntax` with private accessibility (same `HasPrivateAccessibility` shape as SNP0001). Event-field declarations are a different node kind — naturally excluded. `const` fields cannot be written — excluded by modifier check.
2. `GetDeclaredSymbol` → `IFieldSymbol` with `DeclaredAccessibility == Private`. Skip: `IsConst`, `IsFixedSizeBuffer` (unsafe fixed buffers), `RefKind != RefKind.None` (ref fields), implicit/backing fields (never surface through the syntax gate anyway).
3. `ExclusionEngine.ShouldExclude(field)` + `IsNamespaceExcluded` — same guards as SNP0001 (also keeps `[Obsolete]` fields exclusive to SNP0018).
4. `SolutionUsageIndex.GetDocumentsUsingName(project, name)` — private fields are only reachable from within their containing type (incl. nested types), which lives in one project: project-scoped search is sound. Empty set → zero references → skip (SNP0001 territory).
5. `SymbolFinder.FindReferencesAsync` restricted to candidate documents → all `ReferenceLocation`s. No locations → skip (SNP0001).
6. **Classify every location** by resolving its syntax node (`SimpleNameSyntax`) and inspecting its syntactic role — new `FieldReferenceClassifier` (internal static, pure). **Any** location classified Read or ReadWrite suppresses the finding; any *candidate* (unconfirmed) location is treated as a Read (conservative). All locations Write → finding.
7. **Demotion:** field carries a serialization attribute → Moderate instead of High: `JsonInclude`, `DataMember`, `XmlElement` (roadmap) **plus Newtonsoft `JsonProperty`** (judgement call 2). Name-matched via `attr.AttributeClass?.Name` with/without the `Attribute` suffix — the established stand-in pattern.

**Read/write classification matrix (authoritative):**

| Syntactic context of the reference | Classification |
|---|---|
| Target of simple assignment `_x = v` (incl. `obj._x = v`, `this._x = v`, tuple-deconstruction targets) | **Write** |
| Target of compound assignment `_x += v` (any `AddAssignment`…`CoalesceAssignment`) | **ReadWrite** |
| Operand of `++` / `--` (pre or post) | **ReadWrite** |
| `out _x` argument | **Write** |
| `ref _x` argument | **ReadWrite** |
| `in _x` argument / no modifier | **Read** |
| Inside `nameof(_x)` | **Neither** (ignored) |
| Candidate/unconfirmed `ReferenceLocation` | **Read** (conservative) |
| Everything else | **Read** |

Node resolution: unwrap the field name through a member-access `Name` position and parentheses (`( _x ) = 5` is legal); if the name is the *expression* of a member access (`_x.ToString()`) → Read immediately. No ancestor-climbing beyond these transparent wrappers — everything outside them is a use.

### New files / changes (src)

| File | Content |
|---|---|
| `Analysis/WriteOnlyFieldAnalyser.cs` | The analyser (flow above). |
| `Analysis/FieldReferenceClassifier.cs` | `Classify(SimpleNameSyntax node) → Write \| ReadWrite \| Read \| None` — pure syntax-role classification per the matrix; `None` = `nameof`. |
| `Models/FindingCategory.cs` | Add `WriteOnlyField = 16`. |
| `Cli/CliRunner.cs` | Register after `CommentedCodeAnalyser`. |

No changes to `SymbolUsageCollector` / `SolutionUsageIndex` / `SymbolReferenceQuery` — SNP0021 consumes them read-only; SNP0001 tests are untouched by construction.

### Fixture (new file)

`CoreLib/WriteOnlyFields.cs` — one class, members kept alive via a public method so other analysers stay quiet:

- `_retryBudget` — assigned in ctor, never read → **flag High**.
- `_lastSeed` — `readonly`, assigned in ctor only → **flag High**.
- `_outOnly` — only ever passed as `out` → **flag High**.
- `_namedOnly` — written + referenced only via `nameof` → **flag High** (nameof is Neither).
- `_jsonInclude` / `_dataMember` / `_jsonProperty` — written-never-read, carrying locally-defined `JsonIncludeAttribute` / `DataMemberAttribute` / `JsonPropertyAttribute` stand-ins → **flag Moderate** (demoted).
- `_compound` — only ever `+= 1` → not flagged.
- `_counter` — only ever `++` → not flagged.
- `_byRef` — only ever passed as `ref` → not flagged.
- `_read` — written and read → not flagged.
- `_neverTouched` — zero references → not SNP0021 (already SNP0001).
- `private int AutoProp { get; set; }` — auto-property → not flagged (backing field is implicit).
- `_writeSink` — public method performing the writes, referenced from a public entry so the writes are real reachable references.

### Test matrix (`WriteOnlyFieldAnalyserShould`, `[Collection("SampleSolution")]`)

1. Flags ctor-assigned field → High, message names the field.
2. Flags `readonly` field written only in ctor → High.
3. Flags out-only field → High.
4. Flags written + `nameof`-only field → High.
5. Demotes serialization-attributed fields → Moderate (theory over the three stand-ins).
6. Not flag compound-assignment-only field (`+=`).
7. Not flag increment-only field (`++`).
8. Not flag ref-passed field.
9. Not flag a read field.
10. Not flag zero-reference fields (SNP0001 boundary — no SNP0021 finding for `_neverTouched`).
11. Not flag the auto-property / implicit backing field.
12. Not flag anything in files without write-only fields (spot: `DeadCode.cs`).

---

## Story 2B — Redundancy sweep part 1 — SHIPPED 2026-09-18 as 1.3.1

> User call 2026-09-18: approved for implementation. Per-pattern rule IDs locked; SNP0023/SNP0024 remain reserved for Wave 3.

| Sub-rule | ID / Tier | Soundness gate (spike-proven) | Scope restrictions (v1) |
|---|---|---|---|
| Redundant default-value argument | **SNP0022**, High | Literal equals `IParameterSymbol.ExplicitDefaultValue` (via `GetConstantValue`) **and** speculative arg-removal rebinds the *identical* symbol (A1/A2/A3) — plus the caller-info exclusion below. | Trailing positional args only; literal-ish expressions only (literals, ± literals, enum member access); `params` and named args excluded; one trailing arg per invocation per run (iterative convergence). |
| Redundant method type arguments | **SNP0025**, High | Speculative type-list strip rebinds the identical constructed symbol incl. inferred type arguments (B1/B2). | Invocation-level explicit type lists only; nullable-annotated (`string?`) and `dynamic` type args excluded; no method-group conversions in v1. |
| Redundant cast | **SNP0026**, High | *(Wave 3 — attempt only if the proof is clean: static types equal + no user-defined conversion involved.)* | Wave 3. |

### Architecture

`RedundancyAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0022", "SNP0025"]` — one umbrella invocation pass with two evaluators. Per-rule `off` in snipper.json is honoured by the existing report-time `FindingFilter.Apply` (1A contract); a fully-disabled analyser is skipped entirely. Sequential binding per the `ConcurrentBuild=false` contract; semantic work happens only on syntax-pre-filtered candidates.

| File | Content |
|---|---|
| `Analysis/RedundancyAnalyser.cs` | Document loop (skip `ShouldSkipDocument`), `OfType<InvocationExpressionSyntax>`, namespace exclusion via node overload, delegates to both evaluators. |
| `Analysis/RedundantDefaultArgumentEvaluator.cs` | SNP0022: pre-filter (last arg positional + literal-ish) → `DetermineParameter(allowParams: false)` → default/constant equality → caller-info exclusion → speculation gate. |
| `Analysis/RedundantTypeArgumentEvaluator.cs` | SNP0025: pre-filter (generic invoked name) → nullable/dynamic guards → `IsGenericMethod` check → speculation gate. |
| `Analysis/InvocationSpeculation.cs` | `RebindWithoutArgument` / `RebindWithoutTypeArguments` — syntax-rewrite + `GetSpeculativeSymbolInfo(BindAsExpression)` (spike-proven shapes). |
| `Models/FindingCategory.cs` | `RedundantArgument = 17`, `RedundantTypeArguments = 18`. |

### Soundness additions beyond the spike

1. **Caller-info parameters are never candidates.** `[CallerMemberName]` / `[CallerFilePath]` / `[CallerLineNumber]` / `[CallerArgumentExpression]` defaults are injected by the caller when the argument is omitted — removing a matching literal changes runtime semantics even though the rebind is identical. Name-matched `FrozenSet` on the parameter's attributes. (Found in 2B design review, not covered by the spike.)
2. **Nullable-annotated type args are never stripped** — `string?` differs from `string` only by annotation, which is meaningful to nullability analysis and ignored by `SymbolEqualityComparer.Default`. Any `NullableTypeSyntax` inside the type-argument list skips the candidate; `dynamic` is skipped likewise.
3. **Trailing-only convergence:** only the final positional argument is a candidate; when two trailing args both match defaults, the outer one becomes flaggable on the next run after removal. Named arguments are excluded (readability is a legitimate reason for them).
4. **Invocations only in v1.** Object creation (`new C(5, 2)`) shares the machinery but its speculative shape was not spike-proven — deferred to a post-Wave-3 candidate.

### Fixture (new files)

`CoreLib/RedundantInvocations.cs` — SNP0022 scenarios (positives: literal/null/enum defaults, multi-default trailing-only; negatives: value mismatch, named, rebind overload trap, `params`, real `[CallerMemberName]`). `CoreLib/RedundantTypeArgs.cs` — SNP0025 scenarios (positives: plain + member-access invocation; negatives: non-generic overload trap, no-arg generic, return-only inference, nullable annotation). Both wired from `App/Worker.cs`; member names are distinctive (substring-based assertions).

### Test matrix (`RedundancyAnalyserShould`, `[Collection("SampleSolution")]`)

- SNP0022 (11): flag literal/null/enum/trailing-of-multi-default; not flag middle-of-multi-default, value mismatch, named, rebind trap, `params`, caller-info; `DeadCode.cs` spot check (covers both rules).
- SNP0025 (6): flag plain + member-access; not flag overload trap, no-arg generic, return-only inference, nullable-annotated.

---

## Cross-cutting (bundled)

1. **fixture2 generator:** add one write-only-field scenario to `test/Fixtures/Generate-Fixture2.ps1`; re-verify and repin header counts (SNP0021=1; 17→18 default, 19→20 `--config-analysis`).
2. **Dogfood gate:** self-run on `Snipper.slnx` stays 0 findings. Higher stakes this wave — SNP0021 is our first rule whose FP risk is semantic (reflection/serialization writes); every self-finding is individually triaged per the 1.1.1/1.1.2 protocol.
3. README rules-table row SNP0021 (+ rows for deferred SNP0022/0025/0026 are **not** added — nothing ships); `competitive-analysis.md` §5 check-off (item 4 → shipped, noting per-pattern ID amendment on item 6).
4. **Perf budget:** SNP0021 adds one sequential pass of SNP0001's shape (~0.2s self-run); full self-run must stay within noise of the 8.9s 1.2.0 baseline.

## Release

2A shipped 1.3.0. For 2B: bump 1.3.1 → `dotnet pack` → `dotnet tool update` → `snipper --version` → self-run verification → summarise.

## Judgement calls (pre-approved)

1. **`++`/`--` and compound assignment classify as ReadWrite**, deviating from the roadmap row's shorthand "++/-- = write". Semantically both read the old value (IOperation reports an implicit target read; `y = _x++` consumes it), and this matches IDE0052 — the parity the roadmap row claims. Conservative by design for a High-tier rule; the matrix above is authoritative over the shorthand.
2. **Newtonsoft `[JsonProperty]` joins the demotion set** (`JsonInclude`/`DataMember`/`XmlElement` + it). The roadmap's list predates the Newtonsoft ubiquity argument; reflection-driven serializers are exactly SNP0021's FP surface.
3. **`nameof(_x)` counts as neither read nor write** — it proves nothing about value flow.
4. **Unconfirmed candidate reference locations classify as Read** — unknown evidence always suppresses.
5. **Initializer-only fields stay with SNP0001** — a declarator initializer is not a reference; SNP0021 requires ≥1 actual write reference. No double-reporting.
6. **`out`-only fields are flagged** — an `out` write discards any prior value and the written value is never observed; semantically identical to plain assignment.
7. **Caller-info parameters excluded unconditionally (SNP0022)** — the speculation gate cannot see the runtime injection; found in 2B design review, not the spike.
8. **Per-rule evaluation waste accepted** — when snipper.json disables one of SNP0022/0025, the umbrella still evaluates both and the report-time filter drops the disabled rule's findings (1A architecture: analysers never see config). Speculation is cheap and candidate sets are tiny.
9. **Invocations only; object creation deferred** — only spike-proven shapes ship.
10. **2B releases as 1.3.1** — Wave 2's minor-version line was already used by 1.3.0 (2A); Wave 3 keeps 1.4.0. User call 2026-09-18.
