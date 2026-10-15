# Code architecture

A map of the codebase for people who want to contribute to it. It explains how Snipper gets code into
memory, how analysers run, and how findings become a report — and, importantly, which invariants you
must not break when adding to it.

Written against the 1.14.0 tree, 15,632 lines of C# across 92 files in `src/Snipper` (21 registered
analysers, 30 rule IDs, 714 tests), zero third-party runtime dependencies beyond Roslyn, MSBuild, and
Spectre.Console.

For *using* the tool see the [usage guide](usage.md). For *why* the design is the way it is see
[`1_7_0_plan.md`](history/1_7_0_plan.md), [`1_7_3_plan.md`](history/1_7_3_plan.md) and
[`1_7_4_plan.md`](history/1_7_4_plan.md).

---

## Contents

1. [The 60-second version](#the-60-second-version)
2. [C4: system context and containers](#c4-system-context-and-containers)
3. [Sequence: one run, end to end](#sequence-one-run-end-to-end)
4. [Class diagram](#class-diagram)
5. [How code gets into memory](#how-code-gets-into-memory)
6. [The analyser contract](#the-analyser-contract)
7. [Anatomy of a semantic analyser](#anatomy-of-a-semantic-analyser)
8. [The shared index pattern](#the-shared-index-pattern)
9. [Anatomy of the syntax-only analyser](#anatomy-of-the-syntax-only-analyser)
10. [The filtering pipeline](#the-filtering-pipeline)
11. [Namespace exclusion is a filter, not a pass](#namespace-exclusion-is-a-filter-not-a-pass)
12. [Performance: what was measured](#performance-what-was-measured-and-what-was-not)
13. [Git integration](#git-integration)
14. [Output](#output)
15. [Invariants you must not break](#invariants-you-must-not-break)
16. [Adding a rule: checklist](#adding-a-rule-checklist)
17. [Testing conventions](#testing-conventions)
18. [Where things live](#where-things-live)

---

## The 60-second version

```
args ──► CommandLineParser ──► CliRunner ──► MSBuildWorkspace ──► Solution
                          │
                          ├──► up to 19 IWorkspaceAnalyser instances, fanned out in parallel
                          │      each returns its own findings list
                          │
                          ├──► namespace exclusion over the findings
                          ├──► (optional) disabled-rule pass
                          │
                          └──► baseline fingerprint ──► FindingFilter ──► certainty tier
                                                        │
                                        Console table + JSON/SARIF + ledger
```

Five things to internalise before reading further:

1. **`CliRunner` is the orchestrator, and only the orchestrator.** It owns workspace opening, pass
   sequencing, the baseline, and the filter order — the things that genuinely need everything at
   once. Argument parsing, report serialisation, console rendering and analyser construction live
   in `CommandLineParser`, `ReportWriter`, `ConsoleRenderer` and `AnalyserFactory`, all of which are
   pure with respect to the orchestration. It is the only file you need to read to understand the
   run order, and it is half the size it was.
2. **Analysers are independent and stateless.** Each takes the same immutable `Solution` and returns
   its own list. All sharing happens through memoized solution-keyed indexes, never through mutable
   fields.
3. **Compilation warm-up is deliberate and sequential.** Every analyser opens with
   `GetCompilationAsync`; without the pre-warm, 19 analysers discover the same cold compilations at
   once and serialise on Roslyn's compilation tracker. Measured: 33 s naive versus 4.6 s warmed.
4. **Suppression never changes what the analysers produce.** Analysers never see namespace
   exclusions: the CLI filters their findings afterwards, so one pass yields both the visible set
   and the suppression-independent set the baseline is written from. The one channel that does
   stop findings existing is a disabled sole-rule analyser, which is not run; when a baseline or
   the audit needs its findings, it is run on its own.
5. **Determinism is a contract.** Findings come back in analyser declaration order, not completion
   order, each analyser's findings in `FindingOrder`, and reports are byte-identical run to run.

---

## C4: system context and containers

Level 1 (context) and Level 2 (containers). Mermaid's `C4Context`/`C4Container` syntax is not
supported by every renderer; this uses `flowchart` with C4-style boundaries so it renders anywhere
Mermaid does.

```mermaid
flowchart TB
    subgraph L1["Level 1 — System context"]
        direction TB
        DEV(["Developer<br/>runs locally, reads the console table"])
        CI(["CI system<br/>GitHub Actions / Azure / GitLab"])
        USER(["Developer or reviewer<br/>reads the SARIF file in code scanning"])
    end

    subgraph L2["Level 2 — Containers"]
        direction TB

        CLI{{"<b>Snipper CLI</b><br/><i>single-file executable</i><br/>.NET 10 / C# 14"}}

        subgraph PROC["Analysis process"]
            direction TB
            AR{{"<b>AnalysisRunner</b><br/>warm-up + parallel fan-out"}}
            AN["<b>21 analysers</b><br/><i>IWorkspaceAnalyser</i><br/>SNP0001-SNP0034"]
            IDX["<b>Shared indexes</b><br/><i>solution-keyed, memoized</i><br/>usage / framework / inheritance / packages"]
        end

        subgraph PIPE["Post-analysis pipeline"]
            direction TB
            FLT["<b>FindingFilter</b> + baseline<br/><i>SnipperConfig</i>"]
            AUD["<b>SuppressionAudit</b><br/><i>per-channel attribution</i>"]
        end

        OUT["<b>Report writers</b><br/>JSON / SARIF 2.1.0"]
    end

    EXT_ROSLYN{{"<b>Roslyn + MSBuild</b><br/><i>external, resolved at runtime</i>"}}
    EXT_GIT{{"<b>git CLI</b><br/><i>external process</i>"}}
    EXT_SDK{{"<b>.NET SDK</b><br/><i>targeting packs, PackageOverrides</i>"}}
    FS[("Filesystem<br/><i>solution, snipper.json,<br/>baseline, ledger</i>")]

    DEV -->|"snipper ./MyMonorepo.slnx out.json"| CLI
    CI -->|"same command line"| CLI

    CLI -->|"OpenSolutionAsync"| EXT_ROSLYN
    EXT_ROSLYN -->|"reads"| EXT_SDK
    CLI --> AR
    AR --> AN
    AR -->|"SharedIndexes.BuildAsync"| IDX
    AN -->|"SolutionUsageIndex.GetAsync"| IDX
    AN -->|"findings"| FLT
    FLT --> OUT
    AR -.->|"disabled-rule pass"| AUD
    CLI -->|"TryResolveCommitSha, numstat, log"| EXT_GIT
    CLI -->|"discover + read"| FS
    OUT -->|"report.json / report.sarif"| USER
    OUT --> FS
    CLI -->|"exit code"| CI

    classDef ext fill:#eceff1,stroke:#607d8b,stroke-dasharray:4 3
    classDef proc fill:#e8f0fe,stroke:#4285f4
    classDef pipe fill:#fef7e0,stroke:#f9ab00
    classDef out fill:#e6f4ea,stroke:#34a853
    class EXT_ROSLYN,EXT_GIT,EXT_SDK ext
    class AR,AN,IDX,CLI proc
    class FLT,AUD pipe
    class OUT out
```

### The external dependencies, and why each one is shaped the way it is

| Dependency | How it is reached | Why it matters to you |
| --- | --- | --- |
| **Roslyn** (`Microsoft.CodeAnalysis`) | Compile-time reference | `Solution`, `Compilation`, `SemanticModel`, `SyntaxNode`. The whole analyser model is built on these. |
| **MSBuild** (`Microsoft.Build.Locator`) | **Runtime assembly resolution only** — never a compile-time reference | `MSBuildLocator.RegisterDefaults()` must run before any MSBuild/Roslyn workspace type is touched. See [How code gets into memory](#how-code-gets-into-memory). |
| **Spectre.Console** | Compile-time reference | `Status` (spinner), `Table`, `AnsiConsole`. `StatusContext` is **process-wide exclusive and not thread-safe** — analysis progress reaches it only through `ConsoleRenderer.CreateAnalysisProgress`, whose sink takes one lock. |
| **git** | Child process | Everything git-related shells out. Read-only; degrades to silence outside a checkout. |

---

## Sequence: one run, end to end

The container-level interaction for `snipper ./MyMonorepo.slnx report.json --clone-drift
--entropy-budget 1.5 --baseline .snipper/baseline.json`.

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant P as Program
    participant C as CliRunner
    participant MS as MSBuildWorkspace<br/>Roslyn
    participant AR as AnalysisRunner
    participant AN as IWorkspaceAnalyser
    participant BL as BaselineService
    participant FF as FindingFilter
    participant GM as GitMetadata

    User->>P: snipper ./MyMonorepo.slnx report.json<br/>--clone-drift --baseline .b.json --entropy-budget 1.5
    P->>P: --version? then MSBuildLocator.RegisterDefaults()
    P->>C: RunAsync(args)
    C->>C: CommandLineParser.TryParse(args)<br/>invalid ⇒ exit 1
    C->>C: SnipperConfigLoader.Load — tolerant,<br/>never throws
    C->>C: AnalysisExclusions.Create(CLI ∪ config)
    C->>C: AnalyserFactory.Build — drop analysers<br/>whose every rule is off

    rect rgb(232, 240, 254)
        note over C,MS: Load the code
        C->>MS: OpenSolutionAsync(target)
        MS-->>C: Solution
        MS-->>C: workspace warnings (non-fatal, deduped)
    end

    rect rgb(232, 240, 254)
        note over AR,AN: Analysis
        C->>AR: RunAsync(analysers, solution)
        AR->>AR: WarmCompilationsAsync — SEQUENTIAL,<br/>one Compilation build per project
        AR->>AR: SharedIndexes.BuildAsync — the union of the<br/>indexes the analysers declare, concurrently, timed
        par fan-out, bounded by SNIPPER_FANOUT_DOP
            AN->>AN: await AnalysisScope / SolutionUsageIndex<br/>already built, so a cache hit
            AN->>AN: prove or refute each candidate
            AN-->>AR: findings into indexed slot
        and
            AN->>AN: TokenShingleIndex.Build (syntax-only)
            AN->>AN: clone sets → CloneDriftDetector
            AN-->>AR: findings
        end
        AR-->>C: merged in declaration order
    end

    C->>MS: NamespaceExclusionFilter.ApplyAsync<br/>resolve each finding's namespace from its location
    MS-->>C: visible + namespace-excluded

    opt a rule is disabled, and a baseline or the audit is requested
        C->>AR: RunAsync(analysers removed by disabled rules)
        AR-->>C: their findings
    end

    rect rgb(252, 232, 230)
        note over C,FF: Filtering — order is load-bearing
        C->>BL: Load(baselinePath)
        BL-->>C: fingerprints + commitSha
        C->>C: new = visible findings not in baseline
        C->>BL: Write(fingerprints of the<br/>suppression-independent set, headSha)
        C->>FF: Apply(findings, config)<br/>rule off → path glob → severity override
        FF-->>C: filtered
        C->>C: certainty tier floor
    end

    opt entropy requested
        C->>GM: TryResolveCommitSha + TryGetNumstat
        GM->>GM: git rev-parse, git diff --numstat
        GM-->>C: head sha, dirty flag, changed lines
        C->>C: EntropyRateCalculator.Compute → status + rate
        C->>C: ledger.Append — only if Scored
    end

    rect rgb(230, 244, 234)
        note over C,GM: Output
        C->>C: ConsoleRenderer.RenderReport
        C->>C: ReportWriter.Write → BuildJson | BuildSarifJson
        C-->>User: report file + exit 0|1|2|3
    end```

### The three ordering rules worth memorising

1. **Warm-up before fan-out.** Sequential, and it swallows per-project failures. A project that
   will not compile is an analyser concern, not a precondition for the other sixteen.
2. **Baseline classification reads the visible set; baseline writing reads the suppression-independent
   set.** So a finding you cannot see is still recorded, and removing a suppression does not resurface
   it as "new". The suppression-independent set is the visible set, plus what namespace exclusion hid
   from it, plus the disabled-rule pass when a rule is off, so namespace exclusions never cost a
   second pass. Under no suppression the two sets are the same list.
3. **`FindingFilter` runs after fingerprinting.** Rule-off, path globs and severity overrides can
   therefore never churn the baseline file.

---

## Class diagram

The types a contributor touches, and the relationships that matter. Evaluators, scanners and
evidence indexes are omitted except where they are load-bearing.

```mermaid
classDiagram
    direction TB

    class Program {
        +Main(args) int
    }

    class CliRunner {
        +RunAsync(args) Task~int~
        -AppendEntropyLedgerEntry(path, result)
        -IsExcludedFromDenominator(path, base, config) bool
    }

    class CommandLineParser {
        +Usage string
        +TryParse(args, out options, out error) bool
        -IsValidNamespace(value) bool
    }

    class CommandLineOptions {
        +required TargetPath
        +OutputPath
        +BaselinePath
        +MinimumCertainty
        +Format
        +IncludeConfigAnalysis
        +IncludeDuplicateDetection
        +IncludeCloneDrift
        +AuditSuppressions
        +EntropyRateRequested
        +EntropyBudget
        +EntropyLedgerPath
        +EntropyMinimumLines
        +ExcludedNamespaces
        +MalformedNamespaces
    }

    class AnalyserFactory {
        +Build(options, targetPath, baseDirectory) List
        -FindRepositoryRoot(targetPath) string
    }

    class NamespaceExclusionFilter {
        +ApplyAsync(findings, solution, exclusions, ct) NamespaceFilterResult
    }

    class ReportWriter {
        +Write(findings, path, format, commitSha, dirty, audit, entropy) int
        +BuildJson(findings, sha, audit, entropy) string
        +BuildSarifJson(findings) string
    }

    class ConsoleRenderer {
        +RenderReport(findings)
        +RenderSuppressionAudit(audit)
        +RenderEntropyRate(report)
    }

    class ToolVersion {
        +Current string
    }

    class IWorkspaceAnalyser {
        <<interface>>
        +IReadOnlyCollection~string~ RuleIds
        +AnalyzeAsync(solution, ct, progress) Task
    }

    class AnalysisRunner {
        +RunAsync(analysers, solution, progress, ct) Task
        -WarmCompilationsAsync(solution, ct) Task
    }

    class AnalysisParallelism {
        +CreateFanOutOptions(ct) ParallelOptions
    }

    class SolutionUsageIndex {
        -Get(solution) SolutionUsageIndex
        -GetDocumentsUsingName(project, name) ImmutableHashSet~Document~
        +HasNameInProjectScope(projectId, name) bool
    }

    class FrameworkEvidenceIndex {
        -Get(solution) FrameworkEvidenceIndex
    }

    class InheritanceGraph {
        -Get(solution) InheritanceGraph
    }

    class ProjectPackageUsageCache {
        +GetAsync(projectFilePath, ct) ProjectPackageUsageEntry
        +PrefetchAsync(projectFilePaths, ct) Task
    }

    class TokenShingleIndex {
        +Build(documents, options) Dictionary
    }

    class ExclusionEngine {
        +ShouldSkipDocument(path, root, roots) bool
        +IsTestCode(path, projectName) bool
        +ResolveNamespace(node) string          // enclosing declarations → unit → null (global)
        +IsGeneratedDocument(path) bool
        +GetAnalysisRootDirectories(solution) IReadOnlyList~string~
    }

    class AnalysisExclusions {
        +None AnalysisExclusions
        +Namespaces FrozenSet~string~
        +Create(namespaces) AnalysisExclusions
        +FindCoveringExclusion(namespace) string
    }

    class SnipperConfig {
        +Empty SnipperConfig
        +DisabledRules FrozenSet~string~
        +SeverityOverrides FrozenDictionary
        +ExcludedNamespaces FrozenSet~string~
        +ExcludedPathGlobs FrozenSet~string~
    }

    class SnipperConfigLoader {
        +Load(target, out configPath, warnings) SnipperConfig
        -Discover(target) string
    }

    class FindingFilter {
        +Apply(findings, config) IReadOnlyList
        +Classify(finding, config) FindingFilterClassification
        +IsAnalyserEnabled(ruleIds, config) bool
        +IsEmpty(config) bool
    }

    class FindingFilterClassification {
        +FindingFilterOutcome Outcome
        +string MatchedGlob
        +CertaintyTier EffectiveTier
    }

    class BaselineService {
        +Load(path) BaselineFile
        +Write(path, fingerprints, commitSha) void
        +ComputeFingerprint(finding, baseDirectory) string
        +RequiresDisabledAnalyserPass(baseline, removedCount) bool
    }

    class SuppressionAuditBuilder {
        +Build(analysed, config, tier, disabledShadow, nsExcluded, ran, globProbe, nsProbe) SuppressionAudit
    }

    class NamespaceInventory {
        +CollectDeclaredNamespaces(solution) FrozenSet~string~
        +CollectFiles(solution) GlobFileInventory
        +ExistenceProbe(namespaces) IReadOnlyDictionary
    }

    class EntropyRateCalculator {
        +Compute(inputs) EntropyRateResult
        +CountResolved(known, current) int
        +ToReport(result, budget, monthly) EntropyRateReport
    }

    class DuplicateFragmentAnalyser {
        +RuleIds IReadOnlyCollection~string~
        -AnalyzeAsync(solution, ct, progress) Task
        -CollectFiles(solution, ct) Task
        -FindFragments(windows) fragments, matches
        -KeepMaximalMatches(matches) fragments, matches
        -BuildFindings(fragments, matches, files) findings, cloneSets
        -IsExcluded(unit, fragment, exclusions) bool
    }

    class CloneDriftDetector {
        +Detect(cloneSets, repoRoot) IReadOnlyList
        -Classify(members, commit, hunks) CloneDriftClassification
    }

    class GitHistory {
        +TryGetMostRecentCommits(repoRoot, paths, limit) Map
        +TryGetPatches(repoRoot, commits) List
    }

    class GitProcess {
        +Run(exe, args, cwd, stdin) Result
        +RunTrimmed(exe, argString, cwd) string
    }

    class GitMetadata {
        +TryResolveCommitSha(dir, out dirty) string
        +TryResolveRepositoryRoot(dir) string
        +TryGetNumstat(dir, from, to) List
    }

    class SnipperFinding {
        +string RuleId
        +string Title
        +string Message
        +CertaintyTier Certainty
        +FindingCategory Category
        +string FilePath
        +int LineNumber
        +int CharacterOffset
        +ISymbol Symbol
    }

    class CertaintyTier {
        <<enumeration>>
        Guaranteed = 1
        High = 2
        Moderate = 3
        Advisory = 4
    }

    class FindingCategory {
        <<enumeration>>
        UnreachableCode = 1
        UnusedPrivateMember = 2
        ...
        DuplicateFragment = 25
        CloneDrift = 26
    }

    class JsonReportSerializerContext {
        <<JsonSerializerContext>>
    }

    class SarifLog {
        <<SARIF 2.1.0 DTO>>
    }

    Program --> CliRunner : RunAsync
    CliRunner --> CommandLineParser : TryParse
    CommandLineParser --> CommandLineOptions : produces
    CommandLineParser --> ReportFormat
    CliRunner ..> SnipperConfigLoader : Load
    CliRunner ..> AnalysisRunner : RunAsync
    CliRunner ..> AnalyserFactory : Build
    CliRunner ..> NamespaceExclusionFilter : ApplyAsync
    CliRunner ..> BaselineService : Load, Write
    CliRunner ..> EntropyRateCalculator : Compute, Append
    CliRunner ..> FindingFilter : Apply
    CliRunner ..> SuppressionAuditBuilder : Build
    CliRunner ..> NamespaceInventory : CollectFiles
    CliRunner ..> GitMetadata : sha, numstat
    CliRunner ..> AnalysisExclusions : Create
    NamespaceExclusionFilter ..> AnalysisExclusions : FindCoveringExclusion
    NamespaceExclusionFilter ..> ExclusionEngine : ResolveNamespace
    CliRunner ..> ReportWriter : Write
    CliRunner ..> ConsoleRenderer : Render*
    CliRunner ..> ToolVersion : Current
    AnalyserFactory ..> DuplicateFragmentAnalyser : constructs
    AnalyserFactory ..> CloneDriftDetector : constructs
    ReportWriter ..> JsonReportSerializerContext
    ReportWriter ..> SarifLog
    ReportWriter ..> ToolVersion : Current
    SnipperReport ..> SuppressionAudit
    EntropyRateReport ..> EntropyRateCalculator : formatted by

    AnalysisRunner --> IWorkspaceAnalyser : fans out
    AnalysisRunner --> AnalysisParallelism : bounded by

    IWorkspaceAnalyser <|.. DuplicateFragmentAnalyser
    IWorkspaceAnalyser <|.. UnreachableCodeAnalyser
    IWorkspaceAnalyser <|.. UnusedPrivateMemberAnalyser
    IWorkspaceAnalyser <|.. RedundancyAnalyser

    IWorkspaceAnalyser ..> SolutionUsageIndex : Get(solution)
    IWorkspaceAnalyser ..> FrameworkEvidenceIndex : Get(solution)
    IWorkspaceAnalyser ..> InheritanceGraph : Get(solution)
    IWorkspaceAnalyser ..> ProjectPackageUsageCache
    IWorkspaceAnalyser ..> ExclusionEngine
    IWorkspaceAnalyser ..> SnipperFinding : produces

    DuplicateFragmentAnalyser --> TokenShingleIndex : syntax-only
    DuplicateFragmentAnalyser --> CloneDriftDetector : optional
    DuplicateFragmentAnalyser ..> ExclusionEngine

    CloneDriftDetector --> GitHistory
    GitHistory --> GitProcess
    GitMetadata --> GitProcess

    SnipperFinding --> CertaintyTier
    SnipperFinding --> FindingCategory

    FindingFilter --> SnipperConfig
    FindingFilter ..> FindingFilterClassification : Classify returns
    SnipperConfigLoader --> SnipperConfig
    SuppressionAuditBuilder --> SnipperConfig
    SuppressionAuditBuilder ..> FindingFilter : shares Classify
    SuppressionAuditBuilder ..> NamespaceInventory : probes
    BaselineService ..> SnipperFinding : fingerprints
    EntropyRateCalculator ..> BaselineService : reads fingerprints```

Note the `SuppressionAuditBuilder ..> FindingFilter : shares Classify` edge. The audit and the report
filter call **the same** `Classify` method, deliberately, so the audit's per-channel totals cannot
drift from actual report behaviour.

---

## How code gets into memory

This is the part that surprises people, so it goes first.

### MSBuildLocator must run first

```csharp
var instance = MSBuildLocator.RegisterDefaults();
```

`Program.Main` calls this before touching any MSBuild or Roslyn workspace type. MSBuild assemblies are
**never compile-time referenced** — they are resolved at runtime from the installed .NET SDK, because
a compile-time reference would pin one MSBuild version and break on every SDK upgrade.

Two consequences:

- If you add a file that references a Roslyn workspace type and touches it in a static initialiser or
  a field initialiser, it may run before `RegisterDefaults()` and fail with a confusing assembly-load
  error. Keep MSBuild-touching code behind the call.
- `--version` deliberately short-circuits **before** the locator, so `snipper --version` produces
  machine-clean output with no SDK binding noise.

### Opening the target

```csharp
solution = targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
    ? (await workspace.OpenProjectAsync(targetPath)).Solution
    : await workspace.OpenSolutionAsync(targetPath);
```

MSBuildWorkspace 5.x handles `.sln` and `.slnx` natively through the `SolutionPersistence`
serializer.

### Load failures are non-fatal by design

```csharp
workspace.RegisterWorkspaceFailedHandler(e => { /* dedupe, print, continue */ });
```

A project that fails to restore or compile does not abort the run. Each distinct message prints once
and duplicates are counted, because a monorepo with one broken project should still be analysed. Only
`OpenSolutionAsync`/`OpenProjectAsync` failure itself is fatal (exit 1).

That fatal path catches **`Exception`** rather than a closed type list, and the asymmetry with
`ProjectFileReader` is deliberate: `ProjectFileReader` catches `XmlException` explicitly because it
loads a single known file, while the open call is a boundary into MSBuild + SolutionPersistence and any
failure from it means the target could not be opened. `OperationCanceledException` is rethrown first, so
Ctrl+C is not misreported as a bad solution file. Both the path and the message are `Markup.Escape`d,
because an `XmlException` message routinely ends `[at line 3, position 12]` and unescaped brackets are
parsed as Spectre markup.

The previous filter — IO exceptions plus `InvalidProjectFileException`/`InvalidSolutionFileException` —
was a closed list that leaked. `XmlException` derives from `SystemException`, not `IOException`, so a
hand-written malformed `.slnx` escaped as an unhandled exception and the process died with exit
-532462766 instead of 1. A `.slnx` that is well-formed XML with an invalid schema failed the same way via
`SolutionException`.

`InvalidProjectFileException` and `InvalidSolutionFileException` are no longer matched by name, because
the broad catch subsumes them; the reason they were matched by name — living in runtime-resolved MSBuild
assemblies rather than being compile-time references — is why the list could not simply be widened with
confidence at each new exception type.

### Generated and external code

`ExclusionEngine.ShouldSkipDocument` is the single gate for generated and external code. It rejects:

- Files outside the analysis root directories (so linked files from elsewhere are not analysed twice).
- Generated documents, by path convention and by `AutoGenerated` header.

Namespace exclusion is not a document gate at all, because a file can hold more than one namespace. It
is applied to findings after analysis — see [Namespace exclusion is a filter, not a pass](#namespace-exclusion-is-a-filter-not-a-pass).

Most analysers never call the gate themselves: `AnalysisScope.GetAsync` applies it once per
solution and hands back one flat list of the surviving documents of every compilable project, each with
its syntax root and its project's `InternalsVisibleTo` flag, so the skip decision is made before any
semantic model is fetched. The list is flat so an analyser can bind documents in parallel across the
whole solution rather than one project at a time. Three analysers keep their own loop because
they need a different document set — `ConfigurationBindingAnalyser` gathers evidence from every
document, generated ones included; `DuplicateFragmentAnalyser` collects by path so a linked file counts
once; `UnusedUsingDirectiveAnalyser` starts from compiler diagnostics — and the last two call
`ShouldSkipDocument` directly.

### Test code — the one gate that is not universal

`ShouldSkipDocument` is deliberately **test-code-neutral**: a private member that is genuinely dead is
just as dead in a test project, so the reference-checking rules still analyse it. Only the two
complexity rules skip test code, via a separate predicate `ExclusionEngine.IsTestCode(filePath,
projectName)` that they consult *after* `ShouldSkipDocument`. The split is the point — one gate, two
decisions, so relaxing either cannot silently relax the other.

A document is test code when a **directory segment** is exactly `test` or `tests`
(case-insensitively), or when the project's final dotted segment is exactly `Test` or ends with
`Tests`. Matching is on whole segments, never substrings, so `Contest`, `Testing`, `Latest` and a file
named `MyTests.cs` all stay in scope. Threshold, reason, rule ID and source position are unaffected —
the document is skipped, not downgraded.

Measured effect on the reference monorepo: **2 findings** (SNP0033 and SNP0034 on one `Doubles.cs`
fixture), 2,466 → 2,464 across three interleaved runs, 0 added. That is a small number and is reported
as one; the change earns its place by removing the single worst finding in the report and by protecting
repositories whose fixtures are heavier, not by clearing a queue here.

---

## The analyser contract

```csharp
public interface IWorkspaceAnalyser
{
    IReadOnlyCollection<string> RuleIds { get; }

    Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}
```

That is the entire surface. Three properties make it safe to fan out:

1. **`RuleIds` is the single source of truth for skipping.** `FindingFilter.IsAnalyserEnabled` uses it
   to decide whether an analyser is worth running at all. If you add a rule, add its id here or it
   cannot be disabled.
2. **`Solution` is immutable.** Roslyn solutions are persistent data structures; handing the same
   instance to 17 concurrent analysers is safe by construction.
3. **`progress` reaches the console through one lock.** `StatusContext` is not thread-safe, so the
   runner forwards progress as `IProgress<AnalysisProgress>` and the console sink serialises it; your
   callback must not assume it is on a particular thread.

A note on multi-rule analysers: `RedundancyAnalyser` emits five rules (SNP0022, 0025, 0026, 0028,
0029) from one traversal, because the traversal is the expensive part. **If you disable four of the
five, the analyser still runs and the cost is unchanged.** `DuplicateFragmentAnalyser` is the one
analyser whose `RuleIds` is conditional on its constructor arguments.

---

## Anatomy of a semantic analyser

`UnusedPrivateMemberAnalyser` is 179 lines and is the cleanest example. Every semantic analyser
follows this shape:

```csharp
public sealed class UnusedPrivateMemberAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0001"];

    // The shared indexes this analyser reads; AnalysisRunner builds them before the fan-out.
    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution, CancellationToken cancellationToken, Action<string>? progress = null)
    {
        var usageIndex = await SolutionUsageIndex.GetAsync(solution, cancellationToken);   // shared, built once

        // In-scope documents of every compilable project: generated and external
        // code is already gone, decided once per solution.
        var findings = new ConcurrentBag<SnipperFinding>();
        await Parallel.ForEachAsync(
            await AnalysisScope.GetAsync(solution, cancellationToken),
            AnalysisParallelism.CreateOptions(cancellationToken),
            async (scopedDocument, documentToken) =>
            {
                var (document, root) = scopedDocument;
                var semanticModel = await document.GetSemanticModelAsync(documentToken);
                if (semanticModel is null)
                {
                    return;
                }

                foreach (var node in root.DescendantNodes())
                {
                    // 1. cheap syntactic pre-filter — most nodes never become candidates
                    // 2. declared symbol
                    // 3. usage proof, via the index
                    // 4. emit into the bag
                }
            });

        return [.. findings];   // AnalysisRunner puts it in FindingOrder
    }
}
```

The ordering is the performance design, and you should preserve it when you add a rule:

| Step | Cost | Purpose |
| --- | --- | --- |
| `AnalysisScope` | once per solution | Generated and external code is gone before any semantic model is fetched. |
| `Parallel.ForEachAsync` over documents | none | Documents bind concurrently, bounded by `SNIPPER_MAX_DOP`. Anything other than findings that leaves the loop goes into a per-document slot, so later phases see scope order. |
| Syntactic pre-filter | cheap | Reject most nodes without a symbol. Avoids a `GetDeclaredSymbol` per node. |
| `GetDeclaredSymbol` | moderate | Only for surviving candidates. |
| Usage proof | expensive | `SolutionUsageIndex` name lookup is O(1); a `FindReferencesAsync` is the fallback and is **restricted to the documents the index says can contain the name**. |

The index's invariant is the trick worth understanding: *any semantic reference to a named symbol
must spell its name in source*. So "this name appears in no in-scope document" **proves** zero
references without a reference search. When the name does appear, the search is narrowed to exactly
those documents.

---

## The shared index pattern

Five indexes are shared across analysers, listed in the `SharedIndex` enum: `AnalysisScope`,
`SolutionUsageIndex`, `FrameworkEvidenceIndex`, `InheritanceGraph`, `EnumOrdinalContractIndex`.
A sixth cache, `ProjectPackageUsageCache`, is per project (below).

Each analyser declares the ones it reads, and `AnalysisRunner` builds their union as a stage of its
own, after the compilations are warm and before the fan-out:

```csharp
public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex;
```

```
✓ AnalysisScope: built in 0.3s
✓ InheritanceGraph: built in 0.9s
✓ EnumOrdinalContractIndex: built in 0.9s
✓ SolutionUsageIndex: built in 1.3s
✓ FrameworkEvidenceIndex: built in 1.3s
✓ Shared indexes: 5 built in 1.3s
```

(MILKRUN, 3,610 findings. The builds overlap, so the stage takes as long as the slowest.)

So an index's cost is reported once, under its own name, instead of being charged to whichever
analyser touched it first, and the per-analyser times that follow are comparable. Only what the
run's analysers declare is built: a run limited to the package rules builds none.

Each index is memoized on the `Solution` instance by a `SolutionIndexCache<T>`:

```csharp
private static readonly SolutionIndexCache<SolutionUsageIndex> Cache = new(BuildAsync);

public static Task<SolutionUsageIndex> GetAsync(Solution solution, CancellationToken cancellationToken)
    => Cache.GetAsync(solution, cancellationToken);
```

What this buys, all of which matters:

- **Built once, no matter how many analysers ask.** Concurrent callers await the one build; none
  duplicates it.
- **Callers await, they do not block.** The indexes used to sit behind `Lazy<T>` with a synchronous
  build, so every analyser arriving mid-build parked a thread-pool thread on `Lazy.Value` while the
  build's own `Parallel.ForEach` wanted threads from the same pool.
- **Cancellation works.** The build runs under the token of the caller that started it (in a run,
  the runner's), so Ctrl+C stops it. A cancelled build is dropped from the cache, and a caller still
  waiting on it with a live token of its own starts a fresh one rather than inheriting somebody
  else's cancellation.
- **`ConditionalWeakTable`** keys on object identity, so a discarded `Solution` is collectable. A
  static `Dictionary<Solution, T>` would leak every workspace the process ever opened.
- **The indexes themselves are immutable and frozen** (`FrozenDictionary`, `FrozenSet`,
  `ImmutableHashSet`) once built, so concurrent reads need no locking.

An analyser that reads an index without declaring it still works, since its `GetAsync` builds the
index on first use, but the build lands inside that analyser's time. `SharedIndexDeclarationShould`
runs every analyser on a fresh solution and fails if it built an index it did not declare.

`ProjectPackageUsageCache` applies the pattern per project rather than per solution. Each csproj
entry is a semantic walk of its own, so the cache keeps one `Lazy<Task<…>>` per project path, and
the package analysers call `PrefetchAsync` with the projects they are about to visit. That builds
the missing entries concurrently, bounded by `SNIPPER_MAX_DOP`, before their per-project loops run.

If you add a shared index, give it a `SolutionIndexCache<T>`, a `SharedIndex` member and an entry in
`SharedIndexes`, and declare it on the analysers that read it. If you add a mutable field to an
analyser, you break both the fan-out and the per-document loops inside it - `SNIPPER_MAX_DOP=1` is
the escape hatch, and it is also your signal that you have done something wrong.

### Per-symbol and per-node memos

Some work is a pure function of something immutable, but keyed too coarsely to sit in a
`solution`-level index. Those use the same weakly-keyed memo at a finer grain:

| Memo | Key | Pure function of |
| --- | --- | --- |
| `ExclusionEngine.FrameworkEntryPointTypes` | `INamedTypeSymbol` | The type. Walks every method's attributes, so it is O(members) per *call*, and it is called once per candidate symbol from six analysers. |
| `DocumentIdentifierIndex.ByRoot` | `SyntaxNode` (the document root) | The tree. Two analysers built the same index from the same root. |
| `ProjectFileReader.Cache` | Project path + last-write | The csproj. Five call sites each parsed the same file; invalidated by timestamp because a stat is far cheaper than an XML parse. |

The rule: **key on the narrowest immutable thing that determines the answer.** Roslyn symbols and
syntax nodes are immutable, so a `ConditionalWeakTable` on them is sound, and it keeps the entry
collectable with the compilation that produced it. A lost race just recomputes the same value - that
is safe precisely because the value is a pure function.

---

## Performance: what was measured, and what was not

Every number below comes from an **interleaved A/B** (alternating runs of two binaries so machine
drift cancels). Single-sample deltas on this box are unreliable: one target measured 3.8% *slower*
from a real improvement when compared non-interleaved. Treat sub-5% wall-clock deltas as noise.

### Where the time actually goes

The analyser fan-out is well-tuned and is *not* the bottleneck on real solutions. Phases measured
across six targets:

| Target | total wall | analysis fan-out | MSBuild load + output | load share |
| --- | --- | --- | --- | --- |
| Peckr.sln (8 proj) | 5.41 s | 1.70 s | 3.71 s | **69%** |
| AzureBusDepot.sln (4 proj) | 4.04 s | 1.40 s | 2.64 s | **65%** |
| Cscli.sln (3 proj) | 9.25 s | 4.80 s | 4.45 s | **48%** |
| Snipper.csproj (1 proj) | 7.29 s | 4.50 s | 2.79 s | 38% |

`MSBuildWorkspace.OpenSolutionAsync` is single-threaded per project and everything downstream needs
its `Solution`, so its cost cannot overlap with analysis. Measured CPU/wall is 1.7-3.0x on a 16-core
box - roughly 80% of the machine idle. **The biggest remaining lever is the load phase, not the
analysers.**

### Runtime configuration

`TieredPGO` is off in `Snipper.csproj`. Snipper is short-lived and spends its life repeating the
same Roslyn/MSBuild call patterns, so instrumented tier-1 recompilation costs more than the
optimised code saves:

| Target | wall | CPU |
| --- | --- | --- |
| Snipper.csproj | -5.5% | **-17.3%** |
| Cscli.sln | -6.8% | **-16.1%** |
| Peckr.sln | -2.9% | **-11.6%** |
| AzureBusDepot.sln | -5.4% | **-14.3%** |
| CardanoSharp.sln | -11.1% | **-18.6%** |
| Mintsafe.sln | -7.0% | **-15.0%** |

CPU falls further and more consistently than wall clock, which is the signature of less work rather
than rescheduling. Override with `DOTNET_TieredPGO=1` if a future workload regresses.

Two knobs were measured and **rejected**, which is worth recording so nobody re-litigates them:

- **Server GC** - neutral to *worse* (Cscli 7.91 -> 8.79 s). Startup cost dominates a single-shot
  process.
- **`TieredCompilation=0`** - **+27% worse**. Disabling tiered JIT entirely is far too expensive for
  a 7-second run.

### Cumulative effect

All of the above plus the allocation and memos below, Phase 0 -> current, interleaved:

| Target | wall | CPU |
| --- | --- | --- |
| Snipper.csproj | -10.5% | **-20.3%** |
| Cscli.sln | -13.3% | **-20.4%** |
| Peckr.sln | -12.8% | **-23.4%** |
| AzureBusDepot.sln | -2.2% | -7.8% |
| CardanoSharp.sln | -18.1% | **-25.3%** |
| Mintsafe.sln | -10.2% | -16.4% |

Verified output-neutral: the `findings` array is byte-identical on all six targets.

### The waste that was removed

| Change | What it was wasting |
| --- | --- |
| `FrameworkEvidenceIndex` receiver text | `memberAccess.Expression.ToString()` ran for **every** invocation, but `receiverText` is only read for 7 method names out of ~4,500 invocations. ~99.9% of those strings were built and discarded. Now gated on the identifier first. |
| `FrameworkEvidenceIndex` attribute names | `attribute.Name.ToString()` allocated for every attribute in every document. Now `AttributeNameText`, which is allocation-free for the common unqualified case and **deliberately still falls back to `ToString()` for qualified names** - callers compare the whole rendered string, so `[Newtonsoft.Json.JsonSerializable]` must keep *failing* the `JsonSerializable` comparison. Using a bare identifier here would be a behaviour change disguised as an allocation fix. |
| `SolutionUsageIndex` interning | Every `SimpleNameSyntax` hashed its identifier three times. Once a name is already in the document's set, the other two lookups are provably redundant, so a `names.Add` guard collapses 3 hashes to 1 on the hottest loop in the tool. |
| `CliRunner` fingerprints | `ComputeFingerprint` (`GetRelativePath` + replace + interpolation + UTF8 + SHA-256) ran up to 3x per finding across the audit and baseline blocks. One reference-keyed memo now serves all five call sites. Keyed by **reference**: record equality would also be correct, since the fingerprint is a function of the fields it compares, but it hashes every string field on each lookup. |
| `RedundancyAnalyser` tree walks | Five full `DescendantNodes()` traversals per document. Now one walk into five buckets. The buckets are still consumed in shape order, but **no longer because the report depends on it** - that was a false constraint. The report used to sort by `(Certainty, FilePath)` alone, so findings in one file shared a tie and inherited analyser order, and `SNP0019` inheriting a parallel producer-completion order from `compilation.GetDiagnostics()` permuted its results between runs. The runner and both writers now impose one total order (`FindingOrder`), so bucket consumption order cannot reach the output. |

### Honest gaps

- **The fingerprint memo is reasoned, not measured.** It only runs under `--baseline` /
  `--audit-suppressions`, which the harness does not use; a targeted interleaved A/B on the two
  largest findings sets gave -3.7%, +2.3% and -5.0% - i.e. noise. It is kept because it strictly
  removes duplicate work, but do not quote a number for it.
- **No monorepo-scale target existed locally.** The largest real solution available was 119 `.cs`
  files. A synthetic 40-project / 800-file / 172k-line target was generated to test the per-symbol
  and per-node memos, which are scale-dependent: they measured as *neutral* on small targets and
  **-2.9% wall / -3.0% CPU** at scale. Small-target measurements will under-report these.
  **Superseded 2026-10-06:** `scripts\measure-run.ps1` now measures `C:\ws\milkrun\MILKRUN.slnx`
  (3,864 `.cs` files, 82 projects) directly, and the 1.7.5 wave used it for every number it
  quotes. See "Performance measurement" below.
- `TieredPGO=0` is a global JIT knob shipped in `runtimeconfig.json`, so it applies to every
  consumer. It is validated on one 16-core x64 box only; re-measure on ARM or older x86 before
  trusting it broadly.

### Performance measurement

`scripts\measure-run.ps1` runs the locally built tool N times against a target and reports wall
clock, **total allocated bytes**, GC collection counts and peak working set as min/median.
Setting `SNIPPER_PERF=1` makes the process print one `SNIPPER-PERF` line on stderr at exit;
nothing else observes the variable, so report bytes are identical whether or not it is set.

```console
.\scripts\measure-run.ps1 -Target C:\ws\milkrun\MILKRUN.slnx -Iterations 3 -MaxDop 8
.\scripts\measure-run.ps1 -Target C:\ws\milkrun\MILKRUN.slnx -ExtraArgs @('--duplicate-detection')
```

Three things about it are load-bearing:

- **It fails on changed output.** Each run's report is normalised for its one volatile field,
  `generatedAtUtc`, and hashed; a mismatch between iterations or against `-BaselineHash` exits 1.
  A performance change that moves findings is a regression, not a win, and this is what makes that
  checkable rather than a promise.
- **Two counters, because they answer different questions.** Peak working set settles whether too
  much was held at once; the allocated-bytes counter settles whether the heap was churned. A run
  can be allocation-neutral and still spike, so quoting one and calling it the cost is how the
  wrong optimisation gets chosen.
- **Wall clock is only comparable within a profile.** Repeat runs share warm MSBuild and NuGet
  caches. Measured on MILKRUN, the clone profile reported *lower* wall clock than the default
  profile purely because it ran second. Compare two code versions inside one profile, interleaved
  when in doubt.

Run-to-run spread is 5-25 s on a ~200 s MILKRUN run, so **a difference smaller than the spread is
noise and must be reported as noise** rather than as a speedup. Baseline figures and the full
before/after comparison are in `docs\history\1_7_5_plan.md`.

---

## Anatomy of the syntax-only analyser

`DuplicateFragmentAnalyser` (SNP0031/SNP0032) is deliberately different, and the difference is a
design decision rather than an inconsistency.

It binds **no semantic model**. It tokenises each file into normalised token shingles, indexes 60-token
windows, extends matches, and keeps only maximal ones:

```
CollectFiles ──► TokenShingleIndex.Build ──► FindFragments ──► KeepMaximalMatches ──► BuildFindings
                                                                             │
                                                                             └──► CloneSet[] ──► CloneDriftDetector ──► SNP0032
```

Five things to know:

- **Normalisation is aggressive.** Every identifier becomes `ID`, so structurally uniform code matches.
  That is why the rule is opt-in and why clones confined to one directory are suppressed — sibling
  files sharing a skeleton are duplication by design.
- **The bucket key is a hash; the window is the truth.** `TokenShingleIndex` buckets candidate windows
  by an FNV-1a/32 hash of their 60 tokens. `Extend` therefore **re-verifies all 60 tokens** before
  seeding a forward scan, and bounds-checks its offsets rather than trusting its caller. This is not
  an optimisation detail — it is the only thing standing between a 32-bit collision and a finding
  claiming two unrelated files are duplicates (invariant 22). It also means the `length < WindowTokens`
  guard in `FindFragments` is reachable rather than dead code, since `Extend` returns `0` on mismatch.
- **One pass, two outputs.** `BuildFindings` returns both the findings *and* the clone sets it proved.
  `CloneDriftDetector` consumes those sets, which is why `--clone-drift` costs no extra analysis and
  why `--clone-drift` implies `--duplicate-detection`.
- **Normalisation discards identifiers; a second pass records them.** Aggressive normalisation is what
  makes Type-2 rename-only clones findable, and it is also why two unrelated DTO property bags match:
  `public string? Name { get; init; }` is one shape whatever it is a property of. `CollectEvidence` walks
  each file a **second** time recording identifier text by line, plus the lines carrying a statement,
  arrow expression, or invocation. `MembersAreComparable` then asks, per clone set, whether every
  member's region is declaration-only *and* no member shares an identifier with the anchor; the verdict
  rides on `CloneSet.MembersComparable` and caps 4C at `Advisory` with a caveat.

  Both halves of that conjunction are required, and this is the non-obvious part. Identifier-disjointness
  alone would reject genuine Type-2 method clones — which is the feature 1C advertises, and a guard
  keyed on identifier overlap was measured and **rejected** because it also deleted
  `Metro60ExtendedProductAttributeDefinitions`↔`WoolworthsProductAttributeDefinitions`, which is real
  per-brand schema duplication. Declaration-onlyness alone would reject any two identically-shaped
  configuration classes. Missing evidence returns *comparable*, so the verdict never depends on which
  flags were passed.

  The walk runs only under `--clone-drift` (opt-in on top of an already opt-in rule), so a default
  `--duplicate-detection` run pays nothing for it. See
  [`Snipper-Feature-Parity-Roadmap.md`](Snipper-Feature-Parity-Roadmap.md) for the calibration.
- **It was the first rule to use `<global>`.** Because it resolves namespaces syntactically through
  the enclosing namespace declaration rather than through a type, it could exclude file-scope clones
  when the symbol-based rules could not. Since 1.7.4 that marker is shared: it lives on
  `AnalysisExclusions.GlobalNamespaceMarker`, is honoured for every rule, and is accepted by the
  CLI flag as well as the config file. The analyser no longer resolves namespaces at all: like every
  rule it reports every clone set, and `NamespaceExclusionFilter` judges the set by its anchor
  afterwards, the same way a path glob does.
- **It uses union-find** (`Find`/`Union` with path compression) to group overlapping maximal matches
  into clone sets.

`Extend` and `TokenShingleIndex.Hash` are `internal` rather than `private` purely so
`TokenShingleIndexShould` can test the window logic directly. Both were widened in 1.7.3 and neither is
part of the analyser's contract with anyone else.

---

## The filtering pipeline

Read this top to bottom; the order is the design.

```
analysedFindings                  ← what the analysers produced; they never see any suppression
      │
      ├──► NamespaceExclusionFilter → visible + namespace-excluded (each with its exclusion)
      │
      ├──► [optional] disabled-rule pass: run just the analysers the disabled rules removed
      │
      ├──► BaselineService: classify VISIBLE findings → newFindings
      │      BaselineService.Write(fingerprints of the SUPPRESSION-INDEPENDENT set, headSha)
      │      (visible + namespace-excluded + disabled-rule findings)
      │
      ├──► EntropyRateCalculator.Compute(new, resolved, changedLines) → status + rate
      │
      ├──► FindingFilter.Apply  (rule off → path glob → severity override)
      │
      ├──► certainty tier floor
      │
      └──► RenderReport + WriteReport
```

The subtlety: **classification walks the visible set, writing walks the suppression-independent
set.** So a suppressed finding is still recorded in the baseline, and lifting a suppression does not
resurface it as "new". Before 4A-2 both walked the visible set, which meant adding a namespace
exclusion *removed* fingerprints from the baseline — harmless with no gate, and a CI failure the
moment `--entropy-budget` enforced anything.

Namespace exclusion sits before classification, not with the report-time channels, so that the
"N known, M new" line and the entropy numerator count only what the user can see — exactly what
they counted when exclusions were applied inside the analysers.

The disabled-rule pass is conditioned so it can never cost anything it does not have to: it runs
only when a rule is disabled *and* a baseline or the audit needs that rule's findings. Every other
channel hides findings the normal pass already produced, so it costs nothing extra.

---

## Namespace exclusion is a filter, not a pass

The 4A audit and the 4A-2 baseline both need the **suppression-independent** finding set: every
finding that exists, ignoring configuration. Namespace exclusions used to be applied *inside* the
analysers — seventeen of them took an `AnalysisExclusions` and skipped excluded candidates — so the
only way to see what they hid was a second analysis pass with exclusions lifted. That pass needed its
own machinery (an exclusion-sensitivity partition of the analysers, rebuilt instances so a "lifted"
pass could not silently reuse excluded ones) and could only report an aggregate count, because a
finding's namespace was not recoverable from the finding.

Now the analysers never see exclusions. `NamespaceExclusionFilter` runs once over what they found:

1. **One resolution rule.** Each finding's anchor location (`FilePath`, `LineNumber`,
   `CharacterOffset`) is mapped back to its syntax tree, and `ExclusionEngine.ResolveNamespace` walks
   the enclosing namespace declarations outward, falling back to the unit's own declaration for
   file-level code above a file-scoped `namespace N;`, and to `<global>` for a file that declares no
   namespace at all. This replaces three paths (symbol, semantic model, syntax) that agreed only by
   care, and fixes two shapes the old ones missed: a nested `namespace B` inside `namespace A`
   resolves to `A.B`, and a clone fragment at file level is credited to the file's namespace.
2. **One match rule.** `AnalysisExclusions.FindCoveringExclusion` returns the nearest configured
   exclusion, so the audit gets one row per namespace with an exact count.
3. **Findings outside C# source are never excluded.** Package and project findings land in project and
   lock files, which have no namespace; they must not read as `<global>` code.
4. **A clone set is judged by its anchor**, as a path glob already judges it. Previously excluded
   copies were removed from the set first, so a set between excluded and non-excluded code
   disappeared when fewer than two copies survived; now it is reported when its anchor is visible,
   still naming every copy, and the baseline records the same set whatever is excluded.

The trade-off is that excluded candidates are now analysed and then discarded rather than skipped.
Usage evidence from excluded code was always gathered, so only the per-candidate checks are extra; a
run with a baseline or the audit, which used to pay a whole second pass, now pays nothing.

---
## Git integration

All git access shells out through `GitProcess`, which builds a `ProcessStartInfo` and passes
arguments via `ArgumentList`.

```
GitMetadata ──┐
              ├──► GitProcess ──► git (child process, stdout+stderr drained)
GitHistory ───┘
```

`GitMetadata` (since 1.6.1) serves commit SHA, dirty state, repository root, and `diff --numstat`.
`GitHistory` (4C) adds batched `git log --name-only` and `git show`, plus patch-hunk parsing.

Three rules learned the hard way, each with a bug behind it:

1. **Never hand-quote a path into a command string.** It was done once; git then searched for a
   pathspec that literally contained the quote characters and returned nothing — silently, with no
   error. Use `ArgumentList`.
2. **Never order commits by timestamp.** Git timestamps have one-second resolution. Two commits in the
   same second tie, and "was the sibling caught up later?" silently answered wrong. Use the position
   in `git log`'s newest-first output — `MostRecentCommit.Order` exists for this.
3. **Drain stdout *and* stderr, concurrently.** Draining one while the child fills the other pipe
   deadlocks. This exact omission was the duplicated-drift SNP0032 found on 4C's first dogfood run.

`GitHistory` batches: one `git log --name-only` for the whole repo, then patch fetches in groups,
rather than one call per clone-set member. Per-path `git log` costs ~90 ms of process spawn on
Windows, which would make per-member lookups unusable at monorepo scale.

---

## Output

| Output | Mechanism | Notes |
| --- | --- | --- |
| Console | `RenderReport` | `FindingOrder`, the same order as the JSON and SARIF reports; a Spectre `Table`. |
| JSON | `BuildJson` | Serialised through `JsonReportSerializerContext`, a **source-generated** context — no reflection, no trimming warnings. |
| SARIF 2.1.0 | `BuildSarifJson` | `Guaranteed → error`, `High/Moderate → warning`, `Advisory → note`, plus `properties.certainty` and `properties.category`. |
| Baseline | `BaselineService.Write` | SHA-256 of `RuleId \| relativePath \| Message`, plus an optional `commitSha`. |
| Ledger | `EntropyLedger.Append` | One row per **scored** run only. |

`filePath` is relativised against the target's directory for the report, while suppression globs match
the **absolute** path. That asymmetry is deliberate but it is also the single most common
misconfiguration — see [Path globs](usage.md#path-globs).

Adding a DTO to the JSON report means adding it to `[JsonSerializable]` on
`JsonReportSerializerContext`, or it will not serialise.

---

## Invariants you must not break

Each of these is load-bearing, and several exist because breaking them was measured.

| # | Invariant | Why |
| --- | --- | --- |
| 1 | Findings are reassembled in analyser **declaration order**, not completion order, and each analyser's slot is put in `FindingOrder`. | The merged list must be identical to a sequential run's, so an analyser may emit in any order. |
| 2 | No mutable state on an analyser instance. | Every analyser in the run shares one `Solution` concurrently. |
| 3 | New shared indexes use `SolutionIndexCache<T>` and are declared through `IWorkspaceAnalyser.SharedIndexes`. | Build once, awaited not blocked on, cancellable; do not leak workspaces; build time reported as its own stage. |
| 4 | Shared indexes are frozen/immutable after build. | Lock-free concurrent reads. |
| 5 | `MSBuildLocator.RegisterDefaults()` runs before any MSBuild-touching code. | Runtime MSBuild resolution; a compile-time reference breaks on SDK upgrades. |
| 6 | Every analyser walks `AnalysisScope`, or calls `ExclusionEngine.ShouldSkipDocument` before binding when it needs its own document set. | Generated code is a correctness problem, not just noise. |
| 7 | Cheap syntactic filtering before `GetDeclaredSymbol` and before evidence gathering. | This ordering is the performance budget. |
| 8 | All `AnsiConsole`/`StatusContext` writes during analysis go through the progress sink's lock (`ConsoleRenderer.CreateAnalysisProgress`). | Spectre's status is process-wide exclusive and not thread-safe. |
| 9 | Namespace exclusion suppresses **findings**, never usage evidence. | Otherwise excluding a generated tree makes real references disappear. |
| 9a | Namespace resolution and matching each live in exactly one place — `ExclusionEngine.ResolveNamespace` and `AnalysisExclusions.FindCoveringExclusion` — and a node resolves as enclosing namespace declarations (joined outward) → the unit's own declaration → `<global>`. | Three resolution paths (symbol, semantic model, syntax) used to agree only by care, and a file-scoped `namespace N;` is a *sibling* of its file-level `using` directives, so an ancestor-only walk classifies them as global and `<global>` then suppresses usings in every namespaced file. |
| 10 | Baseline classification reads visible findings; baseline writing reads the suppression-independent set. | Otherwise a config toggle churns the baseline. |
| 11 | `FindingFilter.Classify` is shared with the audit. | The audit must not be able to disagree with report behaviour. |
| 12 | Evidence is never a finding. | A reference to a member is not a reason to flag it. |
| 13 | Config loading never throws. | A typo in `snipper.json` degrades to "less filtering", not a red build. |
| 14 | Git access is read-only and degrades to silence outside a checkout. | Not every consumer has a repository. |
| 15 | Findings are ordered by `FindingOrder` alone. An analyser sorts only where its own logic depends on order, as `CloneDriftDetector`'s first-wins deduplication does. | Byte-identical reports; reviewable diffs. |
| 16 | No `dynamic`; no exceptions for flow control. | The rules are stated per-wave; keep them. |
| 17 | Analysers never take `AnalysisExclusions`; namespace exclusion is applied to their findings by `NamespaceExclusionFilter`. | The moment an analyser filters by namespace, its output depends on configuration: the baseline needs a second, exclusions-lifted pass again, and the audit can no longer say which namespace hid what. |
| 18 | Namespace exclusion runs **before** baseline classification, and the baseline is written from visible + namespace-excluded + disabled-rule findings. | Classifying first would count excluded findings as new, and in the entropy numerator. Writing from the visible set would churn the baseline whenever an exclusion changes. |
| 19 | A cache keyed on a csproj path must be invalidated by last-write time. | `ProjectFileReader` serves five call sites. Mutating its invalidation check so the cache never expires left **every other test green** - nothing in the suite rewrites a csproj between two reads. `ProjectFileReaderShould` exists because of that gap. |
| 20 | Do not "optimise" a name comparison by returning a bare identifier. | Callers compare whole rendered strings, so `[Newtonsoft.Json.JsonSerializable]` deliberately does *not* match `[JsonSerializable]`. Swapping `ToString()` for `SimpleNameOf` would silently widen every attribute rule. Use `AttributeNameText`, which keeps the qualified fallback. |
| 21 | A project path is not unique in a `Solution`. | A multi-targeted csproj appears once per TFM, and a project reached by two referencing paths can appear twice. Keying a dictionary on `project.FilePath` threw `ArgumentException: An item with the same key has already been added` and took the whole run down. `DuplicateProjectPathShould` pins the one-to-many shape. |
| 22 | **A hash-indexed candidate must be verified against the tokens it stands for, not just its bounds.** | `DuplicateFragmentAnalyser.Extend` seeded its forward scan at `WindowTokens`, so offsets `[0,60)` — the window the bucket key was computed from — were never compared. The bucket key is FNV-1a/32, so a collision promoted two unrelated files to a clone. On an 81-project monorepo this produced findings claiming an interpolated `ToString()` was a 60-token duplicate of `Substitute.For<Refit.IApiResponse>()`. 232 such findings disappeared when `Extend` began proving the window. `TokenShingleIndexShould` pins this, including a brute-forced real collision (~130k trials). |
| 23 | **Code written to make a test exercise a rule must be asserted to compile.** | `SampleApp` accumulated 8 compile defects (duplicate `JsonIncludeAttribute`, 5 × `CS0122` on internals, a void call assigned to a discard, a missing `Serilog` using, an illegal local shadow, a `System.Text.Json` name bound to a stand-in type). ~560 tests stayed green, because Snipper reports findings rather than requiring a clean build: a broken fixture still yields partial semantic information, and nothing checked the rest. `FixtureBuildShould` now builds it as part of the suite. |
| 24 | **Do not make a fixture observable through `InternalsVisibleTo` to reach internals.** | It compiles, and it silently demotes real findings: `HierarchyDeadCodeAnalyser` treats a type with friend assemblies as Advisory, because a friend assembly is a legitimate external caller. Adding it to `SampleApp` broke 5 tests asserting Moderate. The analyser was right. `CoreLib/InternalFixtureBridge.cs` exposes public entry points instead. |

---

## Adding a rule: checklist

1. **Pick the rule id.** Ids are stable strings, not a dense enumeration — `SNP0014`–`SNP0017` are
   reserved and unused. Take the next free id.
2. **Add the `FindingCategory`** if the rule needs a new one. They are `byte`-valued and ordered by
   when the rule shipped.
3. **Create the analyser** in `src/Snipper/Analysis/`, implementing `IWorkspaceAnalyser`.
   - Declare `RuleIds`.
   - Reuse an existing shared index if one fits; do not add a private full-solution scan.
   - Follow the pre-filter ordering in [Anatomy of a semantic analyser](#anatomy-of-a-semantic-analyser).
4. **Register it** in `AnalyserFactory.Build`. If it is opt-in, add a flag and say why in a comment
   with a measurement — both existing opt-in rules have one.
5. **Choose a certainty tier per outcome.** Most rules emit two tiers for a demotion path
   (SNP0005 is Moderate, Advisory when `InternalsVisibleTo` is present). A tier is a claim about
   evidence, not about severity of consequence.
6. **Red first.** In a **new** test file, with the test failing for the right reason.
7. **Green, then verify the test can fail.** Deliberately break the decision logic and confirm a test
   catches it. 4C's two mutations caught 3 and 1 failures respectively; that is the bar. 1.7.3's
   window-verification mutation turns all six window tests red.
8. **If you added fixture code to make a rule reachable, check it compiles.** See
   [Testing conventions](#testing-conventions) — `SampleApp` went years without compiling.
9. **Dogfood.** Run it on Snipper's own solution. Fix what it finds in your own code — do not suppress.
   4C found a real duplicated-git-runner bug in its own author on the day it was written.
10. **Document.** README rule-catalogue row; `usage.md` if it adds an option; the current wave's
   `plan_1_7_x.md` for the design and measurements.
11. **Full suite green**, and leave the tree cleaner than you found it.

---

## Testing conventions

- `test/Snipper.Tests/`, xUnit + FluentAssertions. 714 tests, ~2 min per run. The test sources
  themselves are 55 files / ~8,560 lines; the remainder of the tree is the `TestAssets/SampleApp`
  fixture the analyser tests run against.
- **The fixture must compile, and that is asserted.** `FixtureBuildShould` builds every project in
  `SampleApp` and fails on any diagnostic. Before it existed, `SampleApp` carried 8 compile defects
  (duplicate `JsonIncludeAttribute`, five `CS0122` on internal fixtures, a void call bound to a
  discard, a missing `Serilog` using, an illegal local shadow, and `System.Text.Json.JsonSerializer`
  binding to the `CoreLib.JsonSerializer` stand-in) while ~560 tests passed. Nothing noticed because
  Snipper *reports* findings rather than requiring a clean build — a broken fixture still yields
  partial semantic information, so most assertions still had something to chew on. One detail is
  worth knowing before you write such code: `OuterShadowedIsUnused` needs an inner local shadowing an
  outer one, which is `CS0136` in **every** form — nested block, `for`, `foreach`, `using`, `catch`,
  lambda parameter, `out var`, `is var`. Of nine shapes tried, exactly one compiles: a local
  function's parameter.
- **Do not reach fixture internals with `InternalsVisibleTo`.** It compiles, and it changes findings:
  `HierarchyDeadCodeAnalyser` demotes a type with friend assemblies from Moderate to Advisory, because
  a friend assembly is a legitimate external caller. Using it broke 5 tests asserting Moderate before
  it was reverted in favour of `CoreLib/InternalFixtureBridge.cs`.
- **Any test that calls `CliRunner.RunAsync` needs `[Collection("CliRuns")]`.** Spectre.Console's
  `Status` is process-wide exclusive, so parallel collections deadlock or interleave.
- **Prefer the extracted seams over `RunAsync` where you can.** `CommandLineParser`,
  `ReportWriter` and `AnalyserFactory` need no workspace and no console, so their tests run
  concurrently with the rest of the suite. Roughly 21% of the suite is still serialised behind
  `CliRuns` purely because it drives the CLI end to end; that share is the measure of how much
  coverage can still be lifted out of the orchestrator.
- Tests that create temporary git repositories must **clear read-only attributes before
  `Directory.Delete`**, and swallow `UnauthorizedAccessException` as a backstop. Swallowing alone once
  hid a leak of 256 directories.
- Assert on the JSON report for anything end-to-end; console output is for humans.
- A test that passes because the fixture is too weak is worse than no test. 4C's patch-parser tests
  were rewritten against **real git output** rather than a canned patch string for exactly this reason.
  1.7.3's version of the same lesson is stronger: the fixture did not fail *softly*, it failed to
  compile at all, and 581 tests still passed.

---

## Where things live

| Path | Holds |
| --- | --- |
| `Program.cs` | Entry point. MSBuildLocator. `--version` short-circuit. |
| `Cli/CliRunner.cs` | **Orchestration only**: `RunAsync` reads as its stages — options → workspace → plan → passes → audit → baseline → filter → report — each handing the next an immutable record. The run order lives here and nowhere else. |
| `Cli/CommandLineParser.cs` | Pure `string[]` → `CommandLineOptions`. No workspace, no console, no git. |
| `Cli/AnalyserFactory.cs` | Which analysers run, and the opt-in gates. |
| `Cli/ReportWriter.cs` | JSON and SARIF 2.1.0 serialisation, plus the report DTOs. |
| `Cli/ConsoleRenderer.cs` | Findings table, suppression audit table, entropy table, and the thread-safe analysis progress sink. |
| `Cli/ToolVersion.cs`, `ReportFormat.cs` | Small shared types, split out so the above do not depend on `CliRunner`. |
| `Cli/AnalysisRunner.cs` | Compilation warm-up, the shared-index stage, parallel fan-out, progress reporting, deterministic merge. No console dependency. |
| `Cli/SnipperConfig.cs`, `SnipperConfigLoader.cs` | `snipper.json` schema and tolerant discovery. |
| `Cli/FindingFilter.cs` | The three report-time channels and their precedence. Shared with the audit. |
| `Cli/BaselineService.cs` | Fingerprints, load/write, and the "does a disabled rule need its own pass" predicate. |
| `Cli/NamespaceExclusionFilter.cs` | Namespace exclusion over findings: location → namespace → covering exclusion. |
| `Cli/SuppressionAudit.cs`, `NamespaceInventory.cs` | 4A audit, `obsolete[]` detection, glob/file and namespace probes. |
| `Cli/EntropyRate.cs`, `EntropyLedger.cs` | 4B rate calculation, statuses, monthly ledger. |
| `Cli/GitMetadata.cs`, `Analysis/GitProcess.cs` | git subprocess plumbing. |
| `Cli/JsonReportSerializerContext.cs`, `SarifModels.cs` | Source-generated JSON and SARIF DTOs. |
| `Analysis/IWorkspaceAnalyser.cs` | The contract. Read this first. |
| `Analysis/AnalysisScope.cs` | The in-scope documents most analysers walk, flat across projects. |
| `Analysis/SharedIndex.cs` | The shared indexes an analyser can declare, and their build as a set. |
| `Analysis/SolutionIndexCache.cs` | The awaitable, cancellable once-per-solution memo behind each of them. |
| `Analysis/SolutionUsageIndex.cs` etc. | Shared solution-keyed indexes. |
| `Analysis/ExclusionEngine.cs` | Generated/external gates, namespace resolution (`ResolveNamespace`), plus `IsTestCode` — complexity-only, never used by `ShouldSkipDocument`. |
| `Analysis/DuplicateFragmentAnalyser.cs`, `CloneDriftDetector.cs`, `GitHistory.cs` | 4C. |
| `Models/SnipperFinding.cs`, `CertaintyTier.cs`, `FindingCategory.cs`, `FindingOrder.cs` | The finding record, its two enums, and the one total order over findings. |

---

## See also

- [Usage guide](usage.md) — options, filtering semantics, and the analyser→rule catalogue.
- [CI integration](ci-integration.md) — running this in a pipeline.
- [`1_7_3_plan.md`](history/1_7_3_plan.md) — the clone-window verification fix, and the guard that the test fixture compiles.
- [`1_7_4_plan.md`](history/1_7_4_plan.md) — namespace exclusion reaching file-scope code, and a suspected SNP0019 defect withdrawn by a fixture.
- [`1_7_0_plan.md`](history/1_7_0_plan.md) — design rationale and measurements for the 1.7.0 wave.
- [Feature parity roadmap](Snipper-Feature-Parity-Roadmap.md) — what is planned next.
- [Documentation history](history/README.md) — superseded release plans and wave specs. Invariants
  22–24 and the `FixtureBuildShould` guidance above were added from the 1.7.3 release; the plan
  documents they came from are filed there.
