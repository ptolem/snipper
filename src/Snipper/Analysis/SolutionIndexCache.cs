namespace Snipper.Analysis;

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>
/// One index per <see cref="Solution"/>, built at most once and shared by every caller —
/// the memo behind each of the <see cref="SharedIndex"/> indexes.
/// <para>
/// Callers await the build rather than block on it. These indexes used to sit behind
/// <c>Lazy</c>(ExecutionAndPublication) with a synchronous build inside, so every analyser
/// that arrived during a build parked a thread-pool thread on <c>Lazy.Value</c> while the
/// build's own <c>Parallel.ForEach</c> needed threads from the same pool, and the build ran
/// with <see cref="CancellationToken.None"/>, so Ctrl+C waited for it.
/// </para>
/// <para>
/// The build runs under the token of the caller that started it, with the cancellation
/// rules of <see cref="SharedTask{T}"/>: a cancelled build is dropped from the cache and
/// never inherited by a caller still waiting with a live token, and a build that fails
/// stays failed, as it did with <c>Lazy</c>.
/// </para>
/// </summary>
internal sealed class SolutionIndexCache<T>(Func<Solution, CancellationToken, Task<T>> build)
    where T : class
{
    private readonly ConditionalWeakTable<Solution, SharedTask<T>> _builds = new();
    private readonly Lock _gate = new();

    public Task<T> GetAsync(Solution solution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return SharedTask<T>.AwaitAsync(() => GetOrStart(solution, cancellationToken), cancellationToken);
    }

    /// <summary>Whether the index for <paramref name="solution"/> has been built.</summary>
    public bool IsBuilt(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return _builds.TryGetValue(solution, out var pending) && pending.Task.IsCompletedSuccessfully;
    }

    private SharedTask<T> GetOrStart(Solution solution, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_builds.TryGetValue(solution, out var existing) && existing.IsReusable)
            {
                return existing;
            }

            var started = SharedTask<T>.Start(ct => build(solution, ct), cancellationToken);
            _builds.AddOrUpdate(solution, started);
            return started;
        }
    }
}
