namespace Snipper.Analysis;

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Shared speculation helpers for the redundancy rules (SNP0022/SNP0025): rewrite
/// an invocation's syntax without inserting it into the tree, then ask the semantic
/// model how the rewritten form would bind. The flag may ship only when the
/// speculatively re-bound symbol equals the original — this is what makes
/// "redundant" claims sound against overload-rebinding traps (spike-proven
/// 2026-09-18: traps rebind to a different symbol; safe cases compare equal).
/// Invocations touching a conditional access (?.) never speculate: the rewritten
/// form would carry a MemberBindingExpression detached from its conditional-access
/// parent, which crashes Roslyn's speculative binder with a NullReferenceException
/// (monorepo crash 2026-09-18) — and null-propagation changes what the rewrite
/// even means, so these invocations are never redundancy candidates anyway.
/// </summary>
internal static class InvocationSpeculation
{
    // Per-tree memo: does the document contain any conditional access at all?
    // Most trees don't — their candidate invocations skip the subtree scan
    // entirely (the 1.4.1 guard cost SNP0022/25/26 ~7s on the monorepo). The
    // accept/reject set is unchanged: the tree-level answer is a pure superset
    // gate in front of the exact subtree scan.
    private static readonly ConditionalWeakTable<SyntaxTree, StrongBox<bool>> ConditionalAccessPresence = new();
    /// <summary>
    /// Re-binds <paramref name="invocation"/> as if the argument at
    /// <paramref name="argumentIndex"/> were absent. Null when the rewritten
    /// invocation does not resolve to a method at all.
    /// </summary>
    public static IMethodSymbol? RebindWithoutArgument(
        InvocationExpressionSyntax invocation,
        int argumentIndex,
        SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (ContainsConditionalAccess(invocation))
        {
            return null;
        }

        var rewrittenArguments = invocation.ArgumentList.Arguments.RemoveAt(argumentIndex);
        var rewritten = invocation.WithArgumentList(invocation.ArgumentList.WithArguments(rewrittenArguments));
        return semanticModel
            .GetSpeculativeSymbolInfo(invocation.SpanStart, rewritten, SpeculativeBindingOption.BindAsExpression)
            .Symbol as IMethodSymbol;
    }

    /// <summary>
    /// Re-binds <paramref name="invocation"/> as if the explicit type argument list
    /// were absent (plain <c>Foo&lt;T&gt;()</c> and member-access <c>obj.Foo&lt;T&gt;()</c>
    /// forms). Null when inference cannot produce a method without the list.
    /// </summary>
    public static IMethodSymbol? RebindWithoutTypeArguments(
        InvocationExpressionSyntax invocation,
        GenericNameSyntax genericName,
        SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(genericName);
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (ContainsConditionalAccess(invocation))
        {
            return null;
        }

        var plainName = SyntaxFactory.IdentifierName(genericName.Identifier);
        var rewritten = invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? invocation.WithExpression(memberAccess.WithName(plainName))
            : invocation.WithExpression(plainName);
        return semanticModel
            .GetSpeculativeSymbolInfo(invocation.SpanStart, rewritten, SpeculativeBindingOption.BindAsExpression)
            .Symbol as IMethodSymbol;
    }

    /// <summary>
    /// Syntax-only guard, checked before any speculative call: true when the
    /// invocation subtree contains a conditional access or member binding —
    /// either as the invoked expression (<c>receiver?.Foo(args)</c>,
    /// <c>receiver?.Helper.Foo&lt;T&gt;(args)</c>) or nested inside an argument.
    /// Trees with no conditional access anywhere are pre-cleared by a memoized
    /// tree-level probe, so clean invocations pay O(1) instead of O(subtree).
    /// </summary>
    private static bool ContainsConditionalAccess(SyntaxNode node)
    {
        return TreeMayContainConditionalAccess(node.SyntaxTree)
            && node.DescendantNodesAndSelf().Any(static n => n is ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax);
    }

    private static bool TreeMayContainConditionalAccess(SyntaxTree tree)
    {
        return ConditionalAccessPresence.GetValue(
            tree,
            static t => new StrongBox<bool>(
                t.GetRoot().DescendantNodes().Any(static n => n is ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax))).Value;
    }
}
