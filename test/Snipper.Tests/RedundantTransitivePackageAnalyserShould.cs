namespace Snipper.Tests;

using System.Collections.Frozen;
using FluentAssertions;
using NuGet.Versioning;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

public sealed class RedundantTransitivePackageAnalyserShould
{
    private const string Tfm = "net10.0";

    [Fact]
    public void Flag_Direct_Reference_Supplied_Transitively_At_Same_Or_Higher_Version_For_Evaluator()
    {
        var model = CreateModel(
            Pkg("Serilog", "4.0.0"),
            Pkg("Serilog.Sinks.Console", "6.0.0", ("Serilog", "4.0.0")));

        var found = TransitiveRedundancyEvaluator.TryFindProvidingParent(
            model, Tfm, model.PackagesByTfm[Tfm]["Serilog"], ["Serilog.Sinks.Console"], out var parent);

        found.Should().BeTrue();
        parent.Should().Be("Serilog.Sinks.Console");
    }

    [Fact]
    public void Not_Flag_When_Direct_Version_Exceeds_Transitive_For_Evaluator()
    {
        var model = CreateModel(
            Pkg("Serilog", "4.3.0"),
            Pkg("Serilog.Sinks.Console", "6.0.0", ("Serilog", "4.0.0")));

        TransitiveRedundancyEvaluator.TryFindProvidingParent(
            model, Tfm, model.PackagesByTfm[Tfm]["Serilog"], ["Serilog.Sinks.Console"], out _).Should().BeFalse();
    }

    [Fact]
    public void Not_Flag_When_Not_Transitively_Reachable_For_Evaluator()
    {
        var model = CreateModel(
            Pkg("Serilog", "4.0.0"),
            Pkg("Serilog.Sinks.Console", "6.0.0"));

        TransitiveRedundancyEvaluator.TryFindProvidingParent(
            model, Tfm, model.PackagesByTfm[Tfm]["Serilog"], ["Serilog.Sinks.Console"], out _).Should().BeFalse();
    }

    [Fact]
    public void Return_Null_When_Assets_File_Is_Missing_For_Reader()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Snipper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var fakeProject = Path.Combine(tempDirectory, "Fake.csproj");
        File.WriteAllText(fakeProject, "<Project />");

        try
        {
            NuGetLockFileReader.Read(fakeProject).Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static LockFileModel CreateModel(params ResolvedPackage[] packages)
    {
        var perTfm = packages.ToFrozenDictionary(static p => p.Id, StringComparer.OrdinalIgnoreCase);
        return new LockFileModel(new Dictionary<string, FrozenDictionary<string, ResolvedPackage>>
        {
            [Tfm] = perTfm,
        }.ToFrozenDictionary());
    }

    private static ResolvedPackage Pkg(string id, string version, params (string DependencyId, string MinimumVersion)[] dependencies)
    {
        var dependencyMap = dependencies.ToFrozenDictionary(
            static d => d.DependencyId,
            static d => VersionRange.Parse(d.MinimumVersion),
            StringComparer.OrdinalIgnoreCase);
        return new ResolvedPackage(id, NuGetVersion.Parse(version), dependencyMap);
    }
}

[Collection("SampleSolution")]
public sealed class RedundantTransitivePackageAnalyserIntegrationShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Redundant_Package_For_AnalyzeAsync()
    {
        var findings = await new RedundantTransitivePackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0012"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("Serilog", StringComparison.Ordinal)
            && f.Message.Contains("Serilog.Sinks.Console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Providing_Parent_For_AnalyzeAsync()
    {
        var findings = await new RedundantTransitivePackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0012" && f.Message.Contains("Package 'Serilog.Sinks.Console'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Package_That_Snp0003_Already_Flags_For_AnalyzeAsync()
    {
        var findings = await new RedundantTransitivePackageAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0012" && f.Message.Contains("Humanizer.Core", StringComparison.Ordinal));
    }
}
