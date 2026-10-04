namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

/// <summary>
/// Pins the one-to-many project-path mapping in <see cref="UnreferencedPackageAnalyser"/>.
///
/// A csproj path is NOT unique within a <see cref="Solution"/>. A multi-targeted project
/// surfaces once per TFM, and a project reached through two referencing paths can appear
/// twice. Keying a frozen dictionary on the path threw
/// <c>ArgumentException: An item with the same key has already been added</c> on real
/// solutions, and because it threw inside the analyser fan-out it took the whole run down
/// rather than degrading to a missed finding.
///
/// These tests build the duplicate-path shape directly rather than depending on a fixture
/// that happens to restore a particular way, so the regression is pinned to the shape itself.
/// </summary>
public sealed class DuplicateProjectPathShould
{
    private const string SharedProjectPath = @"C:\repo\Shared\Shared.csproj";

    /// <summary>
    /// Two <see cref="Project"/> instances sharing one path, exactly as a multi-targeted
    /// project appears to MSBuildWorkspace. They differ only by assembly name, which is what
    /// makes them distinct compilations.
    /// </summary>
    private static Solution BuildDuplicatePathShape()
    {
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        // ProjectId.CreateNewId takes only a display name; uniqueness comes from the call.
        var net8 = ProjectId.CreateNewId("Shared");
        var net10 = ProjectId.CreateNewId("Shared");
        var consumer = ProjectId.CreateNewId("Consumer");

        return workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(net8, VersionStamp.Create(), "Shared", "Shared.net8.0", LanguageNames.CSharp,
                filePath: SharedProjectPath)
                .WithMetadataReferences([corlib]))
            .AddProject(ProjectInfo.Create(net10, VersionStamp.Create(), "Shared", "Shared.net10.0", LanguageNames.CSharp,
                filePath: SharedProjectPath)
                .WithMetadataReferences([corlib]))
            .AddProject(ProjectInfo.Create(consumer, VersionStamp.Create(), "Consumer", "Consumer", LanguageNames.CSharp,
                filePath: @"C:\repo\Consumer\Consumer.csproj")
                .WithMetadataReferences([corlib]))
            // The consumer references BOTH instances of the same path - the shape that made
            // the old single-valued dictionary throw.
            .AddProjectReference(consumer, new ProjectReference(net8))
            .AddProjectReference(consumer, new ProjectReference(net10));
    }

    [Fact]
    public async Task Not_Throw_When_Two_Projects_Share_One_File_Path_For_AnalyzeAsync()
    {
        var solution = BuildDuplicatePathShape();

        // Precondition: the shape that used to crash - one path, two Project instances.
        solution.Projects
            .Where(static p => p.FilePath is not null)
            .Select(static p => p.FilePath!)
            .Where(static path => string.Equals(path, SharedProjectPath, StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(2, "the regression depends on two projects sharing one path");

        var act = async () => await new UnreferencedPackageAnalyser().AnalyzeAsync(solution, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The usage cache unions every TFM instance of a path into one
    /// <c>UsedAssemblyNames</c> set, so the referenced-assembly test must union the same
    /// way. Picking one TFM arbitrarily would let a reference look unused because the
    /// instance we happened to pick does not use the assembly while its sibling does.
    /// </summary>
    [Fact]
    public async Task Union_Tfm_Instances_When_Testing_A_Referenced_Assembly_For_AnalyzeAsync()
    {
        var solution = BuildDuplicatePathShape();
        var consumer = solution.Projects.Single(static p => p.Name == "Consumer");

        var act = async () => await new UnreferencedPackageAnalyser().AnalyzeAsync(solution, CancellationToken.None);

        // Must complete, and must not report the consumer's own references as unused purely
        // because one TFM instance of the shared path looked empty.
        var findings = await act();

        findings.Should().NotContain(f => f.FilePath.Contains("Consumer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A shared path must be analysed once, not once per TFM instance: the analyser's
    /// per-csproj work is keyed off the path, so a second pass would double-report.
    /// </summary>
    [Fact]
    public async Task Analyse_A_Duplicated_Path_Once_Not_Once_Per_Tfm_For_AnalyzeAsync()
    {
        var solution = BuildDuplicatePathShape();

        var findings = await new UnreferencedPackageAnalyser().AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().OnlyHaveUniqueItems(
            "two findings for one csproj would mean the shared path was analysed twice");
    }
}