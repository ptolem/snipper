namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snipper.Models;

/// <summary>
/// SNP0002 — Flags statements that can never execute because a preceding
/// statement unconditionally exits (return/throw/break/continue/goto).
/// Tier 1 (Guaranteed): control-flow proven via Roslyn flow analysis.
/// </summary>
public sealed class UnreachableCodeAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);

        // Sequential binding: workspace compilations are built with
        // ConcurrentBuild=false; concurrent flow analysis/binding is unsupported.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"UnreachableCodeAnalyser: scanning {project.Name}");

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

                AnalyzeDocument(semanticModel, root, findings, _exclusions, cancellationToken);
            }
        }

        return findings;
    }

    private static void AnalyzeDocument(
        SemanticModel semanticModel,
        SyntaxNode root,
        List<SnipperFinding> findings,
        AnalysisExclusions exclusions,
        CancellationToken cancellationToken)
    {
        var reportedSpans = new List<TextSpan>();

        foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (block.Statements.Count < 2 || IsWithinReportedSpan(block.Span, reportedSpans))
            {
                continue;
            }

            // Correction: ControlFlowAnalysis exposes no UnreachableStatements API;
            // reverting to per-statement analysis with early exit at the first dead
            // statement (the only reachable prefix is analyzed in the common case).
            foreach (var statement in block.Statements)
            {
                // Local function declarations are hoisted: they can be called from
                // anywhere in the method, so placement after a return/throw is an
                // idiom ("helpers at the bottom"), not dead code.
                if (IsHoistedLocalFunction(statement))
                {
                    continue;
                }

                // Empty statements (a stray `;` — commonly trailing a bottom-declared
                // local function) carry no operation. Flagging one reads as "the local
                // function is dead" and is noise unworthy of a Guaranteed finding.
                if (statement is EmptyStatementSyntax)
                {
                    continue;
                }

                var flow = semanticModel.AnalyzeControlFlow(statement);
                if (flow is null || flow.StartPointIsReachable)
                {
                    continue;
                }

                if (!ExclusionEngine.IsNamespaceExcluded(statement, semanticModel, exclusions, cancellationToken))
                {
                    var lineSpan = statement.GetLocation().GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0002",
                        Title: "Unreachable Code",
                        Message: "Statement can never execute; a preceding statement unconditionally exits the block.",
                        Certainty: CertaintyTier.Guaranteed,
                        Category: FindingCategory.UnreachableCode,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: null));
                }

                // Everything after the first unreachable statement in this block is
                // dead too; report once and skip descendants to avoid noise.
                reportedSpans.Add(TextSpan.FromBounds(statement.SpanStart, block.Span.End));
                break;
            }
        }
    }

    private static bool IsHoistedLocalFunction(StatementSyntax statement)
    {
        // A label can wrap the declaration (`done: static void H() { }`) — the
        // local function is still hoisted, so unwrap labels (possibly nested)
        // before judging.
        return statement is LocalFunctionStatementSyntax
            || statement is LabeledStatementSyntax labeled && IsHoistedLocalFunction(labeled.Statement);
    }

    private static bool IsWithinReportedSpan(TextSpan span, List<TextSpan> reportedSpans)
    {
        foreach (var reported in reportedSpans)
        {
            if (reported.Contains(span))
            {
                return true;
            }
        }

        return false;
    }
}
