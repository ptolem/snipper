namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Snipper.Models;

/// <summary>
/// SNP0003 / SNP0004 — Flags PackageReference and ProjectReference items whose
/// assemblies contribute zero symbols used by the referencing project.
/// Tier 2 (High): MSBuild item evaluation + cross-assembly symbol usage analysis.
/// Packages contributing no compile-time assemblies (analysers, build tasks,
/// source generators) are deliberately skipped — they show zero usage by design.
/// Soundness gate: a package whose own assemblies are unused is still kept when its
/// exclusive transitive subtree (packages only it can reach in the lock-file graph)
/// contributes used symbols — removing the reference would evict those packages and
/// break compilation (e.g. CSharp.Workspaces rooting the CSharp compiler assembly).
/// </summary>
public sealed class UnreferencedPackageAnalyser : IWorkspaceAnalyser
{
    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();

        // Frozen: built once per run, queried for every ProjectReference in every project.
        var projectsByPath = solution.Projects
            .Where(static p => p.FilePath is not null)
            .ToFrozenDictionary(static p => p.FilePath!, static p => p, StringComparer.OrdinalIgnoreCase);

        // Multi-targeted projects surface as one Project per TFM. Merge them: reference
        // removal affects the csproj (every TFM), and assembly identity is compared by
        // name because each TFM compilation binds its own symbol instances.
        var projectGroups = solution.Projects
            .Where(static p => p.SupportsCompilation && p.FilePath is not null && File.Exists(p.FilePath))
            .GroupBy(static p => p.FilePath!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in projectGroups)
        {
            var projectFilePath = group.Key;
            var projectName = group.First().Name;

            var projectFile = ProjectFileReader.Read(projectFilePath);
            if (projectFile is null || (projectFile.PackageReferences.Length == 0 && projectFile.ProjectReferences.Length == 0))
            {
                continue;
            }

            progress?.Invoke($"UnreferencedPackageAnalyser: scanning {projectName}");

            var usedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assemblyNamesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in group)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    continue;
                }

                var usedAssemblies = await SymbolUsageCollector.CollectUsedAssembliesAsync(project, cancellationToken).ConfigureAwait(false);
                foreach (var usedAssembly in usedAssemblies)
                {
                    usedAssemblyNames.Add(usedAssembly.Name);
                }

                foreach (var pair in PackageAssemblyUsage.MapAssembliesToPackages(compilation))
                {
                    if (!assemblyNamesByPackage.TryGetValue(pair.Key, out var names))
                    {
                        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        assemblyNamesByPackage[pair.Key] = names;
                    }

                    foreach (var assembly in pair.Value)
                    {
                        names.Add(assembly.Name);
                    }
                }
            }

            var lockModel = new NuGetLockFileReader().Read(projectFilePath);

            foreach (var package in projectFile.PackageReferences)
            {
                if (package.CompileAssetsExcluded)
                {
                    continue;
                }

                if (!assemblyNamesByPackage.TryGetValue(package.Id, out var packageAssemblyNames))
                {
                    // Build-only/analyser/source-generator package — cannot judge statically.
                    continue;
                }

                if (packageAssemblyNames.Any(usedAssemblyNames.Contains))
                {
                    continue;
                }

                // Own assemblies unused — but the reference may still root a transitive
                // subtree the project depends on.
                if (lockModel is not null && IsSubtreeLoadBearing(lockModel, projectFile, package.Id, assemblyNamesByPackage, usedAssemblyNames))
                {
                    continue;
                }

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0003",
                    Title: "Unreferenced Package",
                    Message: $"Package '{package.Id}' contributes assemblies but no symbol from it is used in project '{projectName}'.",
                    Certainty: CertaintyTier.High,
                    Category: FindingCategory.UnreferencedPackage,
                    FilePath: projectFilePath,
                    LineNumber: package.LineNumber,
                    CharacterOffset: 1,
                    Symbol: null));
            }

            foreach (var projectReference in projectFile.ProjectReferences)
            {
                if (projectReference.ReferenceOutputAssemblyDisabled || projectReference.IsAnalyser)
                {
                    continue;
                }

                if (!projectsByPath.TryGetValue(projectReference.FullPath, out var referencedProject))
                {
                    continue;
                }

                var referencedCompilation = await referencedProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (referencedCompilation is null)
                {
                    continue;
                }

                if (!usedAssemblyNames.Contains(referencedCompilation.Assembly.Name))
                {
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0004",
                        Title: "Unreferenced Project Reference",
                        Message: $"Project reference '{Path.GetFileNameWithoutExtension(projectReference.FullPath)}' is declared but no symbol from it is used in project '{projectName}'.",
                        Certainty: CertaintyTier.High,
                        Category: FindingCategory.UnreferencedProject,
                        FilePath: projectFilePath,
                        LineNumber: projectReference.LineNumber,
                        CharacterOffset: 1,
                        Symbol: null));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// True when removing <paramref name="packageId"/> would evict a transitively supplied
    /// package whose assemblies the project uses. A subtree package only counts when no
    /// other direct reference can reach it — shared packages survive the removal.
    /// </summary>
    private static bool IsSubtreeLoadBearing(
        LockFileModel lockModel,
        ProjectFileInfo projectFile,
        string packageId,
        Dictionary<string, HashSet<string>> assemblyNamesByPackage,
        HashSet<string> usedAssemblyNames)
    {
        foreach (var packagesByTfm in lockModel.PackagesByTfm.Values)
        {
            if (!packagesByTfm.TryGetValue(packageId, out var root))
            {
                continue;
            }

            foreach (var subtreeId in ComputeExclusiveSubtreeIds(packagesByTfm, root, projectFile))
            {
                if (assemblyNamesByPackage.TryGetValue(subtreeId, out var names)
                    && names.Any(usedAssemblyNames.Contains))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> ComputeExclusiveSubtreeIds(
        FrozenDictionary<string, ResolvedPackage> packages,
        ResolvedPackage root,
        ProjectFileInfo projectFile)
    {
        // Reachable from any OTHER direct reference: these packages survive removal.
        var reachableFromOthers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<string>();
        foreach (var direct in projectFile.PackageReferences)
        {
            if (direct.Id.Equals(root.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (packages.TryGetValue(direct.Id, out var other))
            {
                foreach (var dependencyId in other.Dependencies.Keys)
                {
                    frontier.Enqueue(dependencyId);
                }
            }
        }

        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            if (!reachableFromOthers.Add(id))
            {
                continue;
            }

            if (packages.TryGetValue(id, out var package))
            {
                foreach (var dependencyId in package.Dependencies.Keys)
                {
                    frontier.Enqueue(dependencyId);
                }
            }
        }

        // Reachable only from the root's subtree — removing the root evicts them. Traversal
        // stops at shared packages: their own subtrees survive via the other reference.
        var exclusive = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependencyId in root.Dependencies.Keys)
        {
            frontier.Enqueue(dependencyId);
        }

        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            if (!visited.Add(id) || reachableFromOthers.Contains(id))
            {
                continue;
            }

            exclusive.Add(id);
            if (packages.TryGetValue(id, out var package))
            {
                foreach (var dependencyId in package.Dependencies.Keys)
                {
                    frontier.Enqueue(dependencyId);
                }
            }
        }

        return exclusive;
    }
}
