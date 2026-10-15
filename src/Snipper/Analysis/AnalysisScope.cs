namespace Snipper.Analysis;

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>A document an analyser should judge, with the syntax root it was judged by.</summary>
internal sealed record ScopedDocument(Document Document, SyntaxNode Root);

/// <summary>A compilable project and its in-scope documents, in project order.</summary>
internal sealed record ProjectScope(Project Project, IReadOnlyList<ScopedDocument> Documents);

/// <summary>
/// The documents the per-document analysers walk: every compilable project, and within it
/// every document with a syntax tree that <see cref="ExclusionEngine.ShouldSkipDocument"/>
/// does not rule out (generated, external, or carrying an auto-generated header).
/// <para>
/// Fourteen analysers used to open with the same loop prologue to arrive at this list, and
/// most of them fetched the semantic model before asking whether the document was in
/// scope at all. Deciding it once per solution means the skip test runs before any
/// binding, and an analyser's loop starts at the code that is actually its own.
/// </para>
/// <para>
/// Projects are kept even when every document was skipped, because some analysers do
/// per-project work (friend-assembly checks, progress) that does not depend on documents.
/// Order is solution order, then document order - the same traversal the loops had.
/// </para>
/// <para>
/// Analysers that need a different set keep their own loop: ConfigurationBinding scans
/// generated code on purpose, DuplicateFragment collects by path across linked files, and
/// UnusedUsingDirective is driven by compiler diagnostics rather than by documents.
/// </para>
/// </summary>
internal static class AnalysisScope
{
    private static readonly ConditionalWeakTable<Solution, Lazy<IReadOnlyList<ProjectScope>>> Cache = new();

    public static IReadOnlyList<ProjectScope> Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // Lazy + ExecutionAndPublication: exactly one build even if analysers race.
        return Cache.GetValue(
                solution,
                static s => new Lazy<IReadOnlyList<ProjectScope>>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private static List<ProjectScope> Build(Solution solution)
    {
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var projects = new List<ProjectScope>();

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            var documents = new List<ScopedDocument>();
            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                // Synchronous like the other solution-keyed indexes: AnalysisRunner has
                // already built every compilation, so the trees are parsed and this only
                // reads them back.
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                documents.Add(new ScopedDocument(document, root));
            }

            projects.Add(new ProjectScope(project, documents));
        }

        return projects;
    }
}
