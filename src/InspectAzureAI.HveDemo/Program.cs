using System.Globalization;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.HveDemo.Fake;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// A console app that runs one Inspect eval end to end: the HVE Core tasks (see <see cref="HveTasks"/>) in one cell of
/// the harness x framework matrix (<see cref="HveVariant"/>: the GitHub Copilot CLI or Inspect's generic agent loop, with
/// or without the HVE Core plugin), against either a scripted model (<c>--fake</c>, no network) or an Azure AI Foundry
/// deployment, with every sample inside its own Docker container (or the fake sandbox that plays one on this host).
/// </summary>
public static class Program
{
    private const string Usage = """
        InspectAzureAI.HveDemo: the GitHub Copilot CLI or Inspect's generic agent loop, with or without Microsoft HVE Core
        (agents, skills, instructions, prompts), as an Inspect eval in Docker, with the dataset, solver, scorer and task
        components spelled out.

        usage: dotnet run --project src/InspectAzureAI.HveDemo -- [options]

          --task implement|review|skill|suite
                                 which task to run (default: suite, every sample with a per-kind breakdown)
          --harness copilot|generic
                                 the agent runtime: copilot (default) runs the GitHub Copilot CLI inside the sandbox, bridged
                                 to the model; generic is Inspect's own agent loop (basic_agent + sandbox bash + submit)
          --framework hve|none   the engineering framework layered on the harness: hve (default) provisions the vendored
                                 HVE Core plugin and briefs the agent to use it; none provisions and briefs nothing
          --solver copilot|basic deprecated aliases: copilot = --harness copilot --framework hve, basic = --harness generic
                                 --framework none; a later --harness/--framework wins
          --fake                 drive the eval with a scripted model (no network, deterministic); default when
                                 AZUREAI_BASE_URL is not set. With the fake sandbox a fake copilot binary plays the CLI
                                 against the real bridge; with docker the real CLI runs on the scripted model.
          --model <name>         Foundry deployment name (default: $INSPECT_AZUREAI_MODEL or gpt-5.4-mini)
          --route models|anthropic|responses
                                 Foundry route; claude-* models pick anthropic automatically (the CLI then speaks the
                                 Anthropic wire to the bridge) and gpt-5.6* / o-series / -pro / codex models pick responses
          --sandbox docker|local|fake
                                 docker (default) builds hve/sandbox/Dockerfile; local runs on this host in a temp
                                 directory with the host's copilot; fake is the scripted sandbox (default with --fake)
          --limit <n>            first n samples
          --epochs <n>           run every sample n times (default 1)
          --max-samples <n>      samples (containers, bridged CLI sessions) in flight at once (default 4 for docker,
                                 1 for fake, unlimited for local); the deployment answers a burst of eight with 429s
          --log-dir <dir>        where the .eval log goes (default ./logs)
          --copilot-version <v>  auto (default: a sandbox copilot, else the pinned 1.0.83), sandbox, or a version
          --plugin-dir <path>    use a plugin directory that already exists in the sandbox instead of copying the
                                 vendored HVE Core subset to /opt/hve-core (only with --framework hve)
          --debug                keep the CLI's raw stdout/stderr in the sample store (copilot_cli_debug)
          --no-cleanup           keep the containers after the run for inspection
          --help

        Azure runs sign in with Entra ID (az login) and read the endpoint from AZUREAI_BASE_URL. The Copilot CLI
        release tarball is cached under ~/.cache/inspect-azureai/copilot-cli-downloads.
        """;

    /// <summary>Output tokens per bridged generation (the Copilot CLI's own <c>max_tokens</c> on the Anthropic wire; the provider default is 2048).</summary>
    public const int MaxOutputTokens = 8192;

    /// <summary>Docker samples in flight at once unless <c>--max-samples</c> says otherwise.</summary>
    public const int DefaultDockerMaxSamples = 4;

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

    /// <summary>Runs one eval with the parsed options and returns the process exit code (0 success, 1 the eval did not succeed).</summary>
    public static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SolverAlias is { } alias)
        {
            // One line. --solver basic accepted a --plugin-dir before the split (it only suppressed the plugin copy), so the alias
            // still parses with one and the note says it is ignored rather than the run failing on an old command line.
            var ignored = options.PluginDir is not null && !options.Variant.UsesFramework ? " (--plugin-dir is ignored: none provisions no plugin)" : "";
            Console.Error.WriteLine($"note: --solver {alias} is deprecated; use --harness {options.Harness} --framework {options.Framework}{ignored}");
        }

        // 1. The sandbox spec. "docker" with a directory means "build the Dockerfile in it"; "fake" registers the scripted sandbox.
        FakeSandboxScript? script = null;
        SandboxSpec sandbox;
        switch (options.Sandbox)
        {
            case "fake":
                (sandbox, script) = HveFake.Register();
                break;
            case "local":
                sandbox = new SandboxSpec("local");
                break;
            default:
                sandbox = HveTasks.DockerSandbox();
                break;
        }

        // 2. The cell, and where the plugin lives in the sandbox: copied to /opt/hve-core, or (local) the host copy, or the
        //    caller's directory. Under --framework none the task provisions nothing at that path and nothing reads it.
        var variant = options.Variant;
        var pluginSandboxPath = options.PluginDir is not null ? null : options.Sandbox == "local" ? null : HveData.PluginSandboxPath;
        var pluginDir = options.PluginDir ?? (options.Sandbox == "local" ? HveData.PluginDirectory : HveData.PluginSandboxPath);

        // 3. The model. --fake is a ScriptedModelApi; otherwise FoundryModels picks the Azure route for the deployment. The
        //    bridge forwards no request-level generation config, so the CLI's turns would otherwise run under the provider's
        //    2048-token default; a file-writing agent needs the headroom the CLI itself asks for on the Anthropic wire (8192).
        var model = options.Fake ? FakeHveModel.Create() : FoundryModels.Create(options.Model, new GenerateConfig { MaxTokens = MaxOutputTokens }, route: options.Route);
        var anthropic = string.Equals(options.Route, "anthropic", StringComparison.OrdinalIgnoreCase)
            || (options.Route is null && options.Model?.StartsWith("claude", StringComparison.OrdinalIgnoreCase) == true);

        // 4. The solver options: how the Copilot CLI is installed and pointed at the bridge, and (generic+hve) where the
        //    host can read the plugin to embed a sample's agent body: the vendored copy, or a --plugin-dir that exists on
        //    this host under --sandbox local; a sandbox-only --plugin-dir leaves it null and the agent reads its own agent file.
        var solverOptions = new HveSolverOptions
        {
            CopilotVersion = script is not null ? HveFake.CopilotVersion : options.CopilotVersion,
            PluginDir = pluginDir,
            HostPluginDirectory = options.PluginDir is null ? HveData.PluginDirectory : options.Sandbox == "local" && Directory.Exists(options.PluginDir) ? options.PluginDir : null,
            Provider = anthropic && !options.Fake ? CopilotCliProvider.Anthropic : CopilotCliProvider.OpenAI,
            Debug = options.Debug,
        };

        // 5. The task: dataset + solver + scorers + sandbox + limits.
        var task = HveTasks.Build(options.Task, variant, sandbox, solverOptions, pluginSandboxPath: pluginSandboxPath);

        Console.WriteLine("InspectAzureAI HVE Core demo");
        Console.WriteLine($"task     : {task.Name} ({task.Dataset.Count} samples, kinds: {string.Join(", ", HveDataset.KindsOf(task.Dataset))})");
        Console.WriteLine($"model    : {model.Name}{(options.Fake ? " (scripted, offline)" : "")}");
        Console.WriteLine($"sandbox  : {SandboxText(sandbox, variant.UsesFramework ? pluginSandboxPath : null)}");
        Console.WriteLine($"harness  : {HarnessText(variant, solverOptions)}");
        Console.WriteLine($"framework: {FrameworkText(variant, solverOptions)}");
        Console.WriteLine($"scorers  : {string.Join(", ", task.Scorers.Select(s => s.Name))}");
        Console.WriteLine($"log dir  : {Path.GetFullPath(options.LogDir)}");
        Console.WriteLine();

        // 6. Run it. The runner provisions sandboxes, copies the workspace and the plugin, runs setup scripts, drives the solver, scores, and writes the .eval log.
        var evalOptions = new EvalOptions
        {
            Model = model,
            Limit = options.Limit,
            Epochs = options.Epochs,
            LogDir = options.LogDir,
            LogFormat = LogFormat.Eval,
            Cleanup = options.Cleanup,
            Reporter = new ConsoleEvalReporter(),

            // The fake sandbox runs its checks and setup scripts on this host, one sample at a time keeps the run deterministic
            // (and keeps one bridge listener at a time on the loopback address); Docker samples run a few at a time so
            // the deployment is not hit by every sample's first turn at once (the live suite drew six 429s from eight).
            MaxSamples = script is not null ? 1 : options.MaxSamples ?? (sandbox.Type == "docker" ? DefaultDockerMaxSamples : null),
        };
        var log = await Eval.RunAsync(task, evalOptions, cancellationToken);

        Console.WriteLine();
        PrintSummary(log, task);
        return log.Status == EvalStatus.Success ? 0 : 1;
    }

    private static string SandboxText(SandboxSpec sandbox, string? pluginSandboxPath) => sandbox.Type switch
    {
        "docker" => $"docker, image built from {Path.Combine(sandbox.Config!, "Dockerfile")}{(pluginSandboxPath is not null ? $"; plugin copied to {pluginSandboxPath}" : "")}",
        "local" => "local temp directory on this host (demo only, no isolation; the host's copilot and plugin copy are used)",
        _ => "fake, scripted on this host (a mirror directory plays /workspace; FakeCopilotCli plays the CLI against the real bridge)",
    };

    private static string HarnessText(HveVariant variant, HveSolverOptions options) => variant.IsCopilot
        ? $"copilot: GitHub Copilot CLI inside the sandbox, bridged to the model (version {options.CopilotVersion}, {options.Provider} wire)"
        : "generic: Inspect basic_agent loop with the sandbox bash tool and submit (no external CLI)";

    private static string FrameworkText(HveVariant variant, HveSolverOptions options) => (variant.UsesFramework, variant.IsCopilot, options.HostPluginDirectory) switch
    {
        (false, _, _) => "none: no plugin provisioned or briefed (the repository's .github overlay stays)",
        (true, true, _) => $"hve: HVE Core plugin at {options.PluginDir} (--plugin-dir, --agent from metadata; the briefing names its agents, skills, prompts and instruction files)",
        (true, false, not null) => $"hve: HVE Core plugin at {options.PluginDir}, read with bash; the briefing describes its layout and embeds the sample's agent body",
        (true, false, null) => $"hve: HVE Core plugin at {options.PluginDir}, read with bash; the briefing describes its layout (the host cannot read the plugin: the agent reads its own agent file)",
    };

    /// <summary>Per-sample scores, the metrics (including the suite's per-kind breakdown) and a legend of the components that ran.</summary>
    public static void PrintSummary(EvalLog log, EvalTask task)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(task);
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

        var scorerNames = task.Scorers.Select(scorer => scorer.Name).ToList();
        Console.WriteLine($"{"sample",-28} {"epoch",5} {"kind",-10} {string.Join(" ", scorerNames.Select(name => $"{name,-18}"))} artefact / error");
        foreach (var sample in samples)
        {
            var kind = HveDataset.Kind(sample.Metadata);
            var cells = scorerNames.Select(name => $"{ScoreText(sample.Scores, name),-18}");
            var detail = sample.Error is { } sampleError
                ? $"error: {sampleError.Message.Split('\n')[0]}"
                : HveDataset.Artefact(sample.Metadata);
            Console.WriteLine($"{sample.Id,-28} {sample.Epoch,5} {kind,-10} {string.Join(" ", cells)} {Truncate(detail, 60)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"scorer",-20} {"metric",-12} {"group",-12} {"value",8}");
        foreach (var score in log.Results?.Scores ?? [])
        {
            foreach (var metric in score.Metrics.Values)
            {
                var text = double.IsNaN(metric.Value) ? "n/a" : metric.Value.ToString("0.000", CultureInfo.InvariantCulture);
                Console.WriteLine($"{score.Name,-20} {metric.Name,-12} {metric.Group ?? "",-12} {text,8}");
            }
        }

        Console.WriteLine();
        // The legend derives from the task's metadata and scorer list, so it adapts to the cell: the plugin clause and the
        // hve_artefact_used entry appear under hve only.
        var variant = VariantOf(task);
        Console.WriteLine("components:");
        Console.WriteLine($"  dataset  {task.Dataset.Name}: hve/dataset.json via Datasets.Json + FieldSpec; per-sample files = .github overlay + workspace/<id>{(variant.UsesFramework ? $" + the vendored plugin at {HveData.PluginSandboxPath}" : "")}");
        Console.WriteLine($"  solver   {task.Metadata?.GetValueOrDefault("solver") ?? variant.Label}: {HveSolvers.Describe(variant)}");
        Console.WriteLine($"  scorers  {string.Join(", ", task.Scorers.Select(scorer => ScorerLegend(scorer.Name)))}");
        Console.WriteLine($"  task     {task.Name}: sandbox {task.Sandbox?.Type}, message limit {task.MessageLimit}, time limit {task.TimeLimit?.TotalMinutes} min, fail_on_error {task.FailOnError.Flag?.ToString().ToLowerInvariant() ?? "threshold"}{(task.Scorers.Any(scorer => scorer.Metrics.Any(metric => metric.Name == "grouped")) ? ", each scorer's headline metric also grouped by kind" : "")}");
    }

    private static string ScoreText(IReadOnlyDictionary<string, Score>? scores, string scorer) =>
        scores?.TryGetValue(scorer, out var score) == true
            ? score.Value switch
            {
                ScoreValue.Str s => s.Value,
                ScoreValue.Num n => double.IsNaN(n.Value) ? "n/a" : n.Value.ToString("0.##", CultureInfo.InvariantCulture),
                ScoreValue.Bool b => b.Value ? "true" : "false",
                _ => score.Value.ToString() ?? "",
            }
            : "-";

    private static string Truncate(string text, int width)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= width ? line : line[..(width - 1)] + "…";
    }

    /// <summary>The cell a task was built for, from its <c>harness</c>/<c>framework</c> metadata (the default cell when a key is missing).</summary>
    private static HveVariant VariantOf(EvalTask task) => new(
        Convert.ToString(task.Metadata?.GetValueOrDefault("harness"), CultureInfo.InvariantCulture) is { Length: > 0 } harness ? harness : HveVariant.Default.Harness,
        Convert.ToString(task.Metadata?.GetValueOrDefault("framework"), CultureInfo.InvariantCulture) is { Length: > 0 } framework ? framework : HveVariant.Default.Framework);

    private static string ScorerLegend(string name) => name switch
    {
        HveScorers.ExecCheckName => $"{name} (metadata.check in the sandbox)",
        HveScorers.ArtefactReportedName => $"{name} (Scorers.Includes over the final message)",
        HveScorers.ArtefactQualityName => $"{name} (Scorers.ModelGradedQa over the artefact)",
        HveScorers.ArtefactUsedName => $"{name} (transcript evidence of the HVE components)",
        _ => name,
    };

    /// <summary>The parsed command line.</summary>
    public sealed record Options(
        bool Help,
        bool Fake,
        string Task,
        string Harness,
        string Framework,
        string? SolverAlias,
        string? Model,
        string? Route,
        string Sandbox,
        int? Limit,
        int? Epochs,
        int? MaxSamples,
        string LogDir,
        string CopilotVersion,
        string? PluginDir,
        bool Debug,
        bool Cleanup)
    {
        /// <summary>The validated cell; throws the flag message for a hand-built record with a bad name.</summary>
        public HveVariant Variant => HveVariant.Parse(Harness, Framework);

        /// <summary>
        /// Left to right, last flag wins. The two axes default to <see cref="HveVariant.Default"/>; the deprecated
        /// <c>--solver</c> sets both at once (and is remembered in <see cref="SolverAlias"/> for the stderr note), so a later
        /// <c>--harness</c> or <c>--framework</c> overrides one axis and a later <c>--solver</c> overrides an earlier flag.
        /// <c>--plugin-dir</c> is rejected with an explicit <c>--framework none</c> (nothing would use it) but tolerated when
        /// the <c>none</c> came from <c>--solver basic</c>, which accepted one before the split; <see cref="RunAsync"/> then
        /// notes that it is ignored.
        /// </summary>
        public static Options Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            bool help = false, fake = false, debug = false, cleanup = true, frameworkFromAlias = false;
            string? model = null, route = null, pluginDir = null, sandbox = null, solverAlias = null;
            string task = "suite", harness = HveVariant.Default.Harness, framework = HveVariant.Default.Framework, logDir = "logs", copilotVersion = "auto";
            int? limit = null, epochs = null, maxSamples = null;

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
                    case "--task":
                        task = Value("--task").ToLowerInvariant();
                        if (task is not ("implement" or "review" or "skill" or "suite"))
                        {
                            throw new ArgumentException("--task expects implement, review, skill or suite");
                        }

                        break;
                    case "--harness":
                        harness = HveVariant.Parse(Value("--harness"), framework).Harness;
                        break;
                    case "--framework":
                        framework = HveVariant.Parse(harness, Value("--framework")).Framework;
                        frameworkFromAlias = false;
                        break;
                    case "--solver":
                    {
                        var alias = Value("--solver");
                        var cell = HveVariant.FromSolverAlias(alias);
                        harness = cell.Harness;
                        framework = cell.Framework;
                        frameworkFromAlias = true;
                        solverAlias = alias.ToLowerInvariant();
                        break;
                    }

                    case "--model": model = Value("--model"); break;
                    case "--route": route = Value("--route"); break;
                    case "--sandbox":
                        sandbox = Value("--sandbox").ToLowerInvariant();
                        if (sandbox is not ("docker" or "local" or "fake"))
                        {
                            throw new ArgumentException("--sandbox expects docker, local or fake");
                        }

                        break;
                    case "--limit": limit = IntValue("--limit"); break;
                    case "--epochs": epochs = IntValue("--epochs"); break;
                    case "--max-samples": maxSamples = IntValue("--max-samples"); break;
                    case "--log-dir": logDir = Value("--log-dir"); break;
                    case "--copilot-version": copilotVersion = Value("--copilot-version"); break;
                    case "--plugin-dir": pluginDir = Value("--plugin-dir"); break;
                    case "--debug": debug = true; break;
                    case "--no-cleanup": cleanup = false; break;
                    default: throw new ArgumentException($"unknown option '{args[i]}'");
                }
            }

            // Without an endpoint there is nothing to call, so default to the scripted model rather than fail on every sample.
            if (!fake && model is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZUREAI_BASE_URL")))
            {
                fake = true;
            }

            // The fake sandbox needs the scripted model (its CLI stand-in is driven by it), and --fake defaults to it.
            sandbox ??= fake ? "fake" : "docker";
            if (sandbox == "fake" && !fake)
            {
                throw new ArgumentException("--sandbox fake needs --fake (the fake copilot binary is driven by the scripted model)");
            }

            // Under none no plugin is copied and no briefing names one, so a plugin directory would be silently ignored. The
            // deprecated --solver basic took one before the split (it only suppressed the plugin copy), so an old command line
            // keeps parsing; RunAsync's deprecation note says the directory is ignored.
            if (pluginDir is not null && framework == HveFramework.None && !frameworkFromAlias)
            {
                throw new ArgumentException("--plugin-dir needs --framework hve (with none no plugin is provisioned or briefed)");
            }

            return new Options(help, fake, task, harness, framework, solverAlias, model, route, sandbox, limit, epochs, maxSamples, logDir, copilotVersion, pluginDir, debug, cleanup);
        }
    }
}
