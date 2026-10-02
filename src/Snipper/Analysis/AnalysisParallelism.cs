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
    private const string MaxDopVariable = "SNIPPER_MAX_DOP";
    private const string FanOutDopVariable = "SNIPPER_FANOUT_DOP";

    /// <summary>
    /// Width for a single pass's inner loops (documents, candidates, reference
    /// searches). Defaults to <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    private static int MaxDegreeOfParallelism
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(MaxDopVariable);
            return int.TryParse(raw, out var dop) && dop >= 1
                ? dop
                : Environment.ProcessorCount;
        }
    }

    /// <summary>
    /// Width for the outer analyser fan-out, distinct from the per-pass width
    /// because it nests with it: every analyser already saturates the machine with
    /// its own inner loops. Measured on a 16-core box against the sample app
    /// (190 findings, identical output at every width): fan-out 1 → 6.9s,
    /// 4 → 5.4s, 8 → 4.8s, 16 → 4.6s.
    ///
    /// A naive full-width fan-out without the compilation pre-warm in
    /// <c>AnalysisRunner</c> measured 33s — 17 analysers discovering the same cold
    /// compilations at once and serializing on Roslyn's compilation tracker. That
    /// contention, not the width itself, was the cost; with compilations warm the
    /// full width is also the fastest setting. <c>SNIPPER_FANOUT_DOP</c> narrows
    /// it for constrained CI agents, and <c>SNIPPER_MAX_DOP=1</c> still forces a
    /// fully sequential run, which subsumes it.
    /// </summary>
    private static int FanOutDegreeOfParallelism
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(FanOutDopVariable);
            return int.TryParse(raw, out var configured) && configured >= 1
                ? configured
                : MaxDegreeOfParallelism;
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

    /// <summary>
    /// Options for the outer analyser fan-out — narrower than
    /// <see cref="CreateOptions"/> for the reason documented on
    /// <see cref="FanOutDegreeOfParallelism"/>.
    /// </summary>
    public static ParallelOptions CreateFanOutOptions(CancellationToken cancellationToken)
    {
        return new ParallelOptions
        {
            MaxDegreeOfParallelism = FanOutDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };
    }
}
