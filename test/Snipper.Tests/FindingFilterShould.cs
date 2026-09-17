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
