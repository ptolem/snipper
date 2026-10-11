namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// The callable bodies both complexity rules score, kept in one place because the two
/// rules agree on almost all of them — and the one place they disagree is exactly the
/// kind of difference that should be a parameter rather than a second copy of a switch
/// expression that can drift.
/// </summary>
internal static class ComplexityFunctionBody
{
    /// <summary>
    /// The body of <paramref name="node"/> if it introduces a scope the complexity rules
    /// score, otherwise null.
    /// </summary>
    /// <param name="node">Candidate declaration node.</param>
    /// <param name="includeLambdas">
    /// Whether a lambda is a scoring unit in its own right. SNP0033 says yes — ReSharper's
    /// classical metric scores every callable body independently — and SNP0034 says no,
    /// because cognitive complexity counts a nested lambda *inside* its enclosing method,
    /// at a raised nesting level. Scoring it twice would report one construct at two
    /// numbers that are not the same metric.
    /// </param>
    public static SyntaxNode? TryGetBody(SyntaxNode node, bool includeLambdas)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (includeLambdas)
        {
            switch (node)
            {
                case SimpleLambdaExpressionSyntax simple:
                    return simple.Body;
                case ParenthesizedLambdaExpressionSyntax parenthesized:
                    return parenthesized.Body;
                case AnonymousMethodExpressionSyntax anonymous:
                    return anonymous.Body;
            }
        }

        return node switch
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

            _ => null,
        };
    }
}