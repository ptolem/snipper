namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// SNP0031 - syntax-only token shingle index for duplicate-fragment detection.
/// Each file becomes a normalized token stream (identifiers, literals and
/// interpolated text abstracted; keywords and punctuation verbatim), then every
/// fixed-width window of that stream is hashed into a shared bucket table.
///
/// No semantic model is ever constructed: the engine reads syntax only, so it
/// adds no binding time and is safe to run alongside the other passes.
///
/// Locations are keyed by file PATH rather than document, so a linked/shared
/// file or a multi-TFM document contributes a single copy of its content. The
/// accepted consequence is that intra-file duplication is not reported
/// (dupFinder reports it; Snipper v1 does not).
/// </summary>
internal static class TokenShingleIndex
{
    /// <summary>One occurrence of a window: where it starts, and on which line.</summary>
    internal readonly record struct Location(string Path, int TokenIndex, int Line, int Character);

    /// <summary>Bucket table: window hash to every distinct location holding it.</summary>
    internal sealed record Index(IReadOnlyDictionary<int, List<Location>> Windows);

    private const string IdentifierToken = "ID";
    private const string EscapedIdentifierToken = "ESCID";
    private const string StringToken = "STR";
    private const string CharToken = "CHR";
    private const string NumberToken = "NUM";

    /// <summary>
    /// Subtrees whose tokens are per-project boilerplate and never signal
    /// duplication: identical using lists across many projects would otherwise
    /// dominate the shared-window buckets (measured: 3,521 colliding buckets
    /// from near-identical GlobalUsings.cs files across 82 projects).
    /// </summary>
    private static bool IsExcludedSubtree(SyntaxNode node)
    {
        return node is UsingDirectiveSyntax or ExternAliasDirectiveSyntax;
    }

    /// <summary>
    /// Normalized token text for a file, in source order. Trivia and comments
    /// never appear (only tokens are collected). Using and extern-alias
    /// subtrees are skipped entirely; namespace declarations are kept, since
    /// their bodies are the signal.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Collect(root).Tokens;
    }

    /// <summary>
    /// Line and character for each normalized token, positionally aligned with
    /// <see cref="Tokenize"/>.
    /// </summary>
    public static IReadOnlyList<(int Line, int Character)> TokenLines(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Collect(root).Lines;
    }

    private static (List<string> Tokens, List<(int Line, int Character)> Lines) Collect(SyntaxNode root)
    {
        var tokens = new List<string>();
        var lines = new List<(int Line, int Character)>();
        Walk(root, tokens, lines);
        return (tokens, lines);
    }

    /// <summary>
    /// Depth-first collection of every token outside the excluded subtrees.
    /// Recursion on <see cref="SyntaxNode.ChildNodes"/> is deliberate: it is the
    /// only traversal that lets a whole using block be pruned.
    /// </summary>
    private static void Walk(
        SyntaxNode node,
        List<string> tokens,
        List<(int Line, int Character)> lines)
    {
        if (IsExcludedSubtree(node))
        {
            return;
        }

        foreach (var child in node.ChildNodes())
        {
            if (IsExcludedSubtree(child))
            {
                continue;
            }

            foreach (var token in child.ChildTokens())
            {
                tokens.Add(Normalize(token));
                var start = token.GetLocation().GetLineSpan().StartLinePosition;
                lines.Add((start.Line + 1, start.Character + 1));
            }

            Walk(child, tokens, lines);
        }
    }

    private static string Normalize(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            // Verbatim and \u-escaped identifiers still denote a user-chosen name,
            // so they normalize the same as plain identifiers.
            return token.Text.StartsWith('@') || token.Text.StartsWith("\\u", StringComparison.Ordinal)
                ? EscapedIdentifierToken
                : IdentifierToken;
        }

        return token.Kind() switch
        {
            SyntaxKind.StringLiteralToken => StringToken,
            SyntaxKind.Utf8StringLiteralToken => StringToken,
            SyntaxKind.SingleLineRawStringLiteralToken => StringToken,
            SyntaxKind.MultiLineRawStringLiteralToken => StringToken,
            SyntaxKind.InterpolatedStringTextToken => StringToken,
            SyntaxKind.CharacterLiteralToken => CharToken,
            SyntaxKind.NumericLiteralToken => NumberToken,
            _ => token.Text,
        };
    }

    /// <summary>
    /// Build the bucket table over every supplied file. Identical
    /// (path, tokenIndex) locations are recorded once, which is what makes a
    /// linked file or multi-TFM document contribute only one copy.
    /// </summary>
    public static Index Build(
        IEnumerable<(string Path, SyntaxNode Root)> sources,
        int windowTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowTokens, 1);

        var windows = new Dictionary<int, List<Location>>();
        var seen = new HashSet<(string Path, int TokenIndex)>();

        foreach (var (path, root) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tokens = Tokenize(root);
            if (tokens.Count < windowTokens)
            {
                continue;
            }

            var lines = TokenLines(root);

            for (var start = 0; start + windowTokens <= tokens.Count; start++)
            {
                if (!seen.Add((path, start)))
                {
                    continue;
                }

                var hash = Hash(tokens, start, windowTokens);
                if (!windows.TryGetValue(hash, out var bucket))
                {
                    bucket = [];
                    windows[hash] = bucket;
                }

                var position = lines[start];
                bucket.Add(new Location(path, start, position.Line, position.Character));
            }
        }

        // Deterministic bucket contents regardless of discovery order, so the
        // reported "first other occurrence" never depends on scheduling.
        foreach (var bucket in windows.Values)
        {
            bucket.Sort(static (left, right) =>
            {
                var byPath = string.CompareOrdinal(left.Path, right.Path);
                return byPath != 0 ? byPath : left.TokenIndex.CompareTo(right.TokenIndex);
            });
        }

        return new Index(windows);
    }

    /// <summary>
    /// FNV-1a over the window's normalized token texts, folded character by
    /// character. Collisions are possible at this scale, which is why a bucket
    /// match is only a candidate: <c>DuplicateFragmentAnalyser.Extend</c> proves
    /// the window token by token before extending it, so a collision costs time
    /// and never correctness. <b>Do not "optimise" that verification away</b> -
    /// it is the only thing standing between a 32-bit hash and a reported clone.
    ///
    /// This must hash the token TEXT, never <see cref="string.GetHashCode()"/>.
    /// .NET randomizes string hashing per process, so folding those values would
    /// give a different bucket partitioning on every run: the same window would
    /// land in a different bucket, candidate pairs would be generated in a
    /// different order, and match collapsing would then pick different maximal
    /// fragments. That made SNP0031/SNP0032 output vary between runs on
    /// identical input. FNV-1a over characters is stable for the life of the
    /// binary, which is what deterministic output requires.
    /// </summary>
    internal static int Hash(IReadOnlyList<string> tokens, int start, int length)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        const uint tokenTerminator = 0x1F;

        var hash = offsetBasis;
        for (var index = start; index < start + length; index++)
        {
            foreach (var character in tokens[index])
            {
                hash ^= character;
                hash *= prime;
            }

            // Terminator per token, so ("ab","c") and ("a","bc") cannot fold to
            // the same value and pull unrelated windows into one bucket.
            hash ^= tokenTerminator;
            hash *= prime;
        }

        return unchecked((int)hash);
    }
}