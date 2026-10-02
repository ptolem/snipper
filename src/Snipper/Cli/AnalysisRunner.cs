namespace Snipper.Cli;

using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Spectre.Console;
using Snipper.Analysis;
using Snipper.Models;

/// <summary>
/// Runs the enabled analysers and merges their findings.
///
/// Analysers are independent of one another: each takes the same immutable
/// <see cref="Solution"/> and returns its own finding list, and every
/// solution-keyed index they share (<c>SolutionUsageIndex</c>,
/// <c>FrameworkEvidenceIndex</c>, <c>InheritanceGraph</c>,
/// <c>ProjectPackageUsageCache</c>) is built for concurrent access —
/// memoized through <c>Lazy</c>(ExecutionAndPublication) over a
/// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>.
/// That is what makes fan-out safe here, and it is why those indexes were built
/// thread-safe rather than merely single-threaded-fast. First-touch on a shared
/// index therefore blocks on one build, never duplicates it.
///
/// Findings are reassembled in analyser declaration order regardless of
/// completion order, so the merged report is byte-identical to a sequential run.
/// Scheduling is bounded by <see cref="AnalysisParallelism"/>;
/// <c>SNIPPER_MAX_DOP=1</c> restores strict sequential execution, which is also
/// the escape hatch if a future analyser carries hidden mutable state.
/// </summary>
internal static class AnalysisRunner
{
    public static async Task<IReadOnlyList<SnipperFinding>> RunAsync(
        IReadOnlyList<IWorkspaceAnalyser> analysers,
        Solution solution,
        StatusContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysers);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(cancellationToken);

        // Every analyser opens with the same sequence: GetCompilationAsync then
        // GetSemanticModelAsync per document. Left to the fan-out, 17 analysers
        // discover the same cold compilations simultaneously and serialize on
        // Roslyn's compilation tracker — the fan-out then spends its first seconds
        // in lock contention rather than analysis. Warming sequentially pays each
        // compilation build exactly once, and concurrent readers of an
        // already-built compilation are the supported path.
        await WarmCompilationsAsync(solution, cancellationToken).ConfigureAwait(false);

        // One slot per analyser, indexed by position.
        var results = new IReadOnlyList<SnipperFinding>[analysers.Count];

        // Spectre's StatusContext and AnsiConsole are not thread-safe; every write
        // from a worker funnels through this gate. Held only around the write
        // itself, never across analysis.
        var consoleSync = new object();

        void ReportProgress(string analyserName, string status)
        {
            lock (consoleSync)
            {
                ctx.Status($"[grey]{Markup.Escape(analyserName)}: {Markup.Escape(status)}[/]");
            }
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, analysers.Count),
            AnalysisParallelism.CreateFanOutOptions(cancellationToken),
            async (index, token) =>
            {
                var analyser = analysers[index];
                var analyserName = analyser.GetType().Name;
                ReportProgress(analyserName, "starting...");

                var stopwatch = Stopwatch.StartNew();
                var findings = await analyser.AnalyzeAsync(
                    solution,
                    token,
                    progress: status => ReportProgress(analyserName, status)).ConfigureAwait(false);
                stopwatch.Stop();

                results[index] = findings;

                lock (consoleSync)
                {
                    AnsiConsole.MarkupLine(
                        $"[green]✓[/] [grey]{analyserName}: {findings.Count} finding(s) in {stopwatch.Elapsed.TotalSeconds:0.0}s[/]");
                }
            }).ConfigureAwait(false);

        var merged = new List<SnipperFinding>();
        foreach (var findings in results)
        {
            if (findings is not null)
            {
                merged.AddRange(findings);
            }
        }

        return merged;
    }

    /// <summary>
    /// Forces every compilable project's <see cref="Compilation"/> into the
    /// workspace cache before any analyser starts. Sequential by design: this is
    /// the one phase where serializing is the whole point.
    /// </summary>
    private static async Task WarmCompilationsAsync(Solution solution, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            try
            {
                _ = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            // A project that fails to compile is an analyser concern, not a
            // precondition for the others — warm-up must never fail the run.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Intentionally swallowed: see above.
            }
        }
    }
}
