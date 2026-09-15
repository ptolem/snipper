namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;

/// <summary>
/// Shared package-to-assembly mapping and usage probing. SNP0003's "contributes
/// assemblies but none are used" verdict is reused by SNP0012/SNP0013 as a dedup
/// suppressor: an unused package is reported by SNP0003 alone, never double-reported
/// as redundant or framework-provided.
/// </summary>
internal static class PackageAssemblyUsage
{
    /// <summary>
    /// Maps package id → the assemblies that package contributes to the compilation,
    /// derived from metadata reference paths under the NuGet "packages" folder.
    /// </summary>
    public static FrozenDictionary<string, IAssemblySymbol[]> MapAssembliesToPackages(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var map = new Dictionary<string, List<IAssemblySymbol>>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference { FilePath: { } filePath })
            {
                continue;
            }

            var packageId = ExtractPackageId(filePath);
            if (packageId is null)
            {
                continue;
            }

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            if (!map.TryGetValue(packageId, out var list))
            {
                list = [];
                map[packageId] = list;
            }

            list.Add(assembly);
        }

        // Frozen: built once per project, then queried for every declared PackageReference.
        return map.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the package contributes compile-time assemblies but the project uses
    /// none of them — SNP0003 territory. False when the package is build-only/analyser
    /// (no compile assets) or any contributed assembly is used.
    /// </summary>
    public static async Task<bool> IsUnusedByProjectAsync(
        Project project,
        Compilation compilation,
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var assembliesByPackage = MapAssembliesToPackages(compilation);
        if (!assembliesByPackage.TryGetValue(packageId, out var packageAssemblies))
        {
            return false;
        }

        var usedAssemblies = await SymbolUsageCollector.CollectUsedAssembliesAsync(project, cancellationToken).ConfigureAwait(false);
        foreach (var packageAssembly in packageAssemblies)
        {
            if (usedAssemblies.Contains(packageAssembly))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ExtractPackageId(string referencePath)
    {
        var segments = referencePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (segments[i].Equals("packages", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }
}
