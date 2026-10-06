namespace Snipper.Cli;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Opt-in process-level resource summary, enabled by setting <c>SNIPPER_PERF=1</c>.
/// <para>
/// Exists because the performance claims in the release plans have to be measured rather
/// than extrapolated, and the two questions need different counters. Peak working set
/// settles "did this hold too much memory at once"; the allocated-bytes counter settles
/// "did this churn the heap". A run can be allocation-neutral and still spike, or hold a
/// small working set while generating hundreds of megabytes of garbage, so reporting only
/// one of the two is how the wrong optimisation gets chosen.
/// </para>
/// <para>
/// Deliberately an environment variable rather than a command-line flag. Flags belong to
/// the tool's public surface and are parsed on the default path, whereas this must stay
/// inert unless somebody is deliberately measuring. It writes to stderr, so a piped
/// report is unaffected, and nothing runs unless the variable is set - which is what
/// keeps the report bytes identical either way and preserves the determinism contract.
/// </para>
/// </summary>
internal sealed class PerfSummary
{
    private const string Variable = "SNIPPER_PERF";

    private readonly long _allocatedAtStart;
    private readonly int _gen0AtStart;
    private readonly int _gen1AtStart;
    private readonly int _gen2AtStart;
    private readonly Stopwatch _stopwatch;

    private PerfSummary(long allocatedAtStart, int gen0AtStart, int gen1AtStart, int gen2AtStart)
    {
        _allocatedAtStart = allocatedAtStart;
        _gen0AtStart = gen0AtStart;
        _gen1AtStart = gen1AtStart;
        _gen2AtStart = gen2AtStart;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Begins measuring when <c>SNIPPER_PERF</c> is set, otherwise returns null so the
    /// caller can skip the work entirely.
    /// </summary>
    public static PerfSummary? TryStart()
    {
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw)
            || (!string.Equals(raw, "1", StringComparison.Ordinal)
                && !string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Taken before MSBuildLocator registration in the entry point, so the design-time
        // build that resolves the SDK lands inside the measured window. Measuring only the
        // analysis phase would hide the largest fixed cost of a run.
        return new PerfSummary(
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    /// <summary>
    /// Writes the summary as one machine-parseable line on stderr.
    /// </summary>
    public void Write()
    {
        _stopwatch.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocatedAtStart;
        var gen0 = GC.CollectionCount(0) - _gen0AtStart;
        var gen1 = GC.CollectionCount(1) - _gen1AtStart;
        var gen2 = GC.CollectionCount(2) - _gen2AtStart;

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        // Invariant formatting because a harness parses these numbers, and because a
        // comma decimal separator on a machine with a European locale would otherwise
        // turn a measurement into a parse failure. string.Create keeps the interpolation
        // off the heap, which is the one allocation this type could otherwise be blamed
        // for while reporting on allocations.
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SNIPPER-PERF elapsed={_stopwatch.Elapsed.TotalSeconds:0.000}s "
            + $"allocatedBytes={allocated} gen0={gen0} gen1={gen1} gen2={gen2} "
            + $"peakWorkingSetBytes={process.PeakWorkingSet64} "
            + $"gcHeapBytes={GC.GetTotalMemory(forceFullCollection: false)}"));
    }
}
