namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

/// <summary>
/// Every <see cref="SymbolFinder.FindReferencesAsync(ISymbol, Solution, IImmutableSet{Document}, CancellationToken)"/>
/// the analysers make, memoized per symbol for one <see cref="Solution"/>.
/// <para>
/// The reference search is the most expensive call in the tool, and several rules make it
/// for the same symbol: a private field is searched by SNP0001, SNP0021 and SNP0024's
/// can-be-readonly check; a public or internal member by SNP0005/6 and then by SNP0024's
/// can-be-private and can-be-static checks; a private method by SNP0001, SNP0020 and
/// can-be-static. The first rule to ask starts the search and every later one - including
/// one that arrives while it is still running - awaits the same result.
/// </para>
/// <para>
/// A rule that only wants to know whether there is any reference
/// (<see cref="HasAnyAsync"/>) gets a search of its own that stops at the first one, and is
/// memoized separately - unless a full search for the symbol is already under way, which
/// it reuses. Almost every public member is used, and finding one use is far cheaper than
/// finding them all: an <c>Equals(T)</c> search cascades through <c>IEquatable&lt;T&gt;</c> to
/// every <c>Equals</c> call in the solution. On a large solution this cut SNP0005/6 by 46%.
/// </para>
/// <para>
/// The documents to search are decided here, from the symbol alone, rather than by each
/// caller; see <see cref="GetCandidateDocuments"/>. That is what makes the memo sound -
/// one symbol, one search, whichever rule asks first - and it replaces a scope rule that
/// was spelled out, with small differences, at every call site.
/// </para>
/// <para>
/// Results are held for the life of the solution, as a flat list of locations rather than
/// Roslyn's per-definition grouping, which no caller reads.
/// </para>
/// </summary>
internal sealed class ReferenceSearch
{
    private const string AttributeSuffix = "Attribute";

    private static readonly SolutionIndexCache<ReferenceSearch> Cache = new(CreateAsync);

    private readonly Solution _solution;
    private readonly SolutionUsageIndex _usageIndex;
    private readonly Dictionary<ISymbol, SharedTask<ImmutableArray<ReferenceLocation>>> _searches = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, SharedTask<bool>> _anySearches = new(SymbolEqualityComparer.Default);
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<IAssemblySymbol, AssemblyScope> _assemblyScopes = new(SymbolEqualityComparer.Default);

    private ReferenceSearch(Solution solution, SolutionUsageIndex usageIndex)
    {
        _solution = solution;
        _usageIndex = usageIndex;
    }

    /// <summary>
    /// The search for <paramref name="solution"/>. Cheap: it builds nothing of its own,
    /// but it awaits <see cref="SolutionUsageIndex"/>, which callers must declare.
    /// </summary>
    public static Task<ReferenceSearch> GetAsync(Solution solution, CancellationToken cancellationToken)
        => Cache.GetAsync(solution, cancellationToken);

    /// <summary>
    /// Every reference location of <paramref name="symbol"/>, confirmed and candidate
    /// alike (<see cref="ReferenceLocation.IsCandidateLocation"/>), in the order Roslyn
    /// reports them. Empty when nothing in scope even spells the name, without a search.
    /// </summary>
    public Task<ImmutableArray<ReferenceLocation>> FindAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return SharedTask<ImmutableArray<ReferenceLocation>>.AwaitAsync(
            () => GetOrStart(symbol, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> has any reference location at all - the same answer
    /// as a non-empty <see cref="FindAsync"/>, from a search that stops at the first location.
    /// </summary>
    public async Task<bool> HasAnyAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        // A full search already started answers this for free once it finishes; starting a
        // second, stopping one beside it would only add work.
        bool fullSearchExists;
        lock (_gate)
        {
            fullSearchExists = _searches.TryGetValue(symbol, out var full) && full.IsReusable;
        }

        if (fullSearchExists)
        {
            var references = await FindAsync(symbol, cancellationToken).ConfigureAwait(false);
            return references.Length > 0;
        }

        return await SharedTask<bool>.AwaitAsync(
            () => GetOrStartAny(symbol, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private SharedTask<bool> GetOrStartAny(ISymbol symbol, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_anySearches.TryGetValue(symbol, out var existing) && existing.IsReusable)
            {
                return existing;
            }

            var started = SharedTask<bool>.Start(ct => SearchAnyAsync(symbol, ct), cancellationToken);
            _anySearches[symbol] = started;
            return started;
        }
    }

    private async Task<bool> SearchAnyAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        var documents = GetCandidateDocuments(symbol);
        if (documents.Count == 0)
        {
            return false;
        }

        // Roslyn reports each location through the progress as it is found - the full
        // result is assembled from the same reports - so the first one settles the answer
        // and cancelling the rest of the search loses nothing.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new FirstReferenceProgress(stop);
        try
        {
            await SymbolFinder.FindReferencesAsync(symbol, _solution, progress, documents, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (progress.Found && !cancellationToken.IsCancellationRequested)
        {
            // Stopped by the first reference, not by the caller.
        }

        return progress.Found;
    }

    /// <summary>Cancels the search it is passed to at the first reference location.</summary>
    private sealed class FirstReferenceProgress(CancellationTokenSource stop) : IFindReferencesProgress
    {
        private int _found;

        public bool Found => Volatile.Read(ref _found) != 0;

        public void OnReferenceFound(ISymbol symbol, ReferenceLocation location)
        {
            if (Interlocked.Exchange(ref _found, 1) == 0)
            {
                stop.Cancel();
            }
        }

        public void OnStarted()
        {
        }

        public void OnCompleted()
        {
        }

        public void OnDefinitionFound(ISymbol symbol)
        {
        }

        public void OnFindInDocumentStarted(Document document)
        {
        }

        public void OnFindInDocumentCompleted(Document document)
        {
        }

        public void ReportProgress(int current, int maximum)
        {
        }
    }

    /// <summary>
    /// The documents a reference to <paramref name="symbol"/> could be in.
    /// <list type="bullet">
    /// <item>Scope: the declaring project when the symbol cannot be seen outside it -
    /// private anywhere in its containment chain, or internal anywhere in it without an
    /// <c>InternalsVisibleTo</c> - and the whole solution otherwise. That includes an
    /// override, an interface implementation, and any public method, property or event,
    /// which a derived type can use to implement an interface: the search cascades to the
    /// member overridden or implemented, which may be visible more widely. Within those
    /// limits Roslyn draws the same line itself and never searches a project the symbol is
    /// invisible to, so this only spares it the documents; it never changes what is found.</item>
    /// <item>Within the scope, only documents that spell the name
    /// (<see cref="SolutionUsageIndex"/>): any semantic reference must. An explicit interface
    /// implementation is spelled as the interface member it implements. An attribute class
    /// is applied by its short name, so documents spelling <c>[Name]</c> count for
    /// <c>NameAttribute</c> too. An indexer has no name to spell, so every document in scope
    /// counts.</item>
    /// </list>
    /// </summary>
    internal IImmutableSet<Document> GetCandidateDocuments(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var project = GetConfiningProject(symbol);

        if (symbol is IPropertySymbol { IsIndexer: true })
        {
            var inScope = project is null ? _solution.Projects.SelectMany(static p => p.Documents) : project.Documents;
            return inScope.ToImmutableHashSet();
        }

        var documents = DocumentsUsingName(project, SpelledName(symbol));

        if (symbol is INamedTypeSymbol { Name: { Length: > 9 } typeName }
            && typeName.EndsWith(AttributeSuffix, StringComparison.Ordinal))
        {
            documents = documents.Union(DocumentsUsingName(project, typeName[..^AttributeSuffix.Length]));
        }

        return documents;
    }

    /// <summary>
    /// The name a reference spells. An explicit implementation is named after its interface
    /// (<c>Lib.IGreeter.Greet</c>), but it is called as <c>Greet</c>, through the interface.
    /// </summary>
    private static string SpelledName(ISymbol symbol)
    {
        var implemented = symbol switch
        {
            IMethodSymbol { ExplicitInterfaceImplementations: [var method, ..] } => (ISymbol)method,
            IPropertySymbol { ExplicitInterfaceImplementations: [var property, ..] } => property,
            IEventSymbol { ExplicitInterfaceImplementations: [var interfaceEvent, ..] } => interfaceEvent,
            _ => null,
        };

        return implemented?.Name ?? symbol.Name;
    }

    private static async Task<ReferenceSearch> CreateAsync(Solution solution, CancellationToken cancellationToken)
    {
        var usageIndex = await SolutionUsageIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        return new ReferenceSearch(solution, usageIndex);
    }

    private ImmutableHashSet<Document> DocumentsUsingName(Project? project, string name)
    {
        return project is null
            ? _usageIndex.GetDocumentsUsingName(name)
            : _usageIndex.GetDocumentsUsingName(project, name);
    }

    private SharedTask<ImmutableArray<ReferenceLocation>> GetOrStart(ISymbol symbol, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_searches.TryGetValue(symbol, out var existing) && existing.IsReusable)
            {
                return existing;
            }

            var started = SharedTask<ImmutableArray<ReferenceLocation>>.Start(
                ct => SearchAsync(symbol, ct),
                cancellationToken);
            _searches[symbol] = started;
            return started;
        }
    }

    private async Task<ImmutableArray<ReferenceLocation>> SearchAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        // Nothing in scope spells the name: provably unreferenced, no search needed.
        var documents = GetCandidateDocuments(symbol);
        if (documents.Count == 0)
        {
            return ImmutableArray<ReferenceLocation>.Empty;
        }

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, _solution, documents, cancellationToken).ConfigureAwait(false);

        var locations = ImmutableArray.CreateBuilder<ReferenceLocation>();
        foreach (var referencedSymbol in referencedSymbols)
        {
            locations.AddRange(referencedSymbol.Locations);
        }

        return locations.ToImmutable();
    }

    /// <summary>
    /// The project <paramref name="symbol"/> cannot be seen outside of, or null when it can
    /// be seen from the whole solution.
    /// </summary>
    private Project? GetConfiningProject(ISymbol symbol)
    {
        var confinement = Confinement.None;
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            switch (current.DeclaredAccessibility)
            {
                // NotApplicable (local functions, extension blocks) proves nothing either
                // way; searching wider than needed is always safe.
                case Accessibility.Private:
                    confinement = Confinement.Private;
                    break;
                case Accessibility.Internal or Accessibility.ProtectedAndInternal when confinement is Confinement.None:
                    confinement = Confinement.Internal;
                    break;
            }

            if (confinement is Confinement.Private)
            {
                break;
            }
        }

        // The search cascades from an override to the member it overrides and from an
        // implementation to the interface member, and returns their references too -
        // which can be anywhere that member is visible, not just where this one is. A
        // public member can be an implementation without its own type knowing: a derived
        // type that lists the interface implements it with the inherited member.
        if (confinement is Confinement.None
            || symbol is (IMethodSymbol or IPropertySymbol or IEventSymbol) and { DeclaredAccessibility: Accessibility.Public }
            || symbol.IsOverride
            || InterfaceImplementationQuery.IsInterfaceImplementation(symbol)
            || symbol.ContainingAssembly is not { } assembly)
        {
            return null;
        }

        var scope = _assemblyScopes.GetOrAdd(
            assembly,
            static (a, solution) => new AssemblyScope(
                solution.GetProject(a),
                a.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute")),
            _solution);

        return confinement is Confinement.Internal && scope.HasFriendAssemblies ? null : scope.Project;
    }

    private enum Confinement
    {
        None,
        Internal,
        Private,
    }

    private sealed record AssemblyScope(Project? Project, bool HasFriendAssemblies);
}
