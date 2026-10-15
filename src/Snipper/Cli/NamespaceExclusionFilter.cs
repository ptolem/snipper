namespace Snipper.Cli;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Analysis;
using Snipper.Models;

/// <summary>
/// A finding hidden by a namespace exclusion, and the configured exclusion that hid it.
/// </summary>
internal sealed record NamespaceExcludedFinding(SnipperFinding Finding, string Exclusion);

/// <summary>
/// What namespace exclusion left visible, and what it hid.
/// </summary>
internal sealed record NamespaceFilterResult(
    IReadOnlyList<SnipperFinding> Visible,
    IReadOnlyList<NamespaceExcludedFinding> Excluded);

/// <summary>
/// Applies namespace exclusions to findings after analysis, judging each finding by the
/// namespace its anchor location is declared in.
/// <para>
/// This used to happen inside the analysers: seventeen of them took
/// <see cref="AnalysisExclusions"/> and skipped excluded candidates, each with its own
/// resolution path (symbol, semantic model, or syntax). That made the finding set depend on
/// configuration, so keeping the baseline stable needed a second, exclusions-lifted pass,
/// and the audit could only report an aggregate because a finding's namespace was not
/// recoverable from the finding. Filtering here instead gives one analysis pass whose
/// output already *is* the suppression-independent set, one resolution rule, and an exact
/// per-exclusion attribution.
/// </para>
/// <para>
/// The trade-off is that excluded candidates are now analysed and then discarded, rather
/// than skipped. Usage evidence from excluded code was always gathered, so this only
/// costs the per-candidate checks, never what any other finding depends on.
/// </para>
/// <para>
/// A finding whose location is not in a C# document of the solution — a project file, a
/// lock file, an appsettings file — has no namespace and is never excluded. A clone set
/// (SNP0031/32) is judged by its anchor, as a path glob judges it.
/// </para>
/// </summary>
internal static class NamespaceExclusionFilter
{
    public static async Task<NamespaceFilterResult> ApplyAsync(
        IReadOnlyList<SnipperFinding> findings,
        Solution solution,
        AnalysisExclusions exclusions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(exclusions);

        if (exclusions.Namespaces.Count == 0)
        {
            return new NamespaceFilterResult(findings, []);
        }

        // One lookup per file however many findings land in it. The trees are the ones
        // analysis already parsed, so this is a cache hit, not a re-parse.
        var roots = new Dictionary<string, CompilationUnitSyntax?>(StringComparer.Ordinal);
        var visible = new List<SnipperFinding>(findings.Count);
        var excluded = new List<NamespaceExcludedFinding>();

        foreach (var finding in findings)
        {
            if (!roots.TryGetValue(finding.FilePath, out var root))
            {
                root = await FindCompilationUnitAsync(solution, finding.FilePath, cancellationToken).ConfigureAwait(false);
                roots[finding.FilePath] = root;
            }

            var exclusion = root is not null && FindAnchorNode(root, finding) is { } anchor
                ? exclusions.FindCoveringExclusion(ExclusionEngine.ResolveNamespace(anchor))
                : null;

            if (exclusion is null)
            {
                visible.Add(finding);
            }
            else
            {
                excluded.Add(new NamespaceExcludedFinding(finding, exclusion));
            }
        }

        return new NamespaceFilterResult(visible, excluded);
    }

    private static async Task<CompilationUnitSyntax?> FindCompilationUnitAsync(
        Solution solution,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (filePath.Length == 0)
        {
            return null;
        }

        // A linked file or a multi-targeted project yields several documents for one path.
        // They share the text, so any one of them answers.
        foreach (var documentId in solution.GetDocumentIdsWithFilePath(filePath))
        {
            if (solution.GetDocument(documentId) is { SupportsSyntaxTree: true } document
                && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is CompilationUnitSyntax unit)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>
    /// The node owning the finding's 1-based line and column, or null when the location
    /// lies outside the file (a stale or synthetic location is never guessed at).
    /// </summary>
    private static SyntaxNode? FindAnchorNode(CompilationUnitSyntax root, SnipperFinding finding)
    {
        var lines = root.SyntaxTree.GetText().Lines;
        var lineIndex = finding.LineNumber - 1;
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return null;
        }

        var line = lines[lineIndex];
        var position = line.Start + Math.Clamp(finding.CharacterOffset - 1, 0, line.End - line.Start);

        return root.FindToken(position).Parent ?? root;
    }
}
