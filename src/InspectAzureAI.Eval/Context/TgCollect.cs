using System.Runtime.ExceptionServices;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>_util/_async.py</c> <c>tg_collect</c>: runs every function concurrently under one linked cancellation
/// source. The first failure cancels the remaining functions, waits for them to settle and is then rethrown (Python
/// raises <c>ex.exceptions[0]</c> of the task group); results come back in input order. Caller cancellation propagates
/// as an <see cref="OperationCanceledException"/> once every function has settled.
/// </summary>
internal static class TgCollect
{
    public static async Task<T[]> RunAsync<T>(IReadOnlyList<Func<CancellationToken, Task<T>>> funcs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(funcs);
        var results = new T[funcs.Count];
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        var failureSync = new object();

        await Task.WhenAll(Enumerable.Range(0, funcs.Count).Select(RunOneAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return results;

        async Task RunOneAsync(int index)
        {
            try
            {
                results[index] = await funcs[index](abort.Token).ConfigureAwait(false);
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
