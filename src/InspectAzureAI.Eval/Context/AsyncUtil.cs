using System.Runtime.ExceptionServices;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>_util/_async.py</c>.</summary>
public static class AsyncUtil
{
    /// <summary>
    /// Port of <c>tg_collect(funcs)</c>: runs every function concurrently and returns their results in the order of
    /// <paramref name="funcs"/>. Like an anyio task group, the first function to fail cancels every other one
    /// (through the token each receives), the group waits for them to settle, and that first exception is rethrown
    /// as-is; an <see cref="OperationCanceledException"/> a function raises because of that cancellation is not a
    /// failure. Cancelling <paramref name="cancellationToken"/> cancels every function and surfaces as an
    /// <see cref="OperationCanceledException"/>, even when a function had already failed. Python's functions take
    /// no arguments; here each gets the group's token so siblings can be cancelled. The <c>exception_group</c>
    /// option is not ported.
    /// </summary>
    public static async Task<T[]> TgCollect<T>(IReadOnlyList<Func<CancellationToken, Task<T>>> funcs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(funcs);
        foreach (var func in funcs)
        {
            ArgumentNullException.ThrowIfNull(func, nameof(funcs));
        }

        var results = new T[funcs.Count];
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        var failureSync = new object();

        await Task.WhenAll(funcs.Select((func, index) => RunOneAsync(func, index))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return results;

        async Task RunOneAsync(Func<CancellationToken, Task<T>> func, int index)
        {
            try
            {
                abort.Token.ThrowIfCancellationRequested();
                results[index] = await func(abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lock (failureSync)
                {
                    failure ??= ex;
                }

                await abort.CancelAsync().ConfigureAwait(false);
            }
        }
    }
}
