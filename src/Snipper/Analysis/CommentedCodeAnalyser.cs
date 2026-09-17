namespace Snipper.Analysis;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snipper.Models;

/// <summary>
/// SNP0020 — Flags blocks of commented-out code (Sonar S125 territory). A candidate
/// is a run of ≥2 consecutive comment lines (or a block comment of ≥2 lines) where
/// at least 60% of lines look like code (statement punctuation or leading keyword).
/// Tier 4 (Advisory): heuristic by nature — prose that mentions syntax stays below
/// the threshold; license headers, doc comments (a different trivia kind), URLs, and
/// TODO/FIXME-style markers are excluded. Syntax-only scan.
/// </summary>
public sealed class CommentedCodeAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    private const int MinimumCommentLines = 2;
    private const double CodeLikeThreshold = 0.6;

    private static readonly Regex KeywordStart = new(
        @"^\s*(if|for|foreach|while|return|var|using|switch|try|catch|else|do|throw|new|await|public|private|internal|protected|static)\b",
        RegexOptions.Compiled);

    private static readonly Regex MarkerStart = new(
        @"^\s*(TODO|FIXME|HACK|NOTE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0020"];

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"CommentedCodeAnalyser: scanning {project.Name}");

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null || root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                foreach (var block in CollectCommentBlocks(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (block.Lines.Count < MinimumCommentLines || IsLicenseHeader(block) || StartsWithMarker(block))
                    {
                        continue;
                    }

                    var codeLike = 0;
                    var total = 0;
                    foreach (var line in block.Lines)
                    {
                        if (line.Length == 0 || line.Contains("://", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        total++;
                        if (line.Contains(';', StringComparison.Ordinal)
                            || line.Contains('{', StringComparison.Ordinal)
                            || line.Contains('}', StringComparison.Ordinal)
                            || KeywordStart.IsMatch(line))
                        {
                            codeLike++;
                        }
                    }

                    if (total < MinimumCommentLines || (double)codeLike / total < CodeLikeThreshold)
                    {
                        continue;
                    }

                    if (ExclusionEngine.IsNamespaceExcluded(block.Anchor, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    var lineSpan = block.Anchor.GetLocation().GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0020",
                        Title: "Commented-Out Code",
                        Message: $"{block.Lines.Count} line(s) of commented-out code.",
                        Certainty: CertaintyTier.Advisory,
                        Category: FindingCategory.CommentedOutCode,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: null));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Groups line-adjacent single-line comments into blocks and yields multi-line
    /// block comments whose stripped text spans ≥2 lines. Doc comments are
    /// DocumentationCommentTrivia — a different kind, never collected here.
    /// </summary>
    private static List<CommentBlock> CollectCommentBlocks(SyntaxNode root)
    {
        var blocks = new List<CommentBlock>();
        List<SyntaxTrivia>? currentRun = null;
        var lastLine = -1;

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            {
                var startLine = trivia.GetLocation().GetLineSpan().StartLinePosition.Line;
                if (currentRun is null || startLine != lastLine + 1)
                {
                    FlushRun(blocks, currentRun);
                    currentRun = [];
                }

                currentRun.Add(trivia);
                lastLine = startLine;
            }
            else if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                FlushRun(blocks, currentRun);
                currentRun = null;
                lastLine = -1;

                var lines = StripBlockComment(trivia.ToString());
                if (lines.Count >= MinimumCommentLines && trivia.Token.Parent is { } anchor)
                {
                    blocks.Add(new CommentBlock(lines, anchor, trivia.GetLocation().GetLineSpan().StartLinePosition.Line));
                }
            }

            // All other trivia kinds (EOL, whitespace) are ignored: adjacency is
            // decided by comment line numbers alone.
        }

        FlushRun(blocks, currentRun);
        return blocks;
    }

    private static void FlushRun(List<CommentBlock> blocks, List<SyntaxTrivia>? run)
    {
        if (run is null || run.Count < MinimumCommentLines || run[0].Token.Parent is not { } anchor)
        {
            return;
        }

        var lines = new List<string>(run.Count);
        foreach (var trivia in run)
        {
            lines.Add(StripLineComment(trivia.ToString()));
        }

        blocks.Add(new CommentBlock(lines, anchor, run[0].GetLocation().GetLineSpan().StartLinePosition.Line));
    }

    private static string StripLineComment(string text)
    {
        return text.StartsWith("//", StringComparison.Ordinal) ? text[2..].Trim() : text.Trim();
    }

    private static List<string> StripBlockComment(string text)
    {
        var body = text.StartsWith("/*", StringComparison.Ordinal) ? text[2..] : text;
        if (body.EndsWith("*/", StringComparison.Ordinal))
        {
            body = body[..^2];
        }

        var lines = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("*", StringComparison.Ordinal))
            {
                line = line[1..].Trim();
            }

            lines.Add(line);
        }

        return lines;
    }

    private static bool IsLicenseHeader(CommentBlock block)
    {
        if (block.StartLine > 4)
        {
            return false;
        }

        foreach (var line in block.Lines)
        {
            if (line.Contains("copyright", StringComparison.OrdinalIgnoreCase)
                || line.Contains("licensed under", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithMarker(CommentBlock block)
    {
        foreach (var line in block.Lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            return MarkerStart.IsMatch(line);
        }

        return false;
    }

    private sealed record CommentBlock(List<string> Lines, SyntaxNode Anchor, int StartLine);
}
