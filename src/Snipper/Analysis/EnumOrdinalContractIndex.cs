namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Enums whose *numeric value* is the contract, not their names.
///
/// A reference count cannot prove an enum member dead, because the member's
/// ordinal is a value travelling in messages. Two shapes make that concrete:
///
/// <list type="number">
/// <item>The enum is a type argument of a <c>System.Text.Json.Serialization.
/// JsonConverter</c>-derived type. STJ converts enums by ordinal, and a
/// hand-rolled converter does it explicitly — milkrun's
/// <c>MessageJsonConverter</c> reads the discriminator as an int,
/// <c>Enum.IsDefined(typeof(T), n)</c>, then <c>Enum.ToObject(typeof(T), n)</c>.
/// </item>
/// <item>The enum is the type argument of <c>Enum.GetValues&lt;T&gt;()</c>: the
/// whole enum is reflected over and members are dispatched by value, so a
/// renumbered ordinal re-routes to a different case instead of failing.
/// </item>
/// </list>
///
/// Deleting a member from such an enum shifts every later ordinal. The next
/// in-flight message carrying the old number decodes as a <em>different</em>
/// command and is acted on — silently, which is worse than a crash. So an
/// obsolete member of an ordinal-bound enum is a live contract, not a removal
/// candidate.
///
/// Doctrine matches <see cref="FrameworkEvidenceIndex"/>: over-approximate on
/// unresolvable shapes (drifted compilations, missing references). A wrongly
/// included enum keeps a genuinely dead member alive; a wrongly excluded one
/// loses an ordinal contract. The second failure is the expensive one.
/// </summary>
internal sealed class EnumOrdinalContractIndex
{
    private static readonly ConditionalWeakTable<Solution, Lazy<EnumOrdinalContractIndex>> Cache = new();

    private const string JsonConverterNamespace = "System.Text.Json.Serialization";

    private readonly FrozenSet<INamedTypeSymbol> _ordinalBoundTypes;
    private readonly FrozenSet<string> _ordinalBoundTypeNames;

    private EnumOrdinalContractIndex(FrozenSet<INamedTypeSymbol> ordinalBoundTypes, FrozenSet<string> ordinalBoundTypeNames)
    {
        _ordinalBoundTypes = ordinalBoundTypes;
        _ordinalBoundTypeNames = ordinalBoundTypeNames;
    }

    public static EnumOrdinalContractIndex Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return Cache.GetValue(
                solution,
                static s => new Lazy<EnumOrdinalContractIndex>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// True when this member is an enum member whose ordinal is a wire or
    /// dispatch contract. A plain member returns false without a lookup.
    /// </summary>
    public bool IsOrdinalBound(ISymbol member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (member is not IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } field)
        {
            return false;
        }

        var type = field.ContainingType.OriginalDefinition;
        return _ordinalBoundTypes.Contains(type)
            || _ordinalBoundTypeNames.Contains(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static EnumOrdinalContractIndex Build(Solution solution)
    {
        // (document, enum type argument, declaring type when the argument came from
        // a base list — null for Enum.GetValues<T>, which needs no declaration).
        var candidates = new ConcurrentBag<(Document Document, TypeSyntax Argument, TypeDeclarationSyntax? Declaration)>();

        // Pass 1: syntax only. Neither shape needs a semantic model to spot.
        Parallel.ForEach(
            solution.Projects.SelectMany(static p => p.Documents).Where(static d => d.SupportsSyntaxTree),
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            document =>
            {
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is null)
                {
                    return;
                }

                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case TypeDeclarationSyntax { BaseList: not null } typeDeclaration:
                            foreach (var baseType in typeDeclaration.BaseList.Types)
                            {
                                foreach (var argument in TypeArgumentsOf(baseType.Type))
                                {
                                    candidates.Add((document, argument, typeDeclaration));
                                }
                            }

                            break;

                        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "GetValues" } getValues } }:
                            foreach (var argument in getValues.TypeArgumentList.Arguments)
                            {
                                candidates.Add((document, argument, Declaration: null));
                            }

                            break;
                    }
                }
            });

        // Pass 2: semantic resolution, one model per document. Two passes over the
        // same group so the converter check costs one binding per *declaration*
        // rather than one per argument.
        var resolved = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var sync = new object();
        Parallel.ForEach(
            candidates.GroupBy(static candidate => candidate.Document),
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            group =>
            {
                var semanticModel = group.Key.GetSemanticModelAsync().GetAwaiter().GetResult();
                if (semanticModel is null)
                {
                    return;
                }

                var reflectedEnums = new List<INamedTypeSymbol>();
                var converterBaseEnums = new List<INamedTypeSymbol>();
                var converterDeclarations = new HashSet<TypeDeclarationSyntax>();

                foreach (var (_, argument, declaration) in group)
                {
                    if (semanticModel.GetTypeInfo(argument).Type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                    {
                        continue;
                    }

                    if (declaration is null)
                    {
                        reflectedEnums.Add(enumType.OriginalDefinition);
                    }
                    else
                    {
                        converterBaseEnums.Add(enumType.OriginalDefinition);
                        converterDeclarations.Add(declaration);
                    }
                }

                // Only a converter-derived declaring type makes a base-list
                // argument an ordinal contract. `Dictionary<K,V>` with an enum key
                // is not, and must not be mistaken for one.
                var converterDerived = converterDeclarations.Count > 0
                    && converterDeclarations.Any(declaration =>
                        semanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol type && IsJsonConverterDerived(type));

                lock (sync)
                {
                    foreach (var enumType in reflectedEnums)
                    {
                        resolved.Add(enumType);
                    }

                    if (converterDerived)
                    {
                        foreach (var enumType in converterBaseEnums)
                        {
                            resolved.Add(enumType);
                        }
                    }
                }
            });

        return new EnumOrdinalContractIndex(
            resolved.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default),
            resolved.Select(static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToFrozenSet(StringComparer.Ordinal));
    }

    private static bool IsJsonConverterDerived(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name.StartsWith("JsonConverter", StringComparison.Ordinal)
                && current.ContainingNamespace?.ToDisplayString() == JsonConverterNamespace)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TypeSyntax> TypeArgumentsOf(TypeSyntax type)
    {
        var generic = type switch
        {
            GenericNameSyntax genericName => genericName,
            QualifiedNameSyntax qualified => qualified.Right as GenericNameSyntax,
            AliasQualifiedNameSyntax alias => alias.Name as GenericNameSyntax,
            _ => null,
        };

        return generic is null ? [] : generic.TypeArgumentList.Arguments;
    }
}