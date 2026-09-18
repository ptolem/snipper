namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0025 — Flags explicit method type arguments that type inference infers
/// identically. Sound by construction: the candidate ships only when the
/// speculatively stripped invocation re-binds the identical constructed symbol
/// including inferred type arguments (B1/B2 spike) — so non-generic-overload
/// traps and inference failures are never flagged. Tier 2 (High).
/// </summary>
internal static class RedundantTypeArgumentEvaluator
{
    public static SnipperFinding? TryEvaluate(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(semanticModel);

        // Pre-filter: the invoked name carries an explicit type argument list
        // (plain Foo<T>() or member-access obj.Foo<T>()).
        var invokedName = invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Name
            : invocation.Expression;
        if (invokedName is not GenericNameSyntax genericName
            || genericName.TypeArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        // Nullable-annotated (string?) and dynamic type arguments carry meaning
        // beyond the CLR type: SymbolEqualityComparer.Default ignores nullability
        // annotations, and dynamic is erased to object at runtime. Never strip.
        foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
        {
            if (ContainsNullableAnnotation(typeArgument) || IsDynamic(typeArgument))
            {
                return null;
            }
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol original
            || !original.IsGenericMethod
            || original.TypeArguments.Length == 0)
        {
            return null;
        }

        // Speculation gate: inference must produce the identical constructed symbol.
        var rebound = InvocationSpeculation.RebindWithoutTypeArguments(invocation, genericName, semanticModel);
        if (!SymbolEqualityComparer.Default.Equals(original, rebound))
        {
            return null;
        }

        var lineSpan = genericName.TypeArgumentList.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0025",
            Title: "Redundant Type Arguments",
            Message: $"Type argument list '{genericName.TypeArgumentList}' on '{genericName.Identifier.Text}' is redundant — type inference infers the same signature.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.RedundantTypeArguments,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: original);
    }

    private static bool ContainsNullableAnnotation(TypeSyntax typeArgument)
    {
        if (typeArgument is NullableTypeSyntax)
        {
            return true;
        }

        foreach (var descendant in typeArgument.DescendantNodes())
        {
            if (descendant is NullableTypeSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDynamic(TypeSyntax typeArgument)
    {
        return typeArgument is IdentifierNameSyntax { Identifier.Text: "dynamic" };
    }
}
