namespace Snipper.Analysis;

using System.Collections.Frozen;
using NuGet.Common;
using NuGet.ProjectModel;
using NuGet.Versioning;

/// <summary><see cref="ILockFileReader"/> backed by NuGet.ProjectModel's LockFileFormat.</summary>
internal sealed class NuGetLockFileReader : ILockFileReader
{
    public LockFileModel? Read(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);

        var projectDirectory = Path.GetDirectoryName(projectFilePath);
        if (projectDirectory is null)
        {
            return null;
        }

        var assetsFilePath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assetsFilePath))
        {
            return null;
        }

        LockFile lockFile;
        try
        {
            lockFile = new LockFileFormat().Read(assetsFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }

        var targets = new Dictionary<string, FrozenDictionary<string, ResolvedPackage>>();
        foreach (var target in lockFile.Targets)
        {
            var tfm = target.TargetFramework?.GetShortFolderName();
            if (string.IsNullOrEmpty(tfm))
            {
                continue;
            }

            var packages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in target.Libraries)
            {
                // Only real packages participate; project/workload entries have no version graph.
                if (!string.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase) || library.Version is null)
                {
                    continue;
                }

                var dependencies = library.Dependencies
                    .Where(static d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToFrozenDictionary(
                        static d => d.Id,
                        static d => d.VersionRange ?? VersionRange.None,
                        StringComparer.OrdinalIgnoreCase);

                packages[library.Name] = new ResolvedPackage(library.Name, library.Version, dependencies);
            }

            targets[tfm] = packages.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        return targets.Count == 0 ? null : new LockFileModel(targets.ToFrozenDictionary());
    }
}
