// ============================================================================
//  LAYER 2: THE CONTROL PLANE
//
//  The control plane is the set of pieces that talk to a RUNNING eval from
//  outside the process: the sample buffer the engine's events are mirrored
//  into, the server `inspect view` polls for live progress, and the switch
//  that cancels a run. It sits between the interfaces and the engine because
//  the interfaces (a terminal, VS Code, a browser tab) may be a different
//  process from the eval, and the engine must not care.
//
//  Two one-way channels connect it to the engine, and neither is an import:
//    - down: a CancellationToken handed to eval() when the run starts;
//    - up:   the transcript tap (Transcript.EventEmitted) it subscribes to.
//  Search Layer3_Engine for "_control" — there is nothing to find.
//
//  The demo keeps it in-process (a static list instead of an HTTP server),
//  but the shape is the same: Start() an eval, Status() it, Cancel() it,
//  and list the logs it wrote through the filesystem abstraction.
// ============================================================================
using inspect_ai._eval;
using inspect_ai._util.display;
using inspect_ai._util.file;
using inspect_ai.log;

namespace inspect_ai._control;

/// <summary>A snapshot an outside observer can poll.</summary>
internal sealed record EvalStatus(string Task, int SamplesStarted, int SamplesFinished, string LastActivity, bool Done);

/// <summary>A handle to one running eval.</summary>
internal sealed class RunningEval(string taskName, CancellationTokenSource cancellation)
{
    internal int SamplesStarted;
    internal int SamplesFinished;
    internal string LastActivity = "starting";

    /// <summary>Completes with the log when the engine finishes (or faults / cancels).</summary>
    public Task<EvalLog> Completion { get; internal set; } = Task.FromException<EvalLog>(new InvalidOperationException("not started"));

    public EvalStatus Status() => new(taskName, SamplesStarted, SamplesFinished, LastActivity, Completion.IsCompleted);

    /// <summary>The only downward signal. The engine sees a token, not us.</summary>
    public void Cancel()
    {
        Display.Step("L2 _control", "cancel requested from outside the eval");
        cancellation.Cancel();
    }
}

internal static class ControlPlane
{
    private static readonly List<RunningEval> Running = new();   // no lock: single event loop

    public static RunningEval Start(EvalOptions options)
    {
        var cancellation = new CancellationTokenSource();
        var run = new RunningEval(options.Task, cancellation);

        // The upward channel: mirror transcript events into the run's status.
        // Python does this by writing events to the sample buffer on disk,
        // which the view server reads from another process.
        void Tap(Transcript transcript, Event e)
        {
            switch (e)
            {
                case SampleInitEvent: run.SamplesStarted++; break;
                case SampleDoneEvent: run.SamplesFinished++; break;
            }
            run.LastActivity = $"[{transcript.SampleId}] {e.GetType().Name}";
        }
        Transcript.EventEmitted += Tap;

        Display.Step("L2 _control", $"starting eval '{options.Task}': status endpoint live, cancellation token issued, engine called next");
        run.Completion = RunAndUntap();   // the engine runs synchronously up to its first await, then yields to us
        Running.Add(run);
        return run;

        async Task<EvalLog> RunAndUntap()
        {
            try { return await EvalRunner.eval(options, cancellation.Token); }   // -> layer 3
            finally { Transcript.EventEmitted -= Tap; }
        }
    }

    public static IReadOnlyList<RunningEval> RunningEvals => Running;

    /// <summary>Logs are files; any process can list them through the same abstraction that wrote them.</summary>
    public static IEnumerable<string> ListLogs(string logDir) => FileSystems.List(logDir);
}
