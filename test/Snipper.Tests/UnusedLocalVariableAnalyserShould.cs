namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedLocalVariableAnalyserShould(SampleSolutionFixture fixture)
{
    [Theory]
    [InlineData("unusedLocal")]
    [InlineData("parsedOut")]
    [InlineData("unusedPart")]
    public async Task Flag_Unread_Locals_As_Guaranteed_For_AnalyzeAsync(string localName)
    {
        var analyser = new UnusedLocalVariableAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0009"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.Message.Contains($"'{localName}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("usedLocal")]    // read in the return expression
    [InlineData("usedOut")]      // out-var read in the return expression
    [InlineData("usedPart")]     // deconstruction element that is read
    [InlineData("stream")]       // using declaration — disposal side effect
    [InlineData("helperResult")] // read in the return expression
    [InlineData("handler")]      // invoked via the delegate
    [InlineData("dead")]         // unreachable statement — SNP0002 owns the root cause
    public async Task Not_Flag_Read_Or_Excluded_Locals_For_AnalyzeAsync(string localName)
    {
        var analyser = new UnusedLocalVariableAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains($"'{localName}'", StringComparison.Ordinal));
    }
}
