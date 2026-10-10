namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class ObsoleteMemberAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Private_Member_As_High_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("UnusedOldMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Error_Member_As_High_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("RemovedApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Unreferenced_Obsolete_Public_Member_As_Moderate_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("SunsetApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Referenced_Obsolete_Member_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("StillUsedApi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Obsolete_Interface_Implementation_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("Greet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Non_Obsolete_Unreferenced_Public_Member_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // UnusedPublicMethod is SNP0006's finding; SNP0018 requires the attribute.
        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("UnusedPublicMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Finding_In_Excluded_Namespace_For_AnalyzeAsync()
    {
        var withoutExclusions = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);
        withoutExclusions.Should().Contain(f => f.RuleId == "SNP0018" && f.Message.Contains("ExcludedOldMethod", StringComparison.Ordinal));

        var exclusions = AnalysisExclusions.Create(["Excluded.Fake"]);
        var withExclusions = await new ObsoleteMemberAnalyser(exclusions).AnalyzeAsync(fixture.Solution, CancellationToken.None);
        withExclusions.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("ExcludedOldMethod", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // F3a — wire contracts. [Obsolete] does not stop a serializer, a model
    // binder, or a minimal-API route, so "no references, marked for removal" is
    // advice to delete a live contract.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("LegacyLeafField")]     // on the returned type
    [InlineData("LegacyBaseField")]     // on its BASE — a closure that stops at the leaf misses it
    [InlineData("LegacyBaseFlag")]      // non-string, to prove the walk is not name-shaped
    [InlineData("LegacyEnvelopeField")] // on the response envelope itself
    public async Task Not_Flag_Obsolete_Property_Returned_By_A_Minimal_Api_Route_For_AnalyzeAsync(string member)
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains(member));
    }

    [Fact]
    public async Task Not_Flag_Obsolete_Property_Bound_By_The_Model_Binder_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("LegacyQueryField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Still_Flag_Obsolete_Property_On_A_Type_No_Route_Returns_For_AnalyzeAsync()
    {
        // The negative control: nothing seeds OrphanPayload, so the finding must
        // survive. Without this, the suppression tests prove nothing.
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Message.Contains("OrphanField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Still_Flag_When_An_Application_Defined_MapGet_Is_Not_AspNet_Route_For_AnalyzeAsync()
    {
        // FakeRouter.MapGet is a plain method that happens to share the name. The
        // semantic gate must reject it, so FakeRouterResponse stays reportable.
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Message.Contains("FakeLegacyField", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // F3b — enum ordinals. A reference count says nothing about whether the
    // number is still on the wire.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Bravo")] // type argument of a JsonConverter-derived type
    [InlineData("Two")]  // type argument of Enum.GetValues<T>()
    public async Task Not_Flag_Obsolete_Enum_Member_Whose_Ordinal_Is_A_Contract_For_AnalyzeAsync(string member)
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains(member));
    }

    [Fact]
    public async Task Report_Unbound_Obsolete_Enum_Member_As_Advisory_Naming_The_Renumbering_Hazard_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var finding = findings.Should().ContainSingle(f =>
            f.RuleId == "SNP0018" && f.Message.Contains("Second", StringComparison.Ordinal)).Subject;

        finding.Certainty.Should().Be(CertaintyTier.Advisory);
        finding.Message.Should().Contain("renumbers");
    }

    // -----------------------------------------------------------------------
    // F3c — extension holders are invoked through their methods, never through
    // their own name.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Not_Flag_Obsolete_Static_Extension_Holder_Whose_Method_Is_Used_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // Quoted so this cannot also match DeadLegacyStringExtensions.
        findings.Should().NotContain(f => f.RuleId == "SNP0018" && f.Message.Contains("'LegacyStringExtensions'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Still_Flag_Obsolete_Static_Extension_Holder_With_No_Used_Method_For_AnalyzeAsync()
    {
        var findings = await new ObsoleteMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0018"
            && f.Message.Contains("'DeadLegacyStringExtensions'", StringComparison.Ordinal));
    }
}
