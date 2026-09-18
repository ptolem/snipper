namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// How a single field reference participates in value flow, judged purely from
/// its syntactic role. <see cref="WriteOnlyFieldAnalyser"/> aggregates these
/// across all references of a field: any Read or ReadWrite keeps it alive.
/// </summary>
internal enum FieldReferenceKind
{
    /// <summary>The field's value is consumed (or the context is unknown — the safe default).</summary>
    Read,

    /// <summary>The field is assigned without its previous value being observed.</summary>
    Write,

    /// <summary>The old value feeds the new one (compound assignment, ++/--) or the
    /// storage is aliased via <c>ref</c>.</summary>
    ReadWrite,

    /// <summary><c>nameof(field)</c> — the identifier is data here; proves nothing
    /// about value flow.</summary>
    None,
}

internal static class FieldReferenceClassifier
{
    /// <summary>
    /// Classifies a confirmed field reference by first unwrapping the transparent
    /// containers a write target can wear — member-access name position
    /// (<c>obj._x</c>, <c>this._x</c>), parentheses, and deconstruction tuples —
    /// then reading the role of the first significant ancestor. Anything outside
    /// these shapes is a use: an unknown context must never report a write.
    /// </summary>
    public static FieldReferenceKind Classify(SimpleNameSyntax node)
    {
        ArgumentNullException.ThrowIfNull(node);

        SyntaxNode target = node;
        while (target.Parent is { } parent)
        {
            if (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == target)
            {
                target = memberAccess;
            }
            else if (parent is ParenthesizedExpressionSyntax or TupleExpressionSyntax)
            {
                target = parent;
            }
            else
            {
                break;
            }
        }

        return target.Parent switch
        {
            // nameof(_x): the identifier is data, not a value access. The argument
            // sits inside an ArgumentList, so the invocation is two levels up.
            ArgumentSyntax
            {
                Parent: ArgumentListSyntax
                {
                    Parent: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } },
                },
            }
                => FieldReferenceKind.None,
            ArgumentSyntax argument => argument.RefOrOutKeyword.Kind() switch
            {
                SyntaxKind.OutKeyword => FieldReferenceKind.Write,
                SyntaxKind.RefKeyword => FieldReferenceKind.ReadWrite,
                _ => FieldReferenceKind.Read,
            },
            // Only the assignment target itself is a write; a reference anywhere in
            // the right-hand side (or a nested left-side position such as an indexer
            // argument) is a read. Compound assignment and ??= consume the old value,
            // so they read too.
            AssignmentExpressionSyntax assignment when assignment.Left == target
                => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    ? FieldReferenceKind.Write
                    : FieldReferenceKind.ReadWrite,
            PrefixUnaryExpressionSyntax prefix when prefix.Operand == target && IsIncrementOrDecrement(prefix.Kind())
                => FieldReferenceKind.ReadWrite,
            PostfixUnaryExpressionSyntax postfix when postfix.Operand == target && IsIncrementOrDecrement(postfix.Kind())
                => FieldReferenceKind.ReadWrite,
            _ => FieldReferenceKind.Read,
        };
    }

    private static bool IsIncrementOrDecrement(SyntaxKind kind)
    {
        return kind is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression;
    }
}
