namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;

/// <summary>
/// Filesystem sweep for .csproj files under a root directory that are not part of the
/// loaded workspace — projects removed from the solution but never deleted. Build
/// output and VCS/dependency folders are pruned; inaccessible directories are skipped.
/// </summary>
internal static class DetachedProjectScanner
{
    private static readonly FrozenSet<string> PrunedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FindUnattachedProjectFiles(string rootDirectory, FrozenSet<string> loadedProjectPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(loadedProjectPaths);

        var unattached = new List<string>();
        if (Directory.Exists(rootDirectory))
        {
            Scan(rootDirectory, loadedProjectPaths, unattached);
        }

        return unattached;
    }

    /// <summary>
    /// The directory a detached sweep should start from: the solution file's own
    /// directory, or the common ancestor of the loaded projects when the solution
    /// has no file (a bare project, a filtered workspace).
    /// <para>
    /// Shared by <see cref="OrphanProjectAnalyser"/> and
    /// <see cref="UnreferencedPackageAnalyser"/>, which both need to reason about
    /// projects the workspace never loaded. Returns null when no single root
    /// contains every loaded project — a sweep from an arbitrary root would
    /// walk an unbounded filesystem.
    /// </para>
    /// </summary>
    public static string? ResolveRootDirectory(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        if (solution.FilePath is { Length: > 0 } solutionPath
            && Path.GetDirectoryName(solutionPath) is { Length: > 0 } solutionDirectory)
        {
            return solutionDirectory;
        }

        string? common = null;
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not { Length: > 0 } projectPath || Path.GetDirectoryName(projectPath) is not { Length: > 0 } projectDirectory)
            {
                continue;
            }

            if (common is null)
            {
                common = projectDirectory;
                continue;
            }

            common = CommonAncestor(common, projectDirectory);
            if (common is null)
            {
                return null;
            }
        }

        return common;
    }

    private static string? CommonAncestor(string first, string second)
    {
        var candidate = first;
        while (candidate is { Length: > 0 })
        {
            if (second.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(second, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    private static void Scan(string directory, FrozenSet<string> loadedProjectPaths, List<string> unattached)
    {
        IReadOnlyList<string> entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (!PrunedDirectoryNames.Contains(Path.GetFileName(entry)))
                {
                    Scan(entry, loadedProjectPaths, unattached);
                }

                continue;
            }

            if (!entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(entry);
            if (!loadedProjectPaths.Contains(fullPath))
            {
                unattached.Add(fullPath);
            }
        }
    }
}
