namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnreachableCodeAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Statement_As_Guaranteed_When_A_Preceding_Return_Exits_The_Block_For_AnalyzeAsync()
    {
        var analyser = new UnreachableCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var finding = findings.Should().ContainSingle(f => f.RuleId == "SNP0002").Which;
        finding.Certainty.Should().Be(CertaintyTier.Guaranteed);
        finding.FilePath.Should().EndWith("DeadCode.cs");
        finding.LineNumber.Should().Be(21);
    }

    [Fact]
    public async Task Not_Flag_Local_Functions_When_Declared_After_An_Unconditional_Exit_For_AnalyzeAsync()
    {
        var analyser = new UnreachableCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // The single legitimate SNP0002 finding is the dead statement at line 21;
        // the hoisted bottom-of-method local functions must not add more.
        findings.Should().ContainSingle(f => f.RuleId == "SNP0002");
    }

    [Fact]
    public async Task Not_Flag_Static_Or_Labeled_Local_Functions_When_Declared_After_An_Unconditional_Exit_For_AnalyzeAsync()
    {
        var analyser = new UnreachableCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // Static local functions, local functions in try/catch, and label-wrapped
        // declarations (LocalFunctionEdgeCases in the fixture) are still hoisted —
        // the only SNP0002 remains the dead statement at line 21.
        findings.Should().ContainSingle(f => f.RuleId == "SNP0002");
    }

    [Fact]
    public async Task Not_Flag_Statements_When_Control_Flow_Reaches_Them_For_AnalyzeAsync()
    {
        var analyser = new UnreachableCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0002"
            && f.FilePath.EndsWith("Worker.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Empty_Statement_Trailing_A_Hoisted_Local_Function_For_AnalyzeAsync()
    {
        // A stray `;` after a bottom-declared local function parses as an empty
        // statement after an unconditional return — a token artifact, not dead code.
        var analyser = new UnreachableCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0002"
            && f.FilePath.EndsWith("OrderNumberDecoder.cs", StringComparison.Ordinal));
    }
}
