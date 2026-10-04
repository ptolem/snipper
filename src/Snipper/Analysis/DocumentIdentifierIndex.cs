namespace Snipper.Analysis;

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Document-scoped index of identifier positions, bucketed by identifier text.
///
/// Backs the reference test for symbols that cannot escape their declaring file:
/// locals, parameters, and local functions. Those are unreachable from any other
/// document by construction, so routing them through solution-wide
/// <c>SymbolFinder</c> is pure overhead — it assembles cross-project candidate
/// sets and de-duplicates symbols it will then discard. Measured on the sample
/// app, SNP0009's reference scans were its dominant cost.
///
/// The bucket index is built with one walk per document and then answers each
/// candidate with a short list walk, instead of one full walk per candidate.
/// Binding an identifier and comparing symbols is exactly the test
/// <c>SymbolFinder</c> performs for these symbols: a usage of the local is an
/// identifier whose symbol <em>is</em> the local. Shadowed names and aliases bind
/// to different symbols and correctly do not count as reads.
/// </summary>
internal sealed class DocumentIdentifierIndex
{
    private readonly Dictionary<string, List<SimpleNameSyntax>> _positionsByName;

    /// <summary>
    /// Per-root memo. Two analysers (SNP0009 unused parameters and SNP0016 unused locals)
    /// each built this from the same document root in the same run - two full
    /// <c>DescendantNodes()</c> walks and a fresh <c>List</c> per distinct identifier name,
    /// roughly 150 lists per document, for an index that is a pure function of the root.
    ///
    /// Keyed on the root NODE rather than the SyntaxTree so a caller passing a non-root
    /// subtree still gets an index built from exactly that subtree; document roots are
    /// cached per (document, compilation), so the shared case hits by reference identity.
    /// Held weakly, and a lost race just recomputes the same value.
    /// </summary>
    private static readonly ConditionalWeakTable<SyntaxNode, DocumentIdentifierIndex> ByRoot = new();

    private DocumentIdentifierIndex(Dictionary<string, List<SimpleNameSyntax>> positionsByName)
    {
        _positionsByName = positionsByName;
    }

    public static DocumentIdentifierIndex Build(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return ByRoot.GetValue(root, static node => Create(node));
    }

    private static DocumentIdentifierIndex Create(SyntaxNode root)
    {

        Dictionary<string, List<SimpleNameSyntax>>? positionsByName = null;
        foreach (var node in root.DescendantNodes())
        {
            if (node is not SimpleNameSyntax simpleName)
            {
                continue;
            }

            positionsByName ??= new Dictionary<string, List<SimpleNameSyntax>>(StringComparer.Ordinal);
            var text = simpleName.Identifier.Text;
            if (!positionsByName.TryGetValue(text, out var positions))
            {
                positions = [];
                positionsByName[text] = positions;
            }

            positions.Add(simpleName);
        }

        return new DocumentIdentifierIndex(positionsByName ?? []);
    }

    /// <summary>
    /// True when at least one identifier in the document binds to
    /// <paramref name="symbol"/>.
    /// </summary>
    public bool HasReference(SemanticModel semanticModel, ISymbol symbol, string name)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(name);

        if (!_positionsByName.TryGetValue(name, out var positions))
        {
            return false;
        }

        // Bind only the identifiers that spell this name. Each GetSymbolInfo is a
        // semantic-model call, so the bucket is deliberately short: it holds one
        // entry per textual occurrence, not one per node in the file.
        foreach (var position in positions)
        {
            if (SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(position).Symbol, symbol))
            {
                return true;
            }
        }

        return false;
    }
}
