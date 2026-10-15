namespace Snipper.Cli;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Spectre.Console;
using Snipper.Analysis;
using Snipper.Models;

/// <summary>
/// The orchestrator: opens the workspace, decides which analysers run, sequences the
/// analysis passes, applies the filtering pipeline, and produces output.
/// <para>
/// This type used to hold argument parsing, report serialisation, console rendering and
/// analyser construction as well, at roughly 1,100 lines. Those are now
/// <see cref="CommandLineParser"/>, <see cref="ReportWriter"/>,
/// <see cref="ConsoleRenderer"/> and <see cref="AnalyserFactory"/> respectively — all pure
/// with respect to this orchestration, and all unit-testable without a workspace or a
/// console. What remains here is only what genuinely needs all of them at once: ordering.
/// </para>
/// <para>
/// The <b>order</b> of the stages in <see cref="RunAsync"/> is the design, not their
/// implementation. In particular: analysis runs before filtering; the baseline is
/// classified against visible findings but written from the suppression-independent set;
/// and report-time channels run after fingerprinting so they can never churn the baseline
/// file. Each stage hands the next an immutable record, so what a stage may depend on is
/// visible in its signature.
/// </para>
/// </summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        AnsiConsole.MarkupLine($"[bold blue]Snipper[/] [grey]{ToolVersion.Current}[/]");

        if (!TryParseOptions(args, out var options))
        {
            return 1;
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await OpenSolutionAsync(workspace, options.TargetPath).ConfigureAwait(false);
        if (solution is null)
        {
            return 1;
        }

        var plan = PlanRun(options);
        var passes = await RunAnalysisPassesAsync(plan, solution).ConfigureAwait(false);

        // Shared by the stages below so git is asked about the checkout at most once per run,
        // and only if a baseline or a report needs it.
        var gitHead = new Lazy<GitHead>(() => GitHead.Resolve(plan.BaseDirectory));

        var suppressionAudit = options.AuditSuppressions
            ? BuildSuppressionAudit(plan, passes, solution)
            : null;

        var baseline = options.BaselinePath is { } baselinePath
            ? ApplyBaseline(baselinePath, plan, passes, gitHead.Value)
            : new BaselineOutcome(passes.VisibleFindings, EntropyResult: null);

        var reportableFindings = ApplyReportTimeFilters(baseline.ReportableFindings, plan);

        return Report(reportableFindings, suppressionAudit, baseline.EntropyResult, plan, gitHead);
    }

    private static bool TryParseOptions(string[] args, [NotNullWhen(true)] out CommandLineOptions? options)
    {
        if (!CommandLineParser.TryParse(args, out var parsed, out var parseError))
        {
            AnsiConsole.MarkupLine($"[red]{parseError}[/]");
            if (parseError is not null && parseError.StartsWith("Error: Missing target path", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[yellow]{CommandLineParser.Usage}[/]");
            }

            options = null;
            return false;
        }

        // Reported after parsing rather than printed from inside it, so the parser stays free
        // of Spectre.Console and remains testable without capturing console output. Nothing
        // else prints during parsing, so this preserves the original message order.
        foreach (var malformed in parsed.MalformedNamespaces)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: ignoring malformed namespace '{Markup.Escape(malformed)}'.[/]");
        }

        options = parsed;
        return true;
    }

    /// <summary>
    /// Opens the target, or reports why it could not and returns null. Project-level load
    /// failures are not fatal: each distinct one is printed once and analysis continues.
    /// </summary>
    private static async Task<Solution?> OpenSolutionAsync(MSBuildWorkspace workspace, string targetPath)
    {
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        var duplicateWarningCount = 0;
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
            {
                return;
            }

            if (seenWarnings.Add(e.Diagnostic.Message))
            {
                AnsiConsole.MarkupLine($"[yellow]Workspace warning (non-fatal): {e.Diagnostic.Message}[/]");
            }
            else
            {
                duplicateWarningCount++;
            }
        });

        AnsiConsole.MarkupLine($"[blue]Opening solution/project: {targetPath}...[/]");

        Solution solution;
        try
        {
            // MSBuildWorkspace 5.x handles .sln and .slnx natively via the
            // SolutionPersistence serializer.
            solution = targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(targetPath).ConfigureAwait(false)).Solution
                : await workspace.OpenSolutionAsync(targetPath).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, not a bad target. Reporting this as "failed to open" would misattribute a
            // user interrupt to their solution file.
            throw;
        }
        catch (Exception ex)
        {
            // Any failure here means the target could not be opened, and saying so is more useful
            // than whatever the third-party stack chose to throw. This used to filter on a closed
            // set - IO exceptions plus InvalidProjectFileException/InvalidSolutionFileException,
            // the latter two matched by name because they live in MSBuild assemblies resolved at
            // runtime through MSBuildLocator rather than referenced at compile time. That filter
            // leaked, because the malformed-input cases are none of those:
            //
            //   System.Xml.XmlException         invalid XML in a .slnx, from SolutionPersistence's
            //                                 SlnXMLSerializer constructing an XmlDocument over the
            //                                 stream. Derives from SystemException, not IOException.
            //   SolutionException               .slnx that is well-formed XML with an invalid
            //                                 schema (wrong root element, bad project Type GUID).
            //   InvalidDataException            a .slnf filter naming a solution that is not there.
            //
            // None of those reach the old filter, so a hand-written malformed .slnx escaped as an
            // unhandled exception and the process died on a raw stack trace with the runtime's
            // abort exit code rather than the documented 1.
            //
            // Catching broadly is safe *here* and only here: this try block contains the dispatch
            // ternary and two await calls into MSBuild/SolutionPersistence, and no Snipper logic, so
            // there is no defect of ours for a broad catch to hide. Cancellation is rethrown above.
            // Project-level failures stay non-fatal and continue through the workspace-failed handler.
            //
            // Both interpolations are markup-escaped: an XmlException message routinely ends
            // "[at line 3, position 12]", and unescaped brackets are parsed as Spectre markup.
            AnsiConsole.MarkupLine(
                $"[red]Error: Failed to open '{Markup.Escape(targetPath)}': {Markup.Escape(ex.Message)}[/]");
            return null;
        }

        if (duplicateWarningCount > 0)
        {
            AnsiConsole.MarkupLine($"[grey]({duplicateWarningCount} duplicate workspace warning(s) suppressed; warnings do not block analysis)[/]");
        }

        return solution;
    }

    /// <summary>
    /// Settles everything the run will do from the options and snipper.json, before any
    /// analysis starts.
    /// </summary>
    private static RunPlan PlanRun(CommandLineOptions options)
    {
        var targetPath = options.TargetPath;

        var configWarnings = new List<string>();
        var config = SnipperConfigLoader.Load(targetPath, out var configPath, configWarnings);
        if (configPath is not null)
        {
            AnsiConsole.MarkupLine($"[grey]Config loaded from {Markup.Escape(configPath)}[/]");
        }

        foreach (var warning in configWarnings)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: {Markup.Escape(warning)}[/]");
        }

        // Command-line namespaces union with the config's rather than replacing them, so a
        // CI job can add an exclusion without editing the committed file.
        var excludedNamespaces = options.ExcludedNamespaces.Concat(config.ExcludedNamespaces).ToList();
        var exclusions = AnalysisExclusions.Create(excludedNamespaces);
        if (excludedNamespaces.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Excluding namespaces (findings suppressed, usage evidence retained): {Markup.Escape(string.Join(", ", excludedNamespaces))}[/]");
        }

        // Hoisted ahead of the analyser construction: the baseline block, the suppression
        // audit and SNP0031's copy-naming all resolve paths against this one root, and
        // a second derivation of it is a drift risk that shows up as a baseline that
        // stops matching.
        var baseDirectory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();

        var analysers = AnalyserFactory.Build(options, targetPath, baseDirectory);

        if (!options.IncludeCloneDrift && !options.IncludeDuplicateDetection)
        {
            AnsiConsole.MarkupLine("[grey]Duplicate detection (SNP0031) is off by default — enable with --duplicate-detection.[/]");
        }

        if (!options.IncludeConfigAnalysis)
        {
            AnsiConsole.MarkupLine("[grey]Configuration analysis (SNP0007/SNP0008) is off by default — enable with --config-analysis.[/]");
        }

        // Captured before removal so the audit can run just these analysers and count
        // what the disabled rules are hiding (see docs/history/1_7_0_plan.md).
        List<IWorkspaceAnalyser> removedByDisabledRules = [];
        if (config.DisabledRules.Count > 0)
        {
            removedByDisabledRules = [.. analysers.Where(a => !FindingFilter.IsAnalyserEnabled(a.RuleIds, config))];
            analysers.RemoveAll(a => !FindingFilter.IsAnalyserEnabled(a.RuleIds, config));
            AnsiConsole.MarkupLine($"[grey]Disabled rules (snipper.json): {Markup.Escape(string.Join(", ", config.DisabledRules.Order(StringComparer.Ordinal)))}[/]");
        }

        return new RunPlan(options, config, exclusions, baseDirectory, analysers, removedByDisabledRules);
    }

    /// <summary>
    /// The analysis pass, namespace exclusion over its findings, then the disabled-rule pass
    /// when the plan calls for one.
    /// </summary>
    private static async Task<AnalysisPasses> RunAnalysisPassesAsync(RunPlan plan, Solution solution)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var analysedFindings = await RunUnderStatusAsync(
            "Analyzing solution symbols and references...", plan.Analysers, solution).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[grey]Analysis completed in {totalStopwatch.Elapsed.TotalSeconds:0.0}s.[/]");

        // Shadow time runs from here to the end of the audit build, which reports it.
        var shadowStartTimestamp = Stopwatch.GetTimestamp();

        // A filter over what analysis found, not a pass: the analysers never see
        // exclusions, so what this hides is already in hand for the baseline and the audit.
        var namespaceFilter = await NamespaceExclusionFilter.ApplyAsync(
            analysedFindings, solution, plan.Exclusions, CancellationToken.None).ConfigureAwait(false);

        IReadOnlyList<SnipperFinding> disabledRuleFindings = plan.NeedsDisabledRulePass
            ? await RunUnderStatusAsync(
                plan.Options.AuditSuppressions
                    ? "Auditing disabled rules (shadow pass)..."
                    : "Running disabled rules for the baseline (keeping it stable under configuration changes)...",
                plan.RemovedByDisabledRules,
                solution).ConfigureAwait(false)
            : [];

        return new AnalysisPasses(namespaceFilter.Visible, namespaceFilter.Excluded, disabledRuleFindings, shadowStartTimestamp);
    }

    /// <summary>
    /// One <see cref="AnalysisRunner"/> pass under a status spinner. Spectre's status display
    /// is process-wide exclusive, so passes run one after another, never nested.
    /// </summary>
    private static Task<IReadOnlyList<SnipperFinding>> RunUnderStatusAsync(
        string status,
        IReadOnlyList<IWorkspaceAnalyser> analysers,
        Solution solution) =>
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(status, context => AnalysisRunner.RunAsync(
                analysers,
                solution,
                ConsoleRenderer.CreateAnalysisProgress(context),
                CancellationToken.None));

    private static SuppressionAudit BuildSuppressionAudit(
        RunPlan plan,
        AnalysisPasses passes,
        Solution solution)
    {
        var fileInventory = NamespaceInventory.CollectFiles(solution);
        var namespaceProbe = NamespaceInventory.ExistenceProbe(NamespaceInventory.Collect(solution));

        var suppressionAudit = SuppressionAuditBuilder.Build(
            analysedFindings: passes.VisibleFindings,
            config: plan.Config,
            minimumCertainty: plan.Options.MinimumCertainty,
            disabledRuleShadowFindings: passes.DisabledRuleFindings,
            namespaceExcludedFindings: passes.NamespaceExcluded,
            // Namespace exclusions are always measured now; disabled rules are measured
            // because the audit asks for their pass.
            shadowAnalysisRan: plan.RemovedByDisabledRules.Count > 0 || plan.Exclusions.Namespaces.Count > 0,
            globMatchesNoFile: fileInventory.Count > 0 ? fileInventory.MatchesNoFile : null,
            namespaceExists: namespaceProbe);

        var shadowElapsed = Stopwatch.GetElapsedTime(passes.ShadowStartTimestamp);

        // Headline now, breakdown after the findings table: the disabled-rule pass can be
        // slow on a large solution and the user should not wait for a number.
        AnsiConsole.MarkupLine(
            $"[grey]Suppression audit complete, {shadowElapsed.TotalSeconds:0.0}s — " +
            $"{suppressionAudit.Totals.FindingsHiddenByShadow} finding(s) were hidden by namespace exclusions and disabled rules; " +
            $"breakdown follows the report.[/]");

        return suppressionAudit;
    }

    /// <summary>
    /// Classifies findings against the baseline, rewrites it, and computes the entropy rate
    /// when asked. Only the new findings stay reportable.
    /// </summary>
    private static BaselineOutcome ApplyBaseline(
        string baselinePath,
        RunPlan plan,
        AnalysisPasses passes,
        GitHead gitHead)
    {
        var fingerprints = new FingerprintMemo(plan.BaseDirectory);

        // Single source of truth for the baseline: every finding that exists, ignoring
        // configuration (4A-2). With nothing suppressed it is just the visible set.
        var baselineSourceFindings = passes.SuppressionIndependentFindings;

        // Read once: 4B needs the commit the baseline was stamped at, which is what binds
        // the entropy denominator to exactly the range these findings came from.
        var baselineExisted = File.Exists(baselinePath);
        var knownBaseline = BaselineService.Load(baselinePath);
        var knownFingerprints = knownBaseline.Fingerprints is { Length: > 0 } loaded
            ? loaded.ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;

        // Classification walks the *visible* set: only findings the user can actually
        // see can be new. Writing walks the *suppression-independent* set, so the
        // recorded baseline does not depend on configuration (4A-2). Under no
        // churn-prone suppression the two are the same list.
        var newFindings = new List<SnipperFinding>();
        var suppressedCount = 0;

        foreach (var finding in passes.VisibleFindings)
        {
            if (knownFingerprints.Contains(fingerprints.Get(finding)))
            {
                suppressedCount++;
            }
            else
            {
                newFindings.Add(finding);
            }
        }

        var allFingerprints = new List<string>(baselineSourceFindings.Count);
        foreach (var finding in baselineSourceFindings)
        {
            allFingerprints.Add(fingerprints.Get(finding));
        }

        try
        {
            // Stamp the commit so the next run can diff against this exact reference.
            // Optional in the schema: a baseline written before 4B still loads and is
            // reported as having no reference, rather than being guessed at.
            BaselineService.Write(baselinePath, allFingerprints, gitHead.CommitSha);

            var stabilityNote = plan.BaselineIncludesSuppressed
                ? $" Baseline covers all {baselineSourceFindings.Count} finding(s) including suppressed ones, so configuration changes cannot churn it."
                : string.Empty;

            AnsiConsole.MarkupLine($"[grey]Baseline: {suppressedCount} known finding(s) suppressed, {newFindings.Count} new. Baseline updated at {baselinePath}.{stabilityNote}[/]");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Could not update baseline file: {ex.Message}[/]");
        }

        if (!plan.Options.EntropyRateRequested)
        {
            return new BaselineOutcome(newFindings, EntropyResult: null);
        }

        var currentFingerprints = allFingerprints.ToFrozenSet(StringComparer.Ordinal);

        var entropyResult = EntropyRateCalculator.Compute(new EntropyRateInputs(
            BaselineFileExisted: baselineExisted,
            BaselineCommitSha: knownBaseline.CommitSha,
            HeadCommitSha: gitHead.CommitSha,
            IsGitRepository: gitHead.CommitSha is not null,
            WorkingTreeDirty: gitHead.WorkingTreeDirty,
            NewFindings: newFindings.Count,
            ResolvedFindings: EntropyRateCalculator.CountResolved(knownFingerprints, currentFingerprints),
            ChangedLines: CountChangedLines(knownBaseline.CommitSha, gitHead.CommitSha, plan),
            MinimumLines: plan.Options.EntropyMinimumLines));

        return new BaselineOutcome(newFindings, entropyResult);
    }

    /// <summary>
    /// The entropy denominator: lines changed over exactly the range the recorded findings
    /// came from, or null when that range is unknown. `exclude.paths` is honoured here for
    /// the same reason it is honoured on findings — a regenerated lockfile is not code
    /// anyone wrote, and counting it would swamp the rate.
    /// </summary>
    private static int? CountChangedLines(string? baselineCommitSha, string? headCommitSha, RunPlan plan)
    {
        if (baselineCommitSha is not { Length: > 0 } || headCommitSha is not { Length: > 0 })
        {
            return null;
        }

        var numstat = GitMetadata.TryGetNumstat(plan.BaseDirectory, baselineCommitSha, headCommitSha);
        if (numstat is null)
        {
            return null;
        }

        var total = 0;
        foreach (var entry in numstat)
        {
            if (!IsExcludedFromDenominator(entry.Path, plan.BaseDirectory, plan.Config))
            {
                total += entry.Added + entry.Deleted;
            }
        }

        return total;
    }

    private static IReadOnlyList<SnipperFinding> ApplyReportTimeFilters(
        IReadOnlyList<SnipperFinding> findings,
        RunPlan plan)
    {
        // snipper.json path globs, severity overrides and report-time rule drops apply
        // at report/output time only — after baseline fingerprinting, so toggling those
        // channels never churns the baseline file. Same contract as the tier filter below.
        //
        // Namespace exclusions and disabled rules apply before this point: namespace
        // exclusion straight after analysis, so baseline classification only counts what
        // the user can see, and a disabled sole-rule analyser by not running. Neither churns
        // the baseline either, because it is written from the suppression-independent set
        // (4A-2), and `--audit-suppressions` quantifies what they hide.
        var reportableFindings = FindingFilter.Apply(findings, plan.Config);
        if (reportableFindings.Count != findings.Count)
        {
            AnsiConsole.MarkupLine($"[grey]Config: {findings.Count - reportableFindings.Count} finding(s) suppressed by snipper.json.[/]");
        }

        // Certainty-tier filter applies at report/output time only. The baseline
        // always tracks the full finding set so that switching tiers between runs
        // never churns the baseline file.
        if (plan.Options.MinimumCertainty is { } tier)
        {
            var unfilteredCount = reportableFindings.Count;
            reportableFindings = reportableFindings.Where(f => f.Certainty <= tier).ToArray();
            AnsiConsole.MarkupLine($"[grey]Certainty filter: showing tier {tier} and above — {reportableFindings.Count} of {unfilteredCount} finding(s).[/]");
        }

        return reportableFindings;
    }

    /// <summary>
    /// Renders the console report, records the entropy rate, writes the report file, and
    /// returns the process exit code.
    /// </summary>
    private static int Report(
        IReadOnlyList<SnipperFinding> reportableFindings,
        SuppressionAudit? suppressionAudit,
        EntropyRateResult? entropyResult,
        RunPlan plan,
        Lazy<GitHead> gitHead)
    {
        var options = plan.Options;

        ConsoleRenderer.RenderReport(reportableFindings);

        if (suppressionAudit is not null)
        {
            ConsoleRenderer.RenderSuppressionAudit(suppressionAudit);
        }

        EntropyRateReport? entropyReport = null;

        if (entropyResult is not null)
        {
            // Monthly history comes from the ledger, so it is only available once one is being
            // kept. Reporting an empty series rather than a fabricated zero is the point.
            var monthly = options.EntropyLedgerPath is null
                ? []
                : EntropyLedger.MonthlyRates(EntropyLedger.Load(options.EntropyLedgerPath));

            entropyReport = EntropyRateCalculator.ToReport(entropyResult, options.EntropyBudget, monthly);
            ConsoleRenderer.RenderEntropyRate(entropyReport);

            if (entropyReport.Scored && options.EntropyLedgerPath is not null)
            {
                AppendEntropyLedgerEntry(options.EntropyLedgerPath, entropyResult);
            }
        }

        var writeExitCode = options.OutputPath is null
            ? 0
            : ReportWriter.Write(
                reportableFindings,
                options.OutputPath,
                options.Format,
                gitHead.Value.CommitSha,
                gitHead.Value.WorkingTreeDirty,
                suppressionAudit,
                entropyReport);

        // A real failure outranks a policy failure: CI should see "snipper could not write the
        // report" rather than a budget breach it cannot act on. Exit code 3 is deliberately
        // distinct from 1 (usage) and 2 (IO) so a pipeline can tell the two apart.
        if (writeExitCode != 0)
        {
            return writeExitCode;
        }

        return entropyReport?.BudgetExceeded == true ? 3 : 0;
    }

    /// <summary>
    /// Appends a scored run to the committed ledger. Only scored runs are recorded — a row with
    /// no rate would make the ledger look fuller than the data is.
    /// </summary>
    private static void AppendEntropyLedgerEntry(string ledgerPath, EntropyRateResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            EntropyLedger.Append(
                ledgerPath,
                new EntropyLedgerEntry(
                    CommitSha: result.HeadCommitSha ?? string.Empty,
                    RecordedAtUtc: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                    NewFindings: result.NewFindings,
                    ResolvedFindings: result.ResolvedFindings,
                    LinesChanged: result.LinesChanged ?? 0,
                    BaselineCommitSha: result.BaselineCommitSha,
                    FindingsPerKloc: result.FindingsPerKloc,
                    Scored: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Could not update entropy ledger: {ex.Message}[/]");
        }
    }

    /// <summary>
    /// Whether a changed file counts toward the entropy denominator. Reuses the finding-side
    /// glob semantics so both halves of the ratio describe the same set of code.
    /// </summary>
    private static bool IsExcludedFromDenominator(string relativePath, string baseDirectory, SnipperConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(config);

        if (config.ExcludedPathGlobs.Count == 0)
        {
            return false;
        }

        // numstat emits forward slashes; findings carry native separators. Compare the full
        // path, as FindingFilter does, so one set of globs governs both.
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        foreach (var glob in config.ExcludedPathGlobs)
        {
            if (GlobPattern.IsMatch(glob, fullPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What this run will do, settled from the options and snipper.json before any analysis
    /// starts. The pass decisions are derived here so every later stage reads one answer.
    /// </summary>
    private sealed record RunPlan(
        CommandLineOptions Options,
        SnipperConfig Config,
        AnalysisExclusions Exclusions,
        string BaseDirectory,
        IReadOnlyList<IWorkspaceAnalyser> Analysers,
        IReadOnlyList<IWorkspaceAnalyser> RemovedByDisabledRules)
    {
        /// <summary>
        /// Wave 4 / 4A-2: whether the baseline records findings the user cannot see.
        /// <para>
        /// Namespace exclusions and disabled rules would otherwise rewrite the baseline, so
        /// that the findings they hide resurface as "new" the day someone removes the
        /// suppression (measured: -30 and -19 fingerprints on the SampleApp fixture). The
        /// baseline is therefore written from the suppression-independent set — every
        /// finding that exists, ignoring config — which is the behaviour `exclude.paths`,
        /// severity overrides and `--certainty-tier` have always had.
        /// </para>
        /// </summary>
        public bool BaselineIncludesSuppressed => Options.BaselinePath is not null
            && (Exclusions.Namespaces.Count > 0 || RemovedByDisabledRules.Count > 0);

        /// <summary>
        /// Whether the analysers a disabled rule removed still run on their own: for the
        /// audit to count what they hide, or for the baseline to record it. Namespace
        /// exclusions never need a pass, because the analysers never see them.
        /// </summary>
        public bool NeedsDisabledRulePass => Options.AuditSuppressions
            ? RemovedByDisabledRules.Count > 0
            : BaselineService.RequiresDisabledAnalyserPass(
                baselineRequested: Options.BaselinePath is not null,
                removedAnalyserCount: RemovedByDisabledRules.Count);
    }

    /// <summary>
    /// The findings of the analysis pass, split by namespace exclusion, and of the
    /// disabled-rule pass, which is empty when it was not needed.
    /// </summary>
    private sealed record AnalysisPasses(
        IReadOnlyList<SnipperFinding> VisibleFindings,
        IReadOnlyList<NamespaceExcludedFinding> NamespaceExcluded,
        IReadOnlyList<SnipperFinding> DisabledRuleFindings,
        long ShadowStartTimestamp)
    {
        /// <summary>
        /// Every finding that exists, ignoring configuration. With nothing suppressed this
        /// is the visible set itself.
        /// </summary>
        public IReadOnlyList<SnipperFinding> SuppressionIndependentFindings =>
            NamespaceExcluded.Count == 0 && DisabledRuleFindings.Count == 0
                ? VisibleFindings
                : [.. VisibleFindings, .. NamespaceExcluded.Select(static e => e.Finding), .. DisabledRuleFindings];
    }

    /// <summary>
    /// What survives baseline classification, and the entropy rate when it was requested.
    /// </summary>
    private sealed record BaselineOutcome(
        IReadOnlyList<SnipperFinding> ReportableFindings,
        EntropyRateResult? EntropyResult);

    /// <summary>
    /// The analysed checkout's HEAD and dirty state, asked of git at most once per run. The
    /// baseline stamp and the report header used to ask separately, two git processes each,
    /// and the second ask ran after the baseline write, so a baseline committed inside the
    /// repository made every report warn that the tree was dirty.
    /// </summary>
    private sealed record GitHead(string? CommitSha, bool WorkingTreeDirty)
    {
        public static GitHead Resolve(string directory)
        {
            var commitSha = GitMetadata.TryResolveCommitSha(directory, out var workingTreeDirty);
            return new GitHead(commitSha, workingTreeDirty);
        }
    }

    /// <summary>
    /// One fingerprint per finding instance. ComputeFingerprint is not cheap -
    /// GetRelativePath, a separator replace, an interpolated string, a UTF8 encode and a
    /// SHA256 per call - and the baseline walks two overlapping sets: the visible findings
    /// when classifying, and the suppression-independent set, which contains them, when
    /// writing.
    /// <para>
    /// Keyed by reference: same instance in, same fingerprint out. Record equality would
    /// also be correct - the fingerprint is a function of fields equality compares - but
    /// it hashes every string field on each lookup to get there.
    /// </para>
    /// </summary>
    private sealed class FingerprintMemo(string baseDirectory)
    {
        private readonly Dictionary<SnipperFinding, string> _fingerprints = new(ReferenceEqualityComparer.Instance);

        public string Get(SnipperFinding finding)
        {
            if (!_fingerprints.TryGetValue(finding, out var fingerprint))
            {
                fingerprint = BaselineService.ComputeFingerprint(finding, baseDirectory);
                _fingerprints[finding] = fingerprint;
            }

            return fingerprint;
        }
    }
}
