namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0033 — Flags methods whose cyclomatic complexity exceeds a configurable
/// maximum. Tier 4 (Advisory): unlike every other rule in the tool, nothing here
/// is a provable defect. "This function has 23 branches" is arithmetic; "23 is too
/// many" is policy. The count is reported in the message so the finding can be
/// triaged without re-running anything.
/// <para>
/// Parity target is ReSharper's classical cyclomatic complexity, and that means
/// matching its <em>non-counts</em> as closely as its counts — the places below
/// marked 0 are where a naive implementation over-reports, usually by a factor of
/// two on ordinary code:
/// <list type="table">
/// <item><term><c>if</c> (and each <c>else if</c>, which is itself an <c>if</c>)</term><description>+1</description></item>
/// <item><term>bare <c>else</c></term><description>0 — the negative branch is already the <c>if</c></description></item>
/// <item><term><c>while</c> / <c>do</c> / <c>for</c> / <c>foreach</c></term><description>+1 each</description></item>
/// <item><term><c>case</c> label</term><description>+1 each</description></item>
/// <item><term><c>default</c> label</term><description>0</description></item>
/// <item><term><c>catch</c> clause, and its <c>when</c> filter</term><description>+1 each, so a filtered catch is +2</description></item>
/// <item><term><c>&amp;&amp;</c> / <c>||</c></term><description>+1 each</description></item>
/// <item><term><c>?:</c></term><description>+1</description></item>
/// <item><term>switch-expression arm</term><description>+1 each; the <c>_</c> arm is 0</description></item>
/// <item><term><c>?.</c> null-conditional</term><description>0 — a deliberate deviation from some readings; see the note below</description></item>
/// <item><term><c>??</c>, <c>??=</c>, <c>try</c>, <c>finally</c>, <c>throw</c>, <c>goto</c>, <c>is</c>, <c>as</c></term><description>0</description></item>
/// </list>
/// <para>
/// <c>?.</c> is the one judgement call worth stating, because it is not free:
/// a null-conditional does introduce a branch, and counting it would raise every
/// method that touches a nullable API. ReSharper's classical metric does not count
/// it, and matching that is the parity commitment here. It is a single documented
/// line to change if that call is ever revisited — the table is the contract.
/// <para>
/// Every callable body is scored independently: methods, constructors, destructors,
/// operators, accessors, local functions, and lambdas each get their own count, and
/// a containing method is <em>not</em> charged for a lambda or local function nested
/// inside it. That is ReSharper's model and it also avoids double-charging the one
/// construct most likely to be large. An expression-bodied member is scored from its
/// expression; an accessor is never scored twice as part of its property.
/// <para>
/// The count is purely syntactic: no semantic model, no binding, no cross-project
/// state. That makes it deterministic by construction — unlike SNP0019, whose output
/// is inherited from a producer-completion order — and it cannot be perturbed by
/// restore state.
/// </summary>
public sealed class CyclomaticComplexityAnalyser : IWorkspaceAnalyser
{
    /// <summary>ReSharper's own default for its complexity inspection.</summary>
    public const int DefaultMaxComplexity = 15;

    private readonly AnalysisExclusions _exclusions;
    private readonly int _maxComplexity;

    public CyclomaticComplexityAnalyser(AnalysisExclusions? exclusions = null, int maxComplexity = DefaultMaxComplexity)
    {
        _exclusions = exclusions ?? AnalysisExclusions.None;
        _maxComplexity = maxComplexity > 0 ? maxComplexity : DefaultMaxComplexity;
    }

    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0033"];

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

            progress?.Invoke($"CyclomaticComplexityAnalyser: scanning {project.Name}");

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                // One walk per document, scoring each callable body it finds. The
                // inner Measure walk prunes nested function scopes, so a lambda is
                // counted once as itself and never again as part of its parent.
                foreach (var node in root.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var body = GetFunctionBody(node);
                    if (body is null)
                    {
                        continue;
                    }

                    var complexity = Measure(body);
                    if (complexity <= _maxComplexity)
                    {
                        continue;
                    }

                    // Namespace exclusion is opt-in and needs a semantic model, which this analyser
                    // otherwise never asks for. With no exclusions configured the check cannot
                    // change the outcome, so it is skipped rather than paid for.
                    if (_exclusions.Namespaces.Count > 0)
                    {
                        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        if (model is null
                            || ExclusionEngine.IsNamespaceExcluded(node, model, _exclusions, cancellationToken))
                        {
                            continue;
                        }
                    }

                    var lineSpan = node.GetLocation().GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0033",
                        Title: "High Cyclomatic Complexity",
                        Message: $"Cyclomatic complexity {complexity} exceeds the maximum of {_maxComplexity}.",
                        Certainty: CertaintyTier.Advisory,
                        Category: FindingCategory.Complexity,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: null));
                }
            }
        }

        // Document order is already deterministic — this is a syntax walk, not a
        // diagnostic enumeration — but findings from different projects interleave,
        // so the exit sort is what makes the report byte-stable.
        return findings
            .OrderBy(static f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(static f => f.LineNumber)
            .ThenBy(static f => f.CharacterOffset)
            .ThenBy(static f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(static f => f.Message, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The body of <paramref name="node"/> if it introduces a scope that is scored
    /// on its own, otherwise null.
    /// </summary>
    private static SyntaxNode? GetFunctionBody(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Body ?? (SyntaxNode?)method.ExpressionBody,
        ConstructorDeclarationSyntax constructor => constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody,
        DestructorDeclarationSyntax destructor => destructor.Body ?? (SyntaxNode?)destructor.ExpressionBody,
        OperatorDeclarationSyntax op => op.Body ?? (SyntaxNode?)op.ExpressionBody,
        ConversionOperatorDeclarationSyntax conversion => conversion.Body ?? (SyntaxNode?)conversion.ExpressionBody,
        LocalFunctionStatementSyntax local => local.Body ?? (SyntaxNode?)local.ExpressionBody,
        AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody,

        // An expression-bodied property or indexer is a single implicit accessor. One
        // with a real accessor list is scored through those accessors, never here, so
        // it cannot be counted twice.
        PropertyDeclarationSyntax property when property.AccessorList is null => property.ExpressionBody,
        IndexerDeclarationSyntax indexer when indexer.AccessorList is null => indexer.ExpressionBody,

        SimpleLambdaExpressionSyntax simple => simple.Body,
        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
        AnonymousMethodExpressionSyntax anonymous => anonymous.Body,

        _ => null,
    };

    /// <summary>
    /// Counts decision points in one function body, starting from ReSharper's
    /// baseline of 1. Nested lambdas and local functions are skipped entirely:
    /// they are scored as their own scopes, so descending into them here would
    /// charge the same branches twice.
    /// </summary>
    private static int Measure(SyntaxNode body)
    {
        var complexity = 1;

        foreach (var node in body.DescendantNodes(static child => !IsFunctionScope(child)))
        {
            complexity += CountDecisionPoints(node);
        }

        return complexity;
    }

    private static bool IsFunctionScope(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    private static int CountDecisionPoints(SyntaxNode node) => node switch
    {
        IfStatementSyntax => 1,
        WhileStatementSyntax => 1,
        DoStatementSyntax => 1,
        ForStatementSyntax => 1,
        ForEachStatementSyntax => 1,

        // case labels count; the default label does not.
        CasePatternSwitchLabelSyntax => 1,
        CaseSwitchLabelSyntax => 1,

        // A catch is a branch, and a filtered catch is two.
        CatchClauseSyntax => 1,
        CatchFilterClauseSyntax => 1,

        // Short-circuiting is a branch each.
        BinaryExpressionSyntax binary
            when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) => 1,

        ConditionalExpressionSyntax => 1,

        // Every arm is a decision; the `_` arm is the fallthrough, not a branch.
        SwitchExpressionArmSyntax arm when !arm.Pattern.IsKind(SyntaxKind.DiscardPattern) => 1,

        _ => 0,
    };
}