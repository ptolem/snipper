namespace Snipper.Tests;

using System.Collections.Frozen;
using FluentAssertions;
using NuGet.Versioning;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class FrameworkInboxPackageAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Inbox_Package_At_Lower_Declared_Version_For_AnalyzeAsync()
    {
        var findings = await new FrameworkInboxPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0013"
            && f.Certainty == CertaintyTier.High
            && f.Message.Contains("System.Text.Json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Non_Inbox_Package_For_AnalyzeAsync()
    {
        var findings = await new FrameworkInboxPackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0013" && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0013" && f.Message.Contains("Serilog", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_Packs_Directory_On_Dev_Machine_For_Locator()
    {
        var packsDirectory = TargetingPackLocator.FindPacksDirectory();

        packsDirectory.Should().NotBeNull();
        packsDirectory.Should().ContainEquivalentOf("packs");
    }

    [Fact]
    public void Load_Overrides_From_Real_Targeting_Pack_For_Index()
    {
        var packsDirectory = TargetingPackLocator.FindPacksDirectory();
        packsDirectory.Should().NotBeNull();

        var files = TargetingPackLocator.FindOverrideFiles(packsDirectory!, "net10.0", includeAspNetCore: false);
        files.Should().NotBeEmpty();

        var overrides = FrameworkPackageIndex.Load(files);

        overrides.VersionByPackageId.Should().ContainKey("System.Text.Json");
        overrides.VersionByPackageId["System.Text.Json"].CompareTo(new NuGetVersion(8, 0, 5)).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Return_No_Override_Files_For_Unknown_Tfm_For_Locator()
    {
        var packsDirectory = TargetingPackLocator.FindPacksDirectory();
        packsDirectory.Should().NotBeNull();

        TargetingPackLocator.FindOverrideFiles(packsDirectory!, "net99.0", includeAspNetCore: false).Should().BeEmpty();
    }

    [Fact]
    public void Match_Inbox_Package_At_Or_Below_Override_Version_For_IsInbox()
    {
        var overrides = CreateOverrides(("System.Text.Json", new NuGetVersion(10, 0, 0)));

        FrameworkPackageIndex.IsInbox(overrides, "System.Text.Json", new NuGetVersion(8, 0, 5)).Should().BeTrue();
        FrameworkPackageIndex.IsInbox(overrides, "System.Text.Json", new NuGetVersion(10, 0, 0)).Should().BeTrue();
    }

    [Fact]
    public void Not_Match_When_Declared_Version_Exceeds_Inbox_Or_Package_Is_Absent_For_IsInbox()
    {
        var overrides = CreateOverrides(("System.Text.Json", new NuGetVersion(10, 0, 0)));

        FrameworkPackageIndex.IsInbox(overrides, "System.Text.Json", new NuGetVersion(11, 0, 0)).Should().BeFalse();
        FrameworkPackageIndex.IsInbox(overrides, "Humanizer.Core", new NuGetVersion(2, 14, 1)).Should().BeFalse();
    }

    private static FrameworkPackageOverrides CreateOverrides(params (string Id, NuGetVersion Version)[] entries)
    {
        var map = entries.ToFrozenDictionary(static e => e.Id, static e => e.Version, StringComparer.OrdinalIgnoreCase);
        return new FrameworkPackageOverrides(map);
    }
}
