namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Conservative syntax verdict on reachability, shared by SNP0002 (which
/// statements to flow-analyse) and SNP0009 (its SNP0002 dedup guard). A
/// statement's start point can be unreachable only when an earlier sibling in
/// an enclosing block or switch section can break fall-through — an exit
/// statement, a branch construct whose every arm exits, or a possibly-infinite
/// loop. When this returns false the statement is provably reachable and
/// control-flow analysis would only confirm it, so callers skip the expensive
/// call. Any doubt returns true (analysis runs), which keeps the verdict
/// parity-exact with unconditional flow analysis at a fraction of the cost.
/// </summary>
internal static class UnreachableCodeGate
{
    public static bool MayStartUnreachable(StatementSyntax statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        for (var current = statement; ;)
        {
            StatementSyntax? next;
            switch (current.Parent)
            {
                case BlockSyntax block:
                    if (PrecedesFallThroughBreak(block.Statements, current))
                    {
                        return true;
                    }

                    // An embedded block sits in the parent block's statement list;
                    // otherwise the block belongs to a structured statement
                    // (if/loop/try/...) or a member body, ending the walk.
                    next = block.Parent is BlockSyntax ? block : block.Parent as StatementSyntax;
                    break;

                case SwitchSectionSyntax section:
                    if (PrecedesFallThroughBreak(section.Statements, current))
                    {
                        return true;
                    }

                    next = (SwitchStatementSyntax)section.Parent!;
                    break;

                default:
                    next = null;
                    break;
            }

            if (next is null)
            {
                return false;
            }

            current = next;
        }
    }

    private static bool PrecedesFallThroughBreak(SyntaxList<StatementSyntax> statements, StatementSyntax current)
    {
        foreach (var sibling in statements)
        {
            if (ReferenceEquals(sibling, current))
            {
                return false;
            }

            if (MayEndUnreachable(sibling))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when control may never reach the end of <paramref name="statement"/>.
    /// Conservative by design: unknown shapes answer true.
    /// </summary>
    private static bool MayEndUnreachable(StatementSyntax statement)
    {
        return statement switch
        {
            ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax
                or ContinueStatementSyntax or GotoStatementSyntax => true,
            YieldStatementSyntax yieldStatement => yieldStatement.ReturnOrBreakKeyword.IsKind(SyntaxKind.BreakKeyword),
            IfStatementSyntax ifStatement => ifStatement.Else is not null
                && MayEndUnreachable(ifStatement.Statement)
                && MayEndUnreachable(ifStatement.Else.Statement),
            // A loop that could run forever blocks fall-through; only a literal
            // false condition proves it cannot (parity-safe conservatism — the
            // compiler's constant folding is deliberately not reimplemented).
            WhileStatementSyntax whileStatement => !IsConstantFalse(whileStatement.Condition),
            DoStatementSyntax doStatement => !IsConstantFalse(doStatement.Condition),
            ForStatementSyntax forStatement => forStatement.Condition is null || !IsConstantFalse(forStatement.Condition),
            ForEachStatementSyntax or ForEachVariableStatementSyntax => false,
            SwitchStatementSyntax => true,
            TryStatementSyntax tryStatement => MayEndUnreachable(tryStatement.Block)
                && tryStatement.Catches.All(catchClause => MayEndUnreachable(catchClause.Block)),
            LockStatementSyntax lockStatement => MayEndUnreachable(lockStatement.Statement),
            UsingStatementSyntax usingStatement => usingStatement.Statement is not null && MayEndUnreachable(usingStatement.Statement),
            FixedStatementSyntax fixedStatement => MayEndUnreachable(fixedStatement.Statement),
            CheckedStatementSyntax checkedStatement => MayEndUnreachable(checkedStatement.Block),
            BlockSyntax block => block.Statements.Any(MayEndUnreachable),
            LabeledStatementSyntax labeledStatement => MayEndUnreachable(labeledStatement.Statement),
            _ => false,
        };
    }

    private static bool IsConstantFalse(ExpressionSyntax expression)
    {
        return expression.IsKind(SyntaxKind.FalseLiteralExpression);
    }
}
