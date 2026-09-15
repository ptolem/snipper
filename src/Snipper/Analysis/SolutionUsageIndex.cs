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
/// Built once per solution (parallel, CPU-bound) and shared by the
/// reference-checking analysers via a solution-keyed cache.
/// </summary>
internal sealed class SolutionUsageIndex
{
    private static readonly ConditionalWeakTable<Solution, Lazy<SolutionUsageIndex>> Cache = new();

    private readonly Solution _solution;
    private readonly FrozenDictionary<DocumentId, FrozenSet<string>> _namesByDocument;

    private SolutionUsageIndex(Solution solution, FrozenDictionary<DocumentId, FrozenSet<string>> namesByDocument)
    {
        _solution = solution;
        _namesByDocument = namesByDocument;
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
        return _namesByDocument.TryGetValue(document.Id, out var names) && names.Contains(name);
    }

    /// <summary>
    /// Documents of <paramref name="project"/> that textually use <paramref name="name"/>.
    /// An empty result proves the symbol is unreferenced within the project.
    /// </summary>
    public ImmutableHashSet<Document> GetDocumentsUsingName(Project project, string name)
    {
        var builder = ImmutableHashSet.CreateBuilder<Document>();
        foreach (var document in project.Documents)
        {
            if (_namesByDocument.TryGetValue(document.Id, out var names) && names.Contains(name))
            {
                builder.Add(document);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Documents across the whole solution that textually use <paramref name="name"/>.
    /// An empty result proves the symbol is unreferenced anywhere in the solution.
    /// </summary>
    public ImmutableHashSet<Document> GetDocumentsUsingName(string name)
    {
        var builder = ImmutableHashSet.CreateBuilder<Document>();
        foreach (var project in _solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (_namesByDocument.TryGetValue(document.Id, out var names) && names.Contains(name))
                {
                    builder.Add(document);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static SolutionUsageIndex Build(Solution solution)
    {
        // Interning pool: distinct identifier texts are stored once process-wide
        // per build; per-document sets then hold references, not duplicate strings.
        var internPool = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var harvested = new ConcurrentBag<KeyValuePair<DocumentId, FrozenSet<string>>>();

        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);

        Parallel.ForEach(
            documents,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            document =>
            {
                var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                var root = tree?.GetRoot();
                if (root is null)
                {
                    return;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var text = node.Identifier.Text;
                    names.Add(internPool.GetOrAdd(text, text));
                }

                harvested.Add(new KeyValuePair<DocumentId, FrozenSet<string>>(document.Id, names.ToFrozenSet(StringComparer.Ordinal)));
            });

        return new SolutionUsageIndex(solution, harvested.ToFrozenDictionary());
    }
}
