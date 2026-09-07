// ============================================================================
//  LAYER 3: THE ENGINE
//  Python: inspect_ai/_eval  (eval.py, run.py, task/run.py, loader.py, ...)
//
//  The engine turns "run task X against model Y" into finished samples and a
//  log. Step by step:
//
//    1. resolve the task name through the registry (no import of the author's file);
//    2. resolve the model through get_model() (layer 5) and make it the active model;
//    3. for each sample, under a concurrency limit:
//         a. open a transcript and make it ambient;
//         b. create the sandbox and copy the sample's files in (layer 7);
//         c. build the TaskState and run the task's solver (layer 4),
//            handing it the tool-calling generate loop defined below;
//         d. score the finished state (layer 4);
//    4. compute metrics and write the log through the filesystem abstraction.
//
//  The engine references every layer below it and nothing above. It does not
//  know the control plane exists: it receives a CancellationToken and emits
//  transcript events, and that is the whole of its conversation with layers
//  1 and 2.
// ============================================================================
using System.Text.Json;
using inspect_ai._util._async;
using inspect_ai._util.display;
using inspect_ai._util.file;
using inspect_ai._util.registry;
using inspect_ai.dataset;
using inspect_ai.log;
using inspect_ai.model;
using inspect_ai.scorer;
using inspect_ai.solver;
using inspect_ai.tool;
using inspect_ai.util;

namespace inspect_ai._eval;

/// <summary>What the CLI hands the engine (a slice of Python's eval() keyword arguments).</summary>
internal sealed record EvalOptions(string Task, string Model, string LogDir, int MaxSamples);

internal sealed record SampleResult(string Id, TaskState State, Score Score, Transcript Transcript);

internal sealed record EvalLog(
    string Task, string Model, DateTimeOffset Started, DateTimeOffset Completed,
    IReadOnlyList<SampleResult> Samples, IReadOnlyDictionary<string, double> Metrics, string Location);

internal static class EvalRunner
{
    /// <summary>Module-level state with no lock: only the event-loop thread ever touches it.</summary>
    internal static int ActiveSamples;

    /// <summary>Python: `eval(task, model=..., log_dir=..., max_samples=...)`.</summary>
    public static async Task<EvalLog> eval(EvalOptions options, CancellationToken cancel)
    {
        DisplayOnce.Banner("3", "Step 3 · Engine (L3 _eval): resolve the task, run every sample, write the log");
        var started = DateTimeOffset.UtcNow;

        // 1. Task by name. The registry hands back a fully built EvalTask.
        var task = Registry.Create<EvalTask>(RegistryType.Task, options.Task);

        // 2. Model by name. Layer 5 resolves the provider; the engine holds only a
        //    Model, and makes it ambient so model-graded scorers can find it.
        var model = Models.get_model(options.Model);
        using var activeModel = Models.BeginActive(model);

        // 3. Samples, at most MaxSamples at once — all on the one event loop.
        var generate = TaskGenerate.create(model, cancel);
        var slots = new SemaphoreSlim(options.MaxSamples);
        Display.Step("L3 _eval", $"running {task.dataset.Samples.Count} samples, max {options.MaxSamples} at a time");
        var results = await Task.WhenAll(task.dataset.Samples.Select(sample => run_sample(task, sample, generate, slots, cancel)));

        // 4. Metrics and the log.
        var metrics = new Dictionary<string, double> { ["accuracy"] = Metrics.accuracy(results.Select(r => r.Score).ToList()) };
        var location = LogWriter.Write(options, started, results, metrics);
        Display.Step("L3 _eval", $"accuracy={metrics["accuracy"]:0.00}; log written to {location}");
        return new EvalLog(options.Task, model.Name, started, DateTimeOffset.UtcNow, results, metrics, location);
    }

    private static async Task<SampleResult> run_sample(EvalTask task, Sample sample, Generate generate, SemaphoreSlim slots, CancellationToken cancel)
    {
        await slots.WaitAsync(cancel);
        try
        {
            SingleThreadEventLoop.AssertOnLoopThread("EvalRunner.ActiveSamples");
            ActiveSamples++;   // plain increment: no Interlocked needed on a single loop

            // a. The transcript for this sample becomes ambient; every layer
            //    below finds it through transcript() without being passed it.
            var transcript = new Transcript(sample.Id);
            using var _ = Transcript.Begin(transcript);
            transcript.Emit(new SampleInitEvent("L3 _eval", sample.Id, sample.Input));
            Display.Step("L3 _eval", $"[{sample.Id}] sample started (active={ActiveSamples})");

            // b. The sandbox, if the task asked for one, plus the sample's files.
            using var sandboxScope = task.sandbox is null ? null : Sandboxes.Begin(Sandboxes.Create(task.sandbox, sample.Id));
            foreach (var (path, contents) in sample.Files ?? new Dictionary<string, string>())
                await Sandboxes.sandbox().WriteFile(path, contents);

            // c. The solver. The engine passes `generate` down; the solver never imports the engine.
            var state = new TaskState(sample);
            cancel.ThrowIfCancellationRequested();
            state = await task.solver(state, generate);

            // d. The score.
            var score = await task.scorer(state, new Target(sample.Target));
            transcript.Emit(new ScoreEvent("L3 _eval", score.Value, score.Answer, score.Explanation));
            transcript.Emit(new SampleDoneEvent("L3 _eval", sample.Id, Success: true));
            Display.Step("L3 _eval", $"[{sample.Id}] sample finished: {score.Value}");
            return new SampleResult(sample.Id, state, score, transcript);
        }
        finally
        {
            ActiveSamples--;
            slots.Release();
        }
    }
}

/// <summary>
/// The tool-calling loop (Python: _eval/task/generate.py). Call the model;
/// if it asked for tools, run them and call again; stop when it answers.
/// Built here, in the engine, and injected into solvers as the `generate`
/// callback.
/// </summary>
internal static class TaskGenerate
{
    public static Generate create(Model model, CancellationToken cancel) => async state =>
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            Display.Step("L3 _eval (generate)", $"[{state.SampleId}] calling the model with {state.Messages.Count} messages");
            var output = await model.Generate(state.Messages, state.Tools.Select(t => t.Info).ToList());   // -> layer 5
            state.Output = output;
            state.Messages.Add(output.Message);

            if (output.Message.ToolCalls is not { Count: > 0 })
                return state;   // the model answered; the loop is done

            Display.Step("L3 _eval (generate)", $"[{state.SampleId}] model requested {output.Message.ToolCalls.Count} tool call(s); executing");
            state.Messages.AddRange(await Tools.execute_tools(output.Message, state.Tools));   // -> layer 4 (-> 7 for bash)
        }
    };
}

/// <summary>Writes the eval log as JSON through the filesystem abstraction (Python: log/_recorders).</summary>
internal static class LogWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Write(EvalOptions options, DateTimeOffset started, IReadOnlyList<SampleResult> results, IReadOnlyDictionary<string, double> metrics)
    {
        var document = new
        {
            eval = new { task = options.Task, model = options.Model, created = started },
            results = new { metrics },
            samples = results.Select(r => new
            {
                id = r.Id,
                input = r.State.Input,
                target = r.State.Target,
                messages = r.State.Messages,
                score = r.Score,
                // Declared as object so System.Text.Json serialises each event's runtime type.
                events = r.Transcript.Events.Cast<object>().ToList(),
            }),
        };

        var fileName = $"{started:yyyy-MM-ddTHH-mm-ss}_{options.Task}.json";
        var location = FileSystems.Join(options.LogDir, fileName);
        FileSystems.WriteText(location, JsonSerializer.Serialize(document, Options));
        return location;
    }
}
