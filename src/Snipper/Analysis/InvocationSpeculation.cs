namespace Snipper.Analysis;

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
/// </summary>
internal static class InvocationSpeculation
{
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

        var plainName = SyntaxFactory.IdentifierName(genericName.Identifier);
        var rewritten = invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? invocation.WithExpression(memberAccess.WithName(plainName))
            : invocation.WithExpression(plainName);
        return semanticModel
            .GetSpeculativeSymbolInfo(invocation.SpanStart, rewritten, SpeculativeBindingOption.BindAsExpression)
            .Symbol as IMethodSymbol;
    }
}
