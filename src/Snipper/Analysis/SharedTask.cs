namespace Snipper.Analysis;

/// <summary>
/// A piece of work started by the first caller to ask for it and awaited by every later
/// caller - the shared half of <see cref="SolutionIndexCache{T}"/> and
/// <see cref="ReferenceSearch"/>, which differ only in what they key it on.
/// <para>
/// The work runs under the token of the caller that started it. If that caller cancels,
/// the cancelled task must not be handed out again (<see cref="IsReusable"/>): a later
/// caller starts a fresh one, and a caller already waiting on it with a live token of its
/// own starts a fresh one too, rather than inheriting somebody else's cancellation. Work
/// that fails stays failed.
/// </para>
/// </summary>
internal sealed class SharedTask<T>
{
    private readonly CancellationToken _startedWith;

    private SharedTask(Task<T> task, CancellationToken startedWith)
    {
        Task = task;
        _startedWith = startedWith;
    }

    public Task<T> Task { get; }

    /// <summary>False once the work was cancelled; a cache must then replace it.</summary>
    public bool IsReusable => !Task.IsCanceled;

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool, so its synchronous prologue never
    /// runs under the caller's cache lock.
    /// </summary>
    public static SharedTask<T> Start(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return new SharedTask<T>(System.Threading.Tasks.Task.Run(() => work(cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Awaits the task <paramref name="getOrStart"/> hands out, asking again whenever the
    /// one it got was cancelled by the caller that started it rather than by this one.
    /// <paramref name="getOrStart"/> must replace a task that is not
    /// <see cref="IsReusable"/>.
    /// </summary>
    public static async Task<T> AwaitAsync(Func<SharedTask<T>> getOrStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getOrStart);

        while (true)
        {
            var pending = getOrStart();
            try
            {
                return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pending.WasCancelledByAnotherCaller(cancellationToken))
            {
                // Somebody else's work, abandoned by its caller; this caller still wants it.
            }
        }
    }

    private bool WasCancelledByAnotherCaller(CancellationToken caller)
    {
        return Task.IsCanceled
            && !caller.IsCancellationRequested
            && _startedWith != caller
            && _startedWith.IsCancellationRequested;
    }
}
