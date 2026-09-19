namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0026 (upcast variant) — Flags an explicit cast that merely restates an
/// implicit reference upcast: stripping it changes the expression's static type,
/// so flagging is sound only where that change cannot re-bind anything. Two
/// provably safe shapes: (1) the cast IS a whole argument — an overload-rebind
/// gate speculatively re-binds the enclosing invocation/object-creation with the
/// operand spliced in and requires the SAME method symbol (constructed generics
/// included, so <c>Echo&lt;Base&gt;</c> vs <c>Echo&lt;Derived&gt;</c> fails the
/// gate); (2) a fixed-target context whose target type equals the cast type
/// (explicitly typed declaration/assignment, matching return target). var
/// declarations, casts nested inside larger expressions, lambdas, and anything
/// touching conditional access (the 1.4.1 crash guard) never reach speculation.
/// </summary>
internal static class RedundantUpcastEvaluator
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

        // Upcasts only: implicit reference conversions. Identity casts are
        // RedundantCastEvaluator's territory; boxing/numeric/user-defined
        // conversions all carry real semantics.
        var conversion = semanticModel.ClassifyConversion(cast.Expression, targetType);
        if (!conversion.Exists || !conversion.IsImplicit || !conversion.IsReference)
        {
            return null;
        }

        // Typeless operands (null/default literals) have no static type to fall
        // back to — stripping changes what the expression even binds as.
        var operandType = semanticModel.GetTypeInfo(cast.Expression, cancellationToken).Type;
        if (operandType is null || operandType.TypeKind is TypeKind.Dynamic or TypeKind.Error)
        {
            return null;
        }

        var provablySafe = cast.Parent switch
        {
            ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
                => PassesRebindGate(cast, invocation, semanticModel, cancellationToken),
            ArgumentSyntax { Parent: ArgumentListSyntax { Parent: ObjectCreationExpressionSyntax creation } }
                => PassesRebindGate(cast, creation, semanticModel, cancellationToken),
            EqualsValueClauseSyntax equalsValue when equalsValue.Value == cast
                => IsTypedDeclarationMatch(equalsValue, targetType, semanticModel, cancellationToken),
            AssignmentExpressionSyntax assignment when assignment.Right == cast
                => TypeEquals(semanticModel.GetTypeInfo(assignment.Left, cancellationToken).Type, targetType),
            ReturnStatementSyntax returnStatement when returnStatement.Expression == cast
                => EnclosingReturnTypeMatches(returnStatement, targetType, semanticModel, cancellationToken),
            ArrowExpressionClauseSyntax arrow when arrow.Expression == cast
                => EnclosingReturnTypeMatches(arrow, targetType, semanticModel, cancellationToken),
            _ => false,
        };

        if (!provablySafe)
        {
            return null;
        }

        var lineSpan = cast.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0026",
            Title: "Redundant Cast",
            Message: $"Cast to '{targetType.ToDisplayString()}' is redundant — the operand already converts to it implicitly.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.RedundantCast,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: targetType);
    }

    /// <summary>
    /// Rewrite the enclosing invocation/creation with the operand spliced in for
    /// the cast, speculatively re-bind it, and require the identical method
    /// symbol. <see cref="SymbolEqualityComparer.Default"/> compares constructed
    /// forms, so a generic re-inference to different type arguments fails safe.
    /// </summary>
    private static bool PassesRebindGate(
        CastExpressionSyntax cast,
        ExpressionSyntax enclosing,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // The 1.4.1 crash guard: never speculate trees touching conditional access.
        if (InvocationSpeculation.TouchesConditionalAccess(enclosing))
        {
            return false;
        }

        if (semanticModel.GetSymbolInfo(enclosing, cancellationToken).Symbol is not IMethodSymbol original)
        {
            return false;
        }

        var replacement = SyntaxFactory.ParenthesizedExpression(cast.Expression.WithoutTrivia());
        var rewritten = enclosing.ReplaceNode(cast, replacement);
        var rebound = semanticModel
            .GetSpeculativeSymbolInfo(enclosing.SpanStart, rewritten, SpeculativeBindingOption.BindAsExpression)
            .Symbol as IMethodSymbol;

        return rebound is not null && SymbolEqualityComparer.Default.Equals(rebound, original);
    }

    /// <summary>
    /// <c>T x = (T)e;</c> — an explicitly typed local/field declaration whose
    /// declared type equals the cast type. var is excluded: stripping would flip
    /// the inferred type.
    /// </summary>
    private static bool IsTypedDeclarationMatch(
        EqualsValueClauseSyntax equalsValue,
        ITypeSymbol targetType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (equalsValue.Parent is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration }
            || declaration.Type.IsVar)
        {
            return false;
        }

        return TypeEquals(semanticModel.GetTypeInfo(declaration.Type, cancellationToken).Type, targetType);
    }

    /// <summary>
    /// The return target of the enclosing method, property, or accessor must
    /// equal the cast type. Lambdas are excluded: their delegate type is
    /// inferred from usage, so stripping can flip the inference.
    /// </summary>
    private static bool EnclosingReturnTypeMatches(
        SyntaxNode node,
        ITypeSymbol targetType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax:
                    return false;
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax:
                    var symbol = semanticModel.GetDeclaredSymbol(ancestor, cancellationToken);
                    var returnType = symbol switch
                    {
                        IMethodSymbol method => method.ReturnType,
                        IPropertySymbol property => property.Type,
                        _ => null,
                    };
                    return TypeEquals(returnType, targetType);
            }
        }

        return false;
    }

    private static bool TypeEquals(ITypeSymbol? type, ITypeSymbol targetType)
    {
        return type is not null && SymbolEqualityComparer.Default.Equals(type, targetType);
    }
}
