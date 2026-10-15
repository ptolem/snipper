namespace Snipper.Analysis;

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>A document an analyser should judge, with the syntax root it was judged by.</summary>
internal sealed record ScopedDocument(Document Document, SyntaxNode Root)
{
    /// <summary>
    /// Whether the document's assembly grants <c>InternalsVisibleTo</c>, which widens
    /// what "internal" proves. Decided once per project rather than once per analyser.
    /// </summary>
    public bool HasFriendAssemblies { get; init; }
}

/// <summary>
/// The documents the per-document analysers walk: within every compilable project, every
/// document with a syntax tree that <see cref="ExclusionEngine.ShouldSkipDocument"/> does
/// not rule out (generated, external, or carrying an auto-generated header).
/// <para>
/// Fourteen analysers used to open with the same loop prologue to arrive at this list, and
/// most of them fetched the semantic model before asking whether the document was in
/// scope at all. Deciding it once per solution means the skip test runs before any
/// binding, and an analyser's loop starts at the code that is actually its own.
/// </para>
/// <para>
/// The list is flat - solution order, then document order - so an analyser can fan out
/// across the whole solution with <see cref="AnalysisParallelism.CreateOptions"/> instead
/// of one project at a time; most projects have too few documents to fill the machine.
/// Anything an analyser carries out of the loop besides findings has to be put back in
/// this order, which is what makes its later phases independent of scheduling.
/// </para>
/// <para>
/// Analysers that need a different set keep their own loop: ConfigurationBinding scans
/// generated code on purpose, DuplicateFragment collects by path across linked files, and
/// UnusedUsingDirective is driven by compiler diagnostics rather than by documents.
/// </para>
/// </summary>
internal static class AnalysisScope
{
    private static readonly ConditionalWeakTable<Solution, Lazy<IReadOnlyList<ScopedDocument>>> Cache = new();

    public static IReadOnlyList<ScopedDocument> Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Lazy + ExecutionAndPublication: exactly one build even if analysers race.
        return Cache.GetValue(
                solution,
                static s => new Lazy<IReadOnlyList<ScopedDocument>>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private static List<ScopedDocument> Build(Solution solution)
    {
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var documents = new List<ScopedDocument>();

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            // Synchronous like the other solution-keyed indexes: AnalysisRunner has
            // already built every compilation, so this and the syntax roots below only
            // read back what is cached.
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
            var hasFriendAssemblies = compilation is not null
                && compilation.Assembly.GetAttributes()
                    .Any(static a => a.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute");

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                documents.Add(new ScopedDocument(document, root) { HasFriendAssemblies = hasFriendAssemblies });
            }
        }

        return documents;
    }
}
