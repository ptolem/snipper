namespace Snipper.Cli;

using System.Text.Json;
using Spectre.Console;
using Snipper.Models;

/// <summary>
/// Serialises findings to JSON or SARIF and writes them to disk.
/// <para>
/// Extracted from <see cref="CliRunner"/> so the report shape is a unit with its own
/// contract: it takes a finding list and returns a string, with no workspace, no
/// analysers, and no orchestration state. That is what lets tests assert on report
/// content without running an analysis, and it is why <see cref="BuildJson"/> and
/// <see cref="BuildSarifJson"/> are separate from <see cref="Write"/>.
/// </para>
/// <para>
/// Both writers sort by certainty then path, so two runs over the same findings produce
/// byte-identical output. That determinism is a stated contract, not an accident.
/// </para>
/// </summary>
internal static class ReportWriter
{
    /// <summary>
    /// Writes the report, creating the parent directory if needed. Returns 0 on success
    /// and 2 on an I/O failure — 2 rather than throwing, because a pipeline needs to
    /// distinguish "could not write the report" from "the report says there are findings".
    /// </summary>
    public static int Write(
        IReadOnlyList<SnipperFinding> findings,
        string outputPath,
        ReportFormat format,
        string? targetDirectory,
        SuppressionAudit? suppressionAudit = null,
        EntropyRateReport? entropyRate = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

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

    /// <summary>
    /// Builds the JSON report. <paramref name="suppressionAudit"/> and
    /// <paramref name="entropyRate"/> are omitted entirely when null, so a consumer can
    /// distinguish "not requested" from "requested and empty".
    /// </summary>
    public static string BuildJson(
        IReadOnlyList<SnipperFinding> findings,
        string? commitSha,
        SuppressionAudit? suppressionAudit = null,
        EntropyRateReport? entropyRate = null)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var lineCache = new SourceLineCache();
        // Total order, so the report is byte-identical across runs whatever order the
        // analysers emitted in, and so the JSON and SARIF writers agree with each other.
        var ordered = SortDeterministically(findings);

        var report = new SnipperReport(
            ToolVersion: ToolVersion.Current,
            CommitSha: commitSha,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Findings: ordered
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

    /// <summary>
    /// One total order over findings, shared by every output format.
    /// <para>
    /// Certainty and path alone are not enough. Those two keys were the whole sort once,
    /// and being a stable sort they left every finding sharing a (certainty, path) pair -
    /// all unused usings in one file, say - inheriting whatever order the analyser produced.
    /// SNP0019 inherited a *parallel producer-completion* order from
    /// <c>compilation.GetDiagnostics()</c>, so two runs over the same solution permuted those
    /// findings. Breaking every remaining tie here makes this the last line of defence, and
    /// means a future analyser cannot reintroduce that class of bug.
    /// </para>
    /// </summary>
    private static List<SnipperFinding> SortDeterministically(IEnumerable<SnipperFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return
        [
            .. findings
                .OrderBy(static f => f.Certainty)
                .ThenBy(static f => f.FilePath, StringComparer.Ordinal)
                .ThenBy(static f => f.LineNumber)
                .ThenBy(static f => f.CharacterOffset)
                .ThenBy(static f => f.RuleId, StringComparer.Ordinal)
                .ThenBy(static f => f.Message, StringComparer.Ordinal)
                .ThenBy(static f => f.Category)
                .ThenBy(static f => f.Title, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Builds a SARIF 2.1.0 log. Certainty maps to level (Guaranteed → error, High and
    /// Moderate → warning, Advisory → note) and is *also* preserved in
    /// <c>properties.certainty</c>, because a consumer that only sees SARIF levels cannot
    /// otherwise distinguish High from Moderate.
    /// <para>
    /// No <c>baselineState</c>: baseline filtering happens before serialisation, so this log
    /// contains only surviving findings. Neither the suppression audit nor the entropy
    /// section appears here — both are JSON-only.
    /// </para>
    /// </summary>
    public static string BuildSarifJson(IReadOnlyList<SnipperFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var toolVersion = ToolVersion.Current;
        var lineCache = new SourceLineCache();

        var ordered = SortDeterministically(findings);

        // Same total order as the JSON report, so both formats are stable and agree with
        // each other. GroupBy preserves first-appearance order, so the rule table needs
        // its own ordinal sort on RuleId.
        var rules = findings
            .GroupBy(static f => f.RuleId, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => new SarifReportingDescriptor(
                Id: g.Key,
                Name: g.First().Title,
                ShortDescription: new SarifMultiformatMessageString(g.First().Title)))
            .ToArray();

        var results = ordered
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