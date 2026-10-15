namespace Snipper.Analysis;

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
    private static readonly SolutionIndexCache<IReadOnlyList<ScopedDocument>> Cache = new(BuildAsync);

    public static Task<IReadOnlyList<ScopedDocument>> GetAsync(Solution solution, CancellationToken cancellationToken)
        => Cache.GetAsync(solution, cancellationToken);

    public static bool IsBuilt(Solution solution) => Cache.IsBuilt(solution);

    private static async Task<IReadOnlyList<ScopedDocument>> BuildAsync(Solution solution, CancellationToken cancellationToken)
    {
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var documents = new List<ScopedDocument>();

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            // AnalysisRunner has already built every compilation, so this and the syntax
            // roots below only read back what is cached.
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var hasFriendAssemblies = compilation is not null
                && compilation.Assembly.GetAttributes()
                    .Any(static a => a.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute");

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
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
