namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

// Loaded-orphan scenarios: a dedicated workspace that opens App, DetachedLib and
// PluginLib as loaded projects.
[Collection("DetachedProjects")]
public sealed class OrphanProjectAnalyserShould(DetachedProjectsFixture fixture)
{
    [Fact]
    public async Task Flag_Loaded_Project_With_No_Inbound_References_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0011"
            && f.Certainty == CertaintyTier.Moderate
            && f.Message.Contains("DetachedLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Project_With_Inbound_Reference_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0011" && f.Message.Contains("CoreLib", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0011" && f.Message.Contains("OrphanLib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Entry_Point_Project_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0011" && f.Message.Contains("App", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Plugin_Project_When_Assembly_Name_Is_Spelled_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0011" && f.Message.Contains("PluginLib", StringComparison.Ordinal));
    }
}

// Detached-file scenarios: the shared fixture opens App only, leaving DetachedLib on
// disk but unattached to the workspace.
[Collection("SampleSolution")]
public sealed class OrphanProjectDetachedFileShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Detached_Project_File_On_Disk_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0011"
            && f.Certainty == CertaintyTier.Moderate
            && f.FilePath.EndsWith("DetachedLib.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Not_Flag_Files_Under_Obj_Or_Bin_For_AnalyzeAsync()
    {
        var findings = await new OrphanProjectAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0011"
            && (f.FilePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.FilePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
    }
}
