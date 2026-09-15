namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedNonPrivateMemberAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unused_Internal_Type_As_Moderate_When_No_References_Exist_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0005"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("UnusedInternalType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Unused_Public_Member_As_Advisory_When_On_Exported_Type_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0006"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("UnusedPublicMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demote_To_Advisory_When_Containing_Type_Is_Registered_In_DI_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // The member is public, so the rule is SNP0006 — but DI registration demotes
        // it to Advisory where an unregistered peer (UnusedInternalMethod) is Moderate.
        findings.Should().Contain(f =>
            f.RuleId == "SNP0006"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("UnusedButRegistered", StringComparison.Ordinal));
        findings.Should().Contain(f =>
            f.RuleId == "SNP0006"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("UnusedInternalMethod", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TextExtensions")]
    [InlineData("ViaUsingStatic")]
    public async Task Not_Flag_Static_Class_When_Its_Methods_Are_Used_For_AnalyzeAsync(string typeName)
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0006" && f.Message.Contains($"'{typeName}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Test_Class_Or_Its_Members_When_A_Method_Carries_A_Test_Attribute_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("SampleTests", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("UnusedTestHelper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Interface_Implementation_When_Interface_Method_Has_Callers_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("Greet", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("'Greeter'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Used_Public_Member_When_References_Exist_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0006" && f.Message.Contains("UsedByApp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Module_Initializer_Type_Or_Method_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ModuleBootstrap", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("'Initialize'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Xunit_Lifecycle_Fixture_Or_Collection_Definition_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("FakeFixture", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("InitializeAsync", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("DisposeAsync", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("SampleCollectionDefinition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Entry_Point_Containing_Type_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("'Program'", StringComparison.Ordinal));
    }
}
