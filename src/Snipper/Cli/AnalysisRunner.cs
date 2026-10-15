namespace Snipper.Cli;

using System.Diagnostics;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Snipper.Analysis;
using Snipper.Models;

/// <summary>
/// Runs the enabled analysers and merges their findings.
///
/// Analysers are independent of one another: each takes the same immutable
/// <see cref="Solution"/> and returns its own finding list, and every
/// solution-keyed index they share is immutable once built and built at most once
/// per solution: the <see cref="SharedIndex"/> indexes through
/// <see cref="SolutionIndexCache{T}"/>, <c>ProjectPackageUsageCache</c> per project.
/// That is what makes fan-out safe here.
///
/// The run has three stages: warm the compilations, build the shared indexes the
/// analysers declare, then fan the analysers out. An analyser that reads an index it
/// did not declare still gets it - awaiting one build, never duplicating it - but pays
/// for it inside its own time.
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
    private const string SharedIndexesStage = "Shared indexes";

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

        // The shared indexes are a stage of their own rather than built on first touch
        // inside the fan-out, where their cost was charged to whichever analyser got
        // there first and made the per-analyser times incomparable.
        await BuildSharedIndexesAsync(analysers, solution, progress, cancellationToken).ConfigureAwait(false);

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
    /// Builds the union of the indexes the analysers declare that are not built yet,
    /// reporting each one's own time and then the stage's. Only what this run's analysers
    /// read is built, so a run limited to, say, the package rules builds none.
    /// </summary>
    private static async Task BuildSharedIndexesAsync(
        IReadOnlyList<IWorkspaceAnalyser> analysers,
        Solution solution,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var indexes = SharedIndex.None;
        foreach (var analyser in analysers)
        {
            indexes |= analyser.SharedIndexes;
        }

        // A second run over the same solution - the disabled-rule pass - finds them built.
        indexes &= ~SharedIndexes.Built(solution);
        if (indexes == SharedIndex.None)
        {
            return;
        }

        progress?.Report(new AnalysisProgress(SharedIndexesStage, "building..."));
        var stopwatch = Stopwatch.StartNew();
        await SharedIndexes.BuildAsync(
            indexes,
            solution,
            (index, elapsed) => progress?.Report(new AnalysisProgress(index.ToString(), $"built in {elapsed.TotalSeconds:0.0}s", Completed: true)),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new AnalysisProgress(
            SharedIndexesStage,
            $"{BitOperations.PopCount((uint)indexes)} built in {stopwatch.Elapsed.TotalSeconds:0.0}s",
            Completed: true));
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
