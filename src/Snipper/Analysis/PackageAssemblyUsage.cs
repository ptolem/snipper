namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;

/// <summary>
/// Shared package-to-assembly mapping, derived from metadata reference paths under
/// the NuGet "packages" folder. Consumed by <see cref="ProjectPackageUsageCache"/>,
/// whose per-csproj memoization backs SNP0003's usage verdict and the SNP0012/SNP0013
/// dedup suppressor: an unused package is reported by SNP0003 alone, never
/// double-reported as redundant or framework-provided.
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
