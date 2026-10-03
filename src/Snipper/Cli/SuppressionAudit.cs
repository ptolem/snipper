namespace Snipper.Cli;

using System.Collections.Frozen;
using Snipper.Models;

/// <summary>
/// Which configuration mechanism hid a finding. Reported so a consumer can tell a
/// deliberate, reviewable suppression from a broad one nobody has looked at.
///
/// Declaration order is the report's display order: the most structural suppressions
/// first, the report-time ones last, so the console table and the stale-suppression
/// table read in the same sequence. <see cref="SuppressionAuditBuilder.SortObsolete"/>
/// relies on it and must stay in sync.
/// </summary>
internal enum SuppressionChannel : byte
{
    /// <summary><c>rules.SNxxxx: "off"</c>.</summary>
    DisabledRule = 0,

    /// <summary><c>exclude.namespaces</c>.</summary>
    NamespaceExclusion = 1,

    /// <summary><c>exclude.paths</c> glob.</summary>
    PathGlob = 2,

    /// <summary><c>rules.SNxxxx</c> set to a tier below the finding's own.</summary>
    SeverityOverride = 3,

    /// <summary><c>--certainty-tier</c>. Suppresses by tier, not by config.</summary>
    CertaintyTier = 4,
}

/// <summary>
/// How much evidence stands behind an <see cref="ObsoleteSuppression"/>. A glob that
/// matches no file on disk is certain; a rule that simply produced nothing is only
/// suspected, because a rule can be genuinely clean. Overstating this is how a
/// suppression audit trains people to ignore it.
/// </summary>
internal enum SuppressionConfidence : byte
{
    /// <summary>The suppression's target provably does not exist.</summary>
    Certain = 0,

    /// <summary>The suppression matched nothing this run, but that can be legitimate.</summary>
    Suspected = 1,
}

/// <summary>
/// One configured suppression and what it hid.
/// </summary>
/// <param name="Channel">The mechanism that did the suppressing.</param>
/// <param name="Selector">
/// The configured selector verbatim — a glob, a rule id, a namespace, or a tier name —
/// so the audit output can be pasted back into <c>snipper.json</c>.
/// </param>
/// <param name="SuppressedCount">Findings this selector removed from the report.</param>
/// <param name="DowngradedCount">
/// Findings this selector kept but moved to a lower tier. Zero for every channel except
/// <see cref="SuppressionChannel.SeverityOverride"/>.
/// </param>
/// <param name="RuleIds">Rules affected, sorted, so output is deterministic.</param>
/// <param name="Detail">Human-readable qualifier, e.g. <c>High -&gt; Advisory</c>.</param>
internal sealed record SuppressionEntry(
    SuppressionChannel Channel,
    string Selector,
    int SuppressedCount,
    int DowngradedCount,
    string[] RuleIds,
    string Detail);

/// <summary>
/// A suppression that no longer has anything to suppress.
/// </summary>
internal sealed record ObsoleteSuppression(
    SuppressionChannel Channel,
    string Selector,
    SuppressionConfidence Confidence,
    string Reason);

/// <summary>
/// Headline numbers for the suppression audit.
/// </summary>
/// <param name="FindingsAnalysed">
/// Findings the enabled analysers produced, before any report-time filter.
/// </param>
/// <param name="FindingsHiddenByShadow">
/// Findings that exist only because a shadow pass ran with suppressions lifted — the
/// pre-analysis channels. Zero when no shadow pass ran, which is why
/// <see cref="ShadowAnalysisRan"/> must be read alongside it.
/// </param>
/// <param name="FindingsDropped">Findings removed from the report entirely.</param>
/// <param name="FindingsDowngraded">Findings kept but reported at a lower tier.</param>
/// <param name="FindingsAfterSuppression"><c>FindingsAnalysed - FindingsDropped</c>.</param>
/// <param name="HiddenDebtPercent">
/// Share of total debt that suppression is hiding:
/// <c>(Dropped + HiddenByShadow) / (Analysed + HiddenByShadow)</c>. This is the number
/// the audit exists to produce.
/// </param>
internal sealed record SuppressionTotals(
    int FindingsAnalysed,
    int FindingsHiddenByShadow,
    int FindingsDropped,
    int FindingsDowngraded,
    int FindingsAfterSuppression,
    int HiddenDebtPercent);

/// <summary>
/// Wave 4 / 4A output: what Snipper's suppressions are actually hiding.
/// </summary>
/// <param name="ShadowAnalysisRan">
/// False when the audit ran without a shadow pass, meaning the pre-analysis channels
/// (disabled rules, namespace exclusions) could only be counted to the extent their
/// findings survived into the normal set. Consumers must not read a zero here as
/// "nothing suppressed".
/// </param>
internal sealed record SuppressionAudit(
    SuppressionTotals Totals,
    SuppressionEntry[] PathGlobs,
    SuppressionEntry[] SeverityOverrides,
    SuppressionEntry[] DisabledRules,
    SuppressionEntry[] NamespaceExclusions,
    SuppressionEntry? CertaintyFilter,
    ObsoleteSuppression[] Obsolete,
    bool ShadowAnalysisRan);

/// <summary>
/// Builds a <see cref="SuppressionAudit"/> from findings already in hand plus, for the
/// two pre-analysis channels, the results of an opt-in shadow pass.
///
/// Pure and Roslyn-free so the attribution rules are unit-testable without a workspace.
/// Attribution is exact rather than approximate: a finding suppressed by both a disabled
/// rule and a namespace is credited to the disabled rule only, because the namespace
/// shadow set is computed as a difference against the disabled-rule shadow set.
/// </summary>
internal static class SuppressionAuditBuilder
{
    /// <summary>
    /// Builds the audit.
    /// </summary>
    /// <param name="analysedFindings">
    /// Findings from the normal run: namespace exclusions applied, disabled analysers
    /// removed. This is the pre-report-filter set.
    /// </param>
    /// <param name="config">Resolved configuration.</param>
    /// <param name="minimumCertainty">The <c>--certainty-tier</c> floor, if given.</param>
    /// <param name="disabledRuleShadowFindings">
    /// Findings from the disabled analysers alone, namespace exclusions still applied.
    /// Empty when no shadow pass ran.
    /// </param>
    /// <param name="namespaceShadowFindings">
    /// Findings revealed by lifting namespace exclusions, excluding anything already
    /// attributed to a disabled rule. Empty when no shadow pass ran.
    /// </param>
    /// <param name="shadowAnalysisRan">Whether the shadow inputs above are real.</param>
    /// <param name="globMatchesNoFile">
    /// For a path glob, true when it matches no file on disk — the only *certain*
    /// signal that a glob is stale. Null when file inventory was unavailable.
    /// </param>
    /// <param name="namespaceExists">
    /// Whether a configured namespace is declared anywhere in the analysed solution.
    /// Null when namespace inventory was unavailable.
    /// </param>
    public static SuppressionAudit Build(
        IReadOnlyList<SnipperFinding> analysedFindings,
        SnipperConfig config,
        CertaintyTier? minimumCertainty,
        IReadOnlyList<SnipperFinding>? disabledRuleShadowFindings,
        IReadOnlyList<SnipperFinding>? namespaceShadowFindings,
        bool shadowAnalysisRan,
        Func<string, bool>? globMatchesNoFile,
        Func<string, bool>? namespaceExists)
    {
        ArgumentNullException.ThrowIfNull(analysedFindings);
        ArgumentNullException.ThrowIfNull(config);

        var droppedByPathGlob = new Dictionary<string, (int Count, SortedSet<string> Rules)>(StringComparer.Ordinal);
        var droppedByRule = new Dictionary<string, (int Count, SortedSet<string> Rules)>(StringComparer.Ordinal);
        var downgradedByRule = new Dictionary<string, (int Count, SortedSet<string> Rules)>(StringComparer.Ordinal);
        var hiddenByOverride = new Dictionary<string, (int Count, SortedSet<string> Rules)>(StringComparer.Ordinal);
        var tierCounts = new Dictionary<string, (int Count, SortedSet<string> Rules)>(StringComparer.Ordinal);

        var dropped = 0;
        var downgraded = 0;

        foreach (var finding in analysedFindings)
        {
            var classification = FindingFilter.Classify(finding, config);

            switch (classification.Outcome)
            {
                case FindingFilterOutcome.DroppedDisabledRule:
                    Tally(droppedByRule, finding.RuleId, finding.RuleId);
                    dropped++;
                    continue;

                case FindingFilterOutcome.DroppedPathGlob:
                    Tally(droppedByPathGlob, classification.MatchedGlob, finding.RuleId);
                    dropped++;
                    continue;

                case FindingFilterOutcome.SeverityOverridden:
                    Tally(downgradedByRule, finding.RuleId, finding.RuleId);
                    break;
            }

            // The tier floor runs after the config filter, on the post-override tier —
            // mirrors CliRunner, which applies FindingFilter.Apply first and rewrites
            // Certainty, then filters.
            //
            // Attributing the drop matters here. CertaintyTier is ordered
            // Guaranteed=1..Advisory=4 and the floor keeps `Certainty <= floor`, so a
            // *downgrade* moves a finding away from the floor, not toward it: a
            // High->Advisory override can be what pushes a finding past a Moderate
            // floor. When that happens the override caused the suppression, so the
            // override is credited with the drop and the floor is not. Blaming the
            // floor would send the user to the wrong line of their configuration.
            if (minimumCertainty is { } floor && classification.EffectiveTier > floor)
            {
                if (classification.Outcome == FindingFilterOutcome.SeverityOverridden
                    && finding.Certainty <= floor)
                {
                    Tally(hiddenByOverride, finding.RuleId, finding.RuleId);
                }
                else
                {
                    Tally(tierCounts, floor.ToString(), finding.RuleId);
                }

                dropped++;
                continue;
            }

            if (classification.Outcome == FindingFilterOutcome.SeverityOverridden)
            {
                downgraded++;
            }
        }

        // Pre-analysis channels. Counts come from the shadow sets, not from Classify:
        // these findings were never in analysedFindings to begin with.
        var shadowHidden = 0;

        if (shadowAnalysisRan && disabledRuleShadowFindings is { Count: > 0 })
        {
            foreach (var finding in disabledRuleShadowFindings)
            {
                Tally(droppedByRule, finding.RuleId, finding.RuleId);
                shadowHidden++;
            }
        }

        if (shadowAnalysisRan && namespaceShadowFindings is { Count: > 0 })
        {
            // Reported as a single aggregate entry: attributing per namespace would need
            // one shadow pass per namespace, and a finding's containing namespace is not
            // recoverable from the finding itself.
            shadowHidden += namespaceShadowFindings.Count;
        }

        var pathGlobEntries = BuildEntries(SuppressionChannel.PathGlob, config.ExcludedPathGlobs, droppedByPathGlob, Detail: null);
        var severityEntries = BuildSeverityEntries(config, downgradedByRule, hiddenByOverride, analysedFindings);
        var disabledRuleEntries = BuildEntries(SuppressionChannel.DisabledRule, config.DisabledRules, droppedByRule, Detail: null);
        var namespaceEntries = BuildNamespaceEntries(config, namespaceShadowFindings, shadowAnalysisRan);

        var certaintyEntry = minimumCertainty is { } tierFloor && tierCounts.Count > 0
            ? BuildEntries(SuppressionChannel.CertaintyTier, [tierFloor.ToString()], tierCounts, Detail: null).FirstOrDefault()
            : null;

        var obsolete = BuildObsolete(
            config,
            droppedByRule,
            downgradedByRule,
            shadowAnalysisRan,
            globMatchesNoFile,
            namespaceExists);

        var analysed = analysedFindings.Count;
        var totalDebt = analysed + shadowHidden;
        var hidden = dropped + shadowHidden;

        var totals = new SuppressionTotals(
            FindingsAnalysed: analysed,
            FindingsHiddenByShadow: shadowHidden,
            FindingsDropped: dropped,
            FindingsDowngraded: downgraded,
            FindingsAfterSuppression: analysed - dropped,
            HiddenDebtPercent: totalDebt == 0 ? 0 : (int)Math.Round(100d * hidden / totalDebt, MidpointRounding.AwayFromZero));

        return new SuppressionAudit(
            Totals: totals,
            PathGlobs: pathGlobEntries,
            SeverityOverrides: severityEntries,
            DisabledRules: disabledRuleEntries,
            NamespaceExclusions: namespaceEntries,
            CertaintyFilter: certaintyEntry,
            // Sorted here rather than at render time so the JSON section and the console
            // table present stale suppressions in the same order.
            Obsolete: SortObsolete(obsolete),
            ShadowAnalysisRan: shadowAnalysisRan);
    }

    private static void Tally(Dictionary<string, (int Count, SortedSet<string> Rules)> map, string key, string ruleId)
    {
        map.TryGetValue(key, out var current);
        current.Rules ??= new SortedSet<string>(StringComparer.Ordinal);
        current.Rules.Add(ruleId);
        map[key] = (current.Count + 1, current.Rules);
    }

    private static SuppressionEntry[] BuildEntries(
        SuppressionChannel channel,
        IEnumerable<string> selectors,
        Dictionary<string, (int Count, SortedSet<string> Rules)> tallies,
        string? Detail)
    {
        var entries = new List<SuppressionEntry>();

        // Config order, so the report reads in the same order as snipper.json.
        foreach (var selector in selectors)
        {
            tallies.TryGetValue(selector, out var tally);
            entries.Add(new SuppressionEntry(
                Channel: channel,
                Selector: selector,
                SuppressedCount: tally.Count,
                DowngradedCount: 0,
                RuleIds: tally.Rules is null ? [] : [.. tally.Rules],
                Detail: Detail ?? string.Empty));
        }

        return [.. entries];
    }

    private static SuppressionEntry[] BuildSeverityEntries(
        SnipperConfig config,
        Dictionary<string, (int Count, SortedSet<string> Rules)> downgraded,
        Dictionary<string, (int Count, SortedSet<string> Rules)> hiddenByOverride,
        IReadOnlyList<SnipperFinding> analysedFindings)
    {
        var entries = new List<SuppressionEntry>();

        foreach (var ruleId in config.SeverityOverrides.Keys.Order(StringComparer.Ordinal))
        {
            downgraded.TryGetValue(ruleId, out var downgradeTally);
            hiddenByOverride.TryGetValue(ruleId, out var hiddenTally);

            // Report the tiers actually observed, not just the configured target, so a
            // downgrade that matches nothing is visible as such.
            var observed = analysedFindings
                .Where(f => f.RuleId == ruleId)
                .GroupBy(static f => f.Certainty)
                .OrderBy(static g => (byte)g.Key)
                .Select(static g => $"{g.Key}x{g.Count()}");

            entries.Add(new SuppressionEntry(
                Channel: SuppressionChannel.SeverityOverride,
                Selector: ruleId,
                SuppressedCount: hiddenTally.Count,
                DowngradedCount: downgradeTally.Count,
                RuleIds: Merge(downgradeTally.Rules, hiddenTally.Rules),
                Detail: $"-> {config.SeverityOverrides[ruleId]} (was {string.Join(", ", observed)})"));
        }

        return [.. entries];
    }

    private static string[] Merge(SortedSet<string>? first, SortedSet<string>? second)
    {
        if (first is null)
        {
            return second is null ? [] : [.. second];
        }

        if (second is null)
        {
            return [.. first];
        }

        var merged = new SortedSet<string>(first, StringComparer.Ordinal);
        merged.UnionWith(second);
        return [.. merged];
    }

    private static SuppressionEntry[] BuildNamespaceEntries(
        SnipperConfig config,
        IReadOnlyList<SnipperFinding>? namespaceShadowFindings,
        bool shadowAnalysisRan)
    {
        if (config.ExcludedNamespaces.Count == 0)
        {
            return [];
        }

        var selectors = config.ExcludedNamespaces.Order(StringComparer.Ordinal).ToArray();
        var count = shadowAnalysisRan ? namespaceShadowFindings?.Count ?? 0 : 0;

        // Per-namespace counts need one shadow pass per namespace; the aggregate is
        // reported instead and the limit is stated rather than implied.
        var detail = shadowAnalysisRan
            ? $"aggregate over {selectors.Length} namespace(s); per-namespace split not available"
            : "not measured - rerun with --audit-suppressions and namespace exclusions configured";

        return [new SuppressionEntry(
            Channel: SuppressionChannel.NamespaceExclusion,
            Selector: string.Join(", ", selectors),
            SuppressedCount: count,
            DowngradedCount: 0,
            RuleIds: [],
            Detail: detail)];
    }

    private static ObsoleteSuppression[] BuildObsolete(
        SnipperConfig config,
        Dictionary<string, (int Count, SortedSet<string> Rules)> droppedByRule,
        Dictionary<string, (int Count, SortedSet<string> Rules)> downgradedByRule,
        bool shadowAnalysisRan,
        Func<string, bool>? globMatchesNoFile,
        Func<string, bool>? namespaceExists)
    {
        var obsolete = new List<ObsoleteSuppression>();

        // Path glob: matching no file on disk is the only certain signal. A glob that
        // matched files but produced no findings is legitimate — the code may be clean.
        foreach (var glob in config.ExcludedPathGlobs)
        {
            if (globMatchesNoFile is not null && globMatchesNoFile(glob))
            {
                obsolete.Add(new ObsoleteSuppression(
                    SuppressionChannel.PathGlob,
                    glob,
                    SuppressionConfidence.Certain,
                    "matches no file in the analysed tree"));
            }
        }

        // Disabled rule that hid nothing. Suspected, not certain: a clean rule is not a
        // stale configuration, and deleting it would only cost a little time.
        foreach (var ruleId in config.DisabledRules.Order(StringComparer.Ordinal))
        {
            if (!droppedByRule.TryGetValue(ruleId, out var tally) || tally.Count == 0)
            {
                var reason = shadowAnalysisRan
                    ? "rule produced no findings when run"
                    : "rule produced no findings in the normal run; not shadow-verified";

                obsolete.Add(new ObsoleteSuppression(
                    SuppressionChannel.DisabledRule,
                    ruleId,
                    SuppressionConfidence.Suspected,
                    reason));
            }
        }

        // Severity override on a rule with nothing to downgrade.
        foreach (var ruleId in config.SeverityOverrides.Keys.Order(StringComparer.Ordinal))
        {
            if (!downgradedByRule.TryGetValue(ruleId, out var tally) || tally.Count == 0)
            {
                obsolete.Add(new ObsoleteSuppression(
                    SuppressionChannel.SeverityOverride,
                    ruleId,
                    SuppressionConfidence.Suspected,
                    "no finding of this rule was downgraded"));
            }
        }

        // Namespace exclusion for a namespace that does not exist. Certain when the
        // namespace inventory was available.
        if (namespaceExists is not null)
        {
            foreach (var ns in config.ExcludedNamespaces.Order(StringComparer.Ordinal))
            {
                if (!namespaceExists(ns))
                {
                    obsolete.Add(new ObsoleteSuppression(
                        SuppressionChannel.NamespaceExclusion,
                        ns,
                        SuppressionConfidence.Certain,
                        "namespace is not declared anywhere in the analysed solution"));
                }
            }
        }

        // A tier floor stricter than anything present hides nothing by construction, and
        // an unused floor is a deliberate choice rather than rot, so it is never reported
        // as obsolete. CliRunner already prints it when it drops findings.

        return [.. obsolete];
    }

    /// <summary>
    /// Deterministic ordering for report output: channel, then selector.
    /// </summary>
    public static ObsoleteSuppression[] SortObsolete(IEnumerable<ObsoleteSuppression> obsolete)
    {
        ArgumentNullException.ThrowIfNull(obsolete);

        return [.. obsolete.OrderBy(static o => (byte)o.Channel).ThenBy(static o => o.Selector, StringComparer.Ordinal)];
    }
}

/// <summary>
/// Read-only view of the file inventory the audit uses to decide whether a path glob is
/// certainly stale. Injected rather than read from disk inside the builder so the
/// attribution rules stay pure.
/// </summary>
internal sealed class GlobFileInventory
{
    private readonly FrozenSet<string> _files;

    public GlobFileInventory(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        _files = files.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _files.Count;

    public bool MatchesNoFile(string glob)
    {
        ArgumentException.ThrowIfNullOrEmpty(glob);

        foreach (var file in _files)
        {
            if (GlobPattern.IsMatch(glob, file))
            {
                return false;
            }
        }

        return true;
    }
}
