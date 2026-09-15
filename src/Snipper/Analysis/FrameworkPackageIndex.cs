namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NuGet.Versioning;

/// <summary>
/// Packages that ship inside a shared framework, keyed by package id. Loaded from the
/// PackageOverrides.txt data files inside the installed targeting packs — the same
/// source NuGet's own package pruning uses, so no curated list to maintain.
/// </summary>
internal sealed record FrameworkPackageOverrides(FrozenDictionary<string, NuGetVersion> VersionByPackageId);

internal static class TargetingPackLocator
{
    private const string CorePackName = "Microsoft.NETCore.App.Ref";
    private const string AspNetCorePackName = "Microsoft.AspNetCore.App.Ref";

    /// <summary>
    /// Derives the dotnet root from the running runtime directory
    /// (…/dotnet/shared/Microsoft.NETCore/x.y.z → …/dotnet) and returns its packs folder,
    /// or null when the layout is absent (e.g. non-standard install).
    /// </summary>
    public static string? FindPacksDirectory()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return null;
        }

        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        var packsDirectory = Path.Combine(dotnetRoot, "packs");
        return Directory.Exists(packsDirectory) ? packsDirectory : null;
    }

    /// <summary>
    /// PackageOverrides.txt paths for the highest installed pack whose major version
    /// matches the TFM major ("net10.0" → 10). The ASP.NET Core pack is included only
    /// for web projects. Empty when no matching pack is installed — callers must treat
    /// the project as unjudgeable rather than "nothing is inbox".
    /// </summary>
    public static IReadOnlyList<string> FindOverrideFiles(string packsDirectory, string tfmMoniker, bool includeAspNetCore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tfmMoniker);

        var major = ParseTfmMajor(tfmMoniker);
        if (major is null)
        {
            return [];
        }

        var files = new List<string>(2);
        AddOverrideFile(files, packsDirectory, CorePackName, major.Value);
        if (includeAspNetCore)
        {
            AddOverrideFile(files, packsDirectory, AspNetCorePackName, major.Value);
        }

        return files;
    }

    private static void AddOverrideFile(List<string> files, string packsDirectory, string packName, int tfmMajor)
    {
        var packDirectory = Path.Combine(packsDirectory, packName);
        if (!Directory.Exists(packDirectory))
        {
            return;
        }

        // Directory names may carry prerelease suffixes ("10.0.0-rc.2…"), which
        // Version.TryParse rejects — parse the leading numeric triple instead.
        string? bestFile = null;
        Version? bestVersion = null;
        foreach (var versionDirectory in Directory.EnumerateDirectories(packDirectory))
        {
            var match = Regex.Match(Path.GetFileName(versionDirectory), @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)");
            if (!match.Success
                || !int.TryParse(match.Groups["major"].Value, out var major)
                || !int.TryParse(match.Groups["minor"].Value, out var minor)
                || !int.TryParse(match.Groups["patch"].Value, out var patch)
                || major != tfmMajor)
            {
                continue;
            }

            var overridesFile = Path.Combine(versionDirectory, "data", "PackageOverrides.txt");
            if (!File.Exists(overridesFile))
            {
                continue;
            }

            var version = new Version(major, minor, patch);
            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                bestFile = overridesFile;
            }
        }

        if (bestFile is not null)
        {
            files.Add(bestFile);
        }
    }

    private static int? ParseTfmMajor(string tfmMoniker)
    {
        // Modern TFMs only ("net5.0" and up); netstandard/netcoreapp/net4x predate
        // PackageOverrides.txt semantics for this check and are skipped.
        var match = Regex.Match(tfmMoniker, @"^net(?<major>\d+)(?:\.|[-]|$)");
        return match.Success && int.TryParse(match.Groups["major"].Value, out var major) ? major : null;
    }
}

internal static class FrameworkPackageIndex
{
    /// <summary>
    /// Parses PackageOverrides.txt lines ("PackageId|Version"), unioned across files;
    /// when packs disagree, the highest version wins (conservative — a higher inbox
    /// version makes more declared versions redundant).
    /// </summary>
    public static FrameworkPackageOverrides Load(IReadOnlyList<string> overrideFilePaths)
    {
        ArgumentNullException.ThrowIfNull(overrideFilePaths);

        var map = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in overrideFilePaths)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                var separator = line.IndexOf('|', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var packageId = line[..separator].Trim();
                if (packageId.Length == 0 || !NuGetVersion.TryParse(line[(separator + 1)..].Trim(), out var version))
                {
                    continue;
                }

                if (!map.TryGetValue(packageId, out var existing) || version > existing)
                {
                    map[packageId] = version;
                }
            }
        }

        return new FrameworkPackageOverrides(map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the package ships inbox at a version greater than or equal to the
    /// declared minimum — the reference contributes nothing the framework lacks.
    /// </summary>
    public static bool IsInbox(FrameworkPackageOverrides overrides, string packageId, NuGetVersion declaredMinVersion)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(declaredMinVersion);

        return overrides.VersionByPackageId.TryGetValue(packageId, out var inboxVersion)
            && declaredMinVersion.CompareTo(inboxVersion) <= 0;
    }
}
