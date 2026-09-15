namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;

internal sealed record ProjectGraphNode(
    string FilePath,
    string AssemblyName,
    bool IsEntryPoint,
    bool IsTestProject,
    FrozenSet<string> ReferencedProjectPaths);

internal sealed record ProjectGraph(FrozenDictionary<string, ProjectGraphNode> NodesByPath);

/// <summary>
/// Builds the project reference graph for the loaded solution from csproj facts.
/// Adjacency sets are frozen; orphan detection is an in-degree-0 query — O(V+E),
/// no topological sort needed. Nodes are keyed by normalized full path.
/// </summary>
internal static class ProjectGraphBuilder
{
    public static ProjectGraph Build(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var nodes = new Dictionary<string, ProjectGraphNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not { Length: > 0 } filePath || !File.Exists(filePath))
            {
                continue;
            }

            var info = ProjectFileReader.Read(filePath);
            if (info is null)
            {
                continue;
            }

            var referencedPaths = info.ProjectReferences
                .Select(static r => r.FullPath)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

            var normalizedPath = Path.GetFullPath(filePath);
            nodes[normalizedPath] = new ProjectGraphNode(
                FilePath: normalizedPath,
                AssemblyName: info.AssemblyName,
                IsEntryPoint: IsEntryPoint(info),
                IsTestProject: info.IsTestProject,
                ReferencedProjectPaths: referencedPaths);
        }

        return new ProjectGraph(nodes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsEntryPoint(ProjectFileInfo info)
    {
        if (string.Equals(info.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SDK-style hosts: web, worker, functions, MAUI, Blazor WASM — these projects
        // run standalone and are graph roots even with no inbound references.
        var sdkName = info.SdkName;
        return sdkName is not null
            && (sdkName.Contains(".Web", StringComparison.OrdinalIgnoreCase)
                || sdkName.Contains(".Worker", StringComparison.OrdinalIgnoreCase)
                || sdkName.Contains(".Functions", StringComparison.OrdinalIgnoreCase)
                || sdkName.Contains(".BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)
                || sdkName.Contains(".Maui", StringComparison.OrdinalIgnoreCase));
    }
}
