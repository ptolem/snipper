namespace Snipper.Tests;

using System.Collections.Frozen;
using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

public sealed class FindingFilterShould
{
    [Fact]
    public void Return_The_Same_List_When_Config_Is_Empty_For_Apply()
    {
        var findings = new[] { Finding("SNP0001", "C:/a.cs") };

        FindingFilter.Apply(findings, SnipperConfig.Empty).Should().BeSameAs(findings);
    }

    [Fact]
    public void Drop_Findings_Of_Disabled_Rules_For_Apply()
    {
        var findings = new[] { Finding("SNP0001", "C:/a.cs"), Finding("SNP0002", "C:/b.cs") };
        var config = Config(disabled: ["SNP0001"]);

        var result = FindingFilter.Apply(findings, config);

        result.Should().ContainSingle(f => f.RuleId == "SNP0002");
    }

    [Fact]
    public void Override_Severity_Of_Configured_Rules_For_Apply()
    {
        var findings = new[] { Finding("SNP0018", "C:/a.cs", CertaintyTier.High) };
        var config = Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory });

        var result = FindingFilter.Apply(findings, config);

        result.Should().ContainSingle(f => f.Certainty == CertaintyTier.Advisory && f.RuleId == "SNP0018");
    }

    [Fact]
    public void Drop_Findings_Under_Excluded_Path_Globs_For_Apply()
    {
        var findings = new[]
        {
            Finding("SNP0001", @"C:\ws\Generated\a.cs"),
            Finding("SNP0001", @"C:\ws\Src\b.cs"),
        };
        var config = Config(globs: ["**/Generated/**"]);

        var result = FindingFilter.Apply(findings, config);

        result.Should().ContainSingle(f => f.FilePath.Contains("Src", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_Analyser_Enabled_Only_When_At_Least_One_Rule_Is_Live_For_IsAnalyserEnabled()
    {
        var config = Config(disabled: ["SNP0003", "SNP0004"]);

        FindingFilter.IsAnalyserEnabled(["SNP0003", "SNP0004"], config).Should().BeFalse();
        FindingFilter.IsAnalyserEnabled(["SNP0003", "SNP0005"], config).Should().BeTrue();
    }

    [Fact]
    public void Report_No_Channels_For_IsEmpty_When_Config_Carries_Nothing_This_Filter_Acts_On()
    {
        FindingFilter.IsEmpty(SnipperConfig.Empty).Should().BeTrue();
        FindingFilter.IsEmpty(Config(disabled: ["SNP0001"])).Should().BeFalse();
        FindingFilter.IsEmpty(Config(globs: ["**/Generated/**"])).Should().BeFalse();
        FindingFilter.IsEmpty(Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0001"] = CertaintyTier.Advisory }))
            .Should().BeFalse();
    }

    [Fact]
    public void Prefer_A_Disabled_Rule_Over_A_Matching_Path_Glob_For_Classify()
    {
        // Precedence must match Apply exactly, or the audit would attribute a finding to
        // a different channel than the one that actually removed it.
        var config = Config(disabled: ["SNP0001"], globs: ["**/Generated/**"]);

        var classification = FindingFilter.Classify(Finding("SNP0001", @"C:\ws\Generated\a.cs"), config);

        classification.Outcome.Should().Be(FindingFilterOutcome.DroppedDisabledRule);
    }

    [Fact]
    public void Report_The_Matching_Glob_For_Classify_So_The_Audit_Can_Name_The_Suppression()
    {
        var config = Config(globs: ["**/Vendor/**", "**/Generated/**"]);

        var classification = FindingFilter.Classify(Finding("SNP0001", @"C:\ws\Generated\a.cs"), config);

        classification.Outcome.Should().Be(FindingFilterOutcome.DroppedPathGlob);
        classification.MatchedGlob.Should().Be("**/Generated/**");
    }

    [Fact]
    public void Report_The_Effective_Tier_For_Classify_When_An_Override_Applies()
    {
        var config = Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory });

        var classification = FindingFilter.Classify(Finding("SNP0018", @"C:\ws\a.cs", CertaintyTier.High), config);

        classification.Outcome.Should().Be(FindingFilterOutcome.SeverityOverridden);
        classification.EffectiveTier.Should().Be(CertaintyTier.Advisory);
    }

    [Fact]
    public void Keep_An_Unconfigured_Finding_For_Classify()
    {
        var classification = FindingFilter.Classify(Finding("SNP0001", @"C:\ws\a.cs"), SnipperConfig.Empty);

        classification.Outcome.Should().Be(FindingFilterOutcome.Kept);
        classification.EffectiveTier.Should().Be(CertaintyTier.Guaranteed);
    }

    [Fact]
    public void Agree_With_Apply_On_Every_Finding_For_Classify()
    {
        // The suppression audit reports per-channel totals from Classify while the report
        // is built from Apply. If these two ever diverge the audit would confidently
        // describe a behaviour the tool does not have, so the invariant is pinned here
        // over a matrix that exercises every channel and its precedence.
        var findings = new[]
        {
            Finding("SNP0001", @"C:\ws\Generated\a.cs"),
            Finding("SNP0001", @"C:\ws\Src\b.cs"),
            Finding("SNP0002", @"C:\ws\Generated\c.cs"),
            Finding("SNP0003", @"C:\ws\Src\d.cs"),
            Finding("SNP0018", @"C:\ws\Src\e.cs", CertaintyTier.High),
            Finding("SNP0018", @"C:\ws\Generated\f.cs", CertaintyTier.Moderate),
        };

        var configs = new[]
        {
            SnipperConfig.Empty,
            Config(disabled: ["SNP0001"]),
            Config(disabled: ["SNP0002"], globs: ["**/Generated/**"]),
            Config(globs: ["**/Generated/**", "**/Vendor/**"]),
            Config(overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory }),
            Config(
                disabled: ["SNP0001"],
                globs: ["**/Generated/**"],
                overrides: new Dictionary<string, CertaintyTier> { ["SNP0018"] = CertaintyTier.Advisory }),
        };

        foreach (var config in configs)
        {
            var applied = FindingFilter.Apply(findings, config);

            foreach (var finding in findings)
            {
                var classification = FindingFilter.Classify(finding, config);

                var survives = classification.Outcome switch
                {
                    FindingFilterOutcome.DroppedDisabledRule => false,
                    FindingFilterOutcome.DroppedPathGlob => false,
                    FindingFilterOutcome.SeverityOverridden => true,
                    _ => true,
                };

                applied.Any(f => ReferenceEquals(f, finding) || f.FilePath == finding.FilePath && f.RuleId == finding.RuleId)
                    .Should().Be(survives, $"'{finding.RuleId}' at {finding.FilePath} under this config");

                if (classification.Outcome == FindingFilterOutcome.SeverityOverridden)
                {
                    var matched = applied.First(f => f.FilePath == finding.FilePath && f.RuleId == finding.RuleId);
                    matched.Certainty.Should().Be(classification.EffectiveTier);
                }
            }
        }
    }

    private static SnipperFinding Finding(string ruleId, string filePath, CertaintyTier certainty = CertaintyTier.Guaranteed)
    {
        return new SnipperFinding(
            RuleId: ruleId,
            Title: "title",
            Message: "message",
            Certainty: certainty,
            Category: FindingCategory.UnusedPrivateMember,
            FilePath: filePath,
            LineNumber: 1,
            CharacterOffset: 1);
    }

    private static SnipperConfig Config(
        string[]? disabled = null,
        IReadOnlyDictionary<string, CertaintyTier>? overrides = null,
        string[]? globs = null)
    {
        return new SnipperConfig
        {
            DisabledRules = (disabled ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SeverityOverrides = (overrides ?? new Dictionary<string, CertaintyTier>())
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ExcludedPathGlobs = (globs ?? []).ToFrozenSet(StringComparer.Ordinal),
        };
    }
}
