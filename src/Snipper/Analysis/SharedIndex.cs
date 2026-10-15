namespace Snipper.Analysis;

using System.Diagnostics;
using Microsoft.CodeAnalysis;

/// <summary>
/// The solution-wide indexes analysers share. An analyser names the ones it reads in
/// <see cref="IWorkspaceAnalyser.SharedIndexes"/>, and <c>AnalysisRunner</c> builds that
/// union up front, as a timed stage of its own, before any analyser starts.
/// </summary>
[Flags]
public enum SharedIndex
{
    None = 0,

    /// <summary>The documents the per-document analysers walk; see <see cref="Analysis.AnalysisScope"/>.</summary>
    AnalysisScope = 1 << 0,

    /// <summary>Identifier names per document; see <see cref="Analysis.SolutionUsageIndex"/>.</summary>
    SolutionUsageIndex = 1 << 1,

    /// <summary>Source-declared base → derived edges; see <see cref="Analysis.InheritanceGraph"/>.</summary>
    InheritanceGraph = 1 << 2,

    /// <summary>Types the frameworks use without a C# reference; see <see cref="Analysis.FrameworkEvidenceIndex"/>.</summary>
    FrameworkEvidenceIndex = 1 << 3,

    /// <summary>Enums whose ordinal is a contract; see <see cref="Analysis.EnumOrdinalContractIndex"/>.</summary>
    EnumOrdinalContractIndex = 1 << 4,
}

/// <summary>
/// Builds and inspects the <see cref="SharedIndex"/> indexes as a set.
/// </summary>
internal static class SharedIndexes
{
    private static readonly IndexEntry[] All =
    [
        new(SharedIndex.AnalysisScope, AnalysisScope.GetAsync, AnalysisScope.IsBuilt),
        new(SharedIndex.SolutionUsageIndex, SolutionUsageIndex.GetAsync, SolutionUsageIndex.IsBuilt),
        new(SharedIndex.InheritanceGraph, InheritanceGraph.GetAsync, InheritanceGraph.IsBuilt),
        new(SharedIndex.FrameworkEvidenceIndex, FrameworkEvidenceIndex.GetAsync, FrameworkEvidenceIndex.IsBuilt),
        new(SharedIndex.EnumOrdinalContractIndex, EnumOrdinalContractIndex.GetAsync, EnumOrdinalContractIndex.IsBuilt),
    ];

    /// <summary>
    /// Builds every index in <paramref name="indexes"/> for <paramref name="solution"/>,
    /// concurrently, and reports each one's own build time as it finishes. Each build is
    /// internally parallel too, and none reads another, so they share the machine rather
    /// than queue for it; the times overlap and do not add up to the stage's.
    /// </summary>
    public static Task BuildAsync(
        SharedIndex indexes,
        Solution solution,
        Action<SharedIndex, TimeSpan>? built,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);

        return Task.WhenAll(All
            .Where(entry => indexes.HasFlag(entry.Index))
            .Select(async entry =>
            {
                var start = Stopwatch.GetTimestamp();
                await entry.GetAsync(solution, cancellationToken).ConfigureAwait(false);
                built?.Invoke(entry.Index, Stopwatch.GetElapsedTime(start));
            }));
    }

    /// <summary>The indexes already built for <paramref name="solution"/>.</summary>
    public static SharedIndex Built(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var built = SharedIndex.None;
        foreach (var entry in All)
        {
            if (entry.IsBuilt(solution))
            {
                built |= entry.Index;
            }
        }

        return built;
    }

    private sealed record IndexEntry(
        SharedIndex Index,
        Func<Solution, CancellationToken, Task> GetAsync,
        Func<Solution, bool> IsBuilt);
}
