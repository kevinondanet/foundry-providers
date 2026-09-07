using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The <c>tg_collect</c> port (<see cref="AsyncUtil.TgCollect"/>) behind <c>fork</c> and <c>multi_scorer</c>: ordered results, first-failure cancellation, caller cancellation.</summary>
public sealed class TgCollectTests
{
    private static Func<CancellationToken, Task<string>> Waiting(TaskCompletionSource? started, TaskCompletionSource<bool> cancelled) => async ct =>
    {
        started?.SetResult();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
        catch (OperationCanceledException)
        {
            cancelled.SetResult(true);
            throw;
        }

        return "not cancelled";
    };

    [Fact]
    public async Task results_come_back_in_input_order()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = await AsyncUtil.TgCollect<string>(
        [
            async _ => { await gate.Task; return "first"; },
            _ => { gate.SetResult(); return Task.FromResult("second"); },
        ], CancellationToken.None);

        Assert.Equal(["first", "second"], results);
        Assert.Empty(await AsyncUtil.TgCollect<string>([], CancellationToken.None));
    }

    [Fact]
    public async Task the_first_failure_cancels_the_other_functions_and_is_rethrown_after_they_settle()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<string>> failing = async _ =>
        {
            await started.Task;
            throw new InvalidOperationException("boom");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AsyncUtil.TgCollect([Waiting(started, cancelled), failing], CancellationToken.None));

        Assert.Equal("boom", error.Message);
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task caller_cancellation_propagates_as_cancellation_not_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = AsyncUtil.TgCollect([Waiting(null, cancelled)], cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }
}
