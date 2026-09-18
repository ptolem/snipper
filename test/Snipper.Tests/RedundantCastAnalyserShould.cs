namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class RedundantCastAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Identity_Cast_As_High_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0026"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("Cast to 'string'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Generic_Identity_Cast_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0026"
            && f.Message.Contains("Cast to 'T'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Identity_Cast_In_Argument_Position_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0026"
            && f.Message.Contains("Cast to 'object'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Downcast_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0026" && f.Message.Contains("Cast to 'CastDerived'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Numeric_Or_Unboxing_Casts_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0026" && f.Message.Contains("Cast to 'int'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_User_Defined_Conversion_Casts_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0026" && f.Message.Contains("Cast to 'long'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Implicit_Reference_Upcasts_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0026" && f.Message.Contains("Cast to 'CastBase'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Anything_When_File_Has_No_Casts_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0026" && f.FilePath.Contains("WriteOnlyFields.cs", StringComparison.Ordinal));
    }
}
