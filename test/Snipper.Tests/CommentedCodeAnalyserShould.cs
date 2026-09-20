namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class CommentedCodeAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Commented_Out_Code_Block_As_Advisory_For_AnalyzeAsync()
    {
        var findings = await new CommentedCodeAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0020"
            && f.Certainty == CertaintyTier.Advisory
            && f.FilePath.EndsWith("CommentedOut.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Exactly_One_Block_In_The_Fixture_File_For_AnalyzeAsync()
    {
        var findings = await new CommentedCodeAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // The prose/TODO/URL block in the same file must not add a second finding.
        findings.Should().ContainSingle(f =>
            f.RuleId == "SNP0020"
            && f.FilePath.EndsWith("CommentedOut.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Prose_Comments_In_Other_Files_For_AnalyzeAsync()
    {
        var findings = await new CommentedCodeAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0020" && f.FilePath.EndsWith("DeadCode.cs", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0020" && f.FilePath.EndsWith("Worker.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_Each_Block_At_Its_Own_Start_Line_For_AnalyzeAsync()
    {
        // 1.6.2: two blocks attaching to the same token must report their own
        // start lines — 1.6.1 reported the anchor node's position for both
        // (two milkrun findings pinned to the enclosing method brace).
        var findings = await new CommentedCodeAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var probeFindings = findings
            .Where(f => f.RuleId == "SNP0020" && f.FilePath.EndsWith("CommentedOutLocations.cs", StringComparison.Ordinal))
            .ToList();

        probeFindings.Should().HaveCount(2);
        probeFindings.Select(f => f.LineNumber).Should().BeEquivalentTo([13, 19]);
        probeFindings.Should().OnlyContain(f => f.CharacterOffset == 9);
    }
}
