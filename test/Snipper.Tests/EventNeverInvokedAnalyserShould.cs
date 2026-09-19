namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class EventNeverInvokedAnalyserShould(SampleSolutionFixture fixture)
{
    [Theory]
    [InlineData("NeverRaised")]
    [InlineData("NeverUsed")]
    public async Task Flag_Event_That_Is_Never_Raised_As_Advisory_For_AnalyzeAsync(string eventName)
    {
        var analyser = new EventNeverInvokedAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0030"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains($"'{eventName}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Raised")]
    [InlineData("RaisedDirectly")]
    public async Task Not_Flag_Event_With_A_Raise_Site_For_AnalyzeAsync(string eventName)
    {
        var analyser = new EventNeverInvokedAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0030" && f.Message.Contains($"'{eventName}'", StringComparison.Ordinal));
    }
}
