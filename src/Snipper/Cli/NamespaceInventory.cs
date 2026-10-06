namespace Snipper.Cli;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Analysis;

/// <summary>
/// What the solution actually declares, for the suppression audit (4A) to decide whether
/// a configured suppression can possibly match anything.
/// </summary>
/// <param name="Declared">
/// Source-declared namespace names. Deliberately source-only. Walking
/// <c>Compilation.GlobalNamespace</c> would also surface namespaces from referenced
/// assemblies, which would make almost any string "exist" and silently defeat
/// obsolete-namespace detection. Enumerating namespace *declarations* in syntax keeps
/// the answer to "does this repo declare it".
/// </param>
/// <param name="HasFileScopeCode">
/// Whether any analysed file declares no namespace at all, so that code lives in the
/// global namespace and only the <see cref="AnalysisExclusions.GlobalNamespaceMarker"/>
/// sentinel can exclude it.
/// </param>
internal readonly record struct NamespaceInventoryResult(FrozenSet<string> Declared, bool HasFileScopeCode);

/// <summary>
/// The walk touches only <c>root.Members</c> rather than descending whole files, so
/// cost is proportional to the number of files and namespace declarations, not to the
/// size of the solution. Files with no path (in-memory, source-injected) are skipped.
/// </summary>
internal static class NamespaceInventory
{
    public static NamespaceInventoryResult Collect(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var hasFileScopeCode = false;
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is not CompilationUnitSyntax compilationUnit)
                {
                    continue;
                }

                CollectFromMembers(compilationUnit.Members, namespaces, out var declaredHere);

                // Generated and external files are judged by no rule, so their lack of a
                // namespace says nothing about whether "<global>" can match anything. Every
                // modern project has an ImplicitUsings.g.cs with no namespace, and
                // build-transitive sources such as Microsoft.NET.Test.Sdk.Program.cs are
                // namespace-less too — counting those would make "<global>" look live in
                // every solution, which is the one answer this probe must never give by
                // accident.
                if (!declaredHere && !ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    hasFileScopeCode = true;
                }
            }
        }

        return new NamespaceInventoryResult(namespaces.ToFrozenSet(StringComparer.Ordinal), hasFileScopeCode);
    }

    private static void CollectFromMembers(SyntaxList<MemberDeclarationSyntax> members, HashSet<string> namespaces, out bool declaredNamespace)
    {
        declaredNamespace = false;

        foreach (var member in members)
        {
            if (member is not BaseNamespaceDeclarationSyntax declaration)
            {
                continue;
            }

            declaredNamespace = true;
            namespaces.Add(declaration.Name.ToString());

            // Nested block-scoped namespaces only; a file-scoped namespace cannot nest.
            if (declaration is NamespaceDeclarationSyntax blockScoped)
            {
                CollectFromMembers(blockScoped.Members, namespaces, out _);
            }
        }
    }

    public static GlobFileInventory CollectFiles(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var files = new List<string>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { Length: > 0 } path)
                {
                    files.Add(path);
                }
            }
        }

        return new GlobFileInventory(files);
    }

    /// <summary>
    /// Namespace exclusions match by prefix: excluding <c>A.B</c> also excludes
    /// <c>A.B.C</c>. This mirrors <see cref="AnalysisExclusions.Covers"/>, which walks
    /// *up* from a symbol's namespace looking for an excluded ancestor.
    ///
    /// The probe therefore has to answer the inverse question — "is the configured
    /// namespace, or any namespace beneath it, declared?" — by testing each declared name
    /// against the candidate as a prefix. Walking up from the candidate instead would only
    /// ever find exact declarations, and would report every live parent exclusion as
    /// stale the moment its last direct child was renamed.
    /// </summary>
    public static Func<string, bool> ExistenceProbe(NamespaceInventoryResult inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var declared = inventory.Declared;

        return candidate =>
        {
            // The sentinel is not a namespace name, so the prefix walk can never match it.
            // It is live when some analysed file declares no namespace, and stale only when
            // every file in the solution is namespaced — otherwise the audit would call a
            // suppression stale while it was suppressing findings, which is the exact
            // failure this report exists to prevent.
            if (string.Equals(candidate, AnalysisExclusions.GlobalNamespaceMarker, StringComparison.Ordinal))
            {
                return inventory.HasFileScopeCode;
            }

            foreach (var name in declared)
            {
                if (string.Equals(name, candidate, StringComparison.Ordinal))
                {
                    return true;
                }

                if (name.Length > candidate.Length
                    && name.StartsWith(candidate, StringComparison.Ordinal)
                    && name[candidate.Length] == '.')
                {
                    return true;
                }
            }

            return false;
        };
    }
}
