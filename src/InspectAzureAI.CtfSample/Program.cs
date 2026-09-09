using System.Globalization;
using InspectAzureAI.CtfSample.Components;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.CtfSample;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// A console app that runs one Inspect eval end to end: the <c>ctf</c> task (see <see cref="CtfTask"/>) against
/// either a scripted model (<c>--fake</c>, no network) or an Azure AI Foundry deployment, with every sample inside
/// its own Docker container.
/// </summary>
internal static class Program
{
    private const string Usage = """
        InspectAzureAI.CtfSample: Tasks, Datasets, Solvers and Scorers on a capture-the-flag eval in Docker.

        usage: dotnet run --project src/InspectAzureAI.CtfSample -- [options]

          --fake                 drive the eval with a scripted model (no network, deterministic); default when
                                 AZUREAI_BASE_URL is not set
          --model <name>         Foundry deployment name (default: $INSPECT_AZUREAI_MODEL or gpt-5.4-mini)
          --route models|anthropic|responses
                                 Foundry route; claude-* models pick anthropic and gpt-5.6* / o-series / -pro / codex
                                 models pick responses automatically
          --sandbox docker|local docker (default) builds ctf/Dockerfile; local runs in a temp directory on this host
          --category <name>      only the challenges of one category (forensics, encoding, compression)
          --limit <n>            first n samples
          --epochs <n>           run every sample n times (default 1)
          --log-dir <dir>        where the .eval log goes (default ./logs)
          --no-cleanup           keep the containers after the run for inspection
          --help

        Azure runs sign in with Entra ID (az login) and read the endpoint from AZUREAI_BASE_URL.
        """;

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await RunAsync(options, cts.Token);
        }
        catch (PrerequisiteError ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
    {
        // 1. The model. --fake is a ScriptedModelApi; otherwise FoundryModels picks the Azure route for the deployment.
        var model = options.Fake ? FakeCtfModel.Create() : FoundryModels.Create(options.Model, route: options.Route);

        // 2. The sandbox spec. "docker" with a directory means "build the Dockerfile in it"; each sample gets a fresh container.
        var sandbox = options.Sandbox == "local"
            ? new SandboxSpec("local")
            : new SandboxSpec("docker", CtfData.SandboxDirectory);

        // 3. The task: dataset + solver + scorers + sandbox + limits.
        var task = CtfTask.Build(sandbox, options.Category);

        Console.WriteLine("InspectAzureAI CTF sample");
        Console.WriteLine($"model    : {model.Name}{(options.Fake ? " (scripted, offline)" : "")}");
        Console.WriteLine($"sandbox  : {(sandbox.Type == "docker" ? $"docker, image built from {Path.Combine(sandbox.Config!, "Dockerfile")}" : "local temp directory (demo only, no isolation)")}");
        Console.WriteLine($"dataset  : {task.Dataset.Count} samples, categories: {string.Join(", ", CtfDataset.Categories(task.Dataset))}");
        Console.WriteLine($"solver   : system_message -> recon -> basic_agent(bash, submit)");
        Console.WriteLine($"scorers  : {string.Join(", ", task.Scorers!.Select(s => s.Name))}");
        Console.WriteLine($"log dir  : {Path.GetFullPath(options.LogDir)}");
        Console.WriteLine();

        // 4. Run it. The runner provisions sandboxes, runs setup scripts, drives the solver, scores, and writes the .eval log.
        var evalOptions = new EvalOptions
        {
            Model = model,
            Limit = options.Limit,
            Epochs = options.Epochs,
            LogDir = options.LogDir,
            LogFormat = LogFormat.Eval,
            Cleanup = options.Cleanup,
            Reporter = new ConsoleEvalReporter(),
        };
        var log = await Eval.RunAsync(task, evalOptions, cancellationToken);

        Console.WriteLine();
        PrintSummary(log);
        return log.Status == EvalStatus.Success ? 0 : 1;
    }

    private static void PrintSummary(EvalLog log)
    {
        var samples = log.Samples ?? [];
        var completed = log.Results?.CompletedSamples ?? 0;
        var total = log.Results?.TotalSamples ?? 0;
        var errored = samples.Count(sample => sample.Error is not null);
        Console.WriteLine($"status   : {log.Status.ToString().ToLowerInvariant()} ({completed}/{total} samples completed{(errored > 0 ? $", {errored} with errors" : "")})");
        if (log.Error is { } error)
        {
            Console.WriteLine($"error    : {error.Message.Split('\n')[0]}");
        }

        var usage = log.Stats.ModelUsage.Values.Aggregate(new ModelUsage(), (left, right) => left + right);
        Console.WriteLine($"tokens   : {usage.TotalTokens} ({usage.InputTokens} in, {usage.OutputTokens} out)");
        Console.WriteLine($"log      : {log.Location}");
        Console.WriteLine();

        var scorerNames = log.Results?.Scores.Select(score => score.Name).Distinct().ToList() ?? [];
        Console.WriteLine($"{"sample",-14} {"epoch",5} {"category",-12} {string.Join(" ", scorerNames.Select(name => $"{name,-10}"))} answer");
        foreach (var sample in samples)
        {
            var category = sample.Metadata?.TryGetValue("category", out var value) == true ? value?.ToString() ?? "" : "";
            var cells = scorerNames.Select(name => $"{ScoreText(sample.Scores, name),-10}");
            // includes() lowercases the answer it records (as Python does); flag_exact keeps the submitted case.
            var answer = sample.Error is { } sampleError
                ? $"error: {sampleError.Message.Split('\n')[0]}"
                : sample.Scores?.GetValueOrDefault(CtfScorers.FlagExactName)?.Answer ?? sample.Output.Completion;
            Console.WriteLine($"{sample.Id,-14} {sample.Epoch,5} {category,-12} {string.Join(" ", cells)} {Truncate(answer, 60)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"scorer",-12} {"metric",-10} {"value",8}");
        foreach (var score in log.Results?.Scores ?? [])
        {
            foreach (var metric in score.Metrics.Values)
            {
                var text = double.IsNaN(metric.Value) ? "n/a" : metric.Value.ToString("0.000", CultureInfo.InvariantCulture);
                Console.WriteLine($"{score.Name,-12} {metric.Name,-10} {text,8}");
            }
        }
    }

    private static string ScoreText(IReadOnlyDictionary<string, Score>? scores, string scorer) =>
        scores?.TryGetValue(scorer, out var score) == true
            ? score.Value switch
            {
                ScoreValue.Str s => s.Value,
                ScoreValue.Num n => n.Value.ToString("0.##", CultureInfo.InvariantCulture),
                ScoreValue.Bool b => b.Value ? "true" : "false",
                _ => score.Value.ToString() ?? "",
            }
            : "-";

    private static string Truncate(string text, int width)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= width ? line : line[..(width - 1)] + "…";
    }

    private sealed record Options(
        bool Help,
        bool Fake,
        string? Model,
        string? Route,
        string Sandbox,
        string? Category,
        int? Limit,
        int? Epochs,
        string LogDir,
        bool Cleanup)
    {
        public static Options Parse(string[] args)
        {
            bool help = false, fake = false, cleanup = true;
            string? model = null, route = null, category = null;
            string sandbox = "docker", logDir = "logs";
            int? limit = null, epochs = null;

            for (var i = 0; i < args.Length; i++)
            {
                string Value(string flag) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{flag} requires a value");
                int IntValue(string flag) => int.TryParse(Value(flag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n
                    : throw new ArgumentException($"{flag} expects a positive integer");

                switch (args[i])
                {
                    case "--help" or "-h": help = true; break;
                    case "--fake": fake = true; break;
                    case "--model": model = Value("--model"); break;
                    case "--route": route = Value("--route"); break;
                    case "--sandbox":
                        sandbox = Value("--sandbox").ToLowerInvariant();
                        if (sandbox is not ("docker" or "local"))
                        {
                            throw new ArgumentException("--sandbox expects docker or local");
                        }

                        break;
                    case "--category": category = Value("--category"); break;
                    case "--limit": limit = IntValue("--limit"); break;
                    case "--epochs": epochs = IntValue("--epochs"); break;
                    case "--log-dir": logDir = Value("--log-dir"); break;
                    case "--no-cleanup": cleanup = false; break;
                    default: throw new ArgumentException($"unknown option '{args[i]}'");
                }
            }

            // Without an endpoint there is nothing to call, so default to the scripted model rather than fail on every sample.
            if (!fake && model is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZUREAI_BASE_URL")))
            {
                fake = true;
            }

            return new Options(help, fake, model, route, sandbox, category, limit, epochs, logDir, cleanup);
        }
    }
}
