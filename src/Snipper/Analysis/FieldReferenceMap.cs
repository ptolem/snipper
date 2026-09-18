namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

/// <summary>
/// Shared field-reference enumeration for the value-flow rules (SNP0021
/// write-only, SNP0024 can-be-readonly): every confirmed reference location of
/// a field, resolved to its innermost <see cref="SimpleNameSyntax"/> for
/// <see cref="FieldReferenceClassifier"/> to judge. Extracted from
/// WriteOnlyFieldAnalyser so both rules read identical evidence.
/// </summary>
internal static class FieldReferenceMap
{
    public static async Task<IReadOnlyList<FieldReferenceLocation>> GetReferencesAsync(
        IFieldSymbol field,
        Solution solution,
        IImmutableSet<Document> candidateDocuments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(candidateDocuments);

        var references = await SymbolFinder.FindReferencesAsync(field, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);

        var locations = new List<FieldReferenceLocation>();
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                var node = location.Location.SourceTree?.GetRoot(cancellationToken)
                    .FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true) as SimpleNameSyntax;
                locations.Add(new FieldReferenceLocation(node, location.IsCandidateLocation));
            }
        }

        return locations;
    }
}

/// <summary>
/// One reference to a field. A null <see cref="Node"/> means the location did
/// not resolve to a simple name; <see cref="IsCandidate"/> marks unconfirmed
/// textual matches, which callers must treat as unknown evidence.
/// </summary>
internal sealed record FieldReferenceLocation(SimpleNameSyntax? Node, bool IsCandidate);
