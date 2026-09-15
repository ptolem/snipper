namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedParameterAnalyserShould(SampleSolutionFixture fixture)
{
    [Theory]
    [InlineData("unusedParam")]
    [InlineData("anotherUnusedParam")]
    public async Task Flag_Unused_Parameters_On_Private_Directly_Invoked_Methods_As_Moderate_For_AnalyzeAsync(string parameterName)
    {
        var analyser = new UnusedParameterAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0010"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains($"'{parameterName}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("usedParam")] // read in the method body
    [InlineData("sender")]    // method referenced as a method group — signature fixed by the delegate
    [InlineData("count")]     // method referenced as a method group — signature fixed by the delegate
    [InlineData("value")]     // EntryPoint is public — out of scope for this rule
    [InlineData("name")]      // Greeter.Greet implements an interface contract — out of scope
    public async Task Not_Flag_Used_Or_Contract_Bound_Parameters_For_AnalyzeAsync(string parameterName)
    {
        var analyser = new UnusedParameterAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0010" && f.Message.Contains($"'{parameterName}'", StringComparison.Ordinal));
    }
}
