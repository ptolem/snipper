namespace Snipper.Analysis;

using System.Collections.Frozen;
using NuGet.Versioning;

/// <summary>
/// A resolved package node from project.assets.json. Dependencies carry each edge's
/// version range: redundancy is only sound when the transitively-required minimum is
/// at least the resolved direct version, so ids alone are insufficient.
/// </summary>
internal sealed record ResolvedPackage(
    string Id,
    NuGetVersion Version,
    FrozenDictionary<string, VersionRange> Dependencies);

/// <summary>Per-TFM resolved package graphs, keyed by short folder name ("net10.0").</summary>
internal sealed record LockFileModel(
    FrozenDictionary<string, FrozenDictionary<string, ResolvedPackage>> PackagesByTfm);

internal interface ILockFileReader
{
    /// <summary>
    /// Reads obj/project.assets.json next to the given project file. Returns null on
    /// missing/malformed file or restore drift — never throws.
    /// </summary>
    LockFileModel? Read(string projectFilePath);
}
