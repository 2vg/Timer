using System;
using System.Threading;
using System.Threading.Tasks;
using Timer.Backend.Storage.Scheduling;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class ScoreRecalcSchedulerTests
{
    [Fact]
    public async Task ConstructionIsInertAndStartPerformsTheInitialDurableScan()
    {
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scheduler = new ScoreRecalcScheduler(() =>
        {
            Interlocked.Increment(ref calls);
            scanned.TrySetResult();
            return Task.FromResult(0);
        }, scanInterval: TimeSpan.FromHours(1));

        await Task.Delay(25);
        Assert.Equal(0, Volatile.Read(ref calls));

        scheduler.Start();
        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task PeriodicScanRecoversWhenNoWakeSignalArrives()
    {
        var secondScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scheduler = new ScoreRecalcScheduler(() =>
        {
            if (Interlocked.Increment(ref calls) >= 2)
            {
                secondScan.TrySetResult();
            }

            return Task.FromResult(0);
        }, scanInterval: TimeSpan.FromMilliseconds(20));

        scheduler.Start();
        await secondScan.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task StopWaitsForActiveCallbackAndDisposeIsIdempotent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ScoreRecalcScheduler(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return 0;
        }, scanInterval: TimeSpan.FromHours(1));

        scheduler.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var dispose = scheduler.StopAsync(deadline.Token);
        await Task.Delay(25);
        Assert.False(dispose.IsCompleted);

        release.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Dispose();
        scheduler.Dispose();
    }

    [Fact]
    public async Task StopCancelsTheActiveCallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new ScoreRecalcScheduler(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        scheduler.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await scheduler.StopAsync(deadline.Token);
        Assert.True(scheduler.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SlowCancellationCallbackCannotBlockTheShutdownDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var scheduler = new ScoreRecalcScheduler(async token =>
        {
            using var registration = token.Register(() =>
            {
                cancelling.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            });
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        scheduler.Start();
        // These waits bound events that must happen; cancellation callbacks run on the thread pool, which a
        // loaded parallel test run can delay by seconds. The deadline under test is the 100 ms below.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            var stopping = scheduler.StopAsync(deadline.Token);
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            Assert.False(release.IsSet);
            scheduler.Dispose();
            Assert.False(scheduler.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task StopDeadlineDoesNotWaitForeverForAnUncooperativeCallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ScoreRecalcScheduler(async token =>
        {
            entered.SetResult();
            await release.Task;
            using var registration = token.Register(() => { });
            return 0;
        });
        scheduler.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.StopAsync(deadline.Token));
            scheduler.Dispose();
            Assert.False(scheduler.Completion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
