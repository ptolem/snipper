namespace Snipper.Cli;

using Snipper.Models;

/// <summary>
/// Why <see cref="FindingFilter.Classify"/> kept, dropped, or downgraded a finding.
/// </summary>
internal enum FindingFilterOutcome : byte
{
    Kept = 0,
    DroppedDisabledRule = 1,
    DroppedPathGlob = 2,
    SeverityOverridden = 3,
}

/// <summary>
/// The result of classifying one finding against <c>snipper.json</c>, including
/// which glob matched and which tier replaced the original.
/// </summary>
internal readonly record struct FindingFilterClassification(
    FindingFilterOutcome Outcome,
    string MatchedGlob,
    CertaintyTier EffectiveTier);

/// <summary>
/// Applies snipper.json to findings at report time: disabled rules and path-glob
/// matches are dropped, severity overrides are applied. Pure and workspace-free by
/// design — runs after baseline fingerprinting, so it cannot churn the baseline.
///
/// This filter governs only the channels that suppress findings *after* they exist.
/// Namespace exclusions and whole-analyser rule disables remove findings before they
/// exist; those are handled upstream (as of 4A-2 by fingerprinting the
/// suppression-independent set) and never appear here. <see cref="Classify"/> is shared
/// with the suppression audit so the two can never disagree about why a finding vanished.
/// </summary>
internal static class FindingFilter
{
    public static IReadOnlyList<SnipperFinding> Apply(IReadOnlyList<SnipperFinding> findings, SnipperConfig config)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(config);

        if (IsEmpty(config))
        {
            return findings;
        }

        var result = new List<SnipperFinding>(findings.Count);
        foreach (var finding in findings)
        {
            var classification = Classify(finding, config);
            switch (classification.Outcome)
            {
                case FindingFilterOutcome.DroppedDisabledRule:
                case FindingFilterOutcome.DroppedPathGlob:
                    continue;
                case FindingFilterOutcome.SeverityOverridden:
                    result.Add(finding with { Certainty = classification.EffectiveTier });
                    break;
                default:
                    result.Add(finding);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Classifies a single finding without building a result list. The suppression
    /// audit (4A) reports per-channel totals from this, so audit numbers and report
    /// behaviour come from one implementation rather than two that can drift.
    /// Channel precedence matches <see cref="Apply"/> exactly: a disabled rule wins
    /// over a path glob, which wins over a severity override.
    /// </summary>
    public static FindingFilterClassification Classify(SnipperFinding finding, SnipperConfig config)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(config);

        if (config.DisabledRules.Contains(finding.RuleId))
        {
            return new FindingFilterClassification(FindingFilterOutcome.DroppedDisabledRule, string.Empty, finding.Certainty);
        }

        foreach (var glob in config.ExcludedPathGlobs)
        {
            if (GlobPattern.IsMatch(glob, finding.FilePath))
            {
                return new FindingFilterClassification(FindingFilterOutcome.DroppedPathGlob, glob, finding.Certainty);
            }
        }

        if (config.SeverityOverrides.TryGetValue(finding.RuleId, out var tier))
        {
            return new FindingFilterClassification(FindingFilterOutcome.SeverityOverridden, finding.RuleId, tier);
        }

        return new FindingFilterClassification(FindingFilterOutcome.Kept, string.Empty, finding.Certainty);
    }

    /// <summary>
    /// An analyser is worth running only while at least one of its rules is enabled.
    /// </summary>
    public static bool IsAnalyserEnabled(IReadOnlyCollection<string> ruleIds, SnipperConfig config)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        ArgumentNullException.ThrowIfNull(config);

        foreach (var ruleId in ruleIds)
        {
            if (!config.DisabledRules.Contains(ruleId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when config carries no channel this filter acts on. Callers use it to skip
    /// work entirely — the audit must cost nothing for a user with no suppressions.
    /// </summary>
    public static bool IsEmpty(SnipperConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.DisabledRules.Count == 0
            && config.SeverityOverrides.Count == 0
            && config.ExcludedPathGlobs.Count == 0;
    }
}
