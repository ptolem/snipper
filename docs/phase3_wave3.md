# Phase 3 — Wave 3 Team Spec (3A SNP0023, 3B SNP0024, 3C SNP0026)

**Target release:** 1.4.0 · **Roadmap:** [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md) · **Baseline:** 1.3.1 (19 rules, 158 tests)
**Status:** ✅ **SHIPPED 2026-09-18 as 1.4.0** — all three stories landed per the merge order (foundation → 3A → 3B → 3C → cross-cutting): 22 rules, 188/188 tests, fixture2 repinned 23/25 (verified), dogfood clean. 3C shipped: the spike proved `IsIdentity` discriminates every non-flag shape. Two implementation-time adjustments beyond the spec: (1) can-be-static gained the spec's judgement-call-4 usage gate after DeadCode.cs exposed SNP0006 double-reporting, and its clean-file spot check moved to WriteOnlyFields.cs (DeadCode.cs legitimately contains CA1822 hits — it was authored for other rules); (2) the fixture2 Greeter gained instance state so the one-scenario-per-rule pin stays principled. Dogfood triage: two true positives fixed in our own code (`NuGetLockFileReader.Read` → static, `JsonReportSerializerContext` → sealed) plus one self-inflicted unused using.

## Roadmap deviations found while writing this spec

1. **There is no SNP0018 "override graph".** `ObsoleteMemberAnalyser` uses per-symbol checks (`IsOverride`, explicit/implicit interface-implementation detection) — no reusable graph artifact exists. 3A/3B instead share a **new** solution-scoped `InheritanceGraph` index (below). SNP0018 is untouched.
2. **3C narrows to identity conversions in v1** — the roadmap's "types match" is taken literally; reference-upcast removal changes an expression's *static* type, which can re-bind overloads and flip `var` inference. The upcast variant is documented as a v2 candidate with those caveats, not shipped.

## Planned spike (run at 3C implementation time, ~15 min)

In-memory `CSharpCompilation`: `SemanticModel.ClassifyConversion(operand, targetType)` behaviour for (a) identity conversions, (b) implicit reference upcasts, (c) explicit numeric/boxing (must never flag), (d) user-defined conversions (must never flag). 3C ships **only if the classification behaves exactly as designed** — risk register #3: a wrong "redundant" claim costs more trust than ten missed ones; a messy proof defers 3C rather than ships it.

## Working agreements (carried)

Red-green fixture-first; new fixture scenarios in NEW files (never `DeadCode.cs`); syntax pre-filters before semantic calls; `FrozenSet`/`FrozenDictionary`; no `dynamic`; no exceptions for flow control; sequential semantic binding (`ConcurrentBuild=false`); locally-defined attribute stand-ins where name-matching applies; distinctive fixture member names (substring-based assertions). Test conventions: xUnit + FluentAssertions, Contain/NotContain on names (never counts except the SNP0002 `ContainSingle` anchor), `[Collection("SampleSolution")]` for workspace tests.

---

## Shared foundation — `Analysis/InheritanceGraph.cs`

Solution-keyed memoized index (same `ConditionalWeakTable<Solution, Lazy<…>>` pattern as `SolutionUsageIndex` / `ProjectPackageUsageCache`). One sequential pass: per project → compilation → per source-declared class symbol (records included), record `BaseType` (skip `object`) into a base→derived map.

- `HasAnyDerivedClass(INamedTypeSymbol)` — direct lookup; "never inherited" needs no transitive walk (any direct derived class disproves it).
- `GetTransitivelyDerivedClasses(INamedTypeSymbol)` — for 3A's member check: collect the derived closure, then test each derived type's members for `OverriddenMethod` chains reaching the candidate (equivalently: `derivedTypeSymbol.GetMembers().Any(m => m.OverriddenMethod-walk contains candidate)` — implement via walking `OverriddenMethod` from each override found, or via `SymbolEqualityComparer` on `OriginalDefinition`).
- **Generated/external documents count as evidence** (a source-generated subclass keeps the base alive) — the graph reads compilations, which include them. Findings are never anchored there (`ShouldSkipDocument`).

Perf: one compilation pass per solution, shared by 3A and 3B. Budget +0.3s self-run.

---

## Story 3A — SNP0023 Hierarchy dead code (Moderate)

### Design

`HierarchyDeadCodeAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0023"]`. New `FindingCategory.HierarchyDeadCode = 19`. One rule, two checks (same family — locked by the roadmap); the class-level finding **suppresses member-level findings on the same class** (root-cause dedup, the SNP0002/SNP0009 philosophy).

**Check 1 — virtual member never overridden.** `"Virtual member 'X' is never overridden."`

- Candidates (syntax gate: `virtual` modifier present): ordinary methods and properties; **not** `override` (chain-terminal overrides fulfil an existing contract — being last in a chain is normal), **not** `abstract` (abstract roots are check-2/SNP0005 territory), **not** interface implementations (explicit or implicit — contract dispatch), **not** accessors/events in v1.
- Proof: the containing type's transitive derived closure (via `InheritanceGraph`) contains **no** override of the member.
- Usage gate: skip members with zero references — those are SNP0001/0005/0006 findings, not hierarchy findings (same boundary discipline as SNP0021).

**Check 2 — class with virtual members, never inherited.** `"Class 'X' declares virtual members but is never inherited."`

- Candidates: non-static, non-abstract, non-sealed class (record classes included) declaring ≥1 candidate virtual member (check-1 shape); `InheritanceGraph.HasAnyDerivedClass` is false. Abstract-never-inherited is excluded — never-instantiable chains are SNP0005/0006's job.
- Usage gate: the class must have ≥1 reference (an unused class is plain dead code, not hierarchy dead code).

**Demotions & exclusions (both checks):** `ExclusionEngine.ShouldExclude` + namespace exclusion; exported public surface → **Advisory** (external consumers can inherit/override — the `IsOnExportedType` walk from SNP0006); `InternalsVisibleTo` on the assembly → **Advisory** (friend assemblies can inherit internals — SNP0005 pattern); **string-name evidence** — candidate type names batch-scanned once via `AssemblyNameEvidenceScanner.FindSpelledNames` (name ≥3 chars, spelled in any string literal or solution JSON → suppressed; plugin-loading by name is the classic FP).

### New files / changes (src)

| File | Content |
|---|---|
| `Analysis/InheritanceGraph.cs` | Shared index (above). |
| `Analysis/HierarchyDeadCodeAnalyser.cs` | Both checks, demotions, batch string-evidence. |
| `Analysis/InterfaceImplementationQuery.cs` | `IsInterfaceImplementation` extracted from `ObsoleteMemberAnalyser` (behavior-preserving; SNP0018 tests pin). |
| `Models/FindingCategory.cs` | `HierarchyDeadCode = 19`. |
| `Cli/CliRunner.cs` | Register after `RedundancyAnalyser`. |

### Fixture (new file)

`CoreLib/HierarchyPatterns.cs` — `NeverInheritedBase` (internal, virtuals, zero derived, used via direct instantiation → class finding); `UsedBase` + `UsedDerived : UsedBase` where `UsedBase.NeverOverriddenVirtual` is never overridden (member finding) and `UsedBase.OverriddenVirtual` **is** overridden (no finding); `OverrideChainRoot`/`OverrideChainLeaf` (override members not flagged); `ExportedBase` (public → Advisory demotion); `PluginLoadedBase` (type name spelled in a Worker string literal → suppressed). Wired from `App/Worker.cs`.

### Test matrix (`HierarchyDeadCodeAnalyserShould` — 10)

1. Flag never-inherited virtual class → Moderate, names the class.
2. Member findings suppressed on a class-level finding (no finding for its virtual).
3. Flag never-overridden virtual on an inherited base → Moderate, names the member.
4. Not flag an overridden virtual.
5. Not flag chain-terminal override members.
6. Demote exported-surface findings → Advisory.
7. Suppress on string-name evidence.
8. Not flag the never-inherited class when it has zero references (SNP0005 boundary).
9. Not flag abstract classes (SNP0005/6 boundary).
10. DeadCode.cs spot check.

---

## Story 3B — SNP0024 Tightening (Advisory)

### Design

`TighteningAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser`, `RuleIds = ["SNP0024"]`. New `FindingCategory.Tightening = 20`. One analyser, three sub-checks; flat **Advisory** (locked — these are refactor invitations, not dead code; promotable via `snipper.json` severity override, the 1A synergy).

**Check 1 — member can be static (CA1822 parity).** `"Method 'X' does not use instance state and can be static."`

- Candidates: instance ordinary methods. Excluded: virtual/abstract/override, interface implementations, constructors/accessors/operators, entry points (`ExclusionEngine`), **any method carrying an attribute** (serialization callbacks and lifecycle hooks have fixed signatures — one cheap check kills the FP class), methods in interfaces.
- Proof: no body token binds to instance state — syntax pre-filter rejects bodies containing `this`; then every `SimpleNameSyntax`/`MemberAccessExpressionSyntax` in the body must resolve to something other than an instance member of the containing type hierarchy (static members, locals, parameters, other types are fine). Base-type instance members count as instance state.

**Check 2 — field can be readonly (IDE0044 parity).** `"Field 'X' is assigned only during construction and can be readonly."`

- Evidence: the SNP0021 reference machinery — extract `WriteOnlyFieldAnalyser`'s location enumeration into a shared internal `FieldReferenceMap` (behavior-preserving refactor; SNP0021's 14 tests pin it) and classify with the existing `FieldReferenceClassifier`.
- Rule: every Write occurs in the declarator initializer or an instance ctor of the containing type (static ctor for static fields); no ReadWrite references outside ctors; **no `ref` passes anywhere** (readonly forbids them). Reads anywhere are irrelevant.
- Excluded: `const`, `volatile` (CS0678), fixed buffers, fields in `ref struct` types, implicitly declared fields.

**Check 3 — internal class can be sealed (CA1852 parity).** `"Internal class 'X' has no derived types and can be sealed."`

- Candidates: internal, non-abstract, non-static, non-sealed classes (records included) with `InheritanceGraph.HasAnyDerivedClass` false. Usage gate: skip zero-reference classes (SNP0005 boundary). Friend assemblies make external subclassing possible — the flat Advisory tier already encodes that uncertainty (documented, no extra demotion; Advisory is the floor).

### New files / changes (src)

| File | Content |
|---|---|
| `Analysis/TighteningAnalyser.cs` | All three checks in one type-declaration pass. |
| `Analysis/FieldReferenceMap.cs` | Shared field-reference location enumeration, extracted from `WriteOnlyFieldAnalyser`. |
| `Models/FindingCategory.cs` | `Tightening = 20`. |
| `Cli/CliRunner.cs` | Register after `HierarchyDeadCodeAnalyser`. |

### Fixture (new file)

`CoreLib/TighteningPatterns.cs` — can-be-static: positive (params-only method), negatives (touches instance field, interface implementation, virtual, attributed method); can-be-readonly: positive (ctor-only write), negatives (method write, `ref` pass, `volatile`); can-be-sealed: positive (internal, no derived), negatives (has derived, abstract, already sealed). Wired from `App/Worker.cs`.

### Test matrix (`TighteningAnalyserShould` — 12)

1–4. can-be-static: flag positive; not flag instance-state / interface / virtual cases (attributed folded into the interface case's class).
5–8. can-be-readonly: flag positive; not flag method-written / ref-passed / volatile.
9–12. can-be-sealed: flag positive; not flag has-derived / abstract / sealed; DeadCode.cs spot check (folded into the sealed positive's class).

---

## Story 3C — SNP0026 Redundant cast (High, spike-gated)

### Design

`RedundantCastAnalyser` folds into `RedundancyAnalyser` as a third evaluator (`RuleIds` gains `"SNP0026"`; new `FindingCategory.RedundantCast = 21`) — same umbrella philosophy as 2B.

- Syntax gate: `CastExpressionSyntax`.
- v1 rule: flag **identity conversions only** — `SemanticModel.ClassifyConversion(operand, targetType)` returns `IsIdentity` (or `IsReference` **and** operand type equals target type under `SymbolEqualityComparer.Default`). An identity removal cannot change the expression's static type, so enclosing overload resolution and inference are untouched — sound by the type system, no speculation gate required.
- Never flagged by construction: explicit numeric (narrowing/overflow semantics), boxing/unboxing (runtime effect), user-defined conversions (never identity), `as`/pattern casts (different nodes), `dynamic`-involving casts.
- Message: `"Cast to 'T' is redundant — the expression is already of that type."`
- v2 candidate (documented, not shipped): implicit reference upcasts, requiring an enclosing-invocation rebind gate plus `var`-inference exclusion.

### Fixture / tests (8)

`CoreLib/RedundantCasts.cs` — positives: `(string)s` where `s` is `string`, generic `(T)x` where `x` is `T`; negatives: downcast `(Derived)b`, numeric `(int)d`, user-defined conversion cast, unboxing; DeadCode.cs spot check shared with 3B's.

---

## Cross-cutting (bundled)

1. **fixture2 generator:** one scenario per shipped rule — SNP0023=1 (never-inherited class check), SNP0024=1 (can-be-sealed), SNP0026=1 (identity cast) if 3C ships. Repin: 20→23 default / 22→25 `--config-analysis` (22/24 without 3C).
2. **Dogfood gate:** self-run stays 0. SNP0024's can-be-static is the FP magnet (attributed/lifecycle methods) — every self-finding triaged per protocol; tier protocol applies (Advisory floor already protects CI).
3. README rows SNP0023/0024/(0026) + exclusion-list additions; `competitive-analysis.md` §5 items 7/8 (+6 remainder) and §1 matrix rows.
4. **Perf budget:** InheritanceGraph +0.3s; can-be-static body scans are the wave's perf risk — syntax pre-filters mandatory (`this`-token absence, virtual/attribute modifiers before any binding). Total self-run budget +1.0s over the 1.3.1 Release baseline; report at release.

## Release

Bump 1.4.0 → `dotnet pack` → `dotnet tool update` → `snipper --version` → self-run verification → summarise.

## Judgement calls (pre-approved)

1. **One SNP0023 id for both hierarchy checks** (roadmap-locked); class-level finding suppresses member-level findings on the same class (root cause).
2. **Chain-terminal overrides are never candidates** — overriding fulfils a contract; only virtual chain *roots* are judged speculative.
3. **Abstract classes excluded from check 2** — never-instantiable chains belong to SNP0005/0006.
4. **Usage gates everywhere** — zero-reference types/members stay with SNP0001/0005/0006; no double-reporting across rules.
5. **3C v1 = identity conversions only** — the upcast variant's overload/`var` caveats need a rebind gate that hasn't earned its complexity yet.
6. **`FieldReferenceMap` extracted** from `WriteOnlyFieldAnalyser` for SNP0024 reuse — behavior-preserving, pinned by existing tests.
7. **can-be-static v1 = unattributed instance methods only** — one attribute check eliminates the serialization-callback/lifecycle FP class at trivial cost (safe false negatives accepted).
8. **`IsInterfaceImplementation` extracted** from `ObsoleteMemberAnalyser` into a shared internal helper — single source for a check 3A and SNP0018 both need.
