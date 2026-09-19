namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0028 — Redundant qualification. Two shapes, both speculation-gated the
/// same way as SNP0022/SNP0025: the simplified form is re-bound speculatively
/// and must produce the identical symbol (constructed forms included). (1)
/// <c>this.X</c> where nothing in scope shadows the member — a shadowing
/// parameter/local rebinds to a different symbol and fails the gate. (2) A
/// qualified type name (<c>System.Text.StringBuilder</c>) whose rightmost name
/// binds identically bare — only the outermost qualified name is a candidate,
/// using directives and alias-qualified names are out of scope. Anything
/// touching conditional access never speculates (the 1.4.1 crash guard).
/// </summary>
internal static class RedundantQualifierEvaluator
{
    public static SnipperFinding? TryEvaluateThisQualifier(
        MemberAccessExpressionSyntax access,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (access.Expression is not ThisExpressionSyntax || InsideConditionalAccess(access))
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(access, cancellationToken).Symbol is not { } original
            || original is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol))
        {
            return null;
        }

        var bare = access.Name.WithoutTrivia();
        var speculated = semanticModel
            .GetSpeculativeSymbolInfo(access.SpanStart, bare, SpeculativeBindingOption.BindAsExpression)
            .Symbol;

        if (speculated is null || !SymbolEqualityComparer.Default.Equals(speculated, original))
        {
            return null;
        }

        return CreateFinding(
            access,
            original,
            "Qualifier 'this.' is redundant — the member binds identically without it.");
    }

    public static SnipperFinding? TryEvaluateQualifiedName(
        QualifiedNameSyntax qualifiedName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(semanticModel);

        // Only the outermost qualification is a candidate (so A.B.C flags once),
        // and using directives are the point of the qualification — never redundant.
        if (qualifiedName.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax
            || qualifiedName.Ancestors().Any(static ancestor => ancestor is UsingDirectiveSyntax)
            || InsideConditionalAccess(qualifiedName))
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(qualifiedName, cancellationToken).Symbol is not { } original
            || original is INamespaceSymbol)
        {
            return null;
        }

        var bare = qualifiedName.Right.WithoutTrivia();
        var speculated = semanticModel
            .GetSpeculativeSymbolInfo(qualifiedName.SpanStart, bare, SpeculativeBindingOption.BindAsTypeOrNamespace)
            .Symbol;

        if (speculated is null || !SymbolEqualityComparer.Default.Equals(speculated, original))
        {
            return null;
        }

        return CreateFinding(
            qualifiedName,
            original,
            $"Qualification in '{qualifiedName}' is redundant — '{bare}' binds identically without it.");
    }

    private static bool InsideConditionalAccess(SyntaxNode node)
    {
        return node.Ancestors().Any(static ancestor => ancestor is ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax);
    }

    private static SnipperFinding CreateFinding(SyntaxNode node, ISymbol symbol, string message)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0028",
            Title: "Redundant Qualifier",
            Message: message,
            Certainty: CertaintyTier.High,
            Category: FindingCategory.RedundantQualifier,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: symbol);
    }
}
