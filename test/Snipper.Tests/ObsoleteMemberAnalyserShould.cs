namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class ObsoleteMemberAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Private_Member_As_High_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("UnusedOldMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Error_Member_As_High_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("RemovedApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Public_Member_As_Moderate_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("SunsetApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Referenced_Obsolete_Member_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("StillUsedApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Obsolete_Interface_Implementation_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("Greet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Non_Obsolete_Unreferenced_Public_Member_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // UnusedPublicMethod is SNP0006's finding; SNP0018 requires the attribute.
        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("UnusedPublicMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Finding_In_Excluded_Namespace_For_AnalyzeAsync()
    {
        var withoutExclusions = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);
        withoutExclusions.Should().Contain(f => f.RuleId == "SNP0018" && f.Message.Contains("ExcludedOldMethod", StringComparison.Ordinal));

        var exclusions = AnalysisExclusions.Create(["Excluded.Fake"]);
        var withExclusions = await new ObsoleteMemberAnalyser(exclusions).AnalyzeAsync(fixture.Solution, CancellationToken.None);
        withExclusions.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("ExcludedOldMethod", StringComparison.Ordinal));
    }
}
