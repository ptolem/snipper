namespace Snipper.Cli;

using System.Collections.Frozen;
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
/// The <b>order</b> of the steps below is the design, not their implementation. In
/// particular: analysis runs before filtering; the baseline is classified against visible
/// findings but written from the suppression-independent set; and report-time channels run
/// after fingerprinting so they can never churn the baseline file.
/// </para>
/// </summary>
public static class CliRunner
{
    /// <summary>
    /// Package version (e.g. "1.0.5"). Safe to call before MSBuildLocator
    /// registration — touches no MSBuild/workspace types.
    /// </summary>
    public static string GetToolVersion() => ToolVersion.Current;

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Any(static a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.WriteLine(ToolVersion.Current);
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold blue]Snipper[/] [grey]{ToolVersion.Current}[/]");

        if (!CommandLineParser.TryParse(args, out var options, out var parseError))
        {
            AnsiConsole.MarkupLine($"[red]{parseError}[/]");
            if (parseError is not null && parseError.StartsWith("Error: Missing target path", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[yellow]{CommandLineParser.Usage}[/]");
            }

            return 1;
        }

        var targetPath = options.TargetPath;

        // Reported after parsing rather than printed from inside it, so the parser stays free
        // of Spectre.Console and remains testable without capturing console output. Nothing
        // else prints during parsing, so this preserves the original message order.
        foreach (var malformed in options.MalformedNamespaces)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: ignoring malformed namespace '{Markup.Escape(malformed)}'.[/]");
        }

        using var workspace = MSBuildWorkspace.Create();

        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        var duplicateWarningCount = 0;
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
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
            return 1;
        }

        if (duplicateWarningCount > 0)
        {
            AnsiConsole.MarkupLine($"[grey]({duplicateWarningCount} duplicate workspace warning(s) suppressed; warnings do not block analysis)[/]");
        }

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

        var analysers = AnalyserFactory.Build(options, exclusions, targetPath);

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

        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<SnipperFinding> allFindings = [];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing solution symbols and references...", async ctx =>
            {
                allFindings = await AnalysisRunner.RunAsync(analysers, solution, ctx, CancellationToken.None)
                    .ConfigureAwait(false);
            });

        AnsiConsole.MarkupLine($"[grey]Analysis completed in {totalStopwatch.Elapsed.TotalSeconds:0.0}s.[/]");

        // Hoisted: the baseline block and the suppression audit both need it to compute
        // stable, path-relative fingerprints.
        var baseDirectory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();

        // ---------------------------------------------------------------------
        // Wave 4 / 4A-2 + 4A: the suppression-independent finding set.
        //
        // Two of the five suppression channels remove findings before they exist as
        // objects — namespace exclusions inside the analysers, and disabled rules by
        // removing their analyser. Both therefore rewrite a baseline computed from
        // `allFindings`, so that those findings resurface as "new" the day someone
        // removes the suppression (measured: -30 and -19 fingerprints on the SampleApp
        // fixture). That is harmless today because no gate exists, and a real CI failure
        // the moment 4B enforces a budget.
        //
        // 4A-2 fixes it by fingerprinting the suppression-independent set instead — every
        // finding that exists, ignoring config. This is the same computation the 4A audit
        // already performs for its namespace shadow pass, so it is computed once and
        // shared. It extends to namespaces and disabled rules the behaviour `exclude.paths`,
        // severity overrides and `--certainty-tier` have always had (they apply after
        // fingerprinting, so their findings were always baselined).
        //
        // Conditioned so that it can never cost anything it does not have to: no baseline
        // means nothing to churn, and the churn-free channels are excluded by
        // construction, so a user relying only on those never pays for the extra pass.
        // ---------------------------------------------------------------------
        var hasChurnProneSuppression = excludedNamespaces.Count > 0 || removedByDisabledRules.Count > 0;
        var needsSuppressionIndependentPass = BaselineService.RequiresSuppressionIndependentFingerprints(
            baselineRequested: options.BaselinePath is not null,
            excludedNamespaceCount: excludedNamespaces.Count,
            removedAnalyserCount: removedByDisabledRules.Count);

        var needsDisabledShadow = options.AuditSuppressions && removedByDisabledRules.Count > 0;
        var needsNamespaceShadow = options.AuditSuppressions && excludedNamespaces.Count > 0;

        var needsUnexcludedPass = needsSuppressionIndependentPass
            || (options.AuditSuppressions && hasChurnProneSuppression);

        var shadowStopwatch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<SnipperFinding> disabledShadow = [];
        IReadOnlyList<SnipperFinding> unexcludedFindings = [];

        if (needsDisabledShadow)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Auditing disabled rules (shadow pass)...", async ctx =>
                {
                    disabledShadow = await AnalysisRunner.RunAsync(removedByDisabledRules, solution, ctx, CancellationToken.None)
                        .ConfigureAwait(false);
                });
        }

        if (needsUnexcludedPass)
        {
            var reason = needsSuppressionIndependentPass
                ? "keeping the baseline stable under configuration changes"
                : "auditing namespace exclusions";

            // Only the exclusion-aware half needs re-running. The four package and project
            // graph analysers take no exclusions, so their output is already identical to
            // what a lifted pass would produce; re-running them would repeat the most
            // expensive work in the set for no change in the result.
            //
            // The instances are rebuilt with exclusions lifted rather than reusing the ones
            // already built: the existing instances still hold the original exclusions, so
            // reusing them would run an unlifted pass that looks lifted and silently reports
            // no hidden findings.
            //
            // Restricted to the audit-only case. When this pass feeds the baseline, the
            // whole set is rebuilt, so the recorded baseline cannot depend on which
            // analysers happen to be exclusion-agnostic today.
            IReadOnlyList<IWorkspaceAnalyser>? liftedAnalysers = null;
            IReadOnlyList<SnipperFinding> reusableFindings = [];

            if (options.AuditSuppressions && !needsSuppressionIndependentPass)
            {
                var liftedAll = AnalyserFactory.Build(options, AnalysisExclusions.None, targetPath);
                liftedAnalysers = [.. AnalyserFactory.PartitionByExclusionSensitivity(liftedAll).Aware];
                reusableFindings = FindingsFromExclusionAgnosticAnalysers(analysers, allFindings);
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Re-running analysis with suppressions lifted ({reason})...", async ctx =>
                {
                    var liftedFindings = await AnalysisRunner.RunAsync(
                        liftedAnalysers ?? AnalyserFactory.Build(options, AnalysisExclusions.None, targetPath),
                        solution,
                        ctx,
                        CancellationToken.None).ConfigureAwait(false);

                    unexcludedFindings = liftedAnalysers is null
                        ? liftedFindings
                        : liftedFindings.Concat(reusableFindings).ToArray();
                });
        }

        // Single source of truth for the baseline. `allFindings` already *is* the
        // suppression-independent set unless a churn-prone channel is configured, in
        // which case the lifted pass is authoritative.
        var baselineSourceFindings = needsSuppressionIndependentPass ? unexcludedFindings : allFindings;

        // One fingerprint per finding instance for the whole run. ComputeFingerprint is not
        // cheap - GetRelativePath, a separator replace, an interpolated string, a UTF8 encode
        // and a SHA256 per call - and the audit and baseline blocks below walk overlapping
        // sets: disabledShadow is hashed at both the disabled-set build and the accounted-set
        // build, allFindings at both the accounted-set build and baseline classification, and
        // unexcludedFindings at both the namespace-shadow walk and the baseline write.
        //
        // Keyed by REFERENCE on purpose. SnipperFinding is a record, so default equality
        // would run a structural comparison on every lookup - and two genuinely distinct
        // findings can compare equal, which would hand back the wrong fingerprint. Same
        // instance in, same fingerprint out, because the inputs are deterministic.
        var fingerprintMemo = new Dictionary<SnipperFinding, string>(ReferenceEqualityComparer.Instance);

        string Fingerprint(SnipperFinding finding)
        {
            if (!fingerprintMemo.TryGetValue(finding, out var fingerprint))
            {
                fingerprint = BaselineService.ComputeFingerprint(finding, baseDirectory);
                fingerprintMemo[finding] = fingerprint;
            }

            return fingerprint;
        }

        SuppressionAudit? suppressionAudit = null;

        if (options.AuditSuppressions)
        {
            var disabledFingerprints = disabledShadow
                .Select(Fingerprint)
                .ToFrozenSet(StringComparer.Ordinal);

            // Namespace channel = everything the lifted pass found, minus what the
            // normal run already accounted for and minus the disabled-rule set.
            // Fingerprinted once per finding: this runs over the full finding set on a
            // large repository, and hashing the same finding twice per predicate was pure
            // repeated work. `Fingerprint` also collapses the overlap with the sets above.
            var accountedFingerprints = allFindings
                .Concat(disabledShadow)
                .Select(Fingerprint)
                .ToFrozenSet(StringComparer.Ordinal);

            var namespaceShadow = new List<SnipperFinding>();
            foreach (var finding in unexcludedFindings)
            {
                var fingerprint = Fingerprint(finding);
                if (!accountedFingerprints.Contains(fingerprint) && !disabledFingerprints.Contains(fingerprint))
                {
                    namespaceShadow.Add(finding);
                }
            }

            var fileInventory = NamespaceInventory.CollectFiles(solution);
            var namespaceProbe = NamespaceInventory.ExistenceProbe(NamespaceInventory.CollectDeclaredNamespaces(solution));

            suppressionAudit = SuppressionAuditBuilder.Build(
                analysedFindings: allFindings,
                config: config,
                minimumCertainty: options.MinimumCertainty,
                disabledRuleShadowFindings: disabledShadow,
                namespaceShadowFindings: namespaceShadow,
                shadowAnalysisRan: needsDisabledShadow || needsNamespaceShadow,
                globMatchesNoFile: fileInventory.Count > 0 ? fileInventory.MatchesNoFile : null,
                namespaceExists: namespaceProbe);

            shadowStopwatch.Stop();
            var elapsedSuffix = options.AuditSuppressions ? $", {shadowStopwatch.Elapsed.TotalSeconds:0.0}s" : string.Empty;

            // Headline now, breakdown after the findings table: the shadow passes can be
            // slow on a large solution and the user should not wait for a number.
            AnsiConsole.MarkupLine(
                $"[grey]Suppression audit complete{elapsedSuffix} — " +
                $"{suppressionAudit.Totals.FindingsHiddenByShadow} finding(s) were hidden before analysis; " +
                $"breakdown follows the report.[/]");
        }

        IReadOnlyList<SnipperFinding> reportableFindings = allFindings;
        EntropyRateResult? entropyResult = null;

        if (options.BaselinePath is { } baselinePath)
        {
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

            foreach (var finding in allFindings)
            {
                if (knownFingerprints.Contains(Fingerprint(finding)))
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
                allFingerprints.Add(Fingerprint(finding));
            }

            var headCommitSha = GitMetadata.TryResolveCommitSha(baseDirectory, out var workingTreeDirty);

            try
            {
                // Stamp the commit so the next run can diff against this exact reference.
                // Optional in the schema: a baseline written before 4B still loads and is
                // reported as having no reference, rather than being guessed at.
                BaselineService.Write(baselinePath, allFingerprints, headCommitSha);

                var stabilityNote = needsSuppressionIndependentPass
                    ? $" Baseline covers all {baselineSourceFindings.Count} finding(s) including suppressed ones, so configuration changes cannot churn it."
                    : string.Empty;

                AnsiConsole.MarkupLine($"[grey]Baseline: {suppressedCount} known finding(s) suppressed, {newFindings.Count} new. Baseline updated at {baselinePath}.{stabilityNote}[/]");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not update baseline file: {ex.Message}[/]");
            }

            reportableFindings = newFindings;

            if (options.EntropyRateRequested)
            {
                var currentFingerprints = allFingerprints.ToFrozenSet(StringComparer.Ordinal);
                var resolvedFindings = EntropyRateCalculator.CountResolved(knownFingerprints, currentFingerprints);

                // Denominator: lines changed over exactly the range the recorded findings came
                // from. `exclude.paths` is honoured here for the same reason it is honoured on
                // findings — a regenerated lockfile is not code anyone wrote, and counting it
                // would swamp the rate.
                int? changedLines = null;
                if (knownBaseline.CommitSha is { Length: > 0 } baselineCommit
                    && headCommitSha is { Length: > 0 })
                {
                    var numstat = GitMetadata.TryGetNumstat(baseDirectory, baselineCommit, headCommitSha);
                    if (numstat is not null)
                    {
                        var total = 0;
                        foreach (var entry in numstat)
                        {
                            if (!IsExcludedFromDenominator(entry.Path, baseDirectory, config))
                            {
                                total += entry.Added + entry.Deleted;
                            }
                        }

                        changedLines = total;
                    }
                }

                entropyResult = EntropyRateCalculator.Compute(new EntropyRateInputs(
                    BaselineFileExisted: baselineExisted,
                    BaselineCommitSha: knownBaseline.CommitSha,
                    HeadCommitSha: headCommitSha,
                    IsGitRepository: headCommitSha is not null,
                    WorkingTreeDirty: workingTreeDirty,
                    NewFindings: newFindings.Count,
                    ResolvedFindings: resolvedFindings,
                    ChangedLines: changedLines,
                    MinimumLines: options.EntropyMinimumLines));
            }
        }

        // snipper.json path globs, severity overrides and report-time rule drops apply
        // at report/output time only — after baseline fingerprinting, so toggling those
        // channels never churns the baseline file. Same contract as the tier filter below.
        //
        // Namespace exclusions and disabling a sole-rule analyser used to break that
        // contract, because they suppress before this point. As of 4A-2 they no longer do:
        // the baseline is written from the suppression-independent set above, so every
        // channel is churn-free and `--audit-suppressions` quantifies what they hide.
        var findingsBeforeConfig = reportableFindings.Count;
        reportableFindings = FindingFilter.Apply(reportableFindings, config);
        if (reportableFindings.Count != findingsBeforeConfig)
        {
            AnsiConsole.MarkupLine($"[grey]Config: {findingsBeforeConfig - reportableFindings.Count} finding(s) suppressed by snipper.json.[/]");
        }

        // Certainty-tier filter applies at report/output time only. The baseline
        // above always tracks the full finding set so that switching tiers between
        // runs never churns the baseline file.
        if (options.MinimumCertainty is { } tier)
        {
            var unfilteredCount = reportableFindings.Count;
            reportableFindings = reportableFindings.Where(f => f.Certainty <= tier).ToArray();
            AnsiConsole.MarkupLine($"[grey]Certainty filter: showing tier {tier} and above — {reportableFindings.Count} of {unfilteredCount} finding(s).[/]");
        }

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
            : ReportWriter.Write(reportableFindings, options.OutputPath, options.Format, baseDirectory, suppressionAudit, entropyReport);

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
    /// The findings produced by the exclusion-agnostic analysers, which can be reused as-is
    /// by a lifted pass because their output does not depend on namespace exclusions.
    /// </summary>
    private static IReadOnlyList<SnipperFinding> FindingsFromExclusionAgnosticAnalysers(
        IReadOnlyList<IWorkspaceAnalyser> analysers,
        IReadOnlyList<SnipperFinding> allFindings)
    {
        var agnostic = AnalyserFactory.PartitionByExclusionSensitivity(analysers).Agnostic;

        var agnosticRuleIds = agnostic
            .SelectMany(static a => a.RuleIds)
            .ToFrozenSet(StringComparer.Ordinal);

        return allFindings.Where(f => agnosticRuleIds.Contains(f.RuleId)).ToArray();
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
}