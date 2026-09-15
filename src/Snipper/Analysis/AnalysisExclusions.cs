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
}
