namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>
/// Per-csproj package-usage facts, computed once per solution and shared by the
/// package-level analysers (SNP0003/0012/0013). Collecting used assemblies is the
/// most expensive operation in the tool (a semantic walk of every document), so
/// without this cache each package analyser repeated the walk per project — and
/// per candidate package.
/// All TFM instances of a multi-targeted project contribute to one entry: reference
/// removal affects the csproj (every TFM), and assembly identity is compared by name
/// because each TFM compilation binds its own symbol instances.
/// </summary>
internal sealed class ProjectPackageUsageCache
{
    private static readonly ConditionalWeakTable<Solution, ProjectPackageUsageCache> Cache = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<ProjectPackageUsageEntry>>> _entriesByProjectPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Solution _solution;

    private ProjectPackageUsageCache(Solution solution)
    {
        _solution = solution;
    }

    public static ProjectPackageUsageCache Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return Cache.GetValue(solution, static s => new ProjectPackageUsageCache(s));
    }

    /// <summary>
    /// Usage facts for the given project file, computed on first request. The
    /// computation starts on the thread pool and its task is shared
    /// (ExecutionAndPublication): concurrent callers await it rather than block on
    /// the Lazy while the semantic walk runs, and later callers reuse the result.
    /// </summary>
    public Task<ProjectPackageUsageEntry> GetAsync(string projectFilePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);

        var lazy = _entriesByProjectPath.GetOrAdd(
            projectFilePath,
            static (path, state) => new Lazy<Task<ProjectPackageUsageEntry>>(
                () => Task.Run(() => BuildAsync(state.Solution, path, state.CancellationToken), state.CancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Solution: _solution, CancellationToken: cancellationToken));

        return lazy.Value;
    }

    /// <summary>
    /// Computes the entries for the given project files concurrently, bounded by
    /// SNIPPER_MAX_DOP, so a caller's per-project loop then finds each one ready.
    /// Without it the package analysers walked projects one at a time and every
    /// cache miss was a full semantic walk on the critical path. Entries already
    /// computed or in flight are shared, so overlapping prefetches cost nothing.
    /// </summary>
    public Task PrefetchAsync(IEnumerable<string> projectFilePaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectFilePaths);

        return Parallel.ForEachAsync(
            projectFilePaths.Distinct(StringComparer.OrdinalIgnoreCase),
            AnalysisParallelism.CreateOptions(cancellationToken),
            async (projectFilePath, token) => await GetAsync(projectFilePath, token).ConfigureAwait(false));
    }

    private static async Task<ProjectPackageUsageEntry> BuildAsync(
        Solution solution,
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var usedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assemblyNamesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation
                || project.FilePath is null
                || !project.FilePath.Equals(projectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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

        var lockModel = NuGetLockFileReader.Read(projectFilePath);

        return new ProjectPackageUsageEntry(
            usedAssemblyNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            assemblyNamesByPackage.ToFrozenDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            lockModel);
    }
}

internal sealed record ProjectPackageUsageEntry(
    FrozenSet<string> UsedAssemblyNames,
    FrozenDictionary<string, FrozenSet<string>> AssemblyNamesByPackage,
    LockFileModel? LockModel)
{
    /// <summary>
    /// True when the package contributes compile-time assemblies but none are used —
    /// SNP0003 territory. False when the package is build-only/analyser (no compile
    /// assets recorded) or any contributed assembly name is used.
    /// </summary>
    public bool IsUnusedPackage(string packageId)
    {
        return AssemblyNamesByPackage.TryGetValue(packageId, out var names)
            && !names.Any(UsedAssemblyNames.Contains);
    }
}
