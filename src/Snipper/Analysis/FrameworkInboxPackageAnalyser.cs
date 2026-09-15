namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using NuGet.Versioning;
using Snipper.Models;

/// <summary>
/// SNP0013 — Flags PackageReference items whose package already ships inside the
/// project's own shared framework (per PackageOverrides.txt of the matching targeting
/// pack) at a version greater than or equal to the declared one. Classic relic of
/// netstandard → modern TFM migrations. Tier 2 (High): pack data is authoritative,
/// but a reference pinning a newer version is an intentional ship-in-app upgrade and
/// is never flagged. Multi-targeted projects are flagged only when redundant on every
/// TFM; projects whose TFMs have no installed targeting pack are skipped (cannot judge).
/// Dedup: an unused package is reported by SNP0003 alone.
/// </summary>
public sealed class FrameworkInboxPackageAnalyser : IWorkspaceAnalyser
{
    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();

        var packsDirectory = TargetingPackLocator.FindPacksDirectory();
        if (packsDirectory is null)
        {
            return findings;
        }

        // Few distinct (TFM, web) combinations in practice — resolve once, reuse.
        var overridesCache = new Dictionary<(string Tfm, bool IncludeAspNetCore), FrameworkPackageOverrides?>();

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation || project.FilePath is null || !File.Exists(project.FilePath))
            {
                continue;
            }

            var projectFile = ProjectFileReader.Read(project.FilePath);
            if (projectFile is null || projectFile.PackageReferences.Length == 0 || projectFile.TargetFrameworks.Length == 0)
            {
                continue;
            }

            var includeAspNetCore = IsAspNetCoreProject(projectFile);

            var overridesPerTfm = new List<FrameworkPackageOverrides>(projectFile.TargetFrameworks.Length);
            var unjudgeable = false;
            foreach (var tfm in projectFile.TargetFrameworks)
            {
                var key = (tfm, includeAspNetCore);
                if (!overridesCache.TryGetValue(key, out var overrides))
                {
                    var files = TargetingPackLocator.FindOverrideFiles(packsDirectory, tfm, includeAspNetCore);
                    overrides = files.Count == 0 ? null : FrameworkPackageIndex.Load(files);
                    overridesCache[key] = overrides;
                }

                if (overrides is null)
                {
                    unjudgeable = true;
                    break;
                }

                overridesPerTfm.Add(overrides);
            }

            if (unjudgeable)
            {
                continue;
            }

            progress?.Invoke($"FrameworkInboxPackageAnalyser: scanning {project.Name}");

            Compilation? compilation = null;
            foreach (var package in projectFile.PackageReferences)
            {
                if (package.CompileAssetsExcluded || package.DeclaredMinVersion is null)
                {
                    continue;
                }

                var inboxEverywhere = true;
                foreach (var overrides in overridesPerTfm)
                {
                    if (!FrameworkPackageIndex.IsInbox(overrides, package.Id, package.DeclaredMinVersion))
                    {
                        inboxEverywhere = false;
                        break;
                    }
                }

                if (!inboxEverywhere)
                {
                    continue;
                }

                compilation ??= await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    continue;
                }

                // Dedup: an unused package is SNP0003's finding, not ours.
                if (await PackageAssemblyUsage.IsUnusedByProjectAsync(project, compilation, package.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0013",
                    Title: "Framework-Provided Package",
                    Message: $"Package '{package.Id}' {package.DeclaredMinVersion} ships in the shared framework for every target of project '{project.Name}' — the PackageReference can be removed.",
                    Certainty: CertaintyTier.High,
                    Category: FindingCategory.FrameworkProvidedPackage,
                    FilePath: project.FilePath,
                    LineNumber: package.LineNumber,
                    CharacterOffset: 1,
                    Symbol: null));
            }
        }

        return findings;
    }

    private static bool IsAspNetCoreProject(ProjectFileInfo projectFile)
    {
        if (projectFile.SdkName?.Contains(".Web", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        foreach (var frameworkReferenceId in projectFile.FrameworkReferenceIds)
        {
            if (frameworkReferenceId.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
