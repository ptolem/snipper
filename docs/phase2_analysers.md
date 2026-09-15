# Phase 2 Analysers — Team Spec (SNP0011, SNP0012, SNP0013, SNP0018)

> **Status: SHIPPED in 1.1.0** — 92/92 tests green (65 → 92). Implementation deviations from this spec, all reviewed:
> 1. `ResolvedPackage.Dependencies` carries `FrozenDictionary<string, VersionRange>` (not `FrozenSet<string>`) — redundancy is only sound when the transitively-required *minimum version* ≥ the resolved direct version; ids alone cannot express that.
> 2. SNP0013 test 3 (declared version exceeds inbox) implemented as pure `FrameworkPackageIndex.IsInbox` unit tests instead of a second workspace — same coverage, no extra restore/open cycle.
> 3. `AssemblyNameEvidenceScanner` gained a self-identity exclusion: the assembly's own generated `obj/<AssemblyName>.*.cs` files (AssemblyInfo et al.) spell the name by definition and are not evidence (discovered via failing test — every assembly looked "consumed").
> 4. Fixture `App.csproj` changed `Library` → `Exe` so the entry-point root guard is exercisable; SNP0018 needed solution-wide candidate documents (cross-project callers), not project-scoped.
> 5. `RedundantTransitivePackageAnalyser` exposes a public parameterless ctor + internal `ILockFileReader` ctor (CS0051 — internal seam type on a public analyser).

**Target release:** 1.1.0 (minor bump — additive rules, no breaking changes)
**Team:** 4 senior engineers, one story each. Story 0 (shared plumbing) is a half-day pre-task owned by **Eng 3**.
**Definition of done for every story:** analyser registered in `CliRunner` (on by default, no new flags), `FindingCategory` member added, README rules-table row, all 65 existing tests green, new tests per the story's test matrix, fixture2 (`trimmer-fixture2`) regression run reviewed by lead.

## Story 0 — Shared plumbing (Eng 3, pre-task)

- Add `FindingCategory` members (byte enum, append only — JSON/SARIF serialization must remain stable for existing values):
  `OrphanProject = 10`, `RedundantTransitivePackage = 11`, `FrameworkProvidedPackage = 12`, `ObsoleteUnreferencedMember = 13`.
- Add `PackageReference Include="NuGet.ProjectModel"` (latest stable compatible with net10.0 — brings `NuGet.Versioning` transitively; both SNP0012 and SNP0013 use `NuGetVersion` for comparisons).
- Fixture asset additions land with each story, but Story 0 adds the shared csproj-edit helper to `SampleSolutionFixture` territory: tests needing per-scenario csproj mutations must copy+mutate the fixture directory inside the test (current pattern) — **never mutate the shared `fixture.Solution` workspace.**

**Repo-wide constraints (apply to all stories):** no `dynamic`; no exceptions for flow control (parse failures return empty models); `FrozenSet`/`FrozenDictionary` for all lookup tables; syntax-level pre-filters before any semantic call; `ArgumentNullException.ThrowIfNull` on public entry points; **no concurrent semantic binding** — workspace compilations run with `ConcurrentBuild=false`; parallelism is permitted only for syntax-only work (e.g., literal scans); new analysers are `public sealed` in `Snipper.Analysis`, implement `IWorkspaceAnalyser`, and take `AnalysisExclusions? exclusions = null` via primary constructor **only if** they are member-level rules (project/package-level rules like SNP0003/0004 take no exclusions). Rule IDs/messages must not duplicate SNP0003/0004 coverage: if SNP0003 flags a package as unused, SNP0012/0013 must not also flag that same reference (see per-story dedup notes).

---

## Story 1 — SNP0011 Orphan Projects (Eng 1)

**User story:** As a monorepo maintainer, I want Snipper to flag projects that nothing references — both loaded projects with no inbound `ProjectReference` edges and `.csproj` files left on disk but detached from the solution — so that dead build units can be deleted instead of restored, built, and feared.

**Certainty:** `Moderate` (plugin/reflection loading can invisibly consume an assembly; message must say so).

### Domain types (signatures only)

```csharp
internal sealed record ProjectGraphNode(
    string FilePath,                 // full path, normalized
    string AssemblyName,             // <AssemblyName> or file stem
    bool IsEntryPoint,               // OutputType Exe/WinExe, or Sdk contains .Web/.Worker/.Functions/.BlazorWebAssembly/.Maui
    bool IsTestProject,              // PackageReference to Microsoft.NET.Test.Sdk / Microsoft.Testing.Platform, or <IsTestProject>true</IsTestProject>
    FrozenSet<string> ReferencedProjectPaths);  // outbound edges incl. ReferenceOutputAssembly=false; OrdinalIgnoreCase

internal sealed record ProjectGraph(FrozenDictionary<string, ProjectGraphNode> NodesByPath);

internal static class ProjectGraphBuilder
{
    // Parses every loaded project's csproj (reuse/extend UnreferencedPackageAnalyser's
    // csproj XML parsing — extract it to an internal shared ProjectFileReader as part of this story).
    public static ProjectGraph Build(Solution solution);
}

internal static class DetachedProjectScanner
{
    // Filesystem sweep: *.csproj under rootDirectory, excluding **/bin/** and **/obj/**,
    // not present in loadedProjectPaths.
    public static IReadOnlyList<string> FindUnattachedProjectFiles(
        string rootDirectory, FrozenSet<string> loadedProjectPaths);
}

internal static class AssemblyNameEvidenceScanner
{
    // Syntax-only string-literal scan across ALL documents (incl. generated/external —
    // evidence never findings). True = assembly name is spelled in a string literal or
    // configuration JSON under the solution root → suppress the orphan finding.
    // MAY use Parallel.ForEachAsync — syntax-only work only.
    public static bool IsAssemblyNameSpelled(Solution solution, string assemblyName);
}

public sealed class OrphanProjectAnalyser : IWorkspaceAnalyser
{
    public Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution, CancellationToken cancellationToken, Action<string>? progress = null);
}
```

### Algorithm notes

- **Graph:** adjacency sets in `FrozenDictionary`; orphan = non-root node with **in-degree 0**. Compute in-degree with a single pass over all `ReferencedProjectPaths` — O(V+E); no topological sort needed. Roots (`IsEntryPoint`/`IsTestProject`) are never flagged regardless of in-degree.
- **Detached files:** `HashSet` difference of filesystem scan vs. loaded paths; O(files).
- **Evidence:** `AssemblyNameEvidenceScanner` runs only for would-be findings (lazy — never scan eagerly for all projects).
- Finding `FilePath` = the orphan csproj path, `LineNumber` = 1, `Symbol` = null (project-level, matches SNP0003/0004 style).

### Test matrix (`OrphanProjectAnalyserShould`, `[Collection("SampleSolution")]`)

Fixture additions: `TestAssets/SampleApp/DetachedLib/` (csproj + one class, **no inbound references**; opened explicitly — see case 1), and `PluginLib/` whose assembly name `"PluginLib"` appears in a string literal in `App/Worker.cs`.

| # | Test name | Setup | Expected |
|---|---|---|---|
| 1 | `Flag_Loaded_Project_With_No_Inbound_References_For_AnalyzeAsync` | Dedicated fixture that opens `App.csproj` **and** `DetachedLib.csproj` | One SNP0011 containing `"DetachedLib"`, Moderate |
| 2 | `Not_Flag_Project_With_Inbound_Reference_For_AnalyzeAsync` | Same workspace | No SNP0011 mentioning `CoreLib` or `OrphanLib` (inbound from App) |
| 3 | `Not_Flag_Entry_Point_Or_Test_Project_For_AnalyzeAsync` | Same workspace | No SNP0011 mentioning `App` (OutputType Exe) |
| 4 | `Not_Flag_Plugin_Project_When_Assembly_Name_Is_Spelled_For_AnalyzeAsync` | `PluginLib` opened, name in string literal | No SNP0011 for `PluginLib` |
| 5 | `Flag_Detached_Project_File_On_Disk_For_AnalyzeAsync` | DetachedLib present on disk but **not** opened | SNP0011 at DetachedLib.csproj:1 |
| 6 | `Not_Flag_Files_Under_Obj_Or_Bin_For_AnalyzeAsync` | Restore artefacts exist in fixture | No SNP0011 under `obj`/`bin` |

**Edge cases to document in code:** `ReferenceOutputAssembly=false` counts as inbound (build orchestration); `OutputItemType="Analyzer"` references count as inbound (consumed as analyser); multi-targeted projects are one node.

---

## Story 2 — SNP0012 Redundant Transitive PackageReference (Eng 2)

**User story:** As a maintainer, I want direct `PackageReference`s that are already supplied transitively by another direct dependency — at an equal or higher resolved version — flagged as removable edges, so the restore graph shrinks and future version conflicts diminish.

**Certainty:** `Moderate` (direct references also pin intent against upstream dependency changes; message names the providing parent package).

### Domain types

```csharp
internal sealed record ResolvedPackage(string Id, NuGetVersion Version, FrozenSet<string> DependencyIds);

internal sealed record LockFileModel(
    FrozenDictionary<string, FrozenDictionary<string, ResolvedPackage>> PackagesByTfm);
    // outer key: TFM ("net10.0"); inner key: package id, OrdinalIgnoreCase

internal interface ILockFileReader
{
    // Reads obj/project.assets.json next to the given csproj. Returns null on missing/
    // malformed file or restore drift — never throws (no exceptions for flow control).
    LockFileModel? Read(string projectFilePath);
}

internal sealed class NuGetLockFileReader : ILockFileReader { /* LockFileFormat from NuGet.ProjectModel */ }

internal static class TransitiveRedundancyEvaluator
{
    // BFS from every OTHER direct reference over DependenciesIds; true when candidate
    // is reachable at >= its directly resolved version.
    public static bool TryFindProvidingParent(
        LockFileModel model, string tfm, ResolvedPackage directPackage,
        IReadOnlyCollection<string> otherDirectPackageIds, out string? providingParentId);
}

public sealed class RedundantTransitivePackageAnalyser : IWorkspaceAnalyser { /* IWorkspaceAnalyser */ }
```

### Algorithm notes

- One BFS per candidate over the per-TFM dependency DAG: O(V+E) per candidate, E is small per project. Visited set: `HashSet<string>(OrdinalIgnoreCase)`.
- **Per-TFM:** flag only when redundant in **every** TFM the project targets; skip project when assets file lacks a TFM (restore drift).
- **Skip rules:** package not in lock file; `ExcludeAssets` containing `compile` (consistent with SNP0003); declared version strictly greater than any transitively reachable version (the direct ref is an intentional upgrade); packages with zero compile assets (`analyser`/build-only).
- **Dedup:** do not flag a reference SNP0003 already flags (unused ≠ redundant) — check usage evidence via existing `SymbolUsageCollector` only when redundancy holds, and suppress if zero-usage. (Keeps reports actionable: SNP0003 wins.)
- Test seam: `ILockFileReader` — unit tests use in-memory `LockFileModel` graphs; one integration test uses the real restored fixture.

### Test matrix (`RedundantTransitivePackageAnalyserShould`)

Fixture addition: `App.csproj` gains direct refs to `Serilog` and `Serilog.Sinks.Console` (the sink depends on `Serilog` → `Serilog` is redundant); code in `Worker.cs` uses both (`Log.Logger`, `WriteTo.Console()`) so SNP0003 stays silent.

| # | Test name | Setup | Expected |
|---|---|---|---|
| 1 | `Flag_Direct_Reference_Supplied_Transitively_At_Same_Or_Higher_Version_For_Evaluator` | In-memory graph | `TryFindProvidingParent` true, parent = `Serilog.Sinks.Console` |
| 2 | `Not_Flag_When_Direct_Version_Exceeds_Transitive_For_Evaluator` | In-memory graph, direct pinned higher | false |
| 3 | `Not_Flag_When_Not_Transitively_Reachable_For_Evaluator` | In-memory graph | false |
| 4 | `Flag_Redundant_Package_For_AnalyzeAsync` (integration) | Real fixture | SNP0012, Moderate, message contains `"Serilog"` and `"Serilog.Sinks.Console"` |
| 5 | `Not_Flag_Providing_Parent_For_AnalyzeAsync` | Real fixture | No SNP0012 for `Serilog.Sinks.Console` |
| 6 | `Not_Flag_Package_That_Snp0003_Already_Flags_For_AnalyzeAsync` | `Humanizer.Core` (unused) | No SNP0012 for `Humanizer.Core` |
| 7 | `Return_Empty_When_Assets_File_Missing_For_Reader` | Temp csproj, no restore | `ILockFileReader.Read` returns null, analyser yields nothing |

---

## Story 3 — SNP0013 Framework-Inbox Packages (Eng 3)

**User story:** As a maintainer of a solution that has migrated TFMs over the years, I want `PackageReference`s flagged when the package ships inside the project's own shared framework (e.g., `System.Text.Json`, `System.Memory` on net10.0), so legacy migration artefacts can be deleted outright.

**Certainty:** `High`.

### Domain types

```csharp
internal sealed record FrameworkPackageOverrides(FrozenDictionary<string, NuGetVersion> VersionByPackageId);

internal static class TargetingPackLocator
{
    // Derives dotnet root from RuntimeEnvironment.GetRuntimeDirectory()
    // (…/shared/Microsoft.NETCore/x.y.z → root), then packs/. Returns null when absent.
    public static string? FindPacksDirectory();

    // Highest pack version whose major matches the TFM major, per relevant pack
    // (Microsoft.NETCore.App.Ref always; Microsoft.AspNetCore.App.Ref only when the
    // project uses the Web SDK or FrameworkReference Microsoft.AspNetCore.App).
    // Empty list when no matching pack installed → analyser skips the project.
    public static IReadOnlyList<string> FindOverrideFiles(string packsDirectory, string tfmMoniker, bool includeAspNetCore);
}

internal static class FrameworkPackageIndex
{
    // Parses PackageOverrides.txt lines ("PackageId|Version"), unioned across files.
    public static FrameworkPackageOverrides Load(IReadOnlyList<string> overrideFilePaths);
}

public sealed class FrameworkInboxPackageAnalyser : IWorkspaceAnalyser { /* IWorkspaceAnalyser */ }
```

### Algorithm notes

- Per project: `FrozenDictionary` lookup per declared package — O(1) per query; index built once per (TFM-major, pack-set) combination and cached in a small `Dictionary` keyed by that tuple (few distinct values in practice).
- **Flag rule:** package id present in overrides **and** declared `Version` ≤ overrides version (inbox assembly is ≥ — reference adds nothing) **and** redundant on **all** TFMs of the project **and** compile assets not excluded.
- **Never flag** when declared version exceeds the inbox version (intentional ship-in-app upgrade) or when no matching targeting pack is installed (can't judge).
- **Dedup:** like SNP0012, suppress when SNP0003 already flags the reference.

### Test matrix (`FrameworkInboxPackageAnalyserShould`)

Fixture addition: `CoreLib.csproj` gains `<PackageReference Include="System.Text.Json" Version="8.0.5" />` (inbox on net10.0; version below the net10 pack's override) with a real `JsonSerializer` usage in `Shared` code so SNP0003 stays silent.

| # | Test name | Setup | Expected |
|---|---|---|---|
| 1 | `Load_Overrides_From_Real_Targeting_Pack_For_Index` | Dev-machine pack dir | Contains `System.Text.Json` with version ≥ 8.0.5 |
| 2 | `Flag_Inbox_Package_At_Lower_Declared_Version_For_AnalyzeAsync` | Fixture | SNP0013, High, message contains `"System.Text.Json"` |
| 3 | `Not_Flag_When_Declared_Version_Exceeds_Inbox_For_AnalyzeAsync` | Temp csproj copy with Version bumped above override | No SNP0013 |
| 4 | `Not_Flag_Non_Inbox_Package_For_AnalyzeAsync` | Fixture | No SNP0013 for `Humanizer.Core`/`Serilog` |
| 5 | `Skip_Project_When_No_Matching_Pack_Installed_For_Locator` | Synthetic TFM `net99.0` | Empty override files → no findings, no exception |
| 6 | `Find_Packs_Directory_On_Dev_Machine_For_Locator` | — | Non-null path containing `packs` |

---

## Story 4 — SNP0018 Obsolete Members With Zero Usage (Eng 4)

**User story:** As a maintainer, I want `[Obsolete]` members that nothing references flagged for deletion, so the deprecation cycle actually terminates instead of accumulating years of annotated corpses.

**Certainty:** `High` for non-public members and for `[Obsolete(error: true)]`; `Moderate` for public members (external consumers may exist — same closed-world philosophy as SNP0006).

### Domain types

```csharp
public sealed class ObsoleteMemberAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution, CancellationToken cancellationToken, Action<string>? progress = null);
}
```

Internal helpers stay private; **reuse, don't reinvent:** `ShouldSkipDocument`/analysis-root filtering as in existing member analysers, `ExclusionEngine.IsNamespaceExcluded`, `SolutionUsageIndex.GetDocumentsUsingName` (syntax pre-filter; empty set proves zero references), `SymbolReferenceQuery.HasAnyReferenceAsync` (restricted document set — sequential; **no parallel semantic binding**).

### Algorithm notes

- **Syntax pre-filter per document:** attribute syntax matching name `Obsolete`/`ObsoleteAttribute` → only then bind the attributed symbol.
- **Eligibility (mirror the guards in the existing member analysers):** skip overrides (`IsOverride`), explicit *and* implicit interface implementations (polymorphic dispatch defeats static zero-reference reasoning), serialization constructors, and symbols in generated/excluded documents. Types and members are both candidates; an unused obsolete method on a live class is a valid finding.
- **Reference semantics:** declaration site excluded naturally; references arriving only from *other* obsolete symbols still count as references (known island limitation — SNP0017 territory, document it).
- Finding location = attribute's line (fallback: identifier line); `Symbol` populated.

### Test matrix (`ObsoleteMemberAnalyserShould`)

Fixture additions to `CoreLib`: `LegacyHelper` with `[Obsolete] private void UnusedOldMethod()` (zero calls); `[Obsolete(error: true)] public void RemovedApi()` (zero calls); `[Obsolete] public void StillUsedApi()` (called from `App/Worker.cs`); an `[Obsolete]` interface implementation member.

| # | Test name | Expected |
|---|---|---|
| 1 | `Flag_Unreferenced_Obsolete_Private_Member_As_High_For_AnalyzeAsync` | SNP0018 High for `UnusedOldMethod` |
| 2 | `Flag_Unreferenced_Obsolete_Error_Member_As_High_For_AnalyzeAsync` | SNP0018 High for `RemovedApi` |
| 3 | `Not_Flag_Referenced_Obsolete_Member_For_AnalyzeAsync` | No SNP0018 for `StillUsedApi` |
| 4 | `Not_Flag_Obsolete_Interface_Implementation_For_AnalyzeAsync` | No finding for the implementation member |
| 5 | `Not_Flag_Non_Obsolete_Unreferenced_Public_Member_For_AnalyzeAsync` | SNP0018 never fires without the attribute (that's SNP0006's job) |
| 6 | `Suppress_Finding_In_Excluded_Namespace_For_AnalyzeAsync` | `AnalysisExclusions.Create(["SampleApp.CoreLib.ExcludedZone"])` → excluded obsolete member suppressed |

---

## Merge order & integration

Story 0 → Stories 3/4 (independent) → Stories 1/2 (each extracts/extends shared parsing — SNP0011 extracts `ProjectFileReader`; SNP0012 adds the NuGet packages). Lead reviews fixture2 regression after all four merge; expected new findings there: SNP0012 (if redundant edges exist), SNP0013 (likely — old fixtures usually have inbox refs), SNP0011/SNP0018 as applicable. Then bump to **1.1.0**, pack, update global tool, README rules table + flag docs, `AGENTS.md`/`docs` cross-link.

---

## Lead judgement calls (approved)

1. **SNP0011 certainty = Moderate** — plugin-style assembly loading (`Assembly.Load`, Scrutor-style scanning) is common precisely in monorepos, so the string-literal evidence scanner acts as a *suppressor*, and the residual risk earns Moderate rather than High.
2. **SNP0012/SNP0013 dedup against SNP0003** — an unused package is reported by SNP0003 only, never double-reported as redundant/inbox. This keeps each finding actionable at the cost of a usage-evidence dependency in both stories.
