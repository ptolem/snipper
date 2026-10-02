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
    [InlineData("literal")]    // name spelled only in a string literal — no usage position
    [InlineData("member")]     // name collides with an unrelated type's member
    [InlineData("unread")]     // deconstruction element whose sibling is read
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
    [InlineData("read")]         // deconstruction element that is read
    public async Task Not_Flag_Read_Or_Excluded_Locals_For_AnalyzeAsync(string localName)
    {
        var analyser = new UnusedLocalVariableAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains($"'{localName}'", StringComparison.Ordinal));
    }

    /// <summary>
    /// An outer local shadowed by an inner one of the same name: the read binds to
    /// the inner local, so the outer is dead even though the name is spelled in a
    /// usage position. The document-scoped reference test must bind, not match text.
    /// </summary>
    [Fact]
    public async Task Flag_The_Outer_Local_When_An_Inner_Local_Shadows_Its_Name_For_AnalyzeAsync()
    {
        var analyser = new UnusedLocalVariableAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var shadowed = findings.Where(f => f.RuleId == "SNP0009" && f.Message.Contains("'shadowed'", StringComparison.Ordinal));
        shadowed.Should().ContainSingle();
        shadowed.Single().FilePath.Should().EndWith("LocalShadowingPatterns.cs");
        shadowed.Single().LineNumber.Should().Be(17);
    }
}
