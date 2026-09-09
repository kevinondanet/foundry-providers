using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Runner;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The parsed command line of <c>dotnet run --project examples -- &lt;example&gt; [flags]</c>.</summary>
public sealed record RunOptions
{
    /// <summary>"list", an example name, or null (no command).</summary>
    public string? Command { get; init; }

    public bool Help { get; init; }

    public string? Task { get; init; }

    public bool Fake { get; init; }

    public string? Model { get; init; }

    public string? Route { get; init; }

    /// <summary>"docker", "local", "fake" or "none"; null: the example's default.</summary>
    public string? Sandbox { get; init; }

    public string? Approval { get; init; }

    public string LogDir { get; init; } = "logs";

    public int? Limit { get; init; }

    public int? Epochs { get; init; }

    /// <summary>"conversation" or null.</summary>
    public string? Display { get; init; }

    public IReadOnlyDictionary<string, string> TaskArgs { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the run uses the scripted model: <c>--fake</c>, or no <c>--model</c> and no <c>AZUREAI_BASE_URL</c>.</summary>
    public bool UsesFakeModel => Fake || (Model is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZUREAI_BASE_URL")));

    /// <summary>Parses <paramref name="args"/>; a bad flag is an <see cref="ArgumentException"/>.</summary>
    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new RunOptions();
        var taskArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value(string flag) => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{flag} requires a value");
            int IntValue(string flag) => int.TryParse(Value(flag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new ArgumentException($"{flag} expects a positive integer");

            switch (arg)
            {
                case "--help" or "-h":
                    options = options with { Help = true };
                    break;
                case "--fake":
                    options = options with { Fake = true };
                    break;
                case "--task":
                    options = options with { Task = Value("--task") };
                    break;
                case "--model":
                    options = options with { Model = Value("--model") };
                    break;
                case "--route":
                    var route = Value("--route").ToLowerInvariant();
                    options = route is "models" or "anthropic" or "responses"
                        ? options with { Route = route }
                        : throw new ArgumentException("--route expects models, anthropic or responses");
                    break;
                case "--sandbox":
                    var sandbox = Value("--sandbox").ToLowerInvariant();
                    options = sandbox is "docker" or "local" or "fake" or "none"
                        ? options with { Sandbox = sandbox }
                        : throw new ArgumentException("--sandbox expects docker, local, fake or none");
                    break;
                case "--approval":
                    options = options with { Approval = Value("--approval") };
                    break;
                case "--log-dir":
                    options = options with { LogDir = Value("--log-dir") };
                    break;
                case "--limit":
                    options = options with { Limit = IntValue("--limit") };
                    break;
                case "--epochs":
                    options = options with { Epochs = IntValue("--epochs") };
                    break;
                case "--display":
                    var display = Value("--display").ToLowerInvariant();
                    options = display is "conversation"
                        ? options with { Display = display }
                        : throw new ArgumentException("--display expects conversation");
                    break;
                case "-T":
                    AddTaskArg(taskArgs, Value("-T"));
                    break;
                default:
                    if (arg.StartsWith("-T", StringComparison.Ordinal) && arg.Length > 2)
                    {
                        AddTaskArg(taskArgs, arg[2..]);
                    }
                    else if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        throw new ArgumentException($"unknown option '{arg}'");
                    }
                    else if (options.Command is null)
                    {
                        options = options with { Command = arg };
                    }
                    else
                    {
                        throw new ArgumentException($"unexpected argument '{arg}'");
                    }

                    break;
            }
        }

        return options with { TaskArgs = taskArgs };
    }

    private static void AddTaskArg(Dictionary<string, string> taskArgs, string spec)
    {
        var separator = spec.IndexOf('=');
        if (separator <= 0)
        {
            throw new ArgumentException($"-T expects key=value, got '{spec}'");
        }

        taskArgs[spec[..separator].Trim()] = spec[(separator + 1)..];
    }
}

/// <summary>
/// Runs one <see cref="IExample"/> the way <c>inspect eval</c> runs its Python original: resolves the model
/// (<c>--fake</c>: the example's scripted model; otherwise a Foundry deployment through <see cref="FoundryModels"/>),
/// the sandbox (<c>none</c>, <c>local</c>, <c>docker</c> with the example's compose file, or <c>fake</c>: the
/// example's <see cref="FakeSandboxScript"/> behind <see cref="ScriptedSandboxProvider"/>), the approval policy,
/// the <c>conversation</c> display hook and the <c>-T</c> task args, then evals the chosen task with an
/// <c>.eval</c> log and prints a summary: status, samples, the scores/metrics table, the approval decisions when the
/// log has any, and the log path. Exit codes: 0 success, 1 the log is not a success, 2 usage or prerequisite
/// error, 3 cancelled or unexpected error.
/// </summary>
public static class ExampleRunner
{
    public const string Usage = """
        InspectAzureAI.Examples: C# ports of the inspect_ai examples.

        usage: dotnet run --project examples -- <example> [options]
               dotnet run --project examples -- list

          --task <name>          the @task to run (default: the example's first task)
          --fake                 drive the eval with the example's scripted model (no network, deterministic);
                                 default when AZUREAI_BASE_URL is not set. A human approver is scripted too: it
                                 prints each call escalated to it and rejects it (approving submit), and an
                                 ask_user question the example does not script itself is declined, so nothing
                                 waits on the terminal
          --model <name>         Foundry deployment name (default: $INSPECT_AZUREAI_MODEL or gpt-5.4-mini)
          --route models|anthropic|responses
                                 Foundry route; claude-* models pick anthropic and gpt-5.6* / o-series / -pro /
                                 codex deployments pick responses automatically
          --sandbox docker|local|fake|none
                                 docker gives each sample its own container (with the example's compose file);
                                 local runs the tools on this host with no isolation; fake answers the tools from
                                 the example's script; none puts no sandbox on the task (default: the example's,
                                 or fake/local under --fake)
          --approval <spec>      a JSON approval policy file, or the name of a registered approver applied to
                                 every tool (default: the example's policy, if it has one)
          --log-dir <dir>        where the .eval log goes (default ./logs)
          --limit <n>            first n samples
          --epochs <n>           epochs per sample
          --display conversation print every model turn as it happens
          -T key=value           a task argument (repeatable), read by the example through ExampleContext.TaskArgs
          --help                 this text, plus the example's description, tasks and deviations

        Azure runs sign in with Entra ID (az login) and read the endpoint from AZUREAI_BASE_URL.
        """;

    /// <summary>The entry point: parses <paramref name="args"/>, dispatches <c>list</c> or an example, maps errors to exit codes.</summary>
    public static async Task<int> MainAsync(string[] args, ExampleRegistry? registry = null, TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        registry ??= ExampleRegistry.Default;
        output ??= Console.Out;
        error ??= Console.Error;

        RunOptions options;
        try
        {
            options = RunOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options.Command is null)
        {
            await (options.Help ? output : error).WriteLineAsync(Usage).ConfigureAwait(false);
            return options.Help ? 0 : 2;
        }

        if (options.Command.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            PrintList(registry, output);
            return 0;
        }

        try
        {
            var example = registry.Get(options.Command);
            if (options.Help)
            {
                PrintHelp(example, output);
                return 0;
            }

            return await RunAsync(example, options, output, cancellationToken).ConfigureAwait(false);
        }
        catch (PrerequisiteError ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("cancelled").ConfigureAwait(false);
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await error.WriteLineAsync($"error: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return 3;
        }
    }

    /// <summary>Prints every example: name, default task, description.</summary>
    public static void PrintList(ExampleRegistry registry, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(output);
        var width = Math.Max(8, registry.Examples.Select(example => example.Name.Length).DefaultIfEmpty(0).Max());
        foreach (var example in registry.Examples)
        {
            var tasks = string.Join(", ", example.Tasks.Select(task => task.Name));
            output.WriteLine($"{example.Name.PadRight(width)}  {example.Description}");
            output.WriteLine($"{"".PadRight(width)}  tasks: {tasks}; sandbox: {example.Defaults.Sandbox}{(example.Defaults.NeedsDocker ? " (needs Docker)" : "")}{(example.Defaults.ModelHint is { } hint ? $"; model: {hint}" : "")}");
        }

        if (registry.Examples.Count == 0)
        {
            output.WriteLine("no examples");
        }
    }

    /// <summary>Prints the flags, then the example's description, defaults, tasks and deviations.</summary>
    public static void PrintHelp(IExample example, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(example);
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(Usage);
        output.WriteLine();
        output.WriteLine($"example   : {example.Name}");
        output.WriteLine($"            {example.Description}");
        var defaults = example.Defaults;
        output.WriteLine($"defaults  : sandbox {defaults.Sandbox}{(defaults.ComposeFile is { } compose ? $" ({compose})" : "")}{(defaults.Approval is { } approval ? $"; approval {approval}" : "")}{(defaults.NeedsDocker ? "; needs Docker" : "")}{(defaults.ModelHint is { } hint ? $"; model: {hint}" : "")}");
        output.WriteLine("tasks     :");
        foreach (var task in example.Tasks)
        {
            output.WriteLine($"  {task.Name}{(task.Description is { } description ? $"  {description}" : "")}");
        }

        if (example.Deviations.Count > 0)
        {
            output.WriteLine("deviations from Python:");
            foreach (var deviation in example.Deviations)
            {
                output.WriteLine($"  - {deviation}");
            }
        }
    }

    /// <summary>Runs <paramref name="example"/> with <paramref name="options"/>; throws on usage and prerequisite errors (see <see cref="MainAsync"/> for the exit codes).</summary>
    public static async Task<int> RunAsync(IExample example, RunOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(example);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var exampleTask = options.Task is null
            ? example.Tasks.Count > 0 ? example.Tasks[0] : throw new PrerequisiteError($"example {example.Name} declares no tasks")
            : example.Tasks.FirstOrDefault(task => task.Name.Equals(options.Task, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"unknown task '{options.Task}' for {example.Name} (tasks: {string.Join(", ", example.Tasks.Select(task => task.Name))})");

        var fake = options.UsesFakeModel;
        var directory = ExampleDirectory(example);
        var context = new ExampleContext(directory, null, fake, options.TaskArgs, options.Model, null, output);

        // The sandbox: the flag, else the example's default; a fake run prefers the scripted sandbox, then local.
        var script = fake || options.Sandbox == ScriptedSandboxProvider.TypeName ? example.FakeSandbox(context) : null;
        var defaults = example.Defaults;
        var sandboxType = options.Sandbox ?? (fake && defaults.Sandbox != "none" ? (script is not null ? ScriptedSandboxProvider.TypeName : "local") : defaults.Sandbox);
        SandboxSpec? sandbox = sandboxType switch
        {
            "none" => null,
            "local" => new SandboxSpec("local"),
            "docker" => new SandboxSpec("docker", defaults.ComposeFile is { } compose ? Path.Combine(directory, compose) : null),
            ScriptedSandboxProvider.TypeName => ScriptedSandboxProvider.Register(
                script ?? throw new PrerequisiteError($"example {example.Name} has no fake sandbox script; use --sandbox local or --sandbox docker")),
            _ => throw new ArgumentException($"unknown sandbox '{sandboxType}'"),
        };
        if (!fake && sandbox is null && defaults.NeedsDocker)
        {
            throw new PrerequisiteError($"example {example.Name} needs a Docker sandbox (--sandbox docker)");
        }

        context = context with { Sandbox = sandbox };

        // The model: the example's script under --fake, else the Foundry deployment on its route.
        var model = fake ? example.CreateFakeModel(context) : InspectAzureAI.Eval.Model.Models.Create(options.Model, route: options.Route);
        context = context with { ResolvedModel = model };

        // The approval policy: the flag, else the example's (a file relative to its folder, or an approver name).
        var approvalSpec = options.Approval ?? ResolveDefaultApproval(defaults.Approval, directory);

        var previousPrompter = Approvers.DefaultPrompter;
        var previousInput = InputHandlers.Default;
        if (fake)
        {
            Approvers.DefaultPrompter = new ScriptedRejectPrompter(output);
            InputHandlers.Default = ScriptedInputHandler.Declining(output);
        }

        try
        {
            var task = exampleTask.Build(context);
            if (task.TaskArgs is null && options.TaskArgs.Count > 0)
            {
                task = task with { TaskArgs = options.TaskArgs.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal) };
            }

            var policies = approvalSpec is null ? [] : ResolvePolicies(approvalSpec);
            PrintBanner(example, exampleTask, task, model, fake, sandbox, approvalSpec, policies, options, output);

            var hooks = new List<Hooks>();
            if (example is IExampleHooks withHooks)
            {
                hooks.AddRange(withHooks.Hooks(context));
            }

            if (options.Display == "conversation")
            {
                hooks.Add(new ConversationDisplay(output));
            }

            var evalOptions = new EvalOptions
            {
                Model = model,
                Approval = approvalSpec is null ? null : ApprovalOption.FromSpec(approvalSpec),
                LogDir = options.LogDir,
                LogFormat = LogFormat.Eval,
                Limit = options.Limit,
                Epochs = options.Epochs,
                Reporter = new ConsoleEvalReporter(output),
                Hooks = hooks.Count > 0 ? hooks : null,
            };
            var log = await Eval.RunAsync(task, evalOptions, cancellationToken).ConfigureAwait(false);

            output.WriteLine();
            PrintApprovals(log, output);
            PrintSummary(log, output);
            if (example is IExampleReport report)
            {
                report.Report(log, context);
            }

            return log.Status == EvalStatus.Success ? 0 : 1;
        }
        finally
        {
            Approvers.DefaultPrompter = previousPrompter;
            InputHandlers.Default = previousInput;
        }
    }

    /// <summary>Where the example's data files were copied: <c>AppContext.BaseDirectory/&lt;name&gt;</c>.</summary>
    public static string ExampleDirectory(IExample example)
    {
        ArgumentNullException.ThrowIfNull(example);
        return Path.Combine(AppContext.BaseDirectory, example.Name.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string? ResolveDefaultApproval(string? approval, string directory)
    {
        if (approval is null)
        {
            return null;
        }

        var path = Path.IsPathRooted(approval) ? approval : Path.Combine(directory, approval);
        return File.Exists(path) ? path : approval;
    }

    /// <summary>Resolves the policy spec for the banner; an unreadable or unknown spec is a prerequisite error (exit 2), not a crash.</summary>
    private static IReadOnlyList<ApprovalPolicy> ResolvePolicies(string spec)
    {
        try
        {
            return ApprovalPolicies.Resolve(spec);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or NotSupportedException or FormatException)
        {
            throw new PrerequisiteError($"--approval {spec}: {ex.Message}");
        }
    }

    private static void PrintBanner(
        IExample example,
        ExampleTask exampleTask,
        EvalTask task,
        Model model,
        bool fake,
        SandboxSpec? sandbox,
        string? approvalSpec,
        IReadOnlyList<ApprovalPolicy> policies,
        RunOptions options,
        TextWriter output)
    {
        output.WriteLine($"InspectAzureAI examples: {example.Name}");
        output.WriteLine($"task      : {exampleTask.Name}{(exampleTask.Description is { } description ? $" ({description})" : "")}");
        output.WriteLine($"model     : {model.Name}{(fake ? " (scripted, offline)" : "")}");
        output.WriteLine($"sandbox   : {DescribeSandbox(sandbox)}");
        output.WriteLine($"dataset   : {task.Dataset.Count} samples{(options.Limit is { } limit ? $" (limit {limit})" : "")}{(options.Epochs is { } epochs ? $", {epochs} epochs" : "")}");
        if (options.TaskArgs.Count > 0)
        {
            output.WriteLine($"task args : {string.Join(", ", options.TaskArgs.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }

        if (approvalSpec is not null)
        {
            output.WriteLine($"policy    : {approvalSpec}");
            for (var i = 0; i < policies.Count; i++)
            {
                var policy = policies[i];
                output.WriteLine($"{(i == 0 ? "approvers " : "          ")}: {i + 1}. {policy.Approver.Name,-18} tools: {string.Join(", ", policy.Tools)}");
            }

            output.WriteLine(fake
                ? "human     : scripted prompter (prints each escalated call and rejects it, approves submit; no terminal prompt)"
                : "human     : console prompt (approve / reject / terminate at the terminal)");
        }

        if (options.Display is { } display)
        {
            output.WriteLine($"display   : {display}");
        }

        output.WriteLine($"log dir   : {Path.GetFullPath(options.LogDir)}");
        output.WriteLine();
    }

    private static string DescribeSandbox(SandboxSpec? sandbox) => sandbox switch
    {
        null => "none",
        { Type: "docker", Config: { } config } => $"docker, one container per sample ({config})",
        { Type: "docker" } => "docker, one container per sample",
        { Type: "local" } => "local, this host (demo only, no isolation)",
        { Type: ScriptedSandboxProvider.TypeName } => "fake, scripted by the example",
        { Config: null } => sandbox.Type,
        _ => $"{sandbox.Type} ({sandbox.Config})",
    };

    /// <summary>One table per sample of the approval decisions in its transcript (an escalated call shows one row per approver consulted); nothing when the log has none.</summary>
    public static void PrintApprovals(EvalLog log, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(output);
        var samples = log.Samples ?? [];
        if (!samples.Any(sample => sample.Events.OfType<ApprovalEvent>().Any()))
        {
            return;
        }

        foreach (var sample in samples)
        {
            var approvals = sample.Events.OfType<ApprovalEvent>().ToList();
            output.WriteLine($"sample {sample.Id} (epoch {sample.Epoch}): {approvals.Count} approval decision{(approvals.Count == 1 ? "" : "s")}");
            if (approvals.Count == 0)
            {
                output.WriteLine();
                continue;
            }

            output.WriteLine($"  {"tool",-8} {"argument",-32} {"approver",-18} {"decision",-9} explanation");
            foreach (var approval in approvals)
            {
                output.WriteLine(
                    $"  {approval.Call.Function,-8} {Truncate(FirstArgumentText(approval.Call), 32),-32} {approval.Approver,-18} {approval.Decision,-9} {Truncate(approval.Explanation ?? "", 80)}");
            }

            output.WriteLine();
        }
    }

    /// <summary>Status, sample counts, errors, tokens, the scores/metrics table and the log path.</summary>
    public static void PrintSummary(EvalLog log, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(output);
        var samples = log.Samples ?? [];
        var completed = log.Results?.CompletedSamples ?? 0;
        var total = log.Results?.TotalSamples ?? 0;
        var errored = samples.Count(sample => sample.Error is not null);
        output.WriteLine($"status    : {log.Status.ToString().ToLowerInvariant()} ({completed}/{total} samples completed{(errored > 0 ? $", {errored} with errors" : "")})");
        if (log.Error is { } error)
        {
            output.WriteLine($"error     : {FirstLine(error.Message)}");
        }

        foreach (var sample in samples.Where(sample => sample.Error is not null))
        {
            output.WriteLine($"sample {sample.Id} error: {FirstLine(sample.Error!.Message)}");
        }

        var usage = log.Stats.ModelUsage.Values.Aggregate(new ModelUsage(), (left, right) => left + right);
        output.WriteLine($"tokens    : {usage.TotalTokens} ({usage.InputTokens} in, {usage.OutputTokens} out)");

        var scores = log.Results?.Scores ?? [];
        if (scores.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"{"scorer",-24} {"metric",-20} {"value",10}");
            foreach (var score in scores)
            {
                foreach (var metric in score.Metrics.Values)
                {
                    output.WriteLine($"{score.Name,-24} {metric.Name,-20} {FormatMetric(metric.Value),10}");
                }
            }

            output.WriteLine();
        }
        else if (samples.Any(sample => sample.Scores is { Count: > 0 }))
        {
            output.WriteLine();
            output.WriteLine($"{"sample",-10} {"scorer",-24} {"value",-12} answer");
            foreach (var sample in samples)
            {
                foreach (var pair in sample.Scores ?? EmptyScores)
                {
                    output.WriteLine($"{sample.Id,-10} {pair.Key,-24} {Truncate(pair.Value.Text, 12),-12} {Truncate(pair.Value.Answer ?? "", 60)}");
                }
            }

            output.WriteLine();
        }

        output.WriteLine($"log       : {log.Location}");
    }

    /// <summary>The first argument of <paramref name="call"/> as Python's <c>str()</c> would show it (a string as is, null as None, a boolean as True/False, else JSON text); empty without arguments.</summary>
    public static string FirstArgumentText(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        foreach (var pair in call.Arguments)
        {
            return pair.Value switch
            {
                null => "None",
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
                var node => node.ToJsonString(),
            };
        }

        return "";
    }

    private static readonly IReadOnlyDictionary<string, InspectAzureAI.Eval.Scorers.Score> EmptyScores = new Dictionary<string, InspectAzureAI.Eval.Scorers.Score>();

    private static string FormatMetric(double value) => double.IsNaN(value) ? "n/a" : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }

    private static string Truncate(string text, int width)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= width ? line : line[..(width - 1)] + "…";
    }

    /// <summary>
    /// The human approver's prompter in <c>--fake</c> mode: prints the call it was shown, approves the react agent's
    /// submit call (a policy's <c>human: "*"</c> entry routes it here too) and rejects everything else, so the run
    /// never waits for a terminal. A choice the request does not offer falls back to its first choice.
    /// </summary>
    public sealed class ScriptedRejectPrompter(TextWriter? output = null) : IApprovalPrompter
    {
        private readonly TextWriter _output = output ?? Console.Out;

        public Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var preferred = request.Call.Function == Agents.DefaultSubmitName ? ApprovalDecision.Approve : ApprovalDecision.Reject;
            var decision = request.Choices.Contains(preferred) ? preferred : request.Choices[0];
            _output.WriteLine(
                $"[human approver] {request.Call.Function}({Truncate(FirstArgumentText(request.Call), 48)}) "
                + $"choices: {string.Join("/", request.Choices.Select(choice => choice.ToPython()))} -> {decision.ToPython()} (scripted)");
            return Task.FromResult(decision);
        }
    }
}
