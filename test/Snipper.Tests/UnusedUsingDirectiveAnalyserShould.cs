namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedUsingDirectiveAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unused_Using_Directives_As_Guaranteed_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 2);
        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 3);
    }

    [Fact]
    public async Task Flag_Using_That_Duplicates_A_Global_Using_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 4);
    }

    [Fact]
    public async Task Flag_Unused_Global_Using_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("GlobalUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 3);
    }

    [Fact]
    public async Task Not_Flag_Used_Using_Directives_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 1);
    }

    [Fact]
    public async Task Not_Flag_Files_Without_Unused_Usings_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0019" && f.FilePath.EndsWith("DeadCode.cs", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0019" && f.FilePath.EndsWith("Worker.cs", StringComparison.Ordinal));
    }
}
