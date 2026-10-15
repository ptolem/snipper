namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Snipper.Analysis;
using Xunit;

public sealed class SolutionIndexCacheShould
{
    [Fact]
    public async Task Build_Once_However_Many_Callers_Arrive_During_The_Build()
    {
        using var workspace = new AdhocWorkspace();
        var gate = new TaskCompletionSource();
        var builds = 0;
        var cache = new SolutionIndexCache<object>(async (_, ct) =>
        {
            Interlocked.Increment(ref builds);
            await gate.Task.WaitAsync(ct);
            return new object();
        });

        var callers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(workspace.CurrentSolution, CancellationToken.None))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        builds.Should().Be(1);
        results.Distinct().Should().ContainSingle();
        cache.IsBuilt(workspace.CurrentSolution).Should().BeTrue();
    }

    [Fact]
    public async Task Build_Again_After_A_Cancelled_Build()
    {
        using var workspace = new AdhocWorkspace();
        var cache = new SolutionIndexCache<object>((_, _) => Task.FromResult(new object()));

        var cancelled = () => cache.GetAsync(workspace.CurrentSolution, new CancellationToken(canceled: true));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        cache.IsBuilt(workspace.CurrentSolution).Should().BeFalse();

        var built = await cache.GetAsync(workspace.CurrentSolution, CancellationToken.None);

        built.Should().NotBeNull();
        cache.IsBuilt(workspace.CurrentSolution).Should().BeTrue();
    }

    [Fact]
    public async Task Not_Pass_One_Callers_Cancellation_On_To_Another_Waiting_For_The_Same_Build()
    {
        using var workspace = new AdhocWorkspace();
        var gate = new TaskCompletionSource();
        var builds = 0;
        var cache = new SolutionIndexCache<object>(async (_, ct) =>
        {
            Interlocked.Increment(ref builds);
            await gate.Task.WaitAsync(ct);
            return new object();
        });
        using var first = new CancellationTokenSource();

        var starter = cache.GetAsync(workspace.CurrentSolution, first.Token);
        var waiter = cache.GetAsync(workspace.CurrentSolution, CancellationToken.None);
        await first.CancelAsync();

        await starter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult();
        (await waiter).Should().NotBeNull();
        builds.Should().Be(2);
    }

    [Fact]
    public async Task Keep_A_Failed_Build()
    {
        using var workspace = new AdhocWorkspace();
        var builds = 0;
        var cache = new SolutionIndexCache<object>((_, _) =>
        {
            Interlocked.Increment(ref builds);
            throw new InvalidOperationException("broken");
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var get = () => cache.GetAsync(workspace.CurrentSolution, CancellationToken.None);
            await get.Should().ThrowAsync<InvalidOperationException>();
        }

        builds.Should().Be(1);
    }
}
