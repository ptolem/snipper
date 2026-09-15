namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class ConfigurationBindingAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Json_Key_As_Advisory_When_No_Options_Property_Matches_For_AnalyzeAsync()
    {
        var analyser = new ConfigurationBindingAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0007"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("LegacySection:OldKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Options_Property_As_Advisory_When_Missing_From_All_Settings_Files_For_AnalyzeAsync()
    {
        var analyser = new ConfigurationBindingAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0008"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("RetiredSetting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SalesService:BaseUrl")]
    [InlineData("SalesService:TimeoutSeconds")]
    public async Task Not_Flag_Json_Key_When_A_Bound_Options_Property_Exists_For_AnalyzeAsync(string boundKey)
    {
        var analyser = new ConfigurationBindingAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0007" && f.Message.Contains(boundKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Framework_Section_Keys_When_They_Belong_To_The_Framework_For_AnalyzeAsync()
    {
        var analyser = new ConfigurationBindingAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0007" && f.Message.Contains("Logging:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Json_Key_When_The_Options_Type_Lives_In_A_Metadata_Assembly_For_AnalyzeAsync()
    {
        var analyser = new ConfigurationBindingAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0007" && f.Message.Contains("Json:WriteIndented", StringComparison.Ordinal));
    }
}
