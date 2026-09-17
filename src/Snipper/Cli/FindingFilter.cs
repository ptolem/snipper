namespace Snipper.Cli;

using Snipper.Models;

/// <summary>
/// Applies snipper.json to findings at report time: disabled rules and path-glob
/// matches are dropped, severity overrides are applied. Pure and workspace-free by
/// design — runs after baseline fingerprinting so config never churns the baseline.
/// </summary>
internal static class FindingFilter
{
    public static IReadOnlyList<SnipperFinding> Apply(IReadOnlyList<SnipperFinding> findings, SnipperConfig config)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(config);

        if (config.DisabledRules.Count == 0
            && config.SeverityOverrides.Count == 0
            && config.ExcludedPathGlobs.Count == 0)
        {
            return findings;
        }

        var result = new List<SnipperFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (config.DisabledRules.Contains(finding.RuleId))
            {
                continue;
            }

            var excludedByPath = false;
            foreach (var glob in config.ExcludedPathGlobs)
            {
                if (GlobPattern.IsMatch(glob, finding.FilePath))
                {
                    excludedByPath = true;
                    break;
                }
            }

            if (excludedByPath)
            {
                continue;
            }

            if (config.SeverityOverrides.TryGetValue(finding.RuleId, out var tier) && tier != finding.Certainty)
            {
                result.Add(finding with { Certainty = tier });
            }
            else
            {
                result.Add(finding);
            }
        }

        return result;
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
}
