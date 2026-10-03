namespace Snipper.Cli;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Source-declared namespace names and analysed file paths, used by the suppression
/// audit (4A) to decide whether a configured suppression can possibly match anything.
///
/// Deliberately source-only. Walking <c>Compilation.GlobalNamespace</c> would also
/// surface namespaces from referenced assemblies, which would make almost any string
/// "exist" and silently defeat obsolete-namespace detection. Enumerating namespace
/// *declarations* in syntax keeps the answer to "does this repo declare it".
///
/// The walk touches only <c>root.Members</c> rather than descending whole files, so
/// cost is proportional to the number of files and namespace declarations, not to the
/// size of the solution. Files with no path (in-memory, source-injected) are skipped.
/// </summary>
internal static class NamespaceInventory
{
    public static FrozenSet<string> CollectDeclaredNamespaces(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is not CompilationUnitSyntax compilationUnit)
                {
                    continue;
                }

                CollectFromMembers(compilationUnit.Members, namespaces);
            }
        }

        return namespaces.ToFrozenSet(StringComparer.Ordinal);
    }

    private static void CollectFromMembers(SyntaxList<MemberDeclarationSyntax> members, HashSet<string> namespaces)
    {
        foreach (var member in members)
        {
            if (member is not BaseNamespaceDeclarationSyntax declaration)
            {
                continue;
            }

            namespaces.Add(declaration.Name.ToString());

            // Nested block-scoped namespaces only; a file-scoped namespace cannot nest.
            if (declaration is NamespaceDeclarationSyntax blockScoped)
            {
                CollectFromMembers(blockScoped.Members, namespaces);
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
    /// <c>A.B.C</c>. This mirrors <c>ExclusionEngine.IsNamespaceExcluded</c>, which walks
    /// *up* from a symbol's namespace looking for an excluded ancestor.
    ///
    /// The probe therefore has to answer the inverse question — "is the configured
    /// namespace, or any namespace beneath it, declared?" — by testing each declared name
    /// against the candidate as a prefix. Walking up from the candidate instead would only
    /// ever find exact declarations, and would report every live parent exclusion as
    /// stale the moment its last direct child was renamed.
    /// </summary>
    public static Func<string, bool> ExistenceProbe(FrozenSet<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        return candidate =>
        {
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
