namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Syntax-only index of identifier names used in each document. Harvests
/// <see cref="SimpleNameSyntax"/> nodes (usages), never declaration identifier
/// tokens. Invariant: any semantic reference to a named symbol must spell its
/// name in source — so "name appears in no in-scope document" proves "symbol
/// has no references" without a FindReferencesAsync scan, and when the name
/// does appear the reference search is restricted to exactly those documents.
///
/// All documents are harvested — including generated and external ones — because
/// generated code can legitimately reference project members; the index records
/// usage evidence only, never finding locations.
///
/// Held in both directions, built from one syntax walk: name → documents answers
/// the per-symbol query in O(1), document → names answers the per-declaration
/// query in O(1). Neither orientation substitutes for the other — callers query
/// both ways, once per candidate symbol — and the reverse direction is what keeps
/// a solution-wide lookup from degrading into a fresh scan of every document per
/// queried name. Result sets are memoized, so a repeated (name) or (project, name)
/// query allocates nothing.
///
/// Built once per solution (parallel, CPU-bound) and shared by the
/// reference-checking analysers via a solution-keyed cache.
/// </summary>
internal sealed class SolutionUsageIndex
{
    private static readonly ConditionalWeakTable<Solution, Lazy<SolutionUsageIndex>> Cache = new();

    private static readonly IEqualityComparer<Document> DocumentComparer = new DocumentIdComparer();

    private readonly FrozenDictionary<DocumentId, FrozenSet<string>> _namesByDocument;
    private readonly FrozenDictionary<string, ImmutableHashSet<Document>> _documentsByName;
    private readonly ConcurrentDictionary<(ProjectId Project, string Name), ImmutableHashSet<Document>> _projectScopeMemo = new();

    private SolutionUsageIndex(
        FrozenDictionary<DocumentId, FrozenSet<string>> namesByDocument,
        FrozenDictionary<string, ImmutableHashSet<Document>> documentsByName)
    {
        _namesByDocument = namesByDocument;
        _documentsByName = documentsByName;
    }

    public static SolutionUsageIndex Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Lazy + ExecutionAndPublication: exactly one build even if analysers race.
        return Cache.GetValue(
                solution,
                static s => new Lazy<SolutionUsageIndex>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// True when <paramref name="name"/> appears in a usage position anywhere in the
    /// document. False proves the name is unused there — for symbols confined to a
    /// single document (locals, parameters) that means provably unreferenced.
    /// </summary>
    public bool IsNameUsedInDocument(Document document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(name);

        return _namesByDocument.TryGetValue(document.Id, out var names) && names.Contains(name);
    }

    /// <summary>
    /// Documents of <paramref name="project"/> that textually use <paramref name="name"/>.
    /// An empty result proves the symbol is unreferenced within the project.
    /// </summary>
    public ImmutableHashSet<Document> GetDocumentsUsingName(Project project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(name);

        // The solution-wide set is the superset; narrowing to one project is a
        // filtered pass over it. Memoized because the same (project, name) pair is
        // queried once per candidate symbol — several members typically share a name,
        // so the filter would otherwise repeat verbatim.
        return _projectScopeMemo.GetOrAdd(
            (project.Id, name),
            static (key, self) => self.FilterByProject(key.Project, key.Name),
            this);
    }

    /// <summary>
    /// Documents across the whole solution that textually use <paramref name="name"/>.
    /// An empty result proves the symbol is unreferenced anywhere in the solution.
    /// </summary>
    public ImmutableHashSet<Document> GetDocumentsUsingName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _documentsByName.TryGetValue(name, out var documents)
            ? documents
            : ImmutableHashSet<Document>.Empty.WithComparer(DocumentComparer);
    }

    private ImmutableHashSet<Document> FilterByProject(ProjectId projectId, string name)
    {
        if (!_documentsByName.TryGetValue(name, out var candidates))
        {
            return ImmutableHashSet<Document>.Empty.WithComparer(DocumentComparer);
        }

        ImmutableHashSet<Document>.Builder? builder = null;
        foreach (var document in candidates)
        {
            if (document.Project.Id == projectId)
            {
                builder ??= ImmutableHashSet.CreateBuilder(DocumentComparer);
                builder.Add(document);
            }
        }

        return builder is null
            ? ImmutableHashSet<Document>.Empty.WithComparer(DocumentComparer)
            : builder.ToImmutable();
    }

    private static SolutionUsageIndex Build(Solution solution)
    {
        // Interning pool: distinct identifier texts are stored once per build;
        // per-document sets and reverse buckets then hold references, not copies.
        var internPool = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var namesByDocument = new ConcurrentDictionary<DocumentId, FrozenSet<string>>();
        var documentsByName = new ConcurrentDictionary<string, ConcurrentDictionary<DocumentId, Document>>(StringComparer.Ordinal);

        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);

        Parallel.ForEach(
            documents,
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            document =>
            {
                // Trees are loaded before any analyser runs; awaiting the already-
                // completed task avoids re-entering the async state machine.
                var root = document.GetSyntaxTreeAsync().GetAwaiter().GetResult()?.GetRoot();
                if (root is null)
                {
                    return;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in root.DescendantNodes())
                {
                    if (node is not SimpleNameSyntax simpleName)
                    {
                        continue;
                    }

                    var text = simpleName.Identifier.Text;

                    // .NET does not cache string.GetHashCode, so every hash re-reads the
                    // whole identifier. Unqualified occurrence counts outnumber distinct
                    // identifiers several times over, and once a name is in this
                    // document's set both `documentsByName[name]` (already created on the
                    // first occurrence) and the bucket entry (already holding this
                    // document) are guaranteed to contain it - so lines below are provably
                    // redundant and two of the three hashes go with them.
                    if (!names.Add(text))
                    {
                        continue;
                    }

                    var interned = internPool.GetOrAdd(text, text);

                    var bucket = documentsByName.GetOrAdd(interned, static _ => new ConcurrentDictionary<DocumentId, Document>());
                    bucket.TryAdd(document.Id, document);
                }

                namesByDocument[document.Id] = names.ToFrozenSet(StringComparer.Ordinal);
            });

        // Deterministic order: documents sorted by file path so the candidate set
        // handed to SymbolFinder — and therefore any finding order derived from it —
        // does not depend on parallel scheduling.
        var distinctDocuments = new List<KeyValuePair<DocumentId, Document>>(namesByDocument.Count);
        var seenDocuments = new HashSet<DocumentId>();
        foreach (var bucket in documentsByName.Values)
        {
            foreach (var entry in bucket)
            {
                if (seenDocuments.Add(entry.Key))
                {
                    distinctDocuments.Add(new KeyValuePair<DocumentId, Document>(entry.Key, entry.Value));
                }
            }
        }

        distinctDocuments.Sort(static (left, right) =>
        {
            var byPath = string.CompareOrdinal(left.Value.FilePath ?? string.Empty, right.Value.FilePath ?? string.Empty);
            return byPath != 0 ? byPath : left.Key.Id.CompareTo(right.Key.Id);
        });

        var ordinalByDocument = new Dictionary<DocumentId, int>(distinctDocuments.Count);
        for (var index = 0; index < distinctDocuments.Count; index++)
        {
            ordinalByDocument[distinctDocuments[index].Key] = index;
        }

        var inverted = new Dictionary<string, ImmutableHashSet<Document>>(documentsByName.Count, StringComparer.Ordinal);
        foreach (var (name, bucket) in documentsByName)
        {
            var ordered = new List<Document>(bucket.Count);
            foreach (var document in bucket.Values)
            {
                ordered.Add(document);
            }

            ordered.Sort((left, right) =>
            {
                var leftOrdinal = ordinalByDocument.TryGetValue(left.Id, out var l) ? l : int.MaxValue;
                var rightOrdinal = ordinalByDocument.TryGetValue(right.Id, out var r) ? r : int.MaxValue;
                return leftOrdinal.CompareTo(rightOrdinal);
            });

            inverted[name] = ImmutableHashSet.CreateRange(DocumentComparer, ordered);
        }

        return new SolutionUsageIndex(namesByDocument.ToFrozenDictionary(), inverted.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Roslyn's <see cref="Document"/> does not override equality, so reference
    /// identity would be the default — correct for a single loaded solution, but
    /// fragile across the same DocumentId rebuilt into a new instance. Keying on
    /// the stable <see cref="DocumentId"/> makes merged sets behave regardless.
    /// </summary>
    private sealed class DocumentIdComparer : IEqualityComparer<Document>
    {
        public bool Equals(Document? x, Document? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return x is not null && y is not null && x.Id == y.Id;
        }

        public int GetHashCode(Document obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return obj.Id.GetHashCode();
        }
    }
}
