namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnreferencedPackageAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_PackageReference_As_High_When_No_Package_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var analyser = new UnreferencedPackageAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0003"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_ProjectReference_As_High_When_No_Referenced_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var analyser = new UnreferencedPackageAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0004"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("OrphanLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_ProjectReference_When_Referenced_Symbols_Are_Used_For_AnalyzeAsync()
    {
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0004" && f.Message.Contains("CoreLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Package_When_Its_Exclusive_Transitive_Subtree_Is_Used_For_AnalyzeAsync()
    {
        // Microsoft.Extensions.Logging.Console's own assembly is unused, but removing the
        // direct reference would evict its exclusive subtree (Logging.Abstractions), whose
        // ILogger type IS used — the reference is load-bearing and must not be flagged.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0003" && f.Message.Contains("Microsoft.Extensions.Logging.Console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Package_When_A_Consumer_Project_Uses_It_Transitively_For_AnalyzeAsync()
    {
        // CoreLib declares Serilog but never uses it; App (which references CoreLib)
        // uses Serilog through the transitive package flow. Hub references flow to
        // consumers by default — removal would break App (milkrun FP, 1.6.1).
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0003"
            && f.Message.Contains("'Serilog'", StringComparison.Ordinal)
            && f.Message.Contains("CoreLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Package_When_PrivateAssets_Blocks_The_Consumer_Flow_For_AnalyzeAsync()
    {
        // Same hub shape as Serilog, but PrivateAssets=all stops the package from
        // flowing to consumers — App's usage must not save CoreLib's reference.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0003"
            && f.Message.Contains("Serilog.Sinks.Console", StringComparison.Ordinal)
            && f.Message.Contains("CoreLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cap_Package_At_Moderate_When_An_Unloaded_Project_On_Disk_Could_Consume_It_For_AnalyzeAsync()
    {
        // DetachedLib exists on disk and references CoreLib but is never opened into
        // the workspace, so its symbol usage cannot be analysed. "No consumer uses
        // this package" is unverifiable while it exists — the milkrun
        // Microsoft.AspNetCore.Mvc.NewtonsoftJson finding. High would overstate the
        // evidence; dropping the finding would hide a real candidate.
        var analyser = new UnreferencedPackageAnalyser();

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var capped = findings.Should().ContainSingle(f =>
            f.RuleId == "SNP0003"
            && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal)
            && f.FilePath.Contains("CoreLib", StringComparison.Ordinal)).Subject;

        capped.Certainty.Should().Be(CertaintyTier.Moderate);
    }

    [Fact]
    public async Task Name_The_Unloaded_Consumer_In_A_Capped_Package_Finding_For_AnalyzeAsync()
    {
        // A capped tier that does not say why leaves the reader guessing. The
        // message has to point at the project to add or remove.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0003"
            && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal)
            && f.Message.Contains("DetachedLib", StringComparison.Ordinal)
            && f.Message.Contains("may still use it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keep_An_Unimpacted_Package_At_High_While_Another_Has_An_Unloaded_Consumer_For_AnalyzeAsync()
    {
        // The cap is per-project, not global: Serilog.Sinks.Console is declared by
        // the SAME CoreLib but PrivateAssets=all keeps it away from every consumer,
        // including the unloaded one, so nothing about it is in doubt.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0003"
            && f.Message.Contains("Serilog.Sinks.Console", StringComparison.Ordinal)
            && f.Certainty == CertaintyTier.High
            && !f.Message.Contains("may still use it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_ProjectReference_When_Its_Transitive_Flow_Is_Used_For_AnalyzeAsync()
    {
        // App → FacadeLib: FacadeLib's own assembly is empty, but the reference
        // carries TransitiveLib onward and App uses TransitCatalog — removal
        // would evict the flow and break App (milkrun FP, 1.6.1).
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0004" && f.Message.Contains("FacadeLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_ProjectReference_When_A_Consumer_Uses_Its_Flow_For_AnalyzeAsync()
    {
        // FacadeLib → TransitiveLib: FacadeLib itself uses nothing, but its
        // consumer App uses TransitiveLib's symbols through the chain.
        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0004" && f.Message.Contains("TransitiveLib", StringComparison.Ordinal));
    }
}
