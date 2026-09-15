namespace Snipper.Tests;

using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

/// <summary>
/// Opens App.csproj plus the deliberately unreferenced DetachedLib/PluginLib projects
/// in one workspace, so SNP0011 can be exercised against loaded projects (in-degree 0),
/// name evidence, and entry-point roots. Separate from the shared SampleApp fixture,
/// which opens App alone (leaving DetachedLib detached-on-disk).
/// </summary>
public sealed class DetachedProjectsFixture : IAsyncLifetime
{
    private MSBuildWorkspace? _workspace;

    public Solution Solution { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var sourceAssets = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp");
        Assert.True(Directory.Exists(sourceAssets), $"TestAssets were not copied to the output directory: {sourceAssets}");

        var sampleAppPath = Path.Combine(Path.GetTempPath(), "Snipper.Tests", Guid.NewGuid().ToString("N"), "SampleApp");
        CopyDirectory(sourceAssets, sampleAppPath);

        foreach (var projectFile in Directory.EnumerateFiles(sampleAppPath, "*.csproj", SearchOption.AllDirectories))
        {
            RunDotNetRestore(projectFile);
        }

        _workspace = MSBuildWorkspace.Create();
        await _workspace.OpenProjectAsync(Path.Combine(sampleAppPath, "App", "App.csproj"));
        await _workspace.OpenProjectAsync(Path.Combine(sampleAppPath, "DetachedLib", "DetachedLib.csproj"));
        await _workspace.OpenProjectAsync(Path.Combine(sampleAppPath, "PluginLib", "PluginLib.csproj"));
        Solution = _workspace.CurrentSolution;
    }

    public async Task DisposeAsync()
    {
        _workspace?.Dispose();
        await Task.CompletedTask;
    }

    private static void RunDotNetRestore(string projectFile)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{projectFile}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(milliseconds: 120_000);
        Assert.True(process.ExitCode == 0, $"dotnet restore failed for {projectFile}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }
}

[CollectionDefinition("DetachedProjects")]
public sealed class DetachedProjectsCollection : ICollectionFixture<DetachedProjectsFixture>;
