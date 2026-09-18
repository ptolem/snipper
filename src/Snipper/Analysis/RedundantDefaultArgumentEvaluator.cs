namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0022 — Flags a trailing positional invocation argument whose literal value
/// equals the parameter's declared default. Sound by construction: the candidate
/// must (1) map to a parameter with an explicit default, (2) have a constant value
/// equal to that default, and (3) speculatively re-bind to the identical symbol
/// with the argument removed — so overload-rebinding traps are never flagged.
/// Tier 2 (High): removing the argument unpins the value from future default
/// changes in another assembly, which is a source-hygiene claim, not a binary one.
/// </summary>
internal static class RedundantDefaultArgumentEvaluator
{
    // Caller-info defaults are injected by the caller when the argument is omitted:
    // removing a matching literal changes runtime semantics even though the rebind
    // is identical. Name-matched so no BCL reference is required.
    private static readonly FrozenSet<string> CallerInfoAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CallerMemberName", "CallerMemberNameAttribute",
        "CallerFilePath", "CallerFilePathAttribute",
        "CallerLineNumber", "CallerLineNumberAttribute",
        "CallerArgumentExpression", "CallerArgumentExpressionAttribute",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static SnipperFinding? TryEvaluate(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(semanticModel);

        // Pre-filter: trailing positional argument in a literal-ish shape. Named
        // arguments are excluded — readability is a legitimate reason for them.
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            return null;
        }

        var lastIndex = arguments.Count - 1;
        var last = arguments[lastIndex];
        if (last.NameColon is not null || !IsLiteralish(last.Expression))
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol original)
        {
            return null;
        }

        // Positional mapping: the candidate is never named (pre-filter above),
        // so argument i binds to parameter i — reduced extension methods already
        // exclude the receiver. An index beyond the parameter list means
        // params-array expansion, which is excluded by the IsParams check anyway.
        var parameters = original.Parameters;
        if (lastIndex >= parameters.Length)
        {
            return null;
        }

        var parameter = parameters[lastIndex];
        if (!parameter.HasExplicitDefaultValue || parameter.IsParams)
        {
            return null;
        }

        if (HasCallerInfoAttribute(parameter))
        {
            return null;
        }

        var constant = semanticModel.GetConstantValue(last.Expression, cancellationToken);
        if (!constant.HasValue || !Equals(constant.Value, parameter.ExplicitDefaultValue))
        {
            return null;
        }

        // Speculation gate: removal must re-bind the identical symbol (A1/A2 spike).
        var rebound = InvocationSpeculation.RebindWithoutArgument(invocation, lastIndex, semanticModel);
        if (!SymbolEqualityComparer.Default.Equals(original, rebound))
        {
            return null;
        }

        var lineSpan = last.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0022",
            Title: "Redundant Default Argument",
            Message: $"Argument '{last}' for parameter '{parameter.Name}' matches its default value and can be removed.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.RedundantArgument,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: original);
    }

    private static bool IsLiteralish(ExpressionSyntax expression)
    {
        return expression switch
        {
            // Covers null, true/false, numeric, string, and char literals.
            LiteralExpressionSyntax => true,
            // Enum member access (DispatchMode.Fast).
            MemberAccessExpressionSyntax => true,
            // Signed numeric literals (-1, +2).
            PrefixUnaryExpressionSyntax prefix
                when prefix.OperatorToken.IsKind(SyntaxKind.MinusToken)
                    || prefix.OperatorToken.IsKind(SyntaxKind.PlusToken)
                => prefix.Operand is LiteralExpressionSyntax,
            _ => false,
        };
    }

    private static bool HasCallerInfoAttribute(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass is not null && CallerInfoAttributeNames.Contains(attribute.AttributeClass.Name))
            {
                return true;
            }
        }

        return false;
    }
}
