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
/// The build runs under the token of the caller that started it. If that caller cancels,
/// the cancelled build is dropped from the cache: a later caller starts a fresh one, and a
/// caller already waiting on it with a live token of its own starts a fresh one too,
/// rather than inheriting somebody else's cancellation. A build that fails stays failed,
/// as it did with <c>Lazy</c>.
/// </para>
/// </summary>
internal sealed class SolutionIndexCache<T>(Func<Solution, CancellationToken, Task<T>> build)
    where T : class
{
    private readonly ConditionalWeakTable<Solution, PendingBuild> _builds = new();
    private readonly Lock _gate = new();

    public async Task<T> GetAsync(Solution solution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);

        while (true)
        {
            var pending = GetOrStart(solution, cancellationToken);
            try
            {
                return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pending.WasCancelledByAnotherCaller(cancellationToken))
            {
                // Somebody else's build, abandoned by its caller; this caller still wants it.
            }
        }
    }

    /// <summary>Whether the index for <paramref name="solution"/> has been built.</summary>
    public bool IsBuilt(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return _builds.TryGetValue(solution, out var pending) && pending.Task.IsCompletedSuccessfully;
    }

    private PendingBuild GetOrStart(Solution solution, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_builds.TryGetValue(solution, out var existing) && !existing.Task.IsCanceled)
            {
                return existing;
            }

            // Task.Run, so the build's synchronous prologue never runs under the lock.
            var started = new PendingBuild(Task.Run(() => build(solution, cancellationToken), cancellationToken), cancellationToken);
            _builds.AddOrUpdate(solution, started);
            return started;
        }
    }

    private sealed record PendingBuild(Task<T> Task, CancellationToken StartedWith)
    {
        public bool WasCancelledByAnotherCaller(CancellationToken caller)
        {
            return Task.IsCanceled
                && !caller.IsCancellationRequested
                && StartedWith != caller
                && StartedWith.IsCancellationRequested;
        }
    }
}
