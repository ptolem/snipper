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
    public async Task Report_One_Finding_When_A_Using_Duplicates_A_Global_Using_For_AnalyzeAsync()
    {
        // Roslyn describes ONE directive with TWO diagnostics here: CS8019 ("unnecessary")
        // and CS8933 ("duplicates a global using"), both on the same UsingDirectiveSyntax.
        // Surfacing both doubled every such directive (62 of 250 on MILKRUN) and, since
        // the two messages differ, BaselineService.ComputeFingerprint gave them different
        // hashes - so a consumer acting on one saw the other resurface as new.
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var flagged = findings
            .Where(f => f.RuleId == "SNP0019"
                && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
                && f.LineNumber == 4)
            .ToList();

        flagged.Should().ContainSingle("one directive is one finding, however many diagnostics describe it");
        flagged[0].Message.Should().Contain("duplicates a global using directive",
            "CS8933 is the specific verdict, so it is the one that must survive");
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
    public async Task Flag_Duplicate_Using_With_Distinct_Message_For_AnalyzeAsync()
    {
        // The compiler flags only the second occurrence — the message must make
        // clear it is a duplicate, so consumers keep exactly one copy (FP-4).
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("GlobalUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 7
            && f.Message.Contains("duplicates another using directive", StringComparison.Ordinal));
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
