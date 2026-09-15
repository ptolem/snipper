namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

internal static class SymbolReferenceQuery
{
    /// <summary>
    /// Searches only the documents that textually contain the symbol's name
    /// (per <see cref="SolutionUsageIndex"/>). Any semantic reference must spell
    /// the name in source, so restricting the document set is sound; an empty
    /// set would prove "no references" and callers short-circuit before calling.
    /// FindReferencesAsync itself cannot early-exit, so keeping its document
    /// set minimal is the latency lever.
    /// </summary>
    public static async Task<bool> HasAnyReferenceAsync(
        ISymbol symbol,
        Solution solution,
        IImmutableSet<Document> candidateDocuments,
        CancellationToken cancellationToken)
    {
        // Allocation-free check for any reference location. Avoids the
        // SelectMany/Any LINQ chain (and its enumerator closures) on this hot path.
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);

        foreach (var reference in references)
        {
            foreach (var _ in reference.Locations)
            {
                return true;
            }
        }

        return false;
    }
}
