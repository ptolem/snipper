namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnreferencedPackageAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_PackageReference_As_High_When_No_Package_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var analyser = new UnreferencedPackageAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0003"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_ProjectReference_As_High_When_No_Referenced_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var analyser = new UnreferencedPackageAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0004"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("OrphanLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_ProjectReference_When_Referenced_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0004" && f.Message.Contains("CoreLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Package_When_Its_Exclusive_Transitive_Subtree_Is_Used_For_AnalyzeAsync()
    {
        // Microsoft.Extensions.Logging.Console's own assembly is unused, but removing the
        // direct reference would evict its exclusive subtree (Logging.Abstractions), whose
        // ILogger type IS used — the reference is load-bearing and must not be flagged.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0003" && f.Message.Contains("Microsoft.Extensions.Logging.Console", StringComparison.Ordinal));
    }
}
