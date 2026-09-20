namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class HierarchyDeadCodeAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Override_Family_With_No_External_Caller_As_Moderate_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // One finding at the family root — the override itself does not double-report.
        findings.Should().ContainSingle(f =>
            f.RuleId == "SNP0027"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("'DeadFamilyMethod'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Override_Family_With_An_External_Caller_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0027" && f.Message.Contains("UsedFamilyMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Never_Inherited_Class_With_Virtuals_As_Moderate_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0023"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("'NeverInheritedBase'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Member_Findings_When_The_Class_Level_Finding_Fires_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("VirtualHookOne", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("VirtualHookTwo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Never_Overridden_Virtual_As_Moderate_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0023"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("'SpeculativeHook'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_An_Overridden_Virtual_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("FulfilledHook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Chain_Terminal_Override_Members_Or_Leaf_Classes_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("UsedDerived", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("ContractImpl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demote_Exported_Surface_Findings_To_Advisory_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0023"
            && f.Certainty == CertaintyTier.Advisory
            && f.Message.Contains("'ExportedBase'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Findings_When_The_Type_Name_Is_Spelled_In_A_String_Literal_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("PluginLoadedBase", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_The_Never_Inherited_Class_When_It_Has_Zero_References_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("UnreferencedVirtualBase", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Abstract_Classes_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("AbstractChainRoot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Anything_When_File_Has_No_Hierarchy_Patterns_For_AnalyzeAsync()
    {
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.FilePath.Contains("DeadCode.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Virtual_Declaring_Attribute_Classes_For_AnalyzeAsync()
    {
        // 1.6.2: attribute classes are terminal by convention — a virtual member
        // on one is not a speculative extension point, so the class- and
        // member-level findings never apply (milkrun CustomProductType*Attribute).
        var analyser = new HierarchyDeadCodeAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0023" && f.Message.Contains("FwPrefix", StringComparison.Ordinal));
    }
}
