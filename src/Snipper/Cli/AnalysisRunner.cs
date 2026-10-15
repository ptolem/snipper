namespace Snipper.Cli;

using System.Diagnostics;
using Microsoft.CodeAnalysis;
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
/// completion order, and each analyser's own findings in <see cref="FindingOrder"/>,
/// so the merged list is identical to a sequential run's whatever order each
/// analyser emitted in.
/// Scheduling is bounded by <see cref="AnalysisParallelism"/>;
/// <c>SNIPPER_MAX_DOP=1</c> restores strict sequential execution, which is also
/// the escape hatch if a future analyser carries hidden mutable state.
///
/// Progress goes to an <see cref="IProgress{T}"/> rather than to the console, so this
/// type knows nothing about Spectre.Console. Reports arrive from concurrent workers;
/// the sink is responsible for serialising them (see
/// <see cref="ConsoleRenderer.CreateAnalysisProgress"/>).
/// </summary>
internal static class AnalysisRunner
{
    public static async Task<IReadOnlyList<SnipperFinding>> RunAsync(
        IReadOnlyList<IWorkspaceAnalyser> analysers,
        Solution solution,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysers);
        ArgumentNullException.ThrowIfNull(solution);

        // Every analyser opens with the same sequence: GetCompilationAsync then
        // GetSemanticModelAsync per document. Left to the fan-out, every analyser
        // discovers the same cold compilations simultaneously and serializes on
        // Roslyn's compilation tracker — the fan-out then spends its first seconds
        // in lock contention rather than analysis. Warming sequentially pays each
        // compilation build exactly once, and concurrent readers of an
        // already-built compilation are the supported path.
        await WarmCompilationsAsync(solution, cancellationToken).ConfigureAwait(false);

        // One slot per analyser, indexed by position.
        var results = new IReadOnlyList<SnipperFinding>[analysers.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, analysers.Count),
            AnalysisParallelism.CreateFanOutOptions(cancellationToken),
            async (index, token) =>
            {
                var analyser = analysers[index];
                var analyserName = analyser.GetType().Name;
                progress?.Report(new AnalysisProgress(analyserName, "starting..."));

                var stopwatch = Stopwatch.StartNew();
                var findings = await analyser.AnalyzeAsync(
                    solution,
                    token,
                    progress: status => progress?.Report(new AnalysisProgress(analyserName, status))).ConfigureAwait(false);
                stopwatch.Stop();

                // Each slot is put in FindingOrder here, so an analyser may emit in
                // whatever order its traversal or its parallelism produces.
                results[index] = FindingOrder.Sort(findings);

                progress?.Report(new AnalysisProgress(
                    analyserName,
                    $"{findings.Count} finding(s) in {stopwatch.Elapsed.TotalSeconds:0.0}s",
                    Completed: true));
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

/// <summary>
/// One progress report from <see cref="AnalysisRunner"/>: either a status update while
/// an analyser runs, or, when <paramref name="Completed"/> is set, its final summary.
/// </summary>
internal sealed record AnalysisProgress(string AnalyserName, string Status, bool Completed = false);
