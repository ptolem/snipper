namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>
/// Solution-wide index of source-declared inheritance edges (derived class → base
/// class), built once per solution and shared by the hierarchy rules (SNP0023
/// never-inherited / never-overridden, SNP0024 can-be-sealed). Records and
/// generics are first-class: keys are OriginalDefinition-normalized so a
/// <c>Derived : Base&lt;int&gt;</c> edge lands on the <c>Base&lt;T&gt;</c>
/// declaration. Edges from generated documents count as evidence — a
/// source-generated subclass keeps its base alive. Semantic binding is
/// sequential per the workspace's ConcurrentBuild=false contract; the graph is
/// one compilation walk per project, memoized via the same solution-keyed
/// cache pattern as <see cref="SolutionUsageIndex"/>.
/// </summary>
internal sealed class InheritanceGraph
{
    private static readonly ConditionalWeakTable<Solution, Lazy<InheritanceGraph>> Cache = new();

    private readonly FrozenDictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> _derivedByBase;

    private InheritanceGraph(FrozenDictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> derivedByBase)
    {
        _derivedByBase = derivedByBase;
    }

    public static InheritanceGraph Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Lazy + ExecutionAndPublication: exactly one build even if analysers race.
        return Cache.GetValue(
                solution,
                static s => new Lazy<InheritanceGraph>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// True when any source-declared class directly derives from <paramref name="type"/>.
    /// "Never inherited" needs no transitive walk: a single direct edge disproves it.
    /// </summary>
    public bool HasAnyDerivedClass(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _derivedByBase.ContainsKey(type.OriginalDefinition);
    }

    /// <summary>
    /// Every source-declared class that transitively derives from
    /// <paramref name="type"/> (diamond-safe). SNP0023's member check walks this
    /// closure looking for overrides.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetTransitivelyDerivedClasses(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var result = new List<INamedTypeSymbol>();
        var visited = new HashSet<INamedTypeSymbol>(NamedTypeSymbolComparer.Instance);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(type.OriginalDefinition);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!_derivedByBase.TryGetValue(current, out var derived))
            {
                continue;
            }

            foreach (var derivedClass in derived)
            {
                if (visited.Add(derivedClass.OriginalDefinition))
                {
                    result.Add(derivedClass);
                    queue.Enqueue(derivedClass.OriginalDefinition);
                }
            }
        }

        return result;
    }

    private static InheritanceGraph Build(Solution solution)
    {
        var edges = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(NamedTypeSymbolComparer.Instance);

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in EnumerateDeclaredClasses(compilation.Assembly.GlobalNamespace))
            {
                var baseType = type.BaseType;
                if (baseType is null || baseType.SpecialType == SpecialType.System_Object)
                {
                    continue;
                }

                var key = baseType.OriginalDefinition;
                if (!edges.TryGetValue(key, out var list))
                {
                    list = [];
                    edges[key] = list;
                }

                list.Add(type);
            }
        }

        return new InheritanceGraph(edges.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableArray(),
            NamedTypeSymbolComparer.Instance));
    }

    /// <summary>
    /// SymbolEqualityComparer.Default typed for INamedTypeSymbol — the generic
    /// FrozenDictionary/HashSet factories cannot infer through its contravariance.
    /// </summary>
    private sealed class NamedTypeSymbolComparer : IEqualityComparer<INamedTypeSymbol>
    {
        public static readonly NamedTypeSymbolComparer Instance = new();

        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(INamedTypeSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateDeclaredClasses(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var nested in EnumerateClassAndNested(type))
            {
                yield return nested;
            }
        }

        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in EnumerateDeclaredClasses(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateClassAndNested(INamedTypeSymbol type)
    {
        if (type is { TypeKind: TypeKind.Class, IsImplicitlyDeclared: false })
        {
            yield return type;
        }

        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var nestedClass in EnumerateClassAndNested(nested))
            {
                yield return nestedClass;
            }
        }
    }
}
