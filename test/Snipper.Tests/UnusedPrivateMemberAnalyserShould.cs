namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedPrivateMemberAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unused_Private_Members_As_Guaranteed_When_No_References_Exist_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0001"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.Message.Contains("_unusedPrivateField", StringComparison.Ordinal));
        findings.Should().Contain(f =>
            f.RuleId == "SNP0001"
            && f.Message.Contains("MultiplyUnused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Used_Private_Members_When_References_Exist_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("_usedPrivateField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Local_Functions_When_Declared_After_An_Unconditional_Exit_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("BottomHelper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Explicit_Interface_Implementations_When_Dispatched_Through_The_Contract_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("JsonTypeInfo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Members_When_File_Has_An_Auto_Generated_Header_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("_protoUnusedField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Members_When_Document_Is_Outside_The_Analysis_Roots_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("_sharedUnusedField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Private_Entry_Point_Method_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("'Main'", StringComparison.Ordinal));
    }
}
