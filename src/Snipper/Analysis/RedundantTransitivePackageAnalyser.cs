namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Snipper.Models;

/// <summary>
/// SNP0012 — Flags direct PackageReference items already supplied transitively by
/// another direct reference at an equal or higher required version in every target
/// framework. Removing the edge leaves the resolved graph unchanged. Tier 3 (Moderate):
/// a direct reference also documents intent and pins against upstream dependency
/// changes, so the message names the providing parent and advises verification.
/// Skips projects whose assets file is missing/stale (cannot judge) and packages a
/// target's lock file doesn't resolve. Dedup: an unused package is SNP0003's finding.
/// </summary>
public sealed class RedundantTransitivePackageAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0012"];

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var usageCache = ProjectPackageUsageCache.Get(solution);

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation || project.FilePath is null || !File.Exists(project.FilePath))
            {
                continue;
            }

            var projectFile = ProjectFileReader.Read(project.FilePath);
            if (projectFile is null || projectFile.PackageReferences.Length < 2 || projectFile.TargetFrameworks.Length == 0)
            {
                continue;
            }

            var usage = await usageCache.GetAsync(project.FilePath, cancellationToken).ConfigureAwait(false);
            var model = usage.LockModel;
            if (model is null)
            {
                continue;
            }

            // Multi-targeting: flag only when redundant on ALL TFMs — a TFM missing
            // from the lock file (restore drift) makes the project unjudgeable.
            var targetFrameworks = projectFile.TargetFrameworks;
            var unjudgeable = false;
            foreach (var tfm in targetFrameworks)
            {
                if (!model.PackagesByTfm.ContainsKey(tfm))
                {
                    unjudgeable = true;
                    break;
                }
            }

            if (unjudgeable)
            {
                continue;
            }

            progress?.Invoke($"RedundantTransitivePackageAnalyser: scanning {project.Name}");

            foreach (var package in projectFile.PackageReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (package.CompileAssetsExcluded)
                {
                    continue;
                }

                var otherIds = new List<string>(projectFile.PackageReferences.Length - 1);
                foreach (var other in projectFile.PackageReferences)
                {
                    if (!other.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        otherIds.Add(other.Id);
                    }
                }

                var redundantEverywhere = true;
                string? providingParent = null;
                foreach (var tfm in targetFrameworks)
                {
                    if (!model.PackagesByTfm[tfm].TryGetValue(package.Id, out var resolvedDirect)
                        || !TransitiveRedundancyEvaluator.TryFindProvidingParent(model, tfm, resolvedDirect, otherIds, out var parent))
                    {
                        redundantEverywhere = false;
                        break;
                    }

                    providingParent ??= parent;
                }

                if (!redundantEverywhere)
                {
                    continue;
                }

                // Dedup: an unused package is SNP0003's finding, not ours.
                if (usage.IsUnusedPackage(package.Id))
                {
                    continue;
                }

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0012",
                    Title: "Redundant Transitive Package Reference",
                    Message: $"Package '{package.Id}' is already supplied transitively by '{providingParent}' at an equal or higher version in every target of project '{project.Name}' — the direct PackageReference can be removed.",
                    Certainty: CertaintyTier.Moderate,
                    Category: FindingCategory.RedundantTransitivePackage,
                    FilePath: project.FilePath,
                    LineNumber: package.LineNumber,
                    CharacterOffset: 1,
                    Symbol: null));
            }
        }

        return findings;
    }
}
