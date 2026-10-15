namespace Snipper.Models;

/// <summary>
/// The one total order over findings: certainty, path, line, column, rule, message,
/// category, title. Every place that needs findings in order uses this, so the runner,
/// the JSON and SARIF writers and the console table cannot disagree.
/// <para>
/// Certainty and path alone are not enough. Those two keys were the whole sort once, and
/// being a stable sort they left every finding sharing a (certainty, path) pair - all
/// unused usings in one file, say - inheriting whatever order the analyser produced.
/// SNP0019 inherited a <em>parallel producer-completion</em> order from
/// <c>compilation.GetDiagnostics()</c>, so two runs over the same solution permuted those
/// findings. Breaking every remaining tie here is what lets an analyser emit in any order.
/// </para>
/// <para>
/// Paths and text compare ordinally. A culture-sensitive comparison would make the order
/// depend on the machine's locale.
/// </para>
/// </summary>
public sealed class FindingOrder : IComparer<SnipperFinding>
{
    public static FindingOrder Instance { get; } = new();

    private FindingOrder()
    {
    }

    /// <summary>
    /// Returns the findings in this order. Stable, so two findings equal on every key
    /// keep their input order.
    /// </summary>
    public static List<SnipperFinding> Sort(IEnumerable<SnipperFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return [.. findings.Order(Instance)];
    }

    public int Compare(SnipperFinding? x, SnipperFinding? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var result = Comparer<CertaintyTier>.Default.Compare(x.Certainty, y.Certainty);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x.FilePath, y.FilePath);
        if (result != 0)
        {
            return result;
        }

        result = x.LineNumber.CompareTo(y.LineNumber);
        if (result != 0)
        {
            return result;
        }

        result = x.CharacterOffset.CompareTo(y.CharacterOffset);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x.RuleId, y.RuleId);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x.Message, y.Message);
        if (result != 0)
        {
            return result;
        }

        result = Comparer<FindingCategory>.Default.Compare(x.Category, y.Category);
        if (result != 0)
        {
            return result;
        }

        return string.CompareOrdinal(x.Title, y.Title);
    }
}
