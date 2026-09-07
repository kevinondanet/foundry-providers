// ============================================================================
//  CROSS-CUTTING CONCERN 4 of 4: THE ASYNC FOUNDATION
//  Python: inspect_ai/_util/_async.py  (anyio, one event loop per process)
//
//  Inspect runs the whole eval on ONE anyio event loop. Every coroutine —
//  the CLI, the engine, hundreds of concurrent samples, model calls, sandbox
//  RPCs — is interleaved on a single OS thread. Two things follow:
//
//    1. Concurrency is cooperative: code only yields at an `await`.
//    2. Module-level state (the registry dictionary, active-sample counters,
//       caches) needs NO locks, because two pieces of code can never run at
//       the same instant. That is why you will not find a single lock in the
//       registry or the engine in this demo.
//
//  C# has no built-in single-threaded event loop for console apps (UI apps
//  get one from WPF/WinForms), so this file provides a tiny one: a
//  SynchronizationContext that queues every continuation onto the thread that
//  called Run(). Awaits inside the app resume on that same thread.
// ============================================================================
using System.Collections.Concurrent;

namespace inspect_ai._util._async;

internal sealed class SingleThreadEventLoop : SynchronizationContext
{
    // The work queue. Continuations from timers (Task.Delay) or I/O arrive
    // from other threads, are queued here, and are *executed* on the loop
    // thread by the pump in Run(). BlockingCollection is thread-safe by
    // itself; it is the only synchronised structure in the whole demo.
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private static SingleThreadEventLoop? _current;

    /// <summary>
    /// Run <paramref name="main"/> to completion on the calling thread,
    /// pumping every queued continuation on that thread until it finishes.
    /// This is the C# stand-in for `anyio.run(main)`.
    /// </summary>
    public static T Run<T>(Func<Task<T>> main)
    {
        var loop = new SingleThreadEventLoop();
        var previous = Current;
        _current = loop;
        SetSynchronizationContext(loop);
        try
        {
            // An async method runs synchronously until its first real await;
            // after that every continuation comes back through Post().
            var task = main();
            while (!task.IsCompleted)
            {
                // Short timeout so a continuation that completed the task on
                // another thread (which the demo never does) cannot hang us.
                if (loop._queue.TryTake(out var item, millisecondsTimeout: 50))
                    item.Callback(item.State);
            }
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
            _current = null;
        }
    }

    /// <summary>Called by the awaiter machinery whenever an `await` resumes.
    /// We never run the continuation inline; it goes to the queue so that it
    /// executes on the loop thread, in order.</summary>
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (OnLoopThread) d(state);
        else throw new NotSupportedException("Synchronous Send from another thread is not supported by the demo loop.");
    }

    /// <summary>True when the caller is on the event-loop thread.</summary>
    public static bool OnLoopThread
        => _current is { } loop && Environment.CurrentManagedThreadId == loop._threadId;

    /// <summary>
    /// The lock-free promise made explicit. Mutable module-level state calls
    /// this before touching itself; if the invariant "everything happens on
    /// the loop thread" were ever broken the demo would fail loudly instead
    /// of corrupting a dictionary.
    /// </summary>
    public static void AssertOnLoopThread(string what)
    {
        if (!OnLoopThread)
            throw new InvalidOperationException($"{what} was touched off the event-loop thread; that would need a lock.");
    }
}
