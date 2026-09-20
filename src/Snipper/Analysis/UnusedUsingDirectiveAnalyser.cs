namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0019 — Flags using directives the compiler itself proved unnecessary:
/// CS8019 (never used — ordinary or global alike) and CS8933 (an ordinary using
/// that duplicates a global one). Tier 1 (Guaranteed): compiler-computed Hidden
/// diagnostics, surfaced — not re-derived. Harvested from one full-diagnostics
/// call per project: CS8019's "namespace never used" verdict needs whole-file
/// binding (method bodies included), so declaration-phase diagnostics cannot
/// see it — but per-document GetDiagnostics repeats the same full-unit analysis
/// one semantic model at a time.
/// </summary>
public sealed class UnusedUsingDirectiveAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0019"];

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

            progress?.Invoke($"UnusedUsingDirectiveAnalyser: scanning {project.Name}");

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (diagnostic.Id is not ("CS8019" or "CS8933"))
                {
                    continue;
                }

                // Document/root/semantic-model resolution happens only for flagged
                // diagnostics — a tiny subset of the compilation's declarations.
                var document = diagnostic.Location.SourceTree is { } tree ? solution.GetDocument(tree) : null;
                if (document is null || !document.SupportsSyntaxTree)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null || ExclusionEngine.IsNamespaceExcluded(node, semanticModel, _exclusions, cancellationToken))
                {
                    continue;
                }

                var lineSpan = diagnostic.Location.GetLineSpan();
                findings.Add(new SnipperFinding(
                    RuleId: "SNP0019",
                    Title: "Unused Using Directive",
                    Message: BuildMessage(diagnostic.Id, node),
                    Certainty: CertaintyTier.Guaranteed,
                    Category: FindingCategory.UnusedUsingDirective,
                    FilePath: lineSpan.Path ?? string.Empty,
                    LineNumber: lineSpan.StartLinePosition.Line + 1,
                    CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                    Symbol: null));
            }
        }

        return findings;
    }

    /// <summary>
    /// CS8933 always means "ordinary using duplicating a global one". For CS8019
    /// the compiler gives no reason — but when an identical directive (same name,
    /// global/static/alias shape) appears earlier in the same file, the flagged
    /// occurrence is a verbatim duplicate and the message must say so: consumers
    /// deduplicating by namespace would otherwise remove BOTH copies and break
    /// the build (monorepo FP-4, 1.6.1). Exactly the flagged occurrence goes.
    /// </summary>
    private static string BuildMessage(string diagnosticId, SyntaxNode node)
    {
        if (diagnosticId == "CS8933")
        {
            return $"Using directive '{UsingName(node)}' duplicates a global using directive.";
        }

        if (IsVerbatimDuplicateEarlierInFile(node))
        {
            return $"Using directive '{UsingName(node)}' duplicates another using directive in this file — exactly one occurrence must remain.";
        }

        return $"Using directive '{UsingName(node)}' is unnecessary.";
    }

    private static bool IsVerbatimDuplicateEarlierInFile(SyntaxNode node)
    {
        if (node is not UsingDirectiveSyntax usingDirective || usingDirective.Alias is not null)
        {
            return false;
        }

        var name = usingDirective.Name?.ToString();
        if (name is null || usingDirective.Parent is null)
        {
            return false;
        }

        var isGlobal = usingDirective.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword);
        var isStatic = usingDirective.StaticKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword);

        foreach (var sibling in usingDirective.Parent.ChildNodes())
        {
            // Siblings are ordered; reaching the node itself ends the search.
            if (sibling.SpanStart >= usingDirective.SpanStart)
            {
                break;
            }

            if (sibling is UsingDirectiveSyntax earlier
                && earlier.Alias is null
                && earlier.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword) == isGlobal
                && earlier.StaticKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword) == isStatic
                && string.Equals(earlier.Name?.ToString(), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string UsingName(SyntaxNode node)
    {
        return node is UsingDirectiveSyntax usingDirective
            ? usingDirective.Name?.ToString() ?? node.ToString()
            : node.ToString();
    }
}
