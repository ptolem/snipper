namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// One-pass semantic harvest of symbol-usage evidence for the whole solution,
/// built once and shared by the reference-checking analysers (SNP0005/0006
/// existence checks and rescue passes, SNP0009 local confirmation, SNP0003/0004
/// assembly usage). Replaces per-candidate FindReferencesAsync searches: every
/// bound symbol in every document is recorded exactly once, so repeated scans
/// over the same hot documents disappear.
///
/// Node set and over-approximation mirror the doctrine the package rules
/// (SNP0003/0004) already ran on: every ExpressionSyntax and AttributeSyntax is
/// bound; ambiguous candidates and expression types count as used. Erring
/// towards "used" can only suppress a finding — the safe direction for
/// dead-code claims. Constructor bindings also record their containing type
/// (object creation and attribute application reference the type, matching
/// FindReferencesAsync behaviour).
///
/// ALL documents are harvested — generated and external ones included —
/// because they are legitimate usage evidence; findings still anchor only on
/// eligible documents. Sequential binding per the ConcurrentBuild=false
/// contract. Memoized via the solution-keyed cache pattern (see
/// <see cref="SolutionUsageIndex"/>).
/// </summary>
internal sealed class SolutionReferenceIndex
{
    private static readonly ConditionalWeakTable<Solution, Lazy<SolutionReferenceIndex>> Cache = new();

    private static readonly FrozenSet<IAssemblySymbol> EmptyAssemblies =
        FrozenSet.ToFrozenSet([], (IEqualityComparer<IAssemblySymbol>)SymbolEqualityComparer.Default);

    private readonly FrozenSet<ISymbol> _referencedSymbols;
    private readonly FrozenDictionary<ProjectId, FrozenSet<IAssemblySymbol>> _usedAssembliesByProject;

    private SolutionReferenceIndex(
        FrozenSet<ISymbol> referencedSymbols,
        FrozenDictionary<ProjectId, FrozenSet<IAssemblySymbol>> usedAssembliesByProject)
    {
        _referencedSymbols = referencedSymbols;
        _usedAssembliesByProject = usedAssembliesByProject;
    }

    public static SolutionReferenceIndex Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Lazy + ExecutionAndPublication: exactly one build even if analysers race.
        return Cache.GetValue(
                solution,
                static s => new Lazy<SolutionReferenceIndex>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// True when any document binds a usage to <paramref name="symbol"/> (or, on
    /// ambiguous bindings, to a candidate that could be it). Symbol identity
    /// handles scoping naturally: a same-named symbol in another project is a
    /// different symbol and never satisfies this check. Members also match on
    /// inherited-contract evidence — FindReferencesAsync cascades a member
    /// search to the interface members it implements and their fellow
    /// implementations (unified across generic instantiations), and the index
    /// replicates that family: a call binding one implementation keeps every
    /// implementation of the same contract alive.
    /// </summary>
    public bool IsReferenced(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (_referencedSymbols.Contains(symbol.OriginalDefinition))
        {
            return true;
        }

        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            foreach (var inherited in GetInheritedContracts(symbol))
            {
                if (_referencedSymbols.Contains(inherited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Assemblies <paramref name="project"/> consumes symbols from — the
    /// per-project evidence SNP0003/SNP0004 judge references against.
    /// </summary>
    public FrozenSet<IAssemblySymbol> GetUsedAssemblies(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return _usedAssembliesByProject.TryGetValue(project.Id, out var assemblies) ? assemblies : EmptyAssemblies;
    }

    private static SolutionReferenceIndex Build(Solution solution)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var assembliesByProject = new Dictionary<ProjectId, HashSet<IAssemblySymbol>>();

        // Inherited-contract walks are memoized per distinct member definition —
        // symbol instances repeat across documents within a compilation, and the
        // AllInterfaces x FindImplementationForInterfaceMember scan is the
        // harvest's only super-constant work.
        var inheritedContractsCache = new Dictionary<ISymbol, IReadOnlyList<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            var assemblies = new HashSet<IAssemblySymbol>((IEqualityComparer<IAssemblySymbol>)SymbolEqualityComparer.Default);
            assembliesByProject[project.Id] = assemblies;

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var semanticModel = document.GetSemanticModelAsync().GetAwaiter().GetResult();
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (semanticModel is null || root is null)
                {
                    continue;
                }

                foreach (var node in root.DescendantNodes())
                {
                    if (node is not (ExpressionSyntax or AttributeSyntax))
                    {
                        continue;
                    }

                    var symbolInfo = semanticModel.GetSymbolInfo(node);
                    RecordSymbol(symbolInfo.Symbol, symbols, assemblies, inheritedContractsCache);

                    if (symbolInfo.Symbol is null)
                    {
                        foreach (var candidate in symbolInfo.CandidateSymbols)
                        {
                            RecordSymbol(candidate, symbols, assemblies, inheritedContractsCache);
                        }

                        if (node is ExpressionSyntax expression)
                        {
                            RecordSymbol(semanticModel.GetTypeInfo(expression).Type, symbols, assemblies, inheritedContractsCache);
                        }
                    }
                }
            }
        }

        return new SolutionReferenceIndex(
            symbols.ToFrozenSet(SymbolEqualityComparer.Default),
            assembliesByProject.ToFrozenDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToFrozenSet((IEqualityComparer<IAssemblySymbol>)SymbolEqualityComparer.Default)));
    }

    private static void RecordSymbol(
        ISymbol? symbol,
        HashSet<ISymbol> symbols,
        HashSet<IAssemblySymbol> assemblies,
        Dictionary<ISymbol, IReadOnlyList<ISymbol>> inheritedContractsCache)
    {
        if (symbol is null)
        {
            return;
        }

        symbols.Add(symbol.OriginalDefinition);

        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // FindReferencesAsync cascades a member search across its contract
            // family: the interface members it implements (variance-included via
            // OriginalDefinition unification), every fellow implementation, and
            // the override chain. Recording the inherited side at bind time
            // makes the family visible to IsReferenced's query-side check.
            if (!inheritedContractsCache.TryGetValue(symbol.OriginalDefinition, out var inheritedContracts))
            {
                inheritedContracts = GetInheritedContracts(symbol).ToArray();
                inheritedContractsCache[symbol.OriginalDefinition] = inheritedContracts;
            }

            foreach (var inherited in inheritedContracts)
            {
                symbols.Add(inherited);
            }
        }

        // Extension invocations bind the reduced form; the unreduced definition
        // is what analysers query (FindReferencesAsync cascaded through
        // ReducedFrom internally — the index records it explicitly).
        if (symbol is IMethodSymbol { ReducedFrom: not null } reducedMethod)
        {
            symbols.Add(reducedMethod.ReducedFrom.OriginalDefinition);
        }

        // Object creation and attribute application bind to the constructor;
        // FindReferencesAsync on the type finds those usages, so record it too.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor, ContainingType: not null } method)
        {
            symbols.Add(method.ContainingType.OriginalDefinition);
        }

        var assembly = symbol switch
        {
            IAssemblySymbol assemblySymbol => assemblySymbol,
            IArrayTypeSymbol arrayType => arrayType.ElementType.ContainingAssembly,
            _ => symbol.OriginalDefinition.ContainingAssembly,
        };

        if (assembly is not null)
        {
            assemblies.Add(assembly);
        }
    }

    /// <summary>
    /// The contracts a member fulfils beyond its own name: its override chain
    /// and every interface member it implements (explicitly or implicitly),
    /// OriginalDefinition-normalized so generic instantiations unify into one
    /// contract family. Shared by the harvest (record side) and
    /// <see cref="IsReferenced"/> (query side).
    /// </summary>
    private static IEnumerable<ISymbol> GetInheritedContracts(ISymbol member)
    {
        for (var link = OverriddenLink(member); link is not null; link = OverriddenLink(link))
        {
            yield return link.OriginalDefinition;
        }

        foreach (var explicitImplementation in ExplicitInterfaceImplementations(member))
        {
            yield return explicitImplementation.OriginalDefinition;
        }

        var containingType = member.ContainingType;
        if (containingType is null || containingType.AllInterfaces.Length == 0)
        {
            yield break;
        }

        foreach (var contract in containingType.AllInterfaces)
        {
            foreach (var contractMember in contract.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(contractMember);
                if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, member))
                {
                    yield return contractMember.OriginalDefinition;
                }
            }
        }
    }

    private static ISymbol? OverriddenLink(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol eventSymbol => eventSymbol.OverriddenEvent,
            _ => null,
        };
    }

    private static IEnumerable<ISymbol> ExplicitInterfaceImplementations(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol eventSymbol => eventSymbol.ExplicitInterfaceImplementations,
            _ => [],
        };
    }
}
