using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.SweShowcase.BuiltinTasks;

namespace InspectAzureAI.SweShowcase;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using InspectAzureAI.Provider.Util;

/// <summary>An invalid command line (reported with the help text, exit code 2), as in the Sample app.</summary>
internal sealed class UsageError(string message) : Exception(message);

/// <summary>
/// The showcase's command-line front end in the style of <c>InspectAzureAI.Sample</c>: destructive flag parsing,
/// a switch dispatch over <c>list</c> / <c>run</c> / <c>show</c>, and the same exit codes (0 ok, 1 the eval had
/// errored samples, 2 usage or prerequisite, 3 sign-in / Azure / runtime failure). <see cref="RunAsync"/> is the
/// whole program so tests can drive it in-process.
/// </summary>
internal static class Cli
{
    public const string Help = """
        InspectAzureAI.SweShowcase - Inspect eval components and Inspect SWE agents in C#, on Microsoft Foundry

        usage: dotnet run --project src/InspectAzureAI.SweShowcase -- <command> [options]
               (written `swe-showcase <command>` below)

        commands:
          list                      list the built-in tasks (samples, scorer) and the agents
          run                       run one task with one agent and write an eval log (.eval by default)
          show <log.eval|log.json>  print the scores and a per-sample summary of a log (either format)
          --help                    this text

        run options:
          --task <name>             hello-swe | pytest-fix | system-explorer (required)
          --agent <name>            mini-swe | claude-code | basic | maf (required)
          --model <deployment>      Foundry deployment (default: $INSPECT_AZUREAI_MODEL or gpt-5.4-mini)
          --route models|anthropic  model-inference route (default) or the Anthropic Messages route; claude-* names
                                    take the Anthropic route automatically
          --limit N                 run only the first N samples
          --sample-id ID            run only this sample id (repeatable; wins over --limit)
          --epochs N                run every sample N times (epoch scores reduced with mean)
          --max-samples N           samples in flight at once (default 4)
          --attempts N              submissions the agent may make; an incorrect one is scored and retried (default 1)
          --sandbox docker|local    docker (default) builds sandbox/Dockerfile once and runs one container per sample;
                                    local runs on this host in a temp directory (demo only)
          --log-dir DIR             where the eval log is written (default logs, or $INSPECT_LOG_DIR)
          --log-format eval|json    the log format (default: $INSPECT_LOG_FORMAT, else eval — the .eval zip Inspect's viewer reads)
          --no-cleanup              keep the sandbox containers / temp directories for inspection
          --max-tokens <n|none>     max_tokens sent (default: the provider's max_tokens())
          --reasoning-effort <lvl>  Inspect's reasoning_effort (none|minimal|low|medium|high|xhigh|max)
          --model-arg key=value     repeatable; the Python -M model args (JSON values are parsed)
          --approval <policy>       tool-call approval: a JSON policy file ({"approvers": [{"name", "tools", ...}]}) or a
                                    registered approver name (auto, human); applied to mini-swe and basic bash calls and to
                                    Claude Code's and the Agent Framework agent's tool calls through the bridge
          --cache <expiry|off>      Inspect's prompt cache for every model call (1W, 3D, 12h, ...; 'on' = 1W); a hit replays
                                    the cached output and is recorded as a cache read on the model event
          --compaction <strategy>   edit|summary|trim|auto[:threshold] — compact the conversation once it reaches the threshold
                                    (a token count, or a fraction of the context window; default 0.9). mini-swe and basic only
          --hooks <name[=file]>     lifecycle hooks: sample-log prints one [hook] line per run, task, sample and model event
                                    (sample-log=FILE writes them to a file instead); repeatable / comma-separated
          --cost-limit <dollars>    stop a sample once its model cost exceeds this (needs pricing for the model)
          --model-cost-config FILE  JSON prices per model ({"<deployment>": {"input", "output", "input_cache_write",
                                    "input_cache_read"} in $/million tokens); costs then appear in the log and the summary
          --fake                    a scripted model that solves sample 1 offline; the sandbox defaults to local
          --debug                   Claude Code debug capture and full exception traces

        environment:
          AZUREAI_BASE_URL (or AZURE_ENDPOINT_URL / AZUREAI_ENDPOINT_URL)   the Foundry endpoint
          INSPECT_AZUREAI_MODEL                                            default deployment name
          AZUREAI_ANTHROPIC_BASE_URL                                        Anthropic route base URL (derived when unset)
          INSPECT_LOG_DIR / INSPECT_LOG_FORMAT                              log directory / format defaults
          INSPECT_CACHE_DIR                                                 where --cache keeps its entries
          INSPECT_AZUREAI_MODEL_COST_CONFIG                                 a price file applied to every run

        Authentication is Entra ID only: sign in with `az login` first. Exit codes: 0 ok, 1 the eval had errored
        samples, 2 usage or missing prerequisite, 3 sign-in / Azure / runtime failure.
        """;

    /// <summary>What to try when token acquisition fails (the Sample app's hint).</summary>
    public const string LoginHint = """
        Hints:
          az login                                   sign in (add --tenant <id> for a specific tenant)
          az account set --subscription <name|id>    pick the subscription that owns the endpoint
          AZURE_TENANT_ID=<id>                       pin the tenant DefaultAzureCredential signs in to
          AZUREAI_AUDIENCE=<scope>                   change the token scope (default https://cognitiveservices.azure.com/.default)
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var arguments = args.ToList();
        if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Help);
            return 0;
        }

        RunOptions options;
        try
        {
            options = RunOptions.Parse(arguments);
        }
        catch (UsageError ex)
        {
            Console.Error.WriteLine($"{ex.Message}\n\n{Help}");
            return 2;
        }

        if (arguments.Count == 0)
        {
            Console.Error.WriteLine($"no command given\n\n{Help}");
            return 2;
        }

        var command = arguments[0];
        var rest = arguments.Skip(1).ToList();
        try
        {
            return command switch
            {
                "list" => List(rest),
                "run" => await RunEvalAsync(options, rest, cancellationToken),
                "show" => Show(rest),
                _ => Unknown(command),
            };
        }
        catch (UsageError ex)
        {
            Console.Error.WriteLine($"{ex.Message}\n\n{Help}");
            return 2;
        }
        catch (PrerequisiteError ex)
        {
            Console.Error.WriteLine(ProviderUtil.StripRichMarkup(ex.Message));
            return 2;
        }
        catch (Exception ex) when (IsSignInFailure(ex))
        {
            Console.Error.WriteLine($"Entra ID sign-in failed: {SignInFailureMessage(ex)}\n\n{LoginHint}");
            return 3;
        }
        catch (RequestFailedException ex)
        {
            Console.Error.WriteLine($"Azure request failed (HTTP {ex.Status}): {AzureAIModelApi.AzureErrorMessage(ex)}");
            return 3;
        }
        catch (ServiceResponseException ex)
        {
            Console.Error.WriteLine($"Azure response could not be read (retryable): {ex.Message}");
            return 3;
        }
        catch (SandboxUnavailableException ex)
        {
            Console.Error.WriteLine($"Sandbox unavailable: {Describe(ex, options.Debug)}");
            return 3;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Run failed: {Describe(ex, options.Debug)}");
            return 3;
        }
    }

    public static int List(List<string> rest)
    {
        if (rest.Count > 0)
        {
            throw new UsageError($"unexpected argument '{rest[0]}'");
        }

        Console.WriteLine("tasks:");
        foreach (var task in ShowcaseTasks.All)
        {
            var samples = task.LoadDataset().Count;
            Console.WriteLine($"  {task.Name,-16} {samples} samples  scorer={task.ScorerName,-16} {task.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("agents:");
        Console.WriteLine("  mini-swe         native C# port of mini-swe-agent's bash tool-calling loop (inspect_swe mini_swe_agent)");
        Console.WriteLine("  claude-code      the Claude Code CLI inside the sandbox, its API calls bridged to the task model (inspect_swe claude_code)");
        Console.WriteLine("  basic            Inspect's basic_agent ReAct loop with the sandbox bash tool and a submit tool");
        Console.WriteLine("  maf              a Microsoft Agent Framework ChatClientAgent with the sandbox bash tool, its model calls bridged in-process to the task model");
        Console.WriteLine();
        Console.WriteLine($"sandbox image: {Path.Combine(TaskData.SandboxDirectory, "Dockerfile")}");
        return 0;
    }

    public static int Show(List<string> rest)
    {
        if (rest.Count != 1)
        {
            throw new UsageError("show expects exactly one argument: the path of an eval log (.eval or .json)");
        }

        var path = rest[0];
        if (!File.Exists(path))
        {
            throw new UsageError($"log file not found: {path}");
        }

        // read_eval_log: the .eval zip (Python's default and ours) or the JSON file, by extension
        EvalLog log;
        try
        {
            log = EvalLogFiles.ReadEvalLog(path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new UsageError($"{path} is not an eval log this showcase can read: {ex.Message}");
        }

        Console.WriteLine($"log      : {path}");
        Console.WriteLine($"format   : {(Path.GetExtension(path).Equals(LogFormat.Json.Extension(), StringComparison.OrdinalIgnoreCase) ? LogFormat.Json : LogFormat.Eval).Name()}");
        Console.WriteLine($"task     : {log.Eval.Task} (version {log.Eval.TaskVersion}, run {log.Eval.RunId})");
        Console.WriteLine($"model    : {log.Eval.Model}");
        Console.WriteLine($"sandbox  : {DescribeSandbox(log.Eval.Sandbox)}");
        Console.WriteLine($"created  : {log.Eval.Created.ToString("u", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"epochs   : {log.Eval.Config.Epochs ?? 1}");
        if (log.Eval.Config.Approval is { } approval)
        {
            Console.WriteLine($"approval : {approval.ToJsonString()}");
        }

        if (log.Eval.Config.CostLimit is { } costLimit)
        {
            Console.WriteLine($"cost lim : {RunWiring.FormatCost(costLimit)} per sample");
        }

        PrintSummary(log);
        Console.WriteLine();
        Console.WriteLine("samples:");
        foreach (var sample in log.Samples ?? [])
        {
            var tokens = sample.ModelUsage.Values.Sum(usage => usage.TotalTokens);
            var modelCalls = sample.Events.Count(e => e is ModelEvent);
            var toolCalls = sample.Events.Count(e => e is ToolEvent);
            var scores = sample.Scores is { Count: > 0 } scored
                ? string.Join(", ", scored.Select(pair => $"{pair.Key}={pair.Value.Text}"))
                : "no scores";
            var seconds = (sample.TotalTime ?? 0).ToString("F1", CultureInfo.InvariantCulture);
            var cost = RunWiring.TotalCost(sample.ModelUsage) is { } sampleCost ? $" | {RunWiring.FormatCost(sampleCost)}" : "";
            var line = $"  {sample.Id} (epoch {sample.Epoch}): {scores} | {tokens} tokens{cost} | {seconds}s | {modelCalls} model calls, {toolCalls} tool calls";
            var features = RunWiring.DescribeEvents(sample);
            if (features.Length > 0)
            {
                line += $" | {features}";
            }

            if (sample.Limit is { } limit)
            {
                line += $" | {limit.Type} limit";
            }

            if (sample.Error is { } error)
            {
                line += $" | error: {FirstLine(error.Message)}";
            }

            Console.WriteLine(line);
            var answer = FirstLine(sample.Output.Completion).Trim();
            if (answer.Length > 0)
            {
                Console.WriteLine($"      answer: {Truncate(answer, 100)}");
            }

            foreach (var (name, score) in sample.Scores ?? new Dictionary<string, Score>(StringComparer.Ordinal))
            {
                // the last line carries the verdict of a check (pytest's summary, a model judge's GRADE line)
                if (score.Explanation is { Length: > 0 } explanation)
                {
                    Console.WriteLine($"      {name}: {Truncate(LastLine(explanation), 100)}");
                }
            }
        }

        return 0;
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'\n\n{Help}");
        return 2;
    }

    public static bool IsSignInFailure(Exception ex) =>
        ex is CredentialUnavailableException or AuthenticationFailedException
        || (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsSignInFailure));

    public static string SignInFailureMessage(Exception ex) =>
        ex is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
            ? SignInFailureMessage(aggregate.InnerExceptions[^1])
            : ex.Message;

    private static async Task<int> RunEvalAsync(RunOptions options, List<string> rest, CancellationToken cancellationToken)
    {
        if (rest.Count > 0)
        {
            throw new UsageError($"unexpected argument '{rest[0]}'");
        }

        var definition = ShowcaseTasks.Resolve(options.Task);
        var agent = AgentChoice.Resolve(options.Agent);
        if (options.Compaction is not null)
        {
            AgentChoice.RejectCompaction(agent);
        }

        var sandboxType = options.Sandbox ?? (options.Fake ? "local" : "docker");
        var sandbox = sandboxType == "docker" ? new SandboxSpec("docker", TaskData.RequireSandboxDirectory()) : new SandboxSpec("local");
        var model = options.Fake
            ? new Model(FakeScripts.For(definition.Name, agent), options.GenerateConfig)
            : await CreateFoundryModelAsync(options, cancellationToken);
        var task = definition.Build(new TaskBuildContext(AgentChoice.Solver(agent, options.Attempts, options.Debug, options.Cache, options.Compaction?.Hook()), sandbox));

        Console.WriteLine($"task     : {task.Name} ({task.Dataset.Count} samples, scorer {definition.ScorerName})");
        Console.WriteLine($"agent    : {agent} (attempts {options.Attempts})");
        Console.WriteLine($"model    : {model.Name}{(options.Fake ? " (--fake: scripted turns, no network)" : "")}");
        Console.WriteLine($"sandbox  : {DescribeSandbox(sandbox)}");
        Console.WriteLine($"log dir  : {Path.GetFullPath(options.LogDir)}");
        foreach (var line in RunWiring.DescribeLines(options))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();

        var hooks = RunWiring.CreateHooks(options, Console.Out);
        EvalLog log;
        try
        {
            var evalOptions = RunWiring.EvalOptions(options, model, options.Limit, new ConsoleEvalReporter(Console.Out), hooks);
            log = await Eval.RunAsync(task, evalOptions, cancellationToken);
        }
        finally
        {
            RunWiring.DisposeHooks(hooks);
        }

        Console.WriteLine();
        PrintSummary(log);
        Console.WriteLine($"show it with: swe-showcase show {log.Location}");
        var errored = log.Status == EvalStatus.Error || (log.Samples?.Any(sample => sample.Error is not null) ?? false);
        return errored ? 1 : 0;
    }

    /// <summary>
    /// Port of <c>get_model()</c> for the flags: <see cref="FoundryModels"/> picks the route. The token preflight
    /// turns a sign-in problem into exit code 3 up front instead of an error recorded on every sample.
    /// </summary>
    private static async Task<Model> CreateFoundryModelAsync(RunOptions options, CancellationToken cancellationToken)
    {
        Model model;
        try
        {
            model = FoundryModels.Create(options.Model, options.GenerateConfig, options.Route, modelArgs: options.ModelArgs);
        }
        catch (ArgumentException ex)
        {
            throw new UsageError(ex.Message);
        }

        var credential = model.Api switch
        {
            AzureAIModelApi azure => azure.Credential,
            AnthropicFoundryModelApi anthropic => anthropic.Credential,
            _ => null,
        };
        if (credential is not null)
        {
            await credential.GetTokenAsync(new TokenRequestContext([credential.Scope]), cancellationToken);
        }

        return model;
    }

    private static void PrintSummary(EvalLog log)
    {
        var samples = log.Samples ?? [];
        var errored = samples.Count(sample => sample.Error is not null);
        var completed = log.Results?.CompletedSamples ?? 0;
        var total = log.Results?.TotalSamples ?? 0;
        var status = log.Status.ToString().ToLowerInvariant();
        Console.WriteLine($"status   : {status} ({completed}/{total} samples completed{(errored > 0 ? $", {errored} with errors" : "")})");
        if (log.Error is { } error)
        {
            Console.WriteLine($"error    : {FirstLine(error.Message)}");
        }

        var usage = log.Stats.ModelUsage.Values.Aggregate(new ModelUsage(), (left, right) => left + right);
        Console.WriteLine($"tokens   : {usage.TotalTokens} ({usage.InputTokens} in, {usage.OutputTokens} out)");
        if (RunWiring.TotalCost(log.Stats.ModelUsage) is { } cost)
        {
            Console.WriteLine($"cost     : {RunWiring.FormatCost(cost)}");
        }

        Console.WriteLine();

        var scores = log.Results?.Scores ?? [];
        if (scores.Count == 0)
        {
            Console.WriteLine("no scores");
            return;
        }

        Console.WriteLine($"{"scorer",-18} {"metric",-10} {"value",8}");
        foreach (var score in scores)
        {
            foreach (var metric in score.Metrics.Values)
            {
                Console.WriteLine($"{score.Name,-18} {metric.Name,-10} {FormatMetric(metric.Value),8}");
            }
        }
    }

    private static string FormatMetric(double value) => double.IsNaN(value) ? "n/a" : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string DescribeSandbox(SandboxSpec? sandbox) => sandbox switch
    {
        null => "none",
        { Type: "local" } => "local (temp directory on this host; demo only)",
        { Config: null } => sandbox.Type,
        _ => $"{sandbox.Type} ({sandbox.Config})",
    };

    private static string Describe(Exception ex, bool debug) => debug ? ex.ToString() : ex.Message;

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }

    private static string LastLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? "";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
