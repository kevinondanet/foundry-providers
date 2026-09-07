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
    using inspect_ai._view;

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

        /// <summary>Python: `inspect eval <task> --model <m> --log-dir <d> --max-samples <n>`.</summary>
        private static async Task<int> Eval(string[] rest)
        {
            Display.Banner("Step 1 · Interfaces (L1 _cli): parse the command line");
            if (rest.Length == 0 || rest[0].StartsWith("--")) { Console.WriteLine("eval needs a task name"); return 1; }
            var options = new EvalOptions(
                Task: rest[0],
                Model: Option(rest, "--model") ?? "mock/gpt-demo",
                LogDir: Option(rest, "--log-dir") ?? "memory://logs",
                MaxSamples: int.Parse(Option(rest, "--max-samples") ?? "1"));
            var cancelAfter = Option(rest, "--cancel-after") is { } ms ? TimeSpan.FromMilliseconds(int.Parse(ms)) : (TimeSpan?)null;
            Display.Step("L1 _cli", $"eval task={options.Task} model={options.Model} log-dir={options.LogDir} max-samples={options.MaxSamples}");

            Display.Banner("Step 2 · Control plane (L2 _control): start the eval and expose it to outside observers");
            var run = ControlPlane.Start(options);            // -> layer 2 (-> layer 3)
            var watcher = View.Watch(run, cancelAfter);       // a concurrent "inspect view" on the same loop

            EvalLog log;
            try
            {
                log = await run.Completion;
            }
            catch (OperationCanceledException)
            {
                await watcher;
                Display.Banner("Result: cancelled by the control plane before completion");
                return 2;
            }
            await watcher;

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
            return 0;
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

        private static void Usage()
        {
            Console.WriteLine("""
                usage:
                  inspect-layers                       full narrated walk-through (eval, view, layer check)
                  inspect-layers eval <task> [--model mock/gpt-demo] [--log-dir memory://logs]
                                             [--max-samples 1] [--cancel-after <ms>]
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
                var line = $"poll: started={status.SamplesStarted} finished={status.SamplesFinished} last={status.LastActivity}";
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
