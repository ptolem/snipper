namespace Snipper.Analysis;

using NuGet.Versioning;

/// <summary>
/// Decides whether a direct PackageReference is already supplied transitively by
/// another direct reference at a sufficient version. BFS over the per-TFM dependency
/// DAG from every other direct reference, tracking the strongest (highest) minimum
/// version each closure requires of the candidate. O(V+E) per candidate.
/// </summary>
internal static class TransitiveRedundancyEvaluator
{
    /// <summary>
    /// True when another direct reference's transitive closure requires
    /// <paramref name="directPackage"/> at a minimum version greater than or equal to
    /// its resolved version — removing the direct edge would not change the graph.
    /// <paramref name="providingParentId"/> receives the direct reference whose closure
    /// supplies the candidate (for the finding message).
    /// </summary>
    public static bool TryFindProvidingParent(
        LockFileModel model,
        string tfm,
        ResolvedPackage directPackage,
        IReadOnlyCollection<string> otherDirectPackageIds,
        out string? providingParentId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(directPackage);
        ArgumentNullException.ThrowIfNull(otherDirectPackageIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tfm);

        providingParentId = null;
        if (!model.PackagesByTfm.TryGetValue(tfm, out var packages))
        {
            return false;
        }

        foreach (var rootId in otherDirectPackageIds)
        {
            if (rootId.Equals(directPackage.Id, StringComparison.OrdinalIgnoreCase) || !packages.ContainsKey(rootId))
            {
                continue;
            }

            NuGetVersion? strongestRequirement = null;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
            var queue = new Queue<string>([rootId]);

            while (queue.Count > 0)
            {
                if (!packages.TryGetValue(queue.Dequeue(), out var current))
                {
                    continue;
                }

                foreach (var (dependencyId, dependencyRange) in current.Dependencies)
                {
                    if (dependencyId.Equals(directPackage.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        // An edge into the candidate: record the strongest requirement;
                        // no need to traverse into the candidate itself.
                        var requiredMinimum = dependencyRange.MinVersion;
                        if (requiredMinimum is not null
                            && (strongestRequirement is null || requiredMinimum.CompareTo(strongestRequirement) > 0))
                        {
                            strongestRequirement = requiredMinimum;
                        }

                        continue;
                    }

                    if (visited.Add(dependencyId))
                    {
                        queue.Enqueue(dependencyId);
                    }
                }
            }

            if (strongestRequirement is not null && strongestRequirement.CompareTo(directPackage.Version) >= 0)
            {
                providingParentId = rootId;
                return true;
            }
        }

        return false;
    }
}
