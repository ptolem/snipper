namespace Snipper.Cli;

using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Spectre.Console;
using Snipper.Analysis;
using Snipper.Models;

public static class CliRunner
{
    private static readonly string ToolVersion = ComputeToolVersion();

    /// <summary>
    /// Package version (e.g. "1.0.5"). Safe to call before MSBuildLocator
    /// registration — touches no MSBuild/workspace types.
    /// </summary>
    public static string GetToolVersion() => ToolVersion;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(static a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.WriteLine(ToolVersion);
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold blue]Snipper[/] [grey]{ToolVersion}[/]");

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            AnsiConsole.MarkupLine("[red]Error: Missing target path. Provide a .sln, .slnx, or .csproj path.[/]");
            AnsiConsole.MarkupLine("[yellow]Usage: Snipper <path-to-solution-or-project> [[output-file]] [[--format json|sarif]] [[--baseline <path>]] [[--certainty-tier guaranteed|high|moderate|advisory]] [[--exclude-namespaces <list>]] [[--config-analysis]] [[--duplicate-detection]] [[--audit-suppressions]] [[--entropy-rate]] [[--entropy-budget <per-kloc>]] [[--entropy-ledger <path>]] [[--entropy-min-lines <n>]] [[--version]][/]");
            return 1;
        }

        var targetPath = Path.GetFullPath(args[0]);
        if (!File.Exists(targetPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Target file does not exist: {targetPath}[/]");
            return 1;
        }

        string? outputPath = null;
        string? baselinePath = null;
        CertaintyTier? minimumCertainty = null;
        var format = ReportFormat.Json;
        var includeConfigAnalysis = false;
        var includeDuplicateDetection = false;
        var auditSuppressions = false;
        var entropyRateRequested = false;
        double? entropyBudget = null;
        string? entropyLedgerPath = null;
        var entropyMinimumLines = EntropyRateCalculator.DefaultMinimumLines;
        var excludedNamespaces = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !Enum.TryParse(args[i + 1], ignoreCase: true, out format))
                {
                    AnsiConsole.MarkupLine("[red]Error: --format requires a value of 'json' or 'sarif'.[/]");
                    return 1;
                }

                i++;
            }
            else if (string.Equals(args[i], "--certainty-tier", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !Enum.TryParse<CertaintyTier>(args[i + 1], ignoreCase: true, out var parsedTier))
                {
                    AnsiConsole.MarkupLine("[red]Error: --certainty-tier requires a value of 'guaranteed', 'high', 'moderate', or 'advisory'.[/]");
                    return 1;
                }

                minimumCertainty = parsedTier;
                i++;
            }
            else if (string.Equals(args[i], "--baseline", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    AnsiConsole.MarkupLine("[red]Error: --baseline requires a file path.[/]");
                    return 1;
                }

                baselinePath = Path.GetFullPath(args[i + 1]);
                i++;
            }
            else if (string.Equals(args[i], "--entropy-rate", StringComparison.OrdinalIgnoreCase))
            {
                entropyRateRequested = true;
            }
            else if (string.Equals(args[i], "--entropy-budget", StringComparison.OrdinalIgnoreCase))
            {
                // Parsed with InvariantCulture on purpose: a budget is a CI-facing number and
                // must not shift meaning with the machine's locale.
                if (i + 1 >= args.Length
                    || !double.TryParse(
                        args[i + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var budget)
                    || budget < 0)
                {
                    AnsiConsole.MarkupLine("[red]Error: --entropy-budget requires a non-negative number of findings per kLOC.[/]");
                    return 1;
                }

                entropyBudget = budget;
                entropyRateRequested = true;
                i++;
            }
            else if (string.Equals(args[i], "--entropy-ledger", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    AnsiConsole.MarkupLine("[red]Error: --entropy-ledger requires a file path.[/]");
                    return 1;
                }

                entropyLedgerPath = Path.GetFullPath(args[i + 1]);
                entropyRateRequested = true;
                i++;
            }
            else if (string.Equals(args[i], "--entropy-min-lines", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var minimumLines))
                {
                    AnsiConsole.MarkupLine("[red]Error: --entropy-min-lines requires an integer line count.[/]");
                    return 1;
                }

                entropyMinimumLines = minimumLines;
                entropyRateRequested = true;
                i++;
            }
            else if (string.Equals(args[i], "--exclude-namespaces", StringComparison.OrdinalIgnoreCase))
            {
                // Repeatable flag; each occurrence may carry a comma-separated list.
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    AnsiConsole.MarkupLine("[red]Error: --exclude-namespaces requires a value (comma-separated and/or repeatable).[/]");
                    return 1;
                }

                foreach (var entry in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IsValidNamespace(entry))
                    {
                        excludedNamespaces.Add(entry);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: ignoring malformed namespace '{Markup.Escape(entry)}'.[/]");
                    }
                }

                i++;
            }
            else if (string.Equals(args[i], "--config-analysis", StringComparison.OrdinalIgnoreCase))
            {
                includeConfigAnalysis = true;
            }
            else if (string.Equals(args[i], "--duplicate-detection", StringComparison.OrdinalIgnoreCase))
            {
                includeDuplicateDetection = true;
            }
            else if (string.Equals(args[i], "--audit-suppressions", StringComparison.OrdinalIgnoreCase))
            {
                auditSuppressions = true;
            }
            else if (outputPath is null && !string.IsNullOrWhiteSpace(args[i]))
            {
                outputPath = Path.GetFullPath(args[i]);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error: Unexpected argument '{args[i]}'.[/]");
                return 1;
            }
        }

        using var workspace = MSBuildWorkspace.Create();

        // "New" is defined relative to a recorded baseline, so an entropy rate without one has
        // no numerator. This is a usage error rather than a silent zero: a quiet 0.00 would be
        // indistinguishable from a genuinely clean change, and would pass any budget.
        if (entropyRateRequested && baselinePath is null)
        {
            AnsiConsole.MarkupLine("[red]Error: --entropy-rate requires --baseline, because 'new' findings are defined relative to a recorded baseline.[/]");
            return 1;
        }

        // Design-time build diagnostics (e.g. NuGet vulnerability audit warnings)
        // are non-fatal: the project still loads and is analyzed. Print each
        // distinct message once and summarize duplicates instead of spamming.
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
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException
            || ex.GetType().Name is "InvalidProjectFileException" or "InvalidSolutionFileException")
        {
            // InvalidProjectFileException lives in Microsoft.Build assemblies that are
            // runtime-resolved via MSBuildLocator, not compile-time referenced — matched by name.
            AnsiConsole.MarkupLine($"[red]Error: Failed to open '{targetPath}': {ex.Message}[/]");
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

        excludedNamespaces.AddRange(config.ExcludedNamespaces);

        var exclusions = AnalysisExclusions.Create(excludedNamespaces);
        if (excludedNamespaces.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Excluding namespaces (findings suppressed, usage evidence retained): {Markup.Escape(string.Join(", ", excludedNamespaces))}[/]");
        }

        // Configuration binding analysis (SNP0007/SNP0008) is opt-in: indirect
        // binding through referenced libraries and framework conventions makes its
        // false-positive rate too high for default runs. The analyser remains in
        // the codebase for a long-term fix and is exercised by the test suite.
        //
        // Built by a local factory because the suppression audit (4A) needs the same
        // analyser set under different exclusions: once with namespaces lifted, and
        // once containing only the analysers a disabled rule removed.
        List<IWorkspaceAnalyser> BuildAnalysers(AnalysisExclusions analysisExclusions)
        {
            var built = new List<IWorkspaceAnalyser>
            {
                new UnreachableCodeAnalyser(analysisExclusions),
                new UnusedLocalVariableAnalyser(analysisExclusions),
                new UnusedPrivateMemberAnalyser(analysisExclusions),
                new UnusedParameterAnalyser(analysisExclusions),
                new UnusedNonPrivateMemberAnalyser(analysisExclusions),
                new UnreferencedPackageAnalyser(),
                new ObsoleteMemberAnalyser(analysisExclusions),
                new OrphanProjectAnalyser(),
                new RedundantTransitivePackageAnalyser(),
                new FrameworkInboxPackageAnalyser(),
                new UnusedUsingDirectiveAnalyser(analysisExclusions),
                new CommentedCodeAnalyser(analysisExclusions),
                new WriteOnlyFieldAnalyser(analysisExclusions),
                new RedundancyAnalyser(analysisExclusions),
                new HierarchyDeadCodeAnalyser(analysisExclusions),
                new TighteningAnalyser(analysisExclusions),
                new EventNeverInvokedAnalyser(analysisExclusions),
            };

            if (includeConfigAnalysis)
            {
                built.Add(new ConfigurationBindingAnalyser(analysisExclusions));
            }

            if (includeDuplicateDetection)
            {
                built.Add(new DuplicateFragmentAnalyser(analysisExclusions));
            }

            return built;
        }

        var analysers = BuildAnalysers(exclusions);

        // Duplicate detection (SNP0031) is opt-in for the same reason, and with
        // a measured result behind it: on Snipper's own solution it produced
        // 1160 findings across 988 clone sets, because all 17 workspace
        // analysers share one 200+ token structural skeleton (project loop,
        // document loop, GetSemanticModelAsync, ShouldSkipDocument,
        // DescendantNodes) and every identifier normalizes to ID. That is a
        // true positive about duplication, but it is duplication by design, and
        // a rule that is red on a clean codebase is not useful on every run.
        // The analyser also applies a cross-directory/cross-project guard, which
        // removes the largest same-directory share of that noise (measured: 1160
        // to 642 findings on Snipper.slnx).
        if (!includeDuplicateDetection)
        {
            AnsiConsole.MarkupLine("[grey]Duplicate detection (SNP0031) is off by default — enable with --duplicate-detection.[/]");
        }

        if (!includeConfigAnalysis)
        {
            AnsiConsole.MarkupLine("[grey]Configuration analysis (SNP0007/SNP0008) is off by default — enable with --config-analysis.[/]");
        }

        // Captured before removal so the audit can run just these analysers and count
        // what the disabled rules are hiding (see docs/plan_1_7_0.md).
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
            baselineRequested: baselinePath is not null,
            excludedNamespaceCount: excludedNamespaces.Count,
            removedAnalyserCount: removedByDisabledRules.Count);

        var needsDisabledShadow = auditSuppressions && removedByDisabledRules.Count > 0;
        var needsNamespaceShadow = auditSuppressions && excludedNamespaces.Count > 0;

        var needsUnexcludedPass = needsSuppressionIndependentPass
            || (auditSuppressions && hasChurnProneSuppression);

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

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Re-running analysis with suppressions lifted ({reason})...", async ctx =>
                {
                    unexcludedFindings = await AnalysisRunner.RunAsync(
                        BuildAnalysers(AnalysisExclusions.None), solution, ctx, CancellationToken.None)
                        .ConfigureAwait(false);
                });
        }

        // Single source of truth for the baseline. `allFindings` already *is* the
        // suppression-independent set unless a churn-prone channel is configured, in
        // which case the lifted pass is authoritative.
        var baselineSourceFindings = needsSuppressionIndependentPass ? unexcludedFindings : allFindings;

        SuppressionAudit? suppressionAudit = null;

        if (auditSuppressions)
        {
            var disabledFingerprints = disabledShadow
                .Select(f => BaselineService.ComputeFingerprint(f, baseDirectory))
                .ToFrozenSet(StringComparer.Ordinal);

            // Namespace channel = everything the lifted pass found, minus what the
            // normal run already accounted for and minus the disabled-rule set.
            var accountedFingerprints = allFindings
                .Concat(disabledShadow)
                .Select(f => BaselineService.ComputeFingerprint(f, baseDirectory))
                .ToFrozenSet(StringComparer.Ordinal);

            var namespaceShadow = unexcludedFindings
                .Where(f => !accountedFingerprints.Contains(BaselineService.ComputeFingerprint(f, baseDirectory)))
                .Where(f => !disabledFingerprints.Contains(BaselineService.ComputeFingerprint(f, baseDirectory)))
                .ToArray();

            var fileInventory = NamespaceInventory.CollectFiles(solution);
            var namespaceProbe = NamespaceInventory.ExistenceProbe(NamespaceInventory.CollectDeclaredNamespaces(solution));

            suppressionAudit = SuppressionAuditBuilder.Build(
                analysedFindings: allFindings,
                config: config,
                minimumCertainty: minimumCertainty,
                disabledRuleShadowFindings: disabledShadow,
                namespaceShadowFindings: namespaceShadow,
                shadowAnalysisRan: needsDisabledShadow || needsNamespaceShadow,
                globMatchesNoFile: fileInventory.Count > 0 ? fileInventory.MatchesNoFile : null,
                namespaceExists: namespaceProbe);

            shadowStopwatch.Stop();
            var elapsedSuffix = auditSuppressions ? $", {shadowStopwatch.Elapsed.TotalSeconds:0.0}s" : string.Empty;

            // Headline now, breakdown after the findings table: the shadow passes can be
            // slow on a large solution and the user should not wait for a number.
            AnsiConsole.MarkupLine(
                $"[grey]Suppression audit complete{elapsedSuffix} — " +
                $"{suppressionAudit.Totals.FindingsHiddenByShadow} finding(s) were hidden before analysis; " +
                $"breakdown follows the report.[/]");
        }

        IReadOnlyList<SnipperFinding> reportableFindings = allFindings;
        EntropyRateResult? entropyResult = null;

        if (baselinePath is not null)
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
                if (knownFingerprints.Contains(BaselineService.ComputeFingerprint(finding, baseDirectory)))
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
                allFingerprints.Add(BaselineService.ComputeFingerprint(finding, baseDirectory));
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

            if (entropyRateRequested)
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
                    MinimumLines: entropyMinimumLines));
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
        if (minimumCertainty is { } tier)
        {
            var unfilteredCount = reportableFindings.Count;
            reportableFindings = reportableFindings.Where(f => f.Certainty <= tier).ToArray();
            AnsiConsole.MarkupLine($"[grey]Certainty filter: showing tier {tier} and above — {reportableFindings.Count} of {unfilteredCount} finding(s).[/]");
        }

        RenderReport(reportableFindings);

        if (suppressionAudit is not null)
        {
            RenderSuppressionAudit(suppressionAudit);
        }

        EntropyRateReport? entropyReport = null;

        if (entropyResult is not null)
        {
            // Monthly history comes from the ledger, so it is only available once one is being
            // kept. Reporting an empty series rather than a fabricated zero is the point.
            var monthly = entropyLedgerPath is null
                ? []
                : EntropyLedger.MonthlyRates(EntropyLedger.Load(entropyLedgerPath));

            entropyReport = EntropyRateCalculator.ToReport(entropyResult, entropyBudget, monthly);
            RenderEntropyRate(entropyReport);

            if (entropyReport.Scored && entropyLedgerPath is not null)
            {
                AppendEntropyLedgerEntry(entropyLedgerPath, entropyResult);
            }
        }

        var writeExitCode = outputPath is null
            ? 0
            : WriteReport(reportableFindings, outputPath, format, Path.GetDirectoryName(targetPath), suppressionAudit, entropyReport);

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

    private static int WriteReport(
        IReadOnlyList<SnipperFinding> findings,
        string outputPath,
        ReportFormat format,
        string? targetDirectory,
        SuppressionAudit? suppressionAudit = null,
        EntropyRateReport? entropyRate = null)
    {
        var commitSha = GitMetadata.TryResolveCommitSha(targetDirectory, out var workingTreeDirty);
        if (workingTreeDirty)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: the analysed working tree has uncommitted changes — finding locations may already have drifted.[/]");
        }

        var json = format switch
        {
            ReportFormat.Sarif => BuildSarifJson(findings),
            _ => BuildJson(findings, commitSha, suppressionAudit, entropyRate),
        };

        try
        {
            var parentDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(outputPath, json);
            AnsiConsole.MarkupLine($"[green]{format.ToString().ToUpperInvariant()} report written to: {outputPath}[/]");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Error: Failed to write report to {outputPath}: {ex.Message}[/]");
            return 2;
        }
    }

    internal static string BuildJson(
        IReadOnlyList<SnipperFinding> findings,
        string? commitSha,
        SuppressionAudit? suppressionAudit = null,
        EntropyRateReport? entropyRate = null)
    {
        var lineCache = new SourceLineCache();
        var report = new SnipperReport(
            ToolVersion: ToolVersion,
            CommitSha: commitSha,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Findings: findings
                .OrderBy(static f => f.Certainty)
                .ThenBy(static f => f.FilePath)
                .Select(f => new FindingReportEntry(
                    RuleId: f.RuleId,
                    Title: f.Title,
                    Message: f.Message,
                    Certainty: f.Certainty.ToString(),
                    Category: f.Category.ToString(),
                    FilePath: f.FilePath,
                    LineNumber: f.LineNumber,
                    CharacterOffset: f.CharacterOffset,
                    LineText: lineCache.GetLine(f.FilePath, f.LineNumber)))
                .ToArray(),
            Suppression: suppressionAudit,
            EntropyRate: entropyRate);

        return JsonSerializer.Serialize(report, JsonReportSerializerContext.Default.SnipperReport);
    }

    private static bool IsValidNamespace(string value)
    {
        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0 || (!char.IsLetter(segment[0]) && segment[0] != '_'))
            {
                return false;
            }

            for (var i = 1; i < segment.Length; i++)
            {
                if (!char.IsLetterOrDigit(segment[i]) && segment[i] != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string ComputeToolVersion()
    {
        var assembly = typeof(CliRunner).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit>" SourceLink suffix when present.
            var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    internal static string BuildSarifJson(IReadOnlyList<SnipperFinding> findings)
    {
        var toolVersion = ToolVersion;
        var lineCache = new SourceLineCache();

        var rules = findings
            .GroupBy(static f => f.RuleId, StringComparer.Ordinal)
            .Select(static g => new SarifReportingDescriptor(
                Id: g.Key,
                Name: g.First().Title,
                ShortDescription: new SarifMultiformatMessageString(g.First().Title)))
            .ToArray();

        var results = findings
            .Select(f => new SarifResult(
                RuleId: f.RuleId,
                Level: f.Certainty switch
                {
                    CertaintyTier.Guaranteed => "error",
                    CertaintyTier.High => "warning",
                    CertaintyTier.Moderate => "warning",
                    _ => "note",
                },
                Message: new SarifMessage(f.Message),
                Locations:
                [
                    new SarifLocation(new SarifPhysicalLocation(
                        ArtifactLocation: new SarifArtifactLocation(new Uri(Path.GetFullPath(f.FilePath)).AbsoluteUri),
                        Region: new SarifRegion(
                            f.LineNumber,
                            f.CharacterOffset,
                            lineCache.GetLine(f.FilePath, f.LineNumber) is { } lineText ? new SarifArtifactContent(lineText) : null)))
                ],
                Properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["certainty"] = f.Certainty.ToString(),
                    ["category"] = f.Category.ToString(),
                }))
            .ToArray();

        var log = new SarifLog(
            Schema: "https://json.schemastore.org/sarif-2.1.0.json",
            Version: "2.1.0",
            Runs:
            [
                new SarifRun(
                    Tool: new SarifTool(new SarifToolDriver(
                        Name: "Snipper",
                        Version: toolVersion,
                        InformationUri: "https://github.com/Snipper",
                        Rules: rules)),
                    Results: results)
            ]);

        return JsonSerializer.Serialize(log, JsonReportSerializerContext.Default.SarifLog);
    }

    private enum ReportFormat : byte
    {
        Json,
        Sarif,
    }

    internal sealed record SnipperReport(
        string ToolVersion,
        string? CommitSha,
        DateTimeOffset GeneratedAtUtc,
        FindingReportEntry[] Findings,
        SuppressionAudit? Suppression = null,
        EntropyRateReport? EntropyRate = null);

    internal sealed record FindingReportEntry(
        string RuleId,
        string Title,
        string Message,
        string Certainty,
        string Category,
        string FilePath,
        int LineNumber,
        int CharacterOffset,
        string? LineText);

    /// <summary>
    /// Renders the 4A suppression audit. The JSON section is for machines; this table is
    /// where a human decides whether to delete a suppression, so a clean bill of health
    /// gets one line rather than an empty table.
    /// </summary>
    private static void RenderSuppressionAudit(SuppressionAudit audit)
    {
        var entries = new List<SuppressionEntry>();
        entries.AddRange(audit.DisabledRules);
        entries.AddRange(audit.NamespaceExclusions);
        entries.AddRange(audit.PathGlobs);
        entries.AddRange(audit.SeverityOverrides);
        if (audit.CertaintyFilter is { } certainty)
        {
            entries.Add(certainty);
        }

        var suppressing = entries.Where(e => e.SuppressedCount > 0 || e.DowngradedCount > 0).ToArray();

        if (suppressing.Length == 0)
        {
            AnsiConsole.MarkupLine("[green]Suppression audit: no active suppression is hiding anything.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Channel[/]");
            table.AddColumn("[bold]Selector[/]");
            table.AddColumn("[bold]Hidden[/]");
            table.AddColumn("[bold]Rules[/]");

            foreach (var entry in suppressing)
            {
                var count = entry.DowngradedCount > 0
                    ? $"{entry.DowngradedCount} downgraded"
                    : $"{entry.SuppressedCount} hidden";

                table.AddRow(
                    entry.Channel.ToString(),
                    Markup.Escape(entry.Selector),
                    count,
                    entry.RuleIds.Length == 0 ? "-" : Markup.Escape(string.Join(", ", entry.RuleIds)));
            }

            AnsiConsole.Write(table);
        }

        var totals = audit.Totals;
        var share = totals.HiddenDebtPercent == 0
            ? "nothing hidden"
            : $"[bold]{totals.HiddenDebtPercent}%[/] of analysed debt hidden";

        AnsiConsole.MarkupLine(
            $"Suppression audit: {share} — {totals.FindingsDropped} dropped and " +
            $"{totals.FindingsDowngraded} downgraded from {totals.FindingsAnalysed} analysed" +
            $"{(totals.FindingsHiddenByShadow > 0 ? $", plus {totals.FindingsHiddenByShadow} hidden before analysis" : string.Empty)}.");

        if (!audit.ShadowAnalysisRan && (audit.DisabledRules.Length > 0 || audit.NamespaceExclusions.Length > 0))
        {
            AnsiConsole.MarkupLine(
                "[yellow]Note: disabled rules and namespace exclusions suppress findings before analysis, " +
                "so their totals below are incomplete — a shadow pass did not run for them.[/]");
        }

        if (audit.Obsolete.Length > 0)
        {
            var obsolete = new Table().Border(TableBorder.Rounded);
            obsolete.AddColumn("[bold]Stale[/]");
            obsolete.AddColumn("[bold]Channel[/]");
            obsolete.AddColumn("[bold]Selector[/]");
            obsolete.AddColumn("[bold]Why[/]");

            foreach (var entry in audit.Obsolete)
            {
                var confidence = entry.Confidence == SuppressionConfidence.Certain ? "certain" : "suspected";
                obsolete.AddRow(
                    confidence,
                    entry.Channel.ToString(),
                    Markup.Escape(entry.Selector),
                    Markup.Escape(entry.Reason));
            }

            AnsiConsole.Write(obsolete);
        }
    }

    private static void RenderReport(IReadOnlyList<SnipperFinding> findings)
    {
        if (findings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No dead code or unused artifacts discovered.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Certainty[/]");
        table.AddColumn("[bold]Rule[/]");
        table.AddColumn("[bold]Location[/]");
        table.AddColumn("[bold]Description[/]");

        var sortedFindings = findings.OrderBy(static f => f.Certainty).ThenBy(static f => f.FilePath);

        foreach (var f in sortedFindings)
        {
            var certaintyMarkup = f.Certainty switch
            {
                CertaintyTier.Guaranteed => "[red]Guaranteed (100%)[/]",
                CertaintyTier.High => "[orange1]High (~90%)[/]",
                CertaintyTier.Moderate => "[yellow]Moderate (~70%)[/]",
                CertaintyTier.Advisory => "[blue]Advisory (~50%)[/]",
                _ => "[grey]Unknown[/]"
            };

            var relativeLocation = $"{Path.GetFileName(f.FilePath)}:{f.LineNumber}:{f.CharacterOffset}";
            table.AddRow(certaintyMarkup, f.RuleId, relativeLocation, Markup.Escape(f.Message));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold]Total Candidates Identified:[/] [green]{findings.Count}[/]");
    }

    /// <summary>
    /// Renders the 4B entropy rate. The console is where a maintainer decides whether a
    /// change is acceptable, so an unmeasurable rate is stated as a reason rather than
    /// than a confident-looking 0.00 - which would pass a budget while measuring nothing.
    /// </summary>
    private static void RenderEntropyRate(EntropyRateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rate = EntropyRateCalculator.Format(report.FindingsPerKloc);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Entropy[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("New findings", report.NewFindings.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Fingerprints resolved", report.ResolvedFindings.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Lines changed", report.LinesChanged?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        table.AddRow("Findings per kLOC", rate);

        if (report.Budget is { } budget)
        {
            table.AddRow("Budget", EntropyRateCalculator.Format(budget));
            table.AddRow(
                "Gate",
                report.BudgetExceeded
                    ? "[red]exceeded[/]"
                    : report.Scored ? "[green]within budget[/]" : "[yellow]not scored[/]");
        }

        AnsiConsole.Write(table);

        if (!report.Scored && report.Reason is { } reason)
        {
            AnsiConsole.MarkupLine($"[yellow]Entropy rate not scored:[/] {reason}");
        }

        if (report.Monthly.Length > 0)
        {
            AnsiConsole.MarkupLine("[grey]Monthly:[/]");

            var monthly = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Month[/]")
                .AddColumn("[bold]New[/]")
                .AddColumn("[bold]Resolved[/]")
                .AddColumn("[bold]kLOC changed[/]")
                .AddColumn("[bold]Per kLOC[/]");

            foreach (var month in report.Monthly)
            {
                monthly.AddRow(
                    month.Month,
                    month.NewFindings.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    month.ResolvedFindings.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    EntropyRateCalculator.Format(month.LinesChanged / 1000.0),
                    EntropyRateCalculator.Format(month.FindingsPerKloc));
            }

            AnsiConsole.Write(monthly);
        }
    }
}
