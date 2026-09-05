using System.Collections.Concurrent;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of the <c>concurrency(name, concurrency)</c> context of <c>util/_concurrency.py</c> in the form the
/// web search providers use it: a process-wide semaphore per key, sized by the first caller to name the key
/// (later callers reuse it unchanged, as in Python). Adaptive and resizable semaphores are not ported.
/// </summary>
internal static class NamedConcurrency
{
    private sealed record Entry(SemaphoreSlim Semaphore, int Limit);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>Waits for a slot under <paramref name="key"/>; disposing the returned lease releases it.</summary>
    public static async Task<IDisposable> EnterAsync(string key, int concurrency, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        var entry = Entries.GetOrAdd(key, _ => new Entry(new SemaphoreSlim(concurrency, concurrency), concurrency));
        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(entry.Semaphore);
    }

    /// <summary>The limit the key was registered with, or null when nothing has used it yet.</summary>
    public static int? Limit(string key) => Entries.TryGetValue(key, out var entry) ? entry.Limit : null;

    /// <summary>Number of leases currently held under <paramref name="key"/>.</summary>
    public static int InUse(string key) => Entries.TryGetValue(key, out var entry) ? entry.Limit - entry.Semaphore.CurrentCount : 0;

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
