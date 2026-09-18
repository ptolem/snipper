namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class TighteningAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Params_Only_Method_As_Advisory_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0024"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("'ParamsOnly'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Method_When_It_Touches_Instance_State_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("TouchesState", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Method_When_It_Implements_An_Interface_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("ContractValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Virtual_Or_Attributed_Methods_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("StatelessVirtual", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("AttributedHook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Ctor_Only_Field_As_Advisory_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0024"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("'_ctorOnly'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_A_Method_Writes_It_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("_methodWritten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Field_When_Passed_By_Ref_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("_refPassed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Volatile_Field_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("_volatileField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Sealable_Internal_Class_As_Advisory_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0024"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("'SealableInternal'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Class_When_It_Has_A_Derived_Type_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("'UnsealedWithDerived'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Abstract_Or_Already_Sealed_Classes_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("AbstractTighteningBase", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.Message.Contains("'AlreadySealedLeaf'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Anything_When_File_Has_No_Tightening_Patterns_For_AnalyzeAsync()
    {
        var analyser = new TighteningAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // WriteOnlyFields.cs is the tightening-clean baseline: DeadCode.cs
        // legitimately contains CA1822 hits (it was authored for other rules).
        findings.Should().NotContain(f => f.RuleId == "SNP0024" && f.FilePath.Contains("WriteOnlyFields.cs", StringComparison.Ordinal));
    }
}
