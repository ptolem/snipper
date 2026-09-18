namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class WriteOnlyFieldAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Ctor_Assigned_Field_As_High_When_Never_Read_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0021"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("_retryBudget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Readonly_Field_As_High_When_Written_Only_In_The_Ctor_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0021"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("_lastSeed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Out_Only_Field_As_High_When_Never_Read_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0021"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("_outOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Written_Field_As_High_When_Otherwise_Referenced_Only_By_Nameof_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0021"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("_namedOnly", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("_jsonInclude")]
    [InlineData("_dataMember")]
    [InlineData("_jsonProperty")]
    public async Task Demote_Serialization_Attributed_Field_To_Moderate_When_Written_Never_Read_For_AnalyzeAsync(string fieldName)
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0021"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains(fieldName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_Only_Compound_Assigned_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("_compound", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_Only_Incremented_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("_counter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_Passed_By_Ref_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("_byRef", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_Read_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("'_read'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_It_Has_Zero_References_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("_neverTouched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Auto_Property_Backing_Field_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.Message.Contains("AutoProp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Anything_When_File_Has_No_Write_Only_Fields_For_AnalyzeAsync()
    {
        var analyser = new WriteOnlyFieldAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0021" && f.FilePath.Contains("DeadCode.cs", StringComparison.Ordinal));
    }
}
