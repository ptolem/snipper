namespace Snipper.Cli;

using System.Collections.Frozen;
using System.Globalization;
using Snipper.Analysis;
using Snipper.Models;

/// <summary>
/// A fully validated command line. Produced only by <see cref="CommandLineParser"/>,
/// so any instance is internally consistent — every cross-field rule (entropy implies a
/// baseline, and so on) has already been checked.
/// </summary>
internal sealed record CommandLineOptions
{
    public required string TargetPath { get; init; }

    public string? OutputPath { get; init; }

    public string? BaselinePath { get; init; }

    public CertaintyTier? MinimumCertainty { get; init; }

    public ReportFormat Format { get; init; } = ReportFormat.Json;

    public bool IncludeConfigAnalysis { get; init; }

    public bool IncludeDuplicateDetection { get; init; }

    public bool IncludeCloneDrift { get; init; }

    public bool AuditSuppressions { get; init; }

    public bool EntropyRateRequested { get; init; }

    public double? EntropyBudget { get; init; }

    public string? EntropyLedgerPath { get; init; }

    public int EntropyMinimumLines { get; init; }

    public FrozenSet<string> ExcludedNamespaces { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Entries the user supplied to <c>--exclude-namespaces</c> that are not syntactically
    /// valid namespaces. Reported rather than fatal: a typo in an exclusion should not
    /// fail a build, but it must not pass unmentioned either.
    /// </summary>
    public IReadOnlyList<string> MalformedNamespaces { get; init; } = [];
}

/// <summary>
/// Turns <c>string[]</c> into a <see cref="CommandLineOptions"/>, or explains why it cannot.
/// <para>
/// Extracted from <see cref="CliRunner"/> because argument parsing is pure: it needs no
/// workspace, no git, and no console. That makes the whole option surface unit-testable in
/// milliseconds, and it is why the parser can be strict about reporting errors while
/// everything downstream stays silent.
/// </para>
/// <para>
/// Nothing here prints. Diagnostics come back as data so the caller decides how to render
/// them, which keeps Spectre.Console out of the parser and keeps the parser testable
/// without capturing console output.
/// </para>
/// </summary>
internal static class CommandLineParser
{
    public const string Usage =
        "Usage: Snipper <path-to-solution-or-project> [[output-file]] [[--format json|sarif]] " +
        "[[--baseline <path>]] [[--certainty-tier guaranteed|high|moderate|advisory]] " +
        "[[--exclude-namespaces <list>]] [[--config-analysis]] [[--duplicate-detection]] " +
        "[[--clone-drift]] [[--audit-suppressions]] [[--entropy-rate]] " +
        "[[--entropy-budget <per-kloc>]] [[--entropy-ledger <path>]] " +
        "[[--entropy-min-lines <n>]] [[--version]]";

    public static bool TryParse(string[] args, out CommandLineOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null!;
        error = null;

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            error = "Error: Missing target path. Provide a .sln, .slnx, or .csproj path.";
            return false;
        }

        var targetPath = Path.GetFullPath(args[0]);
        if (!File.Exists(targetPath))
        {
            error = $"Error: Target file does not exist: {targetPath}";
            return false;
        }

        string? outputPath = null;
        string? baselinePath = null;
        CertaintyTier? minimumCertainty = null;
        var format = ReportFormat.Json;
        var includeConfigAnalysis = false;
        var includeDuplicateDetection = false;
        var includeCloneDrift = false;
        var auditSuppressions = false;
        var entropyRateRequested = false;
        double? entropyBudget = null;
        string? entropyLedgerPath = null;
        var entropyMinimumLines = EntropyRateCalculator.DefaultMinimumLines;
        var excludedNamespaces = new List<string>();
        var malformedNamespaces = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !Enum.TryParse(args[i + 1], ignoreCase: true, out format))
                {
                    error = "Error: --format requires a value of 'json' or 'sarif'.";
                    return false;
                }

                i++;
            }
            else if (string.Equals(args[i], "--certainty-tier", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !Enum.TryParse<CertaintyTier>(args[i + 1], ignoreCase: true, out var parsedTier))
                {
                    error = "Error: --certainty-tier requires a value of 'guaranteed', 'high', 'moderate', or 'advisory'.";
                    return false;
                }

                minimumCertainty = parsedTier;
                i++;
            }
            else if (string.Equals(args[i], "--baseline", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "Error: --baseline requires a file path.";
                    return false;
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
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var budget)
                    || budget < 0)
                {
                    error = "Error: --entropy-budget requires a non-negative number of findings per kLOC.";
                    return false;
                }

                entropyBudget = budget;
                entropyRateRequested = true;
                i++;
            }
            else if (string.Equals(args[i], "--entropy-ledger", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "Error: --entropy-ledger requires a file path.";
                    return false;
                }

                entropyLedgerPath = Path.GetFullPath(args[i + 1]);
                entropyRateRequested = true;
                i++;
            }
            else if (string.Equals(args[i], "--entropy-min-lines", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var minimumLines))
                {
                    error = "Error: --entropy-min-lines requires an integer line count.";
                    return false;
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
                    error = "Error: --exclude-namespaces requires a value (comma-separated and/or repeatable).";
                    return false;
                }

                foreach (var entry in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IsValidNamespace(entry))
                    {
                        excludedNamespaces.Add(entry);
                    }
                    else
                    {
                        malformedNamespaces.Add(entry);
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
            else if (string.Equals(args[i], "--clone-drift", StringComparison.OrdinalIgnoreCase))
            {
                // Implies --duplicate-detection rather than erroring: the two share one shingling
                // pass, and asking for two flags to get one feature would be user-hostile.
                includeCloneDrift = true;
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
                error = $"Error: Unexpected argument '{args[i]}'.";
                return false;
            }
        }

        // "New" is defined relative to a recorded baseline, so an entropy rate without one has
        // no numerator. This is a usage error rather than a silent zero: a quiet 0.00 would be
        // indistinguishable from a genuinely clean change, and would pass any budget.
        if (entropyRateRequested && baselinePath is null)
        {
            error = "Error: --entropy-rate requires --baseline, because 'new' findings are defined relative to a recorded baseline.";
            return false;
        }

        options = new CommandLineOptions
        {
            TargetPath = targetPath,
            OutputPath = outputPath,
            BaselinePath = baselinePath,
            MinimumCertainty = minimumCertainty,
            Format = format,
            IncludeConfigAnalysis = includeConfigAnalysis,
            IncludeDuplicateDetection = includeDuplicateDetection,
            IncludeCloneDrift = includeCloneDrift,
            AuditSuppressions = auditSuppressions,
            EntropyRateRequested = entropyRateRequested,
            EntropyBudget = entropyBudget,
            EntropyLedgerPath = entropyLedgerPath,
            EntropyMinimumLines = entropyMinimumLines,
            ExcludedNamespaces = excludedNamespaces.ToFrozenSet(StringComparer.Ordinal),
            MalformedNamespaces = malformedNamespaces,
        };
        return true;
    }

    /// <summary>
    /// Whether a string is a syntactically valid namespace: dot-separated segments, each
    /// starting with a letter or underscore and continuing with letters, digits or
    /// underscores. Deliberately not applied to config-file entries, which are validated by
    /// a different (absent) path — see the usage guide's namespace section.
    /// <para>
    /// The <c>&lt;global&gt;</c> sentinel is accepted here too, even though it is not a
    /// valid namespace by this grammar (angle brackets), because it is the documented way
    /// to exclude file-scope code and was previously reachable only from the config file.
    /// That asymmetry was not a deliberate safety property — the config file skips this
    /// grammar entirely — so it only made the feature harder to discover and use.
    /// </para>
    /// </summary>
    private static bool IsValidNamespace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(value, AnalysisExclusions.GlobalNamespaceMarker, StringComparison.Ordinal))
        {
            return true;
        }

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
}