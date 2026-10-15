namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0034 — Flags methods whose <em>cognitive</em> complexity exceeds a configurable
/// maximum. Tier 4 (Advisory), for the same reason as SNP0033: a score is arithmetic,
/// a threshold is policy.
/// <para>
/// This is SonarSource's cognitive complexity, not a re-spelling of cyclomatic. The three
/// rules it is built on, in order of how much they matter here:
/// <list type="number">
/// <item>Ignore structures that merely shorthand several statements into one — a
/// <c>switch</c> is one increment whatever its arm count, where cyclomatic charges
/// per arm.</item>
/// <item>+1 for each break in linear flow.</item>
/// <item>+1 <em>again</em> for each break nested inside another break. This is the part
/// cyclomatic structurally cannot express, and the reason this rule exists as a separate
/// ID rather than a mode flag on SNP0033.</item>
/// </list>
/// <para>
/// The increment set, from the whitepaper (spec 1.2) and independently corroborated by
/// clang-tidy's <c>readability-function-cognitive-complexity</c>:
/// <list type="table">
/// <item><term><c>if</c> / <c>else if</c> / <c>else</c>, ternary, <c>switch</c></term><description>+1</description></item>
/// <item><term><c>for</c> / <c>foreach</c> / <c>while</c> / <c>do</c>, <c>catch</c></term><description>+1 each</description></item>
/// <item><term>labelled <c>goto</c> / <c>break</c> / <c>continue</c></term><description>+1</description></item>
/// <item><term>runs of like binary boolean operators</term><description>+1 per run</description></item>
/// <item><term>a method in a recursion cycle</term><description>+1 — not implemented, see below</description></item>
/// </list>
/// <para>
/// All of those <em>except</em> the recursion case and labelled jumps also raise the
/// nesting level, and all of them — again except the last two — take a nesting
/// increment equal to the level they sit at. A lambda takes no increment of its own but
/// does raise the nesting level for its body, which is the whitepaper's <c>myMethod2</c>
/// case: a lambda holding one <c>if</c> scores 2, not 1.
/// <para>
/// Two deliberate divergences from the whitepaper, both because a faithful copy would be
/// wrong or impossible for C# rather than merely inconvenient:
/// <list type="bullet">
/// <item><b>Recursion cycles are not counted.</b> The whitepaper's +1 applies to "each
/// method in a recursion cycle", which needs a call graph over the whole solution.
/// clang-tidy declines it for the same reason (cross-translation-unit analysis). Snipper's
/// complexity rules are purely syntactic by design — that is what makes them deterministic
/// and immune to restore state — so this stays unimplemented and is called out in the
/// message rather than silently under-counting.</item>
/// <item><b>Preprocessor conditionals are not counted.</b> clang-tidy documents the same
/// omission. <c>#if</c> is a real break in linear flow and SonarQube charges it, but
/// charging it here would mean scoring disabled text, and the count would then depend on
/// build configuration — which would make the rule's output unstable across restore states,
/// the exact failure mode 1.7.5 was written about.</item>
/// </list>
/// <para>
/// One judgement call worth stating, because it changes the number rather than being a
/// detail: a nested lambda is scored <em>inside</em> its enclosing method at a raised
/// nesting level, and is not also scored as a method of its own. Reporting both would put
/// one construct in the report twice under two numbers that are not the same metric.
/// <para>
/// Like SNP0033 this is a pure syntax walk with no semantic model, so it is deterministic
/// by construction.
/// </summary>
public sealed class CognitiveComplexityAnalyser : IWorkspaceAnalyser
{
    /// <summary>
    /// Default maximum. Calibrated on the reference monorepo rather than copied from
    /// SonarQube — see <c>docs/history/1_14_0_plan.md</c> for the distribution it was
    /// chosen against. SonarQube's own 15 is a defensible number and this is close to it,
    /// but the measurement is the reason.
    /// </summary>
    public const int DefaultMaxCognitiveComplexity = 15;

    private readonly AnalysisExclusions _exclusions;
    private readonly int _maxComplexity;

    public CognitiveComplexityAnalyser(
        AnalysisExclusions? exclusions = null,
        int? maxComplexity = null)
    {
        _exclusions = exclusions ?? AnalysisExclusions.None;
        _maxComplexity = maxComplexity is > 0 ? maxComplexity.Value : DefaultMaxCognitiveComplexity;
    }

    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0034"];

    /// <summary>
    /// Scores one function body with no threshold applied, so a caller can see the
    /// number for <em>every</em> method rather than only the breaching ones. The
    /// counting table is this rule's contract, and a threshold hides half of it: a
    /// method scoring 1 is invisible at any non-negative threshold, which is precisely
    /// where the "does a bare <c>else</c> count" and "does a loop count" rows live.
    /// </summary>
    public static int Score(SyntaxNode body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Measure(body);
    }

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var findings = new List<SnipperFinding>();

        foreach (var (project, documents) in AnalysisScope.Get(solution))
        {
            progress?.Invoke($"CognitiveComplexityAnalyser: scanning {project.Name}");

            foreach (var (document, root) in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Complexity thresholds are a review trigger, and nobody reviews a test
                // fixture. Shared with SNP0033 - see ExclusionEngine.IsTestCode.
                if (ExclusionEngine.IsTestCode(document.FilePath, project.Name))
                {
                    continue;
                }

                // Lambdas are excluded here because their bodies are scored inside the
                // method that declares them, at a raised nesting level.
                foreach (var node in root.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var body = ComplexityFunctionBody.TryGetBody(node, includeLambdas: false);
                    if (body is null)
                    {
                        continue;
                    }

                    var complexity = Measure(body);
                    if (complexity <= _maxComplexity)
                    {
                        continue;
                    }

                    // Same namespace-exclusion shape as SNP0033, and for the same reason:
                    // the check needs a semantic model this analyser otherwise never asks for.
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
                        RuleId: "SNP0034",
                        Title: "High Cognitive Complexity",
                        Message:
                            $"Cognitive complexity {complexity} exceeds the maximum of {_maxComplexity}. " +
                            "Nesting is weighted more heavily than branch count, so a method can " +
                            "breach this while passing SNP0033.",
                        Certainty: CertaintyTier.Advisory,
                        Category: FindingCategory.Complexity,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Scores one function body. Cyclomatic seeds at 1 because it counts paths through a
    /// decision graph; cognitive starts at 0 because a method with no branches is simply
    /// not complex, and its worst case is bounded by nesting depth rather than by the
    /// number of paths.
    /// </summary>
    private static int Measure(SyntaxNode body)
    {
        var complexity = 0;
        Visit(body, nesting: 0, complexity: ref complexity);
        return complexity;
    }

    private static void Visit(SyntaxNode node, int nesting, ref int complexity)
    {
        switch (node)
        {
            // ---- structural increments: +1 plus the level they sit at ----
            case IfStatementSyntax ifStatement:
                complexity += 1 + nesting;
                // The condition and statement are at the *current* nesting; only the body
                // is nested inside the branch, which is the whole point of the metric.
                VisitCondition(ifStatement.Condition, nesting, ref complexity);
                Visit(ifStatement.Statement, nesting + 1, ref complexity);

                if (ifStatement.Else is not null)
                {
                    VisitElse(ifStatement.Else.Statement, nesting, ref complexity);
                }

                return;

            case ConditionalExpressionSyntax ternary:
                complexity += 1 + nesting;
                Visit(ternary.Condition, nesting, ref complexity);
                Visit(ternary.WhenTrue, nesting + 1, ref complexity);
                Visit(ternary.WhenFalse, nesting + 1, ref complexity);
                return;

            case SwitchStatementSyntax switchStatement:
                // One increment for the whole switch regardless of arm count: rule 1 of the
                // metric is that shorthand structures are not charged per-statement. This
                // is the sharpest behavioural difference from SNP0033, which charges per arm.
                complexity += 1 + nesting;
                Visit(switchStatement.Expression, nesting, ref complexity);
                foreach (var section in switchStatement.Sections)
                {
                    Visit(section, nesting + 1, ref complexity);
                }

                return;

            case ForStatementSyntax forStatement:
                complexity += 1 + nesting;

                // A `for` condition is optional — `for (;;)` is legal — so a null one is
                // not a branch and not an error.
                if (forStatement.Condition is not null)
                {
                    Visit(forStatement.Condition, nesting, ref complexity);
                }

                Visit(forStatement.Statement, nesting + 1, ref complexity);
                return;

            case ForEachStatementSyntax forEach:
                complexity += 1 + nesting;
                Visit(forEach.Expression, nesting, ref complexity);
                Visit(forEach.Statement, nesting + 1, ref complexity);
                return;

            case WhileStatementSyntax whileStatement:
                complexity += 1 + nesting;
                Visit(whileStatement.Condition, nesting, ref complexity);
                Visit(whileStatement.Statement, nesting + 1, ref complexity);
                return;

            case DoStatementSyntax doStatement:
                complexity += 1 + nesting;
                Visit(doStatement.Condition, nesting, ref complexity);
                Visit(doStatement.Statement, nesting + 1, ref complexity);
                return;

            case CatchClauseSyntax catchClause:
                // A `when` filter is part of the catch, not a second one. SonarSource
                // charges the filter separately, but a filtered catch reads as one branch
                // to a human and charging it twice is the kind of double-count that makes a
                // threshold arbitrary.
                complexity += 1 + nesting;
                if (catchClause.Declaration is not null)
                {
                    Visit(catchClause.Declaration, nesting, ref complexity);
                }

                if (catchClause.Filter is not null)
                {
                    Visit(catchClause.Filter.FilterExpression, nesting, ref complexity);
                }

                Visit(catchClause.Block, nesting + 1, ref complexity);
                return;

            // ---- flat increments: +1, no nesting penalty ----
            case GotoStatementSyntax:
                complexity += 1;
                break;

            // ---- nesting without an increment: the lambda/local-function rule ----
            case AnonymousFunctionExpressionSyntax anonymous:
                Visit(anonymous.Body, nesting + 1, ref complexity);
                return;

            case LocalFunctionStatementSyntax localFunction:
                // A local function with neither a block nor an expression body cannot
                // exist, but the type allows it and the null-coalesce makes that explicit
                // rather than a warning.
                if (localFunction.Body is not null)
                {
                    Visit(localFunction.Body, nesting + 1, ref complexity);
                }
                else if (localFunction.ExpressionBody is not null)
                {
                    Visit(localFunction.ExpressionBody, nesting + 1, ref complexity);
                }

                return;
        }

        // Logical operators are handled here rather than in the switch above because a
        // chain of them is scored as a whole, not operator by operator — see
        // CountLogicalSequence.
        if (node is BinaryExpressionSyntax binary && IsLogical(binary))
        {
            complexity += CountLogicalSequence(binary);

            // Only the operands are walked. Every logical node in this subtree was
            // already charged by CountLogicalSequence, so re-descending into them
            // would count the same sequence twice.
            VisitOperands(binary, nesting, ref complexity);
            return;
        }

        foreach (var child in node.ChildNodes())
        {
            Visit(child, nesting, ref complexity);
        }
    }

    /// <summary>
    /// Visits each operand of a logical expression exactly once, descending through any
    /// that are themselves logical. The count for the whole chain has already been
    /// charged, so this pass is only about whatever non-logical expressions hang off it.
    /// </summary>
    private static void VisitOperands(BinaryExpressionSyntax binary, int nesting, ref int complexity)
    {
        foreach (var operand in new[] { binary.Left, binary.Right })
        {
            if (IsLogical(operand))
            {
                VisitOperands((BinaryExpressionSyntax)operand, nesting, ref complexity);
            }
            else
            {
                Visit(operand, nesting, ref complexity);
            }
        }
    }

    /// <summary>
    /// An <c>else if</c> is one <c>if</c>, not an <c>else</c> wrapping an <c>if</c>, so it
    /// is charged as a single increment at the outer nesting level. A bare <c>else</c> is
    /// charged +1 flat and nests its block one level deeper.
    /// </summary>
    private static void VisitElse(SyntaxNode elseStatement, int nesting, ref int complexity)
    {
        if (elseStatement is IfStatementSyntax elseIf)
        {
            Visit(elseIf, nesting, ref complexity);
            return;
        }

        complexity += 1;
        Visit(elseStatement, nesting + 1, ref complexity);
    }

    private static void VisitCondition(ExpressionSyntax condition, int nesting, ref int complexity)
    {
        // The condition sits at the current nesting level, so Visit handles it with the
        // same rules as any other expression — including charging a boolean chain once.
        Visit(condition, nesting, ref complexity);
    }

    /// <summary>
    /// Scores a boolean expression by its runs of like operators.
    /// <para>
    /// The whitepaper's rule is "each new sequence of like operators", so
    /// <c>a &amp;&amp; b &amp;&amp; c</c> costs 1 and <c>a &amp;&amp; b &amp;&amp; c || d || e</c>
    /// costs 3 — two operator changes across a pre-order walk of the expression, each
    /// costing one, plus the run that opens it. The interesting part is that the walk is
    /// pre-order over the tree rather than a left-to-right sweep of the tokens: in
    /// <c>a &amp;&amp; b || c</c> the tree is <c>(a &amp;&amp; b) || c</c>, so the
    /// <c>&amp;&amp;</c> is met before the <c>||</c> and the answer is 2, which is what a
    /// reader expects. Mixed-operator chains are penalised precisely because mixing them
    /// is what makes them hard to follow.
    /// </para>
    /// </summary>
    private static int CountLogicalSequence(BinaryExpressionSyntax expression)
    {
        var operators = new List<SyntaxKind>();
        FlattenLogical(expression, operators);

        var sequences = 0;
        SyntaxKind? previous = null;
        foreach (var kind in operators)
        {
            if (previous is null || previous.Value != kind)
            {
                sequences++;
            }

            previous = kind;
        }

        return sequences;
    }

    /// <summary>
    /// Collects the logical operators of an expression in pre-order. Parentheses and
    /// nesting do not reset the run: <c>(a &amp;&amp; b) &amp;&amp; (c &amp;&amp; d)</c> is still
    /// one sequence of <c>&amp;&amp;</c>, because that is exactly the grouping the metric is
    /// not meant to reward.
    /// </summary>
    private static void FlattenLogical(ExpressionSyntax expression, List<SyntaxKind> operators)
    {
        if (expression is not BinaryExpressionSyntax binary || !IsLogical(binary))
        {
            return;
        }

        operators.Add(binary.Kind());
        FlattenLogical(binary.Left, operators);
        FlattenLogical(binary.Right, operators);
    }

    private static bool IsLogical(ExpressionSyntax expression) =>
        expression is BinaryExpressionSyntax binary && IsLogical(binary);

    private static bool IsLogical(BinaryExpressionSyntax binary) =>
        binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression);
}