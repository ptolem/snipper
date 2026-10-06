namespace Snipper.Cli;

using System.Globalization;
using Spectre.Console;
using Snipper.Models;

/// <summary>
/// Console rendering for the three human-facing sections: the findings table, the
/// suppression audit, and the entropy rate.
/// <para>
/// Extracted from <see cref="CliRunner"/> to separate *what was found* from *how it is
/// shown*. Every method here is a pure projection of a value onto the terminal, so the
/// decision content (which findings exist, what was suppressed, what the rate is) is
/// testable without asserting on escape sequences.
/// </para>
/// <para>
/// All writes go straight to <see cref="AnsiConsole"/>, which is why callers must not
/// invoke these from an analyser — <c>StatusContext</c> is process-wide exclusive, and
/// concurrent writes interleave. Analysis progress goes through the lock in
/// <see cref="AnalysisRunner"/> instead.
/// </para>
/// </summary>
internal static class ConsoleRenderer
{
    public static void RenderReport(IReadOnlyList<SnipperFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

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

        // StringComparer.Ordinal is load-bearing, not a micro-optimisation: without it this
        // falls back to culture-sensitive ordering, so the console table can list the same
        // findings in a different order than the JSON and SARIF writers, and that order can
        // change with the machine's locale. Every other sort in the tool is explicit ordinal
        // for the same reason - see ReportWriter.SortDeterministically.
        var sortedFindings = findings.OrderBy(static f => f.Certainty)
            .ThenBy(static f => f.FilePath, StringComparer.Ordinal);

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
    /// Renders the 4A suppression audit. The JSON section is for machines; this table is
    /// where a human decides whether to delete a suppression, so a clean bill of health
    /// gets one line rather than an empty table.
    /// </summary>
    public static void RenderSuppressionAudit(SuppressionAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

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

    /// <summary>
    /// Renders the 4B entropy rate. The console is where a maintainer decides whether a
    /// change is acceptable, so an unmeasurable rate is stated as a reason rather than
    /// than a confident-looking 0.00 - which would pass a budget while measuring nothing.
    /// </summary>
    public static void RenderEntropyRate(EntropyRateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rate = EntropyRateCalculator.Format(report.FindingsPerKloc);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Entropy[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("New findings", report.NewFindings.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Fingerprints resolved", report.ResolvedFindings.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Lines changed", report.LinesChanged?.ToString(CultureInfo.InvariantCulture) ?? "-");
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
                    month.NewFindings.ToString(CultureInfo.InvariantCulture),
                    month.ResolvedFindings.ToString(CultureInfo.InvariantCulture),
                    EntropyRateCalculator.Format(month.LinesChanged / 1000.0),
                    EntropyRateCalculator.Format(month.FindingsPerKloc));
            }

            AnsiConsole.Write(monthly);
        }
    }
}