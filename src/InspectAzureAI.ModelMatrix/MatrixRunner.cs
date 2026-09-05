using System.Diagnostics;
using System.Text;
using Azure;
using Azure.Core;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.SweShowcase;
using InspectAzureAI.SweShowcase.BuiltinTasks;

namespace InspectAzureAI.ModelMatrix;

using EvalSet = InspectAzureAI.Eval.Runner.EvalSet.EvalSet;

/// <summary>
/// Discovers every deployment on the Foundry resource behind <c>AZUREAI_BASE_URL</c>, runs the chosen showcase task
/// and agent against each one and collects one <see cref="MatrixRow"/> per deployment. Each deployment is its own
/// eval set (<see cref="EvalSet.RunAsync"/>) in <c>&lt;log-dir&gt;/&lt;deployment&gt;/</c>: a complete log already
/// there is reused without running, an incomplete one is re-run reusing its completed samples, and an eval that
/// errors is retried immediately (<see cref="MatrixOptions.RetryAttempts"/>) — so running the matrix again with the
/// same log directory resumes it. A deployment that fails is recorded, not fatal; only a sign-in failure or
/// cancellation stops the matrix.
/// </summary>
internal sealed class MatrixRunner(MatrixOptions options, TextWriter output)
{
    private readonly Lock _gate = new();

    public async Task<MatrixReport> RunAsync(CancellationToken cancellationToken)
    {
        var definition = ShowcaseTasks.Resolve(options.Task);
        var agent = AgentChoice.Resolve(options.Agent);
        if (options.Run.Fake)
        {
            _ = FakeScripts.For(definition.Name, agent);   // rejects claude-code up front, before any discovery
        }

        if (options.Run.Compaction is not null)
        {
            AgentChoice.RejectCompaction(agent);
        }

        var sandbox = options.SandboxType == "docker"
            ? new SandboxSpec("docker", TaskData.RequireSandboxDirectory())
            : new SandboxSpec("local");

        var (endpoint, resource, deployments, settings) = options.Run.Fake
            ? ("fake://scripted", null, FakeDeployments(), null)
            : await DiscoverAsync(cancellationToken);

        var selection = DeploymentSelection.Select(deployments, options);
        PrintHeader(definition, agent, endpoint, resource, sandbox, deployments.Count, selection);
        foreach (var unknown in DeploymentSelection.UnknownOnly(deployments, options))
        {
            Write($"warning: --only {unknown} matches no deployment");
        }

        var rows = new MatrixRow[selection.Count];
        using var slots = new SemaphoreSlim(Math.Max(1, options.Parallel));
        using var hookFiles = new HookFiles();
        var work = selection.Select(async (entry, index) =>
        {
            if (!entry.Selected)
            {
                rows[index] = MatrixRow.SkippedRow(entry);
                Write($"{entry.Deployment.Name}: skipped ({entry.SkipReason})");
                return;
            }

            await slots.WaitAsync(cancellationToken);
            try
            {
                rows[index] = await RunOneAsync(entry, definition, agent, sandbox, settings, hookFiles, cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        await Task.WhenAll(work);

        return new MatrixReport
        {
            Resource = resource is null ? null : new MatrixResource(resource.Name, resource.ResourceGroup, resource.Location, resource.Kind),
            Endpoint = endpoint,
            Task = definition.Name,
            Scorer = definition.ScorerName,
            Agent = agent,
            Sandbox = sandbox.Type,
            Config = new Dictionary<string, object?>
            {
                ["limit"] = options.Limit,
                ["sampleIds"] = options.Run.SampleIds.Count > 0 ? options.Run.SampleIds : null,
                ["epochs"] = options.Run.Epochs,
                ["attempts"] = options.Run.Attempts,
                ["maxTokens"] = options.Run.MaxTokensSet ? options.Run.MaxTokens : null,
                ["reasoningEffort"] = options.Run.ReasoningEffort,
                ["parallel"] = options.Parallel,
                ["retryAttempts"] = options.RetryAttempts,
                ["logFormat"] = options.Run.LogFormat?.Name(),
                ["approval"] = options.Run.ApprovalSpec,
                ["cache"] = options.Run.Cache is { } cache ? cache.Expiry ?? "never" : null,
                ["compaction"] = options.Run.Compaction?.ToString(),
                ["hooks"] = options.Run.Hooks.Count > 0 ? options.Run.Hooks.Select(h => h.ToString()).ToList() : null,
                ["costLimit"] = options.Run.CostLimit,
                ["modelCostConfig"] = options.Run.ModelCostConfig,
                ["fake"] = options.Run.Fake,
            },
            Rows = rows,
        };
    }

    /// <summary>The offline catalog: two chat deployments, one image model and one failed deployment, so every skip rule shows.</summary>
    internal static IReadOnlyList<FoundryDeployment> FakeDeployments()
    {
        var chat = new Dictionary<string, string> { ["chatCompletion"] = "true" };
        var image = new Dictionary<string, string> { ["chatCompletion"] = "false" };
        return
        [
            new FoundryDeployment("fake-gpt", "gpt-fake", "OpenAI", "1", "Succeeded", null, null, chat),
            new FoundryDeployment("fake-claude", "claude-fake", "Anthropic", "1", "Succeeded", null, null, chat),
            new FoundryDeployment("fake-image", "flux-fake", "Black Forest Labs", "1", "Succeeded", null, null, image),
            new FoundryDeployment("fake-stale", "gpt-fake", "OpenAI", "1", "Failed", null, null, chat),
        ];
    }

    /// <summary>
    /// Resolves the endpoint and credential the way every provider does, acquires a token up front (so a sign-in
    /// problem surfaces before any sample starts) and lists the resource's deployments through Azure Resource Manager.
    /// The returned settings share one token cache across every deployment's model.
    /// </summary>
    private static async Task<(string Endpoint, FoundryResource? Resource, IReadOnlyList<FoundryDeployment> Deployments, AzureAIClientSettings? Settings)> DiscoverAsync(CancellationToken cancellationToken)
    {
        var probe = new AzureAIModelApi(FoundryModels.DefaultModel);
        await probe.Credential.GetTokenAsync(new TokenRequestContext([probe.Credential.Scope]), cancellationToken);
        using var catalog = new FoundryCatalog(probe.Credential.Inner);
        var (resource, deployments) = await catalog.DiscoverAsync(probe.EndpointUrl, cancellationToken: cancellationToken);
        var shared = probe.Settings with { TokenCredential = probe.Credential.Inner };
        return (probe.EndpointUrl, resource, deployments, shared);
    }

    /// <summary>Characters replaced in a deployment name used as a directory: the Windows set (a superset of every platform's), so a log directory copies between machines.</summary>
    private static readonly char[] UnsafePathChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>The eval-set directory of a deployment: its name under the matrix's log directory, with path-unsafe characters replaced.</summary>
    internal static string DeploymentLogDir(string logDir, string deployment)
    {
        var safe = new string(deployment.Select(c => UnsafePathChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        return Path.Combine(logDir, safe.Length == 0 ? "_" : safe);
    }

    private async Task<MatrixRow> RunOneAsync(SelectedDeployment entry, ShowcaseTask definition, string agent, SandboxSpec sandbox, AzureAIClientSettings? settings, HookFiles hookFiles, CancellationToken cancellationToken)
    {
        var name = entry.Deployment.Name;
        Write($"{name}: started ({entry.Route} route, {entry.Deployment.Format})");
        var watch = Stopwatch.StartNew();
        var hooks = RunWiring.CreateHooks(options.Run, choice => HookWriter(choice, name, hookFiles));
        try
        {
            var config = options.Run.GenerateConfig;
            var model = options.Run.Fake
                ? new Model(FakeScripts.For(definition.Name, agent, name), config)
                : FoundryModels.Create(name, config, entry.Route, modelArgs: ModelArgsFor(entry), settings: settings);
            var task = definition.Build(new TaskBuildContext(AgentChoice.Solver(agent, options.Run.Attempts, options.Run.Debug, options.Run.Cache, options.Run.Compaction?.Hook()), sandbox));
            var logDir = DeploymentLogDir(options.Run.LogDir, name);
            var evalOptions = RunWiring.EvalOptions(options.Run, model, options.Limit, new DeploymentReporter(line => Write($"{name}: {line}")), hooks) with { LogDir = logDir };
            var setOptions = new EvalSetOptions
            {
                Eval = evalOptions,
                RetryAttempts = options.RetryAttempts,
                MaxTasks = 1,
                // the same directory serves every run of the matrix, whatever its flags (a changed limit or epochs is a new task identifier)
                LogDirAllowDirty = true,
            };
            var result = await EvalSet.RunAsync([task], setOptions, cancellationToken);
            var log = result.Logs[0];
            // a task whose latest log was already complete comes back as a header (no samples): nothing ran
            var reused = log.Samples is null;
            if (reused)
            {
                log = EvalLogWriter.Read(log.Location ?? LocateLog(logDir, log.Eval.TaskId));
            }

            var row = MatrixRow.FromLog(entry, log, reused ? LogDuration(log) : watch.Elapsed.TotalSeconds, reused);
            var detail = row.Accuracy is { } accuracy ? $" accuracy {accuracy:0.000}" : "";
            var note = row.IsError ? $" {row.Note}" : "";
            var cost = row.Cost is { } dollars ? $", {MatrixReport.FormatCost(dollars)}" : "";
            Write($"{name}: {(reused ? "reused " : "")}{row.Status}{detail}{note} ({row.Tokens} tokens{cost}, {row.Seconds:0.0}s)");
            return row;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Cli.IsSignInFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = Describe(ex);
            Write($"{name}: error {message}");
            if (options.Run.Debug)
            {
                Write(ex.ToString());
            }

            return MatrixRow.Failed(entry, message, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            RunWiring.DisposeHooks(hooks);
        }
    }

    /// <summary>The writer of one hook of a deployment: whole lines, prefixed with the deployment name, to the matrix console or to the hook's file shared by every deployment.</summary>
    private TextWriter HookWriter(HookChoice choice, string deployment, HookFiles hookFiles)
    {
        var file = choice.Path is { } path ? hookFiles.Open(path) : null;
        return new LineWriter(line =>
        {
            var text = $"{deployment}: {line}";
            if (file is null)
            {
                Write(text);
            }
            else
            {
                file.WriteLine(text);
            }
        });
    }

    /// <summary>The log file of a task in a deployment directory (for a reused header that carries no location).</summary>
    private static string LocateLog(string logDir, string taskId) =>
        EvalSetLogs.ListAllEvalLogs(logDir).FirstOrDefault(log => log.Header.Eval.TaskId == taskId)?.Path
        ?? throw new InvalidOperationException($"No log for task {taskId} in {logDir}.");

    /// <summary>The wall-clock span the log records (a reused row has no run of its own to time).</summary>
    private static double LogDuration(EvalLog log) =>
        log.Stats is { StartedAt: { } started, CompletedAt: { } completed } ? Math.Max(0, (completed - started).TotalSeconds) : (log.Samples ?? []).Sum(s => s.TotalTime ?? 0);

    /// <summary>Forwards the eval set's and runner's messages (retries, completion) to the matrix console; per-sample lines stay off to keep it compact.</summary>
    private sealed class DeploymentReporter(Action<string> write) : IEvalReporter
    {
        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text) => write(text);
    }

    /// <summary>A <see cref="TextWriter"/> whose whole lines go through the matrix's locked console writer (hooks of parallel deployments never interleave).</summary>
    private sealed class LineWriter(Action<string> write) : TextWriter
    {
        private readonly StringBuilder _partial = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _partial.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "")
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Flush();
        }

        public override void Flush()
        {
            if (_partial.Length > 0)
            {
                write(_partial.ToString());
                _partial.Clear();
            }
        }
    }

    /// <summary>The user's <c>--model-arg</c>s plus ARM's vendor string, which picks the reasoning-parameter mapping on the model-inference route.</summary>
    private IReadOnlyDictionary<string, object?> ModelArgsFor(SelectedDeployment entry)
    {
        var args = new Dictionary<string, object?>(options.Run.ModelArgs, StringComparer.Ordinal);
        if (entry.Route == DeploymentSelection.ModelsRoute)
        {
            args.TryAdd("model_format", entry.Deployment.Format);
        }

        return args;
    }

    private static string Describe(Exception ex) => ex switch
    {
        RequestFailedException failed => $"Azure request failed (HTTP {failed.Status}): {AzureAIModelApi.AzureErrorMessage(failed)}",
        PrerequisiteError prerequisite => ProviderUtil.StripRichMarkup(prerequisite.Message),
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Describe(aggregate.InnerExceptions[0]),
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private void PrintHeader(ShowcaseTask definition, string agent, string endpoint, FoundryResource? resource, SandboxSpec sandbox, int discovered, IReadOnlyList<SelectedDeployment> selection)
    {
        Write($"task     : {definition.Name} (scorer {definition.ScorerName})");
        Write($"agent    : {agent} (attempts {options.Run.Attempts})");
        Write(resource is null
            ? $"endpoint : {endpoint}"
            : $"resource : {resource.Name} ({resource.ResourceGroup}, {resource.Location}) at {endpoint}");
        Write($"sandbox  : {sandbox.Type}{(sandbox.Config is null ? "" : $" ({sandbox.Config})")}");
        Write($"log dir  : {Path.GetFullPath(options.Run.LogDir)}");
        Write($"samples  : {(options.Run.SampleIds.Count > 0 ? string.Join(",", options.Run.SampleIds) : $"first {options.Limit}")} per deployment, {options.Parallel} deployment(s) at a time");
        var outside = discovered - selection.Count;
        Write($"models   : {discovered} discovered, {selection.Count(s => s.Selected)} selected, {selection.Count(s => !s.Selected)} skipped{(outside > 0 ? $", {outside} outside --only" : "")}");
        Write("");
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            output.WriteLine(line);
        }
    }
}
