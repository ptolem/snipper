namespace Snipper.Cli;

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
            AnsiConsole.MarkupLine("[yellow]Usage: Snipper <path-to-solution-or-project> [[output-file]] [[--format json|sarif]] [[--baseline <path>]] [[--certainty-tier guaranteed|high|moderate|advisory]] [[--exclude-namespaces <list>]] [[--config-analysis]] [[--version]][/]");
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
        var analysers = new List<IWorkspaceAnalyser>
        {
            new UnreachableCodeAnalyser(exclusions),
            new UnusedLocalVariableAnalyser(exclusions),
            new UnusedPrivateMemberAnalyser(exclusions),
            new UnusedParameterAnalyser(exclusions),
            new UnusedNonPrivateMemberAnalyser(exclusions),
            new UnreferencedPackageAnalyser(),
            new ObsoleteMemberAnalyser(exclusions),
            new OrphanProjectAnalyser(),
            new RedundantTransitivePackageAnalyser(),
            new FrameworkInboxPackageAnalyser(),
            new UnusedUsingDirectiveAnalyser(exclusions),
            new CommentedCodeAnalyser(exclusions),
        };

        if (includeConfigAnalysis)
        {
            analysers.Add(new ConfigurationBindingAnalyser(exclusions));
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Configuration analysis (SNP0007/SNP0008) is off by default — enable with --config-analysis.[/]");
        }

        if (config.DisabledRules.Count > 0)
        {
            analysers.RemoveAll(a => !FindingFilter.IsAnalyserEnabled(a.RuleIds, config));
            AnsiConsole.MarkupLine($"[grey]Disabled rules (snipper.json): {Markup.Escape(string.Join(", ", config.DisabledRules.Order(StringComparer.Ordinal)))}[/]");
        }

        var allFindings = new List<SnipperFinding>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing solution symbols and references...", async ctx =>
            {
                var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

                foreach (var analyser in analysers)
                {
                    var analyserName = analyser.GetType().Name;
                    ctx.Status($"[grey]{analyserName}: starting...[/]");

                    var analyserStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var findings = await analyser.AnalyzeAsync(
                        solution,
                        CancellationToken.None,
                        progress: status => ctx.Status($"[grey]{Markup.Escape(status)}[/]")).ConfigureAwait(false);
                    analyserStopwatch.Stop();

                    allFindings.AddRange(findings);
                    AnsiConsole.MarkupLine($"[green]✓[/] [grey]{analyserName}: {findings.Count} finding(s) in {analyserStopwatch.Elapsed.TotalSeconds:0.0}s[/]");
                }

                AnsiConsole.MarkupLine($"[grey]Analysis completed in {totalStopwatch.Elapsed.TotalSeconds:0.0}s.[/]");
            });

        IReadOnlyList<SnipperFinding> reportableFindings = allFindings;

        if (baselinePath is not null)
        {
            var baseDirectory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
            var knownFingerprints = BaselineService.Load(baselinePath);

            var newFindings = new List<SnipperFinding>();
            var suppressedCount = 0;
            var allFingerprints = new List<string>(allFindings.Count);

            foreach (var finding in allFindings)
            {
                var fingerprint = BaselineService.ComputeFingerprint(finding, baseDirectory);
                allFingerprints.Add(fingerprint);

                if (knownFingerprints.Contains(fingerprint))
                {
                    suppressedCount++;
                }
                else
                {
                    newFindings.Add(finding);
                }
            }

            try
            {
                BaselineService.Write(baselinePath, allFingerprints);
                AnsiConsole.MarkupLine($"[grey]Baseline: {suppressedCount} known finding(s) suppressed, {newFindings.Count} new. Baseline updated at {baselinePath}.[/]");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not update baseline file: {ex.Message}[/]");
            }

            reportableFindings = newFindings;
        }

        // snipper.json filtering (disabled rules, severity overrides, path globs) applies
        // at report/output time only — after baseline fingerprinting, so toggling config
        // never churns the baseline file. Same contract as the certainty-tier filter below.
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

        if (outputPath is not null)
        {
            return WriteReport(reportableFindings, outputPath, format);
        }

        return 0;
    }

    private static int WriteReport(IReadOnlyList<SnipperFinding> findings, string outputPath, ReportFormat format)
    {
        var json = format switch
        {
            ReportFormat.Sarif => BuildSarifJson(findings),
            _ => BuildJson(findings),
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

    private static string BuildJson(IReadOnlyList<SnipperFinding> findings)
    {
        var report = findings
            .OrderBy(static f => f.Certainty)
            .ThenBy(static f => f.FilePath)
            .Select(static f => new FindingReportEntry(
                RuleId: f.RuleId,
                Title: f.Title,
                Message: f.Message,
                Certainty: f.Certainty.ToString(),
                Category: f.Category.ToString(),
                FilePath: f.FilePath,
                LineNumber: f.LineNumber,
                CharacterOffset: f.CharacterOffset))
            .ToArray();

        return JsonSerializer.Serialize(report, JsonReportSerializerContext.Default.FindingReportEntryArray);
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

    private static string BuildSarifJson(IReadOnlyList<SnipperFinding> findings)
    {
        var toolVersion = ToolVersion;

        var rules = findings
            .GroupBy(static f => f.RuleId, StringComparer.Ordinal)
            .Select(static g => new SarifReportingDescriptor(
                Id: g.Key,
                Name: g.First().Title,
                ShortDescription: new SarifMultiformatMessageString(g.First().Title)))
            .ToArray();

        var results = findings
            .Select(static f => new SarifResult(
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
                        Region: new SarifRegion(f.LineNumber, f.CharacterOffset)))
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

    internal sealed record FindingReportEntry(
        string RuleId,
        string Title,
        string Message,
        string Certainty,
        string Category,
        string FilePath,
        int LineNumber,
        int CharacterOffset);

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
}
