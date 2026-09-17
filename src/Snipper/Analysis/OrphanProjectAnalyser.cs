namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Snipper.Models;

/// <summary>
/// SNP0011 — Flags projects nothing references: loaded projects with in-degree 0 in
/// the project reference graph that are neither entry points nor test projects, and
/// .csproj files found on disk under the analysis root that the workspace never loaded
/// (removed from the solution, never deleted). Tier 3 (Moderate): plugin/reflection
/// loading can invisibly consume an assembly, so assembly-name string evidence
/// (literals, JSON configuration) suppresses findings and messages advise verifying
/// before deletion. Evidence is gathered in ONE batched pass for all candidates —
/// a per-candidate scan would multiply the sweep by the candidate count.
/// </summary>
public sealed class OrphanProjectAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0011"];

    public Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();

        var graph = ProjectGraphBuilder.Build(solution);
        if (graph.NodesByPath.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SnipperFinding>>(findings);
        }

        var rootDirectory = ResolveRootDirectory(solution);
        var loadedProjectPaths = graph.NodesByPath.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // In-degree pass: one count per inbound ProjectReference edge. ReferenceOutputAssembly=false
        // and analyser-output references count as inbound — both are deliberate consumption.
        var inboundCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.NodesByPath.Values)
        {
            foreach (var referencedPath in node.ReferencedProjectPaths)
            {
                if (graph.NodesByPath.ContainsKey(referencedPath))
                {
                    inboundCounts[referencedPath] = inboundCounts.GetValueOrDefault(referencedPath) + 1;
                }
            }
        }

        var orphanCandidates = new List<(string AssemblyName, string FilePath)>();
        foreach (var node in graph.NodesByPath.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!node.IsEntryPoint && !node.IsTestProject && inboundCounts.GetValueOrDefault(node.FilePath) == 0)
            {
                orphanCandidates.Add((node.AssemblyName, node.FilePath));
            }
        }

        var detachedCandidates = new List<(string AssemblyName, string FilePath)>();
        if (rootDirectory is not null)
        {
            foreach (var detachedFile in DetachedProjectScanner.FindUnattachedProjectFiles(rootDirectory, loadedProjectPaths))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var detachedInfo = ProjectFileReader.Read(detachedFile);
                detachedCandidates.Add((detachedInfo?.AssemblyName ?? Path.GetFileNameWithoutExtension(detachedFile), detachedFile));
            }
        }

        var candidateNames = new List<string>(orphanCandidates.Count + detachedCandidates.Count);
        foreach (var (assemblyName, _) in orphanCandidates)
        {
            candidateNames.Add(assemblyName);
        }

        foreach (var (assemblyName, _) in detachedCandidates)
        {
            candidateNames.Add(assemblyName);
        }

        var evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidateNames.Count > 0)
        {
            progress?.Invoke("OrphanProjectAnalyser: scanning for assembly-name evidence");
            evidence.UnionWith(AssemblyNameEvidenceScanner.FindSpelledNames(solution, candidateNames));
            if (rootDirectory is not null)
            {
                evidence.UnionWith(AssemblyNameEvidenceScanner.FindSpelledNamesInJsonFiles(rootDirectory, candidateNames));
            }
        }

        foreach (var (assemblyName, filePath) in orphanCandidates)
        {
            if (evidence.Contains(assemblyName))
            {
                continue;
            }

            findings.Add(new SnipperFinding(
                RuleId: "SNP0011",
                Title: "Orphan Project",
                Message: $"Project '{assemblyName}' is loaded but no other project references it and it is not an entry point or test project — candidate for removal. Verify plugin/reflection loading before deleting.",
                Certainty: CertaintyTier.Moderate,
                Category: FindingCategory.OrphanProject,
                FilePath: filePath,
                LineNumber: 1,
                CharacterOffset: 1,
                Symbol: null));
        }

        foreach (var (assemblyName, detachedFile) in detachedCandidates)
        {
            if (evidence.Contains(assemblyName))
            {
                continue;
            }

            findings.Add(new SnipperFinding(
                RuleId: "SNP0011",
                Title: "Orphan Project",
                Message: $"Project file '{Path.GetFileNameWithoutExtension(detachedFile)}' exists on disk but is not part of the loaded solution/workspace — re-attach it or delete it. Verify plugin/reflection loading before deleting.",
                Certainty: CertaintyTier.Moderate,
                Category: FindingCategory.OrphanProject,
                FilePath: detachedFile,
                LineNumber: 1,
                CharacterOffset: 1,
                Symbol: null));
        }

        return Task.FromResult<IReadOnlyList<SnipperFinding>>(findings);
    }

    /// <summary>
    /// Solution directory when a solution file was opened; otherwise the common ancestor
    /// of all project directories (single-csproj runs scope the sweep to that project's
    /// own directory subtree rather than the whole repository).
    /// </summary>
    private static string? ResolveRootDirectory(Solution solution)
    {
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
}
