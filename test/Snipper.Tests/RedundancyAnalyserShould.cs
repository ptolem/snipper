namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class RedundancyAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_This_Qualifier_When_No_Shadow_Exists_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // Two positives inside QualifierScenarios.Positive; the shadowed
        // accesses in Negative never flag.
        findings
            .Where(f => f.RuleId == "SNP0028" && f.Message.Contains("'this.'", StringComparison.Ordinal))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Flag_Qualified_Type_Name_When_Bare_Name_Binds_Identically_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // Declaration type + object-creation type — both redundant given the using.
        findings
            .Where(f => f.RuleId == "SNP0028" && f.Message.Contains("'StringBuilder'", StringComparison.Ordinal))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Flag_Empty_Public_Constructor_And_Empty_Destructor_As_High_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0029"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("'EmptyCtorScenario()'", StringComparison.Ordinal));
        findings.Should().Contain(f =>
            f.RuleId == "SNP0029"
            && f.Message.Contains("'~EmptyDtorScenario()'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LoadBearingCtorScenario")]
    [InlineData("NonEmptyCtorScenario")]
    [InlineData("StaticCtorScenario")]
    public async Task Not_Flag_LoadBearing_Or_NonEmpty_Constructors_For_AnalyzeAsync(string typeName)
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0029" && f.Message.Contains(typeName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Literal_Matching_Parameter_Default_As_High_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0022"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("parameter 'times'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Null_Matching_Reference_Parameter_Default_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0022" && f.Message.Contains("parameter 'context'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Enum_Member_Matching_Enum_Parameter_Default_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0022" && f.Message.Contains("parameter 'mode'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Only_The_Trailing_Argument_When_Multiple_Defaults_Match_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0022" && f.Message.Contains("parameter 'third'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Non_Trailing_Argument_When_A_Later_Argument_Is_Present_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("parameter 'second'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Literal_When_It_Differs_From_The_Parameter_Default_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("parameter 'level'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Named_Argument_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("namedTimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Argument_When_Removal_Would_Rebind_To_Another_Overload_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("rebindTimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Params_Array_Arguments_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("parameter 'values'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Caller_Info_Parameter_When_Removal_Changes_Injected_Value_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("parameter 'caller'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Anything_When_File_Has_No_Redundant_Invocations_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            (f.RuleId == "SNP0022" || f.RuleId == "SNP0025")
            && f.FilePath.Contains("DeadCode.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Plain_Generic_Invocation_As_High_When_Inference_Infers_Identically_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0025"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("'EchoExplicit'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Member_Access_Generic_Invocation_When_Inference_Infers_Identically_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0025" && f.Message.Contains("'EchoViaClass'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Type_Arguments_When_Stripping_Would_Rebind_To_Non_Generic_Overload_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("EchoAmbiguous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Type_Arguments_When_The_Method_Has_No_Arguments_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("EchoNoArgs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Type_Arguments_When_Type_Appears_Only_In_The_Return_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("IdentityUninferrable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Nullable_Annotated_Type_Arguments_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("EchoAnnotated", StringComparison.Ordinal));
    }

    // Conditional access (?.): speculation must never run on these shapes —
    // Roslyn's speculative binder throws NullReferenceException on a
    // MemberBindingExpression detached from its conditional-access parent
    // (monorepo crash 2026-09-18). These tests completing at all is the
    // crash-guard assertion; the NotContain clauses pin the no-finding rule.
    [Fact]
    public async Task Not_Flag_Or_Crash_When_The_Invocation_Is_Conditional_On_Its_Receiver_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0022" && f.Message.Contains("GreetViaConditional", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Or_Crash_When_A_Conditional_Access_Chains_Into_A_Generic_Invocation_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("EchoViaConditional", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Or_Crash_When_A_Conditional_Access_Sits_Inside_An_Argument_For_AnalyzeAsync()
    {
        var analyser = new RedundancyAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0025" && f.Message.Contains("EchoPlain", StringComparison.Ordinal));
    }
}
