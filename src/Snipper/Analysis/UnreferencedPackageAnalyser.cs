namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Snipper.Models;

/// <summary>
/// SNP0003 / SNP0004 — Flags PackageReference and ProjectReference items whose
/// assemblies contribute zero symbols used by the referencing project OR any
/// project the reference flows to. Tier 2 (High): MSBuild item evaluation +
/// cross-assembly symbol usage analysis. Packages contributing no compile-time
/// assemblies (analysers, build tasks, source generators) are deliberately
/// skipped — they show zero usage by design.
/// Soundness gates (a reference is load-bearing when ANY holds):
/// 1. Exclusive transitive subtree — removing the reference would evict packages
///    only it can reach whose symbols the project uses (e.g. CSharp.Workspaces
///    rooting the compiler assembly).
/// 2. Transitive consumer closure (1.6.1, monorepo FP wave) — SDK-style
///    references flow transitively to downstream consumers by default, so a hub
///    reference unused within its declaring project is frequently consumed by
///    the project's dependents (global usings with no direct reference of their
///    own). PrivateAssets=all stops the flow: consumer usage does not count.
/// 3. Upstream exclusive flow (1.6.1) — removing a ProjectReference evicts not
///    just the referenced assembly but the project/package assemblies that flow
///    through it; when the project uses one of those (and cannot reach it via
///    any other reference), the reference is load-bearing.
/// 4. Unloaded consumers (F6) — gates 2 and 3 reason over the loaded workspace
///    only. A .csproj on disk but absent from the solution is a real consumer
///    that cannot be analysed, so "no consumer uses this" is unverifiable while
///    one exists. The finding is capped at Moderate and names the project.
/// </summary>
public sealed class UnreferencedPackageAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0003", "SNP0004"];

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();

        // Frozen: built once per run, queried for every ProjectReference in every project.
        //
        // A path can map to MORE THAN ONE Project: a multi-targeted csproj surfaces once
        // per TFM, and a project reached through two referencing paths can appear twice.
        // Keying a frozen dictionary on the path threw `ArgumentException: An item with the
        // same key has already been added` on real solutions for exactly that reason, so the
        // mapping is one-to-many. ProjectPackageUsageCache.BuildAsync unions every TFM
        // instance of a path into a single UsedAssemblyNames set, so the assembly-name
        // comparison below unions them the same way rather than picking one arbitrarily.
        var projectsByPath = solution.Projects
            .Where(static p => p.FilePath is not null)
            .GroupBy(static p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<Project>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var usageCache = ProjectPackageUsageCache.Get(solution);

        // Per-csproj facts for every loaded project: the reference graph both
        // closures are computed over. Multi-targeted projects dedupe by path.
        var projectFileInfos = new Dictionary<string, ProjectFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation || project.FilePath is null || !File.Exists(project.FilePath))
            {
                continue;
            }

            if (projectFileInfos.ContainsKey(project.FilePath))
            {
                continue;
            }

            var projectFile = ProjectFileReader.Read(project.FilePath);
            if (projectFile is not null)
            {
                projectFileInfos[project.FilePath] = projectFile;
            }
        }

        var consumersClosure = ComputeConsumersClosure(projectFileInfos);
        var flowMemo = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Projects on disk that the workspace never loaded. They cannot be analysed —
        // no compilation, so no symbol usage — but they are real consumers, and a
        // message that claims "no symbol is used in this project or its consumers"
        // cannot be verified while one exists. The claim is capped, not dropped.
        var detachedConsumers = FindDetachedConsumerPaths(solution, projectFileInfos);

        // Multi-targeted projects surface as one Project per TFM; usage is judged per
        // csproj (the cache unions all TFM instances) so each file is analysed once.
        var analysedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation || project.FilePath is null || !File.Exists(project.FilePath))
            {
                continue;
            }

            if (!analysedPaths.Add(project.FilePath))
            {
                continue;
            }

            if (!projectFileInfos.TryGetValue(project.FilePath, out var projectFile))
            {
                continue;
            }

            if (projectFile.PackageReferences.Length == 0 && projectFile.ProjectReferences.Length == 0)
            {
                continue;
            }

            progress?.Invoke($"UnreferencedPackageAnalyser: scanning {project.Name}");

            var usage = await usageCache.GetAsync(project.FilePath, cancellationToken).ConfigureAwait(false);

            foreach (var package in projectFile.PackageReferences)
            {
                if (package.CompileAssetsExcluded)
                {
                    continue;
                }

                if (!usage.IsUnusedPackage(package.Id))
                {
                    // Used, build-only/analyser (no compile assets), or unjudgeable.
                    continue;
                }

                // Own assemblies unused — but the reference may still root a transitive
                // subtree the project depends on.
                if (usage.LockModel is not null && IsSubtreeLoadBearing(usage.LockModel, projectFile, package.Id, usage))
                {
                    continue;
                }

                // Hub gate: a consumer downstream may use the package (plus its
                // exclusive subtree) through the transitive flow. PrivateAssets=all
                // stops the package at this project — consumer usage is no evidence.
                if (!package.PrivateAssetsAll
                    && await AnyConsumerUsesAsync(
                        project.FilePath,
                        CollectPackageFlowAssemblies(projectFile, package.Id, usage),
                        consumersClosure,
                        usageCache,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0003",
                    Title: "Unreferenced Package",
                    Message: BuildPackageMessage(package.Id, project.Name, project.FilePath, detachedConsumers, package.PrivateAssetsAll),
                    Certainty: CertifyForDetachedConsumers(detachedConsumers, project.FilePath, package.PrivateAssetsAll),
                    Category: FindingCategory.UnreferencedPackage,
                    FilePath: project.FilePath,
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

                if (!projectsByPath.TryGetValue(projectReference.FullPath, out var referencedProjects))
                {
                    continue;
                }

                // Union across every TFM instance, matching how ProjectPackageUsageCache
                // builds UsedAssemblyNames. Testing only one instance would let a package
                // look unreferenced just because the TFM we happened to pick does not use
                // the assembly while another does.
                var referencedAssemblyIsUsed = false;
                foreach (var referencedProject in referencedProjects)
                {
                    var referencedCompilation = await referencedProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                    if (referencedCompilation is null)
                    {
                        continue;
                    }

                    if (usage.UsedAssemblyNames.Contains(referencedCompilation.Assembly.Name))
                    {
                        referencedAssemblyIsUsed = true;
                        break;
                    }
                }

                if (referencedAssemblyIsUsed)
                {
                    continue;
                }

                // Upstream flow: everything this reference carries onward — the
                // referenced assembly plus the project/package assemblies flowing
                // through it. Removing the reference evicts them all.
                var flow = await ComputeFlowAssembliesAsync(
                    projectReference.FullPath,
                    projectFileInfos,
                    usageCache,
                    flowMemo,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken).ConfigureAwait(false);

                // Load-bearing when the project uses a flowed assembly it cannot
                // reach through any other reference (exclusive flow).
                var surviving = await ComputeSurvivingAssembliesAsync(
                    projectFile,
                    projectReference.FullPath,
                    projectFileInfos,
                    usage,
                    usageCache,
                    flowMemo,
                    cancellationToken).ConfigureAwait(false);

                if (flow.Any(assembly => !surviving.Contains(assembly) && usage.UsedAssemblyNames.Contains(assembly)))
                {
                    continue;
                }

                // Hub gate: consumers may use anything in the flow through this
                // project. PrivateAssets=all stops it at this project.
                if (!projectReference.PrivateAssetsAll
                    && await AnyConsumerUsesAsync(
                        project.FilePath,
                        flow,
                        consumersClosure,
                        usageCache,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0004",
                    Title: "Unreferenced Project Reference",
                    Message: BuildProjectReferenceMessage(projectReference.FullPath, project.Name, project.FilePath, detachedConsumers, projectReference.PrivateAssetsAll),
                    Certainty: CertifyForDetachedConsumers(detachedConsumers, project.FilePath, projectReference.PrivateAssetsAll),
                    Category: FindingCategory.UnreferencedProject,
                    FilePath: project.FilePath,
                    LineNumber: projectReference.LineNumber,
                    CharacterOffset: 1,
                    Symbol: null));
            }
        }

        return findings;
    }

    /// <summary>
    /// Loaded projects reachable — directly or transitively — from a <c>.csproj</c> that
    /// exists on disk but is absent from the workspace.
    /// <para>
    /// The consumers closure above is built from loaded projects only, so a project
    /// dropped from the solution but never deleted is invisible to it. The reference
    /// report says a package is unused "<em>in this project or its consumers</em>";
    /// when an unanalysable consumer exists that sentence is an assumption, not a
    /// measurement. Callers use this map to cap the tier and say so.
    /// </para>
    /// </summary>
    private static FrozenDictionary<string, FrozenSet<string>> FindDetachedConsumerPaths(
        Solution solution,
        IReadOnlyDictionary<string, ProjectFileInfo> loadedProjects)
    {
        var rootDirectory = DetachedProjectScanner.ResolveRootDirectory(solution);
        var loadedPaths = loadedProjects.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var detachedPaths = rootDirectory is null
            ? []
            : DetachedProjectScanner.FindUnattachedProjectFiles(rootDirectory, loadedPaths);
        if (detachedPaths.Count == 0)
        {
            return FrozenDictionary<string, FrozenSet<string>>.Empty;
        }

        // Project references declared BY the detached projects. Their own references
        // matter too: a detached web host reaches a hub through another project.
        var detachedProjectFiles = new Dictionary<string, ProjectFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var detachedPath in detachedPaths)
        {
            if (ProjectFileReader.Read(detachedPath) is { } info)
            {
                detachedProjectFiles[detachedPath] = info;
            }
        }

        var byPath = new Dictionary<string, ProjectFileInfo>(loadedProjects, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in detachedProjectFiles)
        {
            byPath[path] = info;
        }

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var detachedPath in detachedProjectFiles.Keys)
        {
            // Walk outward through every reference edge, loading or detached. An edge
            // marked PrivateAssets=all stops the flow past this project, matching the
            // consumers closure: a consumer that cannot see the assembly cannot use it.
            var frontier = new Queue<(string Path, bool Onward)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in detachedProjectFiles[detachedPath].ProjectReferences)
            {
                if (reference.ReferenceOutputAssemblyDisabled || reference.IsAnalyser)
                {
                    continue;
                }

                frontier.Enqueue((reference.FullPath, !reference.PrivateAssetsAll));
            }

            while (frontier.Count > 0)
            {
                var (path, onward) = frontier.Dequeue();
                if (!seen.Add(path) || !byPath.TryGetValue(path, out var info))
                {
                    continue;
                }

                if (loadedPaths.Contains(path))
                {
                    if (!result.TryGetValue(path, out var detached))
                    {
                        detached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[path] = detached;
                    }

                    detached.Add(detachedPath);
                }

                if (!onward)
                {
                    continue;
                }

                foreach (var reference in info.ProjectReferences)
                {
                    if (reference.ReferenceOutputAssemblyDisabled || reference.IsAnalyser)
                    {
                        continue;
                    }

                    frontier.Enqueue((reference.FullPath, !reference.PrivateAssetsAll));
                }
            }
        }

        return result.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// High normally: the reference is unused across every project the workspace
    /// could analyse. Capped to Moderate when an unloaded project on disk could
    /// still be consuming it, because then the evidence behind "no consumer uses
    /// this" is incomplete — the reference may well be load-bearing.
    /// <para>
    /// A reference marked PrivateAssets=all never reaches consumers at all, so an
    /// unloaded consumer is irrelevant to it and it keeps its tier. Same rule the
    /// hub gate above uses to ignore consumer usage: nothing flows, so nothing
    /// can be proven by a consumer.
    /// </para>
    /// </summary>
    private static CertaintyTier CertifyForDetachedConsumers(
        FrozenDictionary<string, FrozenSet<string>> detachedConsumers,
        string projectPath,
        bool privateAssetsAll)
    {
        return !privateAssetsAll && detachedConsumers.ContainsKey(projectPath)
            ? CertaintyTier.Moderate
            : CertaintyTier.High;
    }

    private static string BuildPackageMessage(
        string packageId,
        string projectName,
        string projectPath,
        FrozenDictionary<string, FrozenSet<string>> detachedConsumers,
        bool privateAssetsAll)
    {
        var suffix = privateAssetsAll ? string.Empty : DescribeDetachedConsumers(detachedConsumers, projectPath);
        return suffix.Length == 0
            ? $"Package '{packageId}' contributes assemblies but no symbol from it is used in project '{projectName}' or its consumers."
            : $"Package '{packageId}' contributes assemblies but no symbol from it is used in project '{projectName}' or its loaded consumers.{suffix}";
    }

    private static string BuildProjectReferenceMessage(
        string referencePath,
        string projectName,
        string projectPath,
        FrozenDictionary<string, FrozenSet<string>> detachedConsumers,
        bool privateAssetsAll)
    {
        var referenceName = Path.GetFileNameWithoutExtension(referencePath);
        var suffix = privateAssetsAll ? string.Empty : DescribeDetachedConsumers(detachedConsumers, projectPath);
        return suffix.Length == 0
            ? $"Project reference '{referenceName}' is declared but no symbol from it or its transitive flow is used in project '{projectName}' or its consumers."
            : $"Project reference '{referenceName}' is declared but no symbol from it or its transitive flow is used in project '{projectName}' or its loaded consumers.{suffix}";
    }

    /// <summary>
    /// Names the unloaded projects that could still be consuming this one, so the
    /// capped finding says what to check instead of only being less certain.
    /// </summary>
    private static string DescribeDetachedConsumers(
        FrozenDictionary<string, FrozenSet<string>> detachedConsumers,
        string projectPath)
    {
        if (!detachedConsumers.TryGetValue(projectPath, out var consumers) || consumers.Count == 0)
        {
            return string.Empty;
        }

        var named = consumers
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return $" Unloaded project(s) outside the solution may still use it: {string.Join(", ", named)}.";
    }

    /// <summary>
    /// Reverse transitive closure over the project-reference graph: for each
    /// project, every loaded project that (transitively) references it. An edge
    /// marked PrivateAssets=all still lets the direct consumer see the flow but
    /// blocks onward propagation, so the walk stops expanding through it.
    /// </summary>
    private static FrozenDictionary<string, FrozenSet<string>> ComputeConsumersClosure(
        IReadOnlyDictionary<string, ProjectFileInfo> projectFileInfos)
    {
        var reverseEdges = new Dictionary<string, List<(string Consumer, bool FlowsOnward)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in projectFileInfos)
        {
            foreach (var reference in info.ProjectReferences)
            {
                if (reference.ReferenceOutputAssemblyDisabled || reference.IsAnalyser
                    || !projectFileInfos.ContainsKey(reference.FullPath))
                {
                    continue;
                }

                if (!reverseEdges.TryGetValue(reference.FullPath, out var edges))
                {
                    edges = [];
                    reverseEdges[reference.FullPath] = edges;
                }

                edges.Add((path, FlowsOnward: !reference.PrivateAssetsAll));
            }
        }

        var closure = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in projectFileInfos.Keys)
        {
            var consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new Queue<(string Path, bool Onward)>();
            if (reverseEdges.TryGetValue(path, out var directEdges))
            {
                foreach (var (consumer, flowsOnward) in directEdges)
                {
                    frontier.Enqueue((consumer, flowsOnward));
                }
            }

            while (frontier.Count > 0)
            {
                var (consumer, onward) = frontier.Dequeue();
                if (!consumers.Add(consumer) || !onward)
                {
                    continue;
                }

                if (reverseEdges.TryGetValue(consumer, out var edges))
                {
                    foreach (var (next, flowsOnward) in edges)
                    {
                        frontier.Enqueue((next, flowsOnward));
                    }
                }
            }

            closure[path] = consumers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }

        return closure.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The assemblies a project reference carries into the referencing project:
    /// the referenced assembly itself, every package assembly resolved into the
    /// referenced project's compilation (they flow onward by default), and the
    /// flow of its own project references, recursively. Memoized per run.
    /// </summary>
    private static async Task<FrozenSet<string>> ComputeFlowAssembliesAsync(
        string projectPath,
        IReadOnlyDictionary<string, ProjectFileInfo> projectFileInfos,
        ProjectPackageUsageCache usageCache,
        IDictionary<string, FrozenSet<string>> memo,
        HashSet<string> visiting,
        CancellationToken cancellationToken)
    {
        if (memo.TryGetValue(projectPath, out var cached))
        {
            return cached;
        }

        if (!projectFileInfos.TryGetValue(projectPath, out var info) || !visiting.Add(projectPath))
        {
            return FrozenSet<string>.Empty;
        }

        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { info.AssemblyName };

        // Package assemblies visible in the referenced project's compilation
        // (its own plus anything flowed into it) flow onward through the edge.
        if (info.PackageReferences.Length > 0)
        {
            var usage = await usageCache.GetAsync(projectPath, cancellationToken).ConfigureAwait(false);
            foreach (var names in usage.AssemblyNamesByPackage.Values)
            {
                assemblies.UnionWith(names);
            }
        }

        foreach (var reference in info.ProjectReferences)
        {
            if (reference.ReferenceOutputAssemblyDisabled || reference.IsAnalyser
                || !projectFileInfos.ContainsKey(reference.FullPath))
            {
                continue;
            }

            var inner = await ComputeFlowAssembliesAsync(
                reference.FullPath, projectFileInfos, usageCache, memo, visiting, cancellationToken).ConfigureAwait(false);
            assemblies.UnionWith(inner);
        }

        var frozen = assemblies.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        memo[projectPath] = frozen;
        return frozen;
    }

    /// <summary>
    /// Assemblies the project would still see after removing one project
    /// reference: the flows of its remaining project references plus the full
    /// reachable subtree of its own direct packages (lock-file graph).
    /// </summary>
    private static async Task<FrozenSet<string>> ComputeSurvivingAssembliesAsync(
        ProjectFileInfo projectFile,
        string removedReferencePath,
        IReadOnlyDictionary<string, ProjectFileInfo> projectFileInfos,
        ProjectPackageUsageEntry usage,
        ProjectPackageUsageCache usageCache,
        IDictionary<string, FrozenSet<string>> flowMemo,
        CancellationToken cancellationToken)
    {
        var surviving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in projectFile.ProjectReferences)
        {
            if (reference.FullPath.Equals(removedReferencePath, StringComparison.OrdinalIgnoreCase)
                || reference.ReferenceOutputAssemblyDisabled || reference.IsAnalyser
                || !projectFileInfos.ContainsKey(reference.FullPath))
            {
                continue;
            }

            var flow = await ComputeFlowAssembliesAsync(
                reference.FullPath,
                projectFileInfos,
                usageCache,
                flowMemo,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            surviving.UnionWith(flow);
        }

        if (usage.LockModel is not null)
        {
            foreach (var packagesByTfm in usage.LockModel.PackagesByTfm.Values)
            {
                foreach (var direct in projectFile.PackageReferences)
                {
                    if (!packagesByTfm.TryGetValue(direct.Id, out var root))
                    {
                        continue;
                    }

                    var frontier = new Queue<string>();
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { direct.Id };
                    foreach (var dependencyId in root.Dependencies.Keys)
                    {
                        frontier.Enqueue(dependencyId);
                    }

                    while (frontier.Count > 0)
                    {
                        var id = frontier.Dequeue();
                        if (!visited.Add(id))
                        {
                            continue;
                        }

                        if (usage.AssemblyNamesByPackage.TryGetValue(id, out var names))
                        {
                            surviving.UnionWith(names);
                        }

                        if (packagesByTfm.TryGetValue(id, out var package))
                        {
                            foreach (var dependencyId in package.Dependencies.Keys)
                            {
                                frontier.Enqueue(dependencyId);
                            }
                        }
                    }
                }
            }
        }

        return surviving.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The assemblies a package reference carries onward to consumers: its own
    /// compile assets plus its exclusive transitive subtree (the same set whose
    /// eviction <see cref="IsSubtreeLoadBearing"/> guards against).
    /// </summary>
    private static FrozenSet<string> CollectPackageFlowAssemblies(
        ProjectFileInfo projectFile,
        string packageId,
        ProjectPackageUsageEntry usage)
    {
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usage.AssemblyNamesByPackage.TryGetValue(packageId, out var own))
        {
            assemblies.UnionWith(own);
        }

        if (usage.LockModel is not null)
        {
            foreach (var packagesByTfm in usage.LockModel.PackagesByTfm.Values)
            {
                if (!packagesByTfm.TryGetValue(packageId, out var root))
                {
                    continue;
                }

                foreach (var subtreeId in ComputeExclusiveSubtreeIds(packagesByTfm, root, projectFile))
                {
                    if (usage.AssemblyNamesByPackage.TryGetValue(subtreeId, out var names))
                    {
                        assemblies.UnionWith(names);
                    }
                }
            }
        }

        return assemblies.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when any project in the consumer closure uses at least one of the
    /// given assemblies — the reference is load-bearing for its dependents even
    /// though the declaring project itself never touches it.
    /// </summary>
    private static async Task<bool> AnyConsumerUsesAsync(
        string projectPath,
        FrozenSet<string> flowAssemblies,
        FrozenDictionary<string, FrozenSet<string>> consumersClosure,
        ProjectPackageUsageCache usageCache,
        CancellationToken cancellationToken)
    {
        if (flowAssemblies.Count == 0 || !consumersClosure.TryGetValue(projectPath, out var consumers))
        {
            return false;
        }

        foreach (var consumerPath in consumers)
        {
            var consumerUsage = await usageCache.GetAsync(consumerPath, cancellationToken).ConfigureAwait(false);
            if (flowAssemblies.Any(consumerUsage.UsedAssemblyNames.Contains))
            {
                return true;
            }
        }

        return false;
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
        ProjectPackageUsageEntry usage)
    {
        foreach (var packagesByTfm in lockModel.PackagesByTfm.Values)
        {
            if (!packagesByTfm.TryGetValue(packageId, out var root))
            {
                continue;
            }

            foreach (var subtreeId in ComputeExclusiveSubtreeIds(packagesByTfm, root, projectFile))
            {
                if (usage.AssemblyNamesByPackage.TryGetValue(subtreeId, out var names)
                    && names.Any(usage.UsedAssemblyNames.Contains))
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
