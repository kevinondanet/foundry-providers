// ============================================================================
//  LAYER 1 (top): THE INTERFACES
//  Python: inspect_ai/_cli (the `inspect` command), inspect_ai/_view (the
//  log viewer and its server), inspect_ai/_display (terminal UI); plus the
//  VS Code extension, which is a separate repository.
//
//  The interfaces parse what the user typed, hand the control plane an
//  EvalOptions, watch progress, and print results. They own no evaluation
//  logic. Everything here is underscore-prefixed: the CLI's argument names
//  are a stable contract, but its Python functions are not.
// ============================================================================
using inspect_ai._control;
using inspect_ai._eval;
using inspect_ai._util.display;
using inspect_ai._util.registry;

namespace inspect_ai._cli
{
    using System.Diagnostics;
    using inspect_ai._view;
    using inspect_ai.log;

    internal static class Cli
    {
        public static async Task<int> Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return 0; }
            switch (args[0])
            {
                case "eval": return await Eval(args[1..]);
                case "list": return List();
                case "view": return View.Show(Option(args, "--log-dir") ?? "memory://logs");
                default: Console.WriteLine($"unknown command '{args[0]}'"); Usage(); return 1;
            }
        }

        /// <summary>
        /// Python: `inspect eval <task> --model <m>[,<m>...] --model-role grader=<m> --max-tasks <n> ...`.
        /// A comma-separated model list runs the task once per model (Python's
        /// eval(model=[...])) and ends with a scoreboard instead of a transcript.
        /// </summary>
        private static async Task<int> Eval(string[] rest)
        {
            Display.Banner("Step 1 · Interfaces (L1 _cli): parse the command line");
            if (rest.Length == 0 || rest[0].StartsWith("--")) { Console.WriteLine("eval needs a task name"); return 1; }
            var task = rest[0];
            var models = (Option(rest, "--model") ?? Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL") ?? "mock/gpt-demo")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var roles = ModelRoles(rest);
            var logDir = Option(rest, "--log-dir") ?? "memory://logs";
            var maxSamples = int.Parse(Option(rest, "--max-samples") ?? "1");
            var maxTasks = int.Parse(Option(rest, "--max-tasks") ?? "1");
            var cancelAfter = Option(rest, "--cancel-after") is { } ms ? TimeSpan.FromMilliseconds(int.Parse(ms)) : (TimeSpan?)null;
            Display.Step("L1 _cli", $"eval task={task} model={string.Join(",", models)} log-dir={logDir} max-samples={maxSamples} max-tasks={maxTasks}"
                + (roles.Count > 0 ? " model-role=" + string.Join(",", roles.Select(r => $"{r.Key}={r.Value}")) : ""));

            Display.Banner("Step 2 · Control plane (L2 _control): start the eval and expose it to outside observers");
            var slots = new SemaphoreSlim(maxTasks);   // Python: --max-tasks, how many task/model pairs run at once
            var outcomes = await Task.WhenAll(models.Select(model =>
                RunOne(new EvalOptions(task, model, logDir, maxSamples, roles), slots, cancelAfter)));

            if (outcomes.Length == 1)
            {
                var only = outcomes[0];
                if (only.Cancelled) { Display.Banner("Result: cancelled by the control plane before completion"); return 2; }
                if (only.Log is null) { Display.Banner($"Result: eval failed: {only.Error?.Message}"); return 1; }
                PrintTranscripts(only.Log);
                return 0;
            }

            Scoreboard(task, roles, logDir, outcomes);
            return outcomes.All(o => o.Log is not null) ? 0 : 1;
        }

        private sealed record EvalOutcome(EvalOptions Options, EvalLog? Log, Exception? Error, bool Cancelled, TimeSpan Elapsed);

        /// <summary>One eval through the control plane, watched from outside. A failure
        /// (a deployment that is not a chat model, say) becomes a row, not a crash.</summary>
        private static async Task<EvalOutcome> RunOne(EvalOptions options, SemaphoreSlim slots, TimeSpan? cancelAfter)
        {
            await slots.WaitAsync();
            var clock = Stopwatch.StartNew();
            try
            {
                var run = ControlPlane.Start(options);            // -> layer 2 (-> layer 3)
                var watcher = View.Watch(run, cancelAfter);       // a concurrent "inspect view" on the same loop
                try
                {
                    var log = await run.Completion;
                    return new EvalOutcome(options, log, null, false, clock.Elapsed);
                }
                catch (OperationCanceledException) when (run.IsCancelled)
                {
                    return new EvalOutcome(options, null, null, true, clock.Elapsed);
                }
                catch (Exception ex)
                {
                    Display.Step("L1 _cli", $"eval on {options.Model} failed: {ex.GetType().Name}: {Truncate(OneLine(ex.Message), 160)}");
                    return new EvalOutcome(options, null, ex, false, clock.Elapsed);
                }
                finally
                {
                    await watcher;
                }
            }
            finally
            {
                slots.Release();
            }
        }

        private static void PrintTranscripts(EvalLog log)
        {
            Display.Banner("Back at the top · the CLI prints what the lower layers wrote into the transcript");
            foreach (var sample in log.Samples)
            {
                Console.WriteLine($"  transcript of sample '{sample.Id}':");
                foreach (var e in sample.Transcript.Events)
                    Console.WriteLine($"    {e.Source,-10} {e.GetType().Name,-16} {e.Summary}");
            }
            Console.WriteLine();
            Console.WriteLine($"  task={log.Task} model={log.Model} samples={log.Samples.Count} accuracy={log.Metrics["accuracy"]:0.00}");
            Console.WriteLine($"  log: {log.Location}");
        }

        /// <summary>One row per model: same task, same samples, same grader, so the numbers compare.</summary>
        private static void Scoreboard(string task, IReadOnlyDictionary<string, string> roles, string logDir, EvalOutcome[] outcomes)
        {
            var grader = roles.TryGetValue("grader", out var g) ? g : "each model grades itself";
            Display.Banner($"Scoreboard · task={task} grader={grader}");

            var sampleIds = outcomes.FirstOrDefault(o => o.Log is not null)?.Log!.Samples.Select(s => s.Id).ToList() ?? new List<string>();
            var modelWidth = Math.Max("model".Length, outcomes.Max(o => o.Options.Model.Length));
            Console.WriteLine($"  {"model".PadRight(modelWidth)}  {"accuracy",8}  {string.Join("  ", sampleIds)}  {"calls",5}  {"tokens in/out",-15}  {"time",5}");

            foreach (var o in outcomes.OrderByDescending(o => o.Log?.Metrics["accuracy"] ?? -1).ThenBy(o => o.Elapsed))
            {
                var name = o.Options.Model.PadRight(modelWidth);
                if (o.Log is null)
                {
                    var why = o.Cancelled ? "cancelled" : $"error: {Truncate(OneLine(o.Error?.Message ?? "unknown"), 100)}";
                    Console.WriteLine($"  {name}  {"-",8}  {why}");
                    continue;
                }

                var log = o.Log;
                var grades = string.Join("  ", log.Samples.Select(s => s.Score.Value.PadRight(s.Id.Length)));
                // Only this model's own calls: with a fixed grader, its calls are in the transcript too.
                var calls = log.Samples.SelectMany(s => s.Transcript.Events.OfType<ModelEvent>()).Where(e => e.Model == log.Model).ToList();
                var tokens = $"{calls.Sum(e => e.InputTokens):N0}/{calls.Sum(e => e.OutputTokens):N0}";
                Console.WriteLine($"  {name}  {log.Metrics["accuracy"],8:0.00}  {grades}  {calls.Count,5}  {tokens,-15}  {o.Elapsed.TotalSeconds,4:0}s");
            }

            var wrong = outcomes.Where(o => o.Log is not null)
                .SelectMany(o => o.Log!.Samples.Where(s => !s.Score.IsCorrect).Select(s => (o.Options.Model, Sample: s)))
                .ToList();
            if (wrong.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  answers that did not score C:");
                foreach (var (model, sample) in wrong)
                    Console.WriteLine($"    {model} [{sample.Id}] {sample.Score.Value}: \"{Truncate(sample.Score.Answer.ReplaceLineEndings(" "), 120)}\"");
            }
            Console.WriteLine();
            Console.WriteLine($"  logs: {logDir}  (inspect-layers view --log-dir {logDir})");
        }

        /// <summary>Python: `inspect list tasks` (extended to every registry kind).</summary>
        private static int List()
        {
            Display.Banner("inspect list · the registry answers every name lookup");
            foreach (var kind in Enum.GetValues<RegistryType>())
                Console.WriteLine($"  {kind,-9} {string.Join(", ", Registry.Names(kind))}");
            return 0;
        }

        private static string? Option(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Python: `--model-role grader=openai/gpt-4o` (repeatable).</summary>
        private static IReadOnlyDictionary<string, string> ModelRoles(string[] args)
        {
            var roles = new Dictionary<string, string>();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--model-role") continue;
                var eq = args[i + 1].IndexOf('=');
                if (eq <= 0) throw new ArgumentException("--model-role expects role=provider/model, e.g. grader=azureai/gpt-5.4-mini");
                roles[args[i + 1][..eq]] = args[i + 1][(eq + 1)..];
            }
            return roles;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

        /// <summary>Collapse a JSON error body onto one line so it fits a table row.</summary>
        private static string OneLine(string s) => string.Join(' ', s.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

        private static void Usage()
        {
            Console.WriteLine("""
                usage:
                  inspect-layers                       full narrated walk-through (eval, view, layer check)
                  inspect-layers eval <task> [--model mock/gpt-demo] [--log-dir memory://logs]
                                             [--max-samples 1] [--max-tasks 1] [--cancel-after <ms>]
                                             [--model-role grader=<provider/model>]
                    tasks:  arithmetic (offline), expenses (multi-step, model-graded)
                    models: mock/<any>, azureai/<deployment>, anthropic/<claude-deployment>
                            a comma-separated list runs the task once per model and prints a scoreboard
                            (or set INSPECT_EVAL_MODEL); --max-tasks is how many models run at once
                    azureai/anthropic need AZUREAI_BASE_URL and an `az login` session
                  inspect-layers list                  everything the registry knows
                  inspect-layers view [--log-dir ...]  read logs back through the filesystem abstraction
                  inspect-layers check-layers          verify no layer references a layer above it
                """);
        }
    }
}

namespace inspect_ai._view
{
    using System.Text.Json;
    using inspect_ai._control;
    using inspect_ai._util.file;

    /// <summary>Stand-in for `inspect view`: a live progress poller and a log reader.</summary>
    internal static class View
    {
        /// <summary>Poll the control plane while the eval runs — the way the
        /// viewer polls the sample buffer from another process. Optionally
        /// cancel the run after a delay to show the downward channel.</summary>
        public static async Task Watch(RunningEval run, TimeSpan? cancelAfter)
        {
            var started = DateTimeOffset.UtcNow;
            var cancelled = false;
            string? last = null;
            while (!run.Completion.IsCompleted)
            {
                var status = run.Status();
                var line = $"poll {status.Model}: started={status.SamplesStarted} finished={status.SamplesFinished} last={status.LastActivity}";
                if (line != last) { Display.Step("L1 _view (outside)", line); last = line; }

                if (!cancelled && cancelAfter is { } after && DateTimeOffset.UtcNow - started > after)
                {
                    cancelled = true;
                    run.Cancel();   // -> layer 2; the engine only ever sees its token trip
                }
                await Task.Delay(100);
            }
        }

        /// <summary>Python: `inspect view --log-dir ...`. Reads through FileSystems, so
        /// memory:// and file:// (and s3://, if registered) all look alike.</summary>
        public static int Show(string logDir)
        {
            Display.Banner($"inspect view · reading logs back from {logDir}");
            var logs = ControlPlane.ListLogs(logDir).ToList();
            if (logs.Count == 0) { Console.WriteLine("  no logs found"); return 1; }
            foreach (var uri in logs)
            {
                using var doc = JsonDocument.Parse(FileSystems.ReadText(uri));
                var eval = doc.RootElement.GetProperty("eval");
                var accuracy = doc.RootElement.GetProperty("results").GetProperty("metrics").GetProperty("accuracy").GetDouble();
                var samples = doc.RootElement.GetProperty("samples");
                Console.WriteLine($"  {uri}");
                Console.WriteLine($"    task={eval.GetProperty("task").GetString()} model={eval.GetProperty("model").GetString()} accuracy={accuracy:0.00}");
                foreach (var s in samples.EnumerateArray())
                    Console.WriteLine($"    - {s.GetProperty("id").GetString(),-10} score={s.GetProperty("score").GetProperty("Value").GetString()} events={s.GetProperty("events").GetArrayLength()}");
            }
            return 0;
        }
    }
}
