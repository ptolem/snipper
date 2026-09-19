namespace Snipper.Analysis;

/// <summary>
/// Central degree-of-parallelism switch for analysis passes. Default: on
/// (<see cref="Environment.ProcessorCount"/>). Set <c>SNIPPER_MAX_DOP=1</c> to
/// force sequential execution — the revert path for the parallel-binding
/// adoption. Safety basis, spike-proven 2026-09-18: per-document parallel
/// semantic queries on an MSBuildWorkspace-loaded solution are binding-identical
/// to sequential (24,301 bindings, 0 drift) at 12.5× speed; syntax-only passes
/// were always parallel. Findings are emitted through order-independent
/// containers, so output is unaffected by scheduling.
/// </summary>
internal static class AnalysisParallelism
{
    private static int MaxDegreeOfParallelism
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("SNIPPER_MAX_DOP");
            return int.TryParse(raw, out var dop) && dop >= 1
                ? dop
                : Environment.ProcessorCount;
        }
    }

    public static ParallelOptions CreateOptions(CancellationToken cancellationToken)
    {
        return new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };
    }
}
