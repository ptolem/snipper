namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0026 — Flags a cast the type system proves redundant: an identity
/// conversion, where the operand is already of the target type. Removing an
/// identity cast cannot change the expression's static type, so enclosing
/// overload resolution and inference are untouched — sound by the type system,
/// no speculation gate required (3C spike: IsIdentity holds only for
/// same-type casts; upcasts classify IsReference, and numeric/boxing/
/// unboxing/user-defined conversions are never identity, so none of them can
/// reach the flag). v1 deliberately excludes implicit reference upcasts:
/// stripping one changes the static type, which can re-bind overloads and flip
/// var inference — that variant needs a rebind gate that has not earned its
/// complexity yet.
/// </summary>
internal static class RedundantCastEvaluator
{
    public static SnipperFinding? TryEvaluate(
        CastExpressionSyntax cast,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var targetType = semanticModel.GetTypeInfo(cast.Type, cancellationToken).Type;
        if (targetType is null || targetType.TypeKind is TypeKind.Dynamic or TypeKind.Error)
        {
            return null;
        }

        var conversion = semanticModel.ClassifyConversion(cast.Expression, targetType);
        if (!conversion.Exists || !conversion.IsIdentity)
        {
            return null;
        }

        var lineSpan = cast.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0026",
            Title: "Redundant Cast",
            Message: $"Cast to '{targetType.ToDisplayString()}' is redundant — the expression is already of that type.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.RedundantCast,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: targetType);
    }
}
