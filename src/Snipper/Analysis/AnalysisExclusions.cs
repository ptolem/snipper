namespace Snipper.Analysis;

using System.Collections.Frozen;

/// <summary>
/// Namespaces excluded from producing findings (exact match plus sub-namespaces:
/// excluding "Company.Domain.Types" also excludes "Company.Domain.Types.Internal").
/// Exclusion suppresses findings only — code in excluded namespaces still counts
/// as usage evidence for the reference-checking rules.
/// </summary>
public sealed record AnalysisExclusions
{
    /// <summary>
    /// Sentinel name meaning "the global namespace" — code that declares no namespace
    /// whatsoever: a top-level-statements <c>Program.cs</c>, a file of global usings, a
    /// file-scope clone fragment.
    /// <para>
    /// Such code has no name for a real namespace exclusion to match, so before this
    /// sentinel it could never be excluded: every rule resolved its namespace from
    /// <c>ContainingNamespace</c> or an enclosing type declaration, both of which are
    /// absent or global for file-scope code, and the finding escaped suppression while
    /// the CLI still listed the namespace as excluded. On the MILKRUN sweep, excluding
    /// <c>Milkrun.Integration.MockingService</c> suppressed nothing for that project's
    /// namespace-less <c>Program.cs</c> and <c>GlobalUsings.cs</c>.
    /// </para>
    /// <para>
    /// This is not a new convention — it was already the marker <c>SNP0031</c> used
    /// internally for file-scope fragments, documented but reachable only through the
    /// config file. It is now the shared sentinel, honoured by every namespace-aware
    /// rule and accepted on the command line too.
    /// </para>
    /// </summary>
    public const string GlobalNamespaceMarker = "<global>";

    public static readonly AnalysisExclusions None = new(FrozenSet<string>.Empty);

    private AnalysisExclusions(FrozenSet<string> namespaces)
    {
        Namespaces = namespaces;
    }

    public FrozenSet<string> Namespaces { get; }

    public static AnalysisExclusions Create(IEnumerable<string> namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);

        var set = namespaces.ToFrozenSet(StringComparer.Ordinal);
        return set.Count == 0 ? None : new AnalysisExclusions(set);
    }

    /// <summary>
    /// Whether file-scope code is excluded, i.e. whether <see cref="GlobalNamespaceMarker"/>
    /// was configured.
    /// </summary>
    public bool IsGlobalNamespaceExcluded => Namespaces.Contains(GlobalNamespaceMarker);

    /// <summary>
    /// Whether <paramref name="declaredNamespace"/> is excluded: an exact match, or a
    /// descendant of an exclusion. The single implementation of that walk, shared by the
    /// symbol, syntax and clone-fragment paths so they cannot drift apart.
    /// <para>
    /// An empty or null name means the global namespace, which is covered only by the
    /// <see cref="GlobalNamespaceMarker"/> sentinel — never by an ancestor walk, since
    /// there is no name to walk up from.
    /// </para>
    /// </summary>
    public bool Covers(string? declaredNamespace)
    {
        if (string.IsNullOrEmpty(declaredNamespace))
        {
            return IsGlobalNamespaceExcluded;
        }

        var name = declaredNamespace;
        while (true)
        {
            if (Namespaces.Contains(name))
            {
                return true;
            }

            var lastDot = name.LastIndexOf('.');
            if (lastDot < 0)
            {
                return false;
            }

            name = name[..lastDot];
        }
    }
}
