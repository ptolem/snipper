namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0029 — Empty type members. (1) A public, parameterless, bodiless instance
/// constructor on a non-abstract class, as the class's only constructor: the
/// compiler synthesizes an identical one. Private/internal constructors are
/// load-bearing (they gate instantiation), static constructors force
/// beforefieldinit semantics, structs and records synthesize differently,
/// initializers and attributes always exclude. (2) An empty destructor: it does
/// no work yet forces the instance through the finalizer queue.
/// </summary>
internal static class EmptyTypeMemberEvaluator
{
    public static SnipperFinding? TryEvaluateConstructor(
        ConstructorDeclarationSyntax constructor,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(constructor);
        ArgumentNullException.ThrowIfNull(semanticModel);

        // Syntax gates: no parameters, no initializer, no attributes, an
        // explicitly empty block body, public accessibility (what the
        // synthesized default ctor would get on a non-abstract class).
        if (constructor.ParameterList.Parameters.Count > 0
            || constructor.Initializer is not null
            || constructor.AttributeLists.Count > 0
            || constructor.Body is not { Statements.Count: 0 }
            || !constructor.Modifiers.Any(static m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            return null;
        }

        if (semanticModel.GetDeclaredSymbol(constructor, cancellationToken) is not { } constructorSymbol
            || constructorSymbol.IsStatic
            || constructorSymbol.ContainingType is not { } containingType
            || containingType.TypeKind is not TypeKind.Class
            || containingType.IsAbstract
            || containingType.IsValueType
            || containingType.IsRecord)
        {
            return null;
        }

        // Only the class's sole constructor can be the synthesized default's
        // twin; with other ctors present, removing this one removes
        // parameterless construction entirely.
        if (containingType.InstanceConstructors.Length != 1)
        {
            return null;
        }

        var lineSpan = constructor.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0029",
            Title: "Empty Constructor",
            Message: $"Constructor '{containingType.Name}()' is empty and can be removed — the compiler synthesizes an identical one.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.EmptyTypeMember,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: constructorSymbol);
    }

    public static SnipperFinding? TryEvaluateDestructor(
        DestructorDeclarationSyntax destructor,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destructor);
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (destructor.AttributeLists.Count > 0
            || destructor.Body is not { Statements.Count: 0 })
        {
            return null;
        }

        if (semanticModel.GetDeclaredSymbol(destructor, cancellationToken) is not { } destructorSymbol
            || destructorSymbol.ContainingType is not { } containingType)
        {
            return null;
        }

        var lineSpan = destructor.GetLocation().GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0029",
            Title: "Empty Destructor",
            Message: $"Destructor '~{containingType.Name}()' is empty and can be removed — it only forces finalization overhead.",
            Certainty: CertaintyTier.High,
            Category: FindingCategory.EmptyTypeMember,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: destructorSymbol);
    }
}
