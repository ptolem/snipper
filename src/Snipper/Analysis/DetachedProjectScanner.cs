namespace Snipper.Analysis;

using System.Collections.Frozen;

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
