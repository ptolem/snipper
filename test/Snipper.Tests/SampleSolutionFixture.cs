namespace Snipper.Tests;

using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

/// <summary>
/// Copies the SampleApp fixture codebase to an isolated temp directory, restores
/// it, and opens it once per test run via MSBuildWorkspace. All analyser test
/// classes share this workspace through the "SampleSolution" collection.
/// </summary>
public sealed class SampleSolutionFixture : IAsyncLifetime
{
    private MSBuildWorkspace? _workspace;

    public string SampleAppPath { get; private set; } = string.Empty;

    public Solution Solution { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var sourceAssets = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp");
        Assert.True(Directory.Exists(sourceAssets), $"TestAssets were not copied to the output directory: {sourceAssets}");

        SampleAppPath = Path.Combine(Path.GetTempPath(), "Snipper.Tests", Guid.NewGuid().ToString("N"), "SampleApp");
        CopyDirectory(sourceAssets, SampleAppPath);

        foreach (var projectFile in Directory.EnumerateFiles(SampleAppPath, "*.csproj", SearchOption.AllDirectories))
        {
            RunDotNetRestore(projectFile);
        }

        _workspace = MSBuildWorkspace.Create();

        var appProject = Path.Combine(SampleAppPath, "App", "App.csproj");
        // Opening App pulls CoreLib and OrphanLib into the workspace via project references.
        await _workspace.OpenProjectAsync(appProject);
        Solution = _workspace.CurrentSolution;
    }

    public async Task DisposeAsync()
    {
        _workspace?.Dispose();

        try
        {
            if (Directory.Exists(SampleAppPath))
            {
                Directory.Delete(SampleAppPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp directory is per-run unique.
        }

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

[CollectionDefinition("SampleSolution")]
public sealed class SampleSolutionCollection : ICollectionFixture<SampleSolutionFixture>;
