using System.Diagnostics;
using Azure;
using Azure.Core;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.SweShowcase;
using InspectAzureAI.SweShowcase.BuiltinTasks;

namespace InspectAzureAI.ModelMatrix;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Discovers every deployment on the Foundry resource behind <c>AZUREAI_BASE_URL</c>, runs the chosen showcase task
/// and agent against each one (an eval and a JSON log per deployment, all through the same runner the showcase
/// uses) and collects one <see cref="MatrixRow"/> per deployment. A deployment that fails is recorded, not fatal;
/// only a sign-in failure or cancellation stops the matrix.
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
                rows[index] = await RunOneAsync(entry, definition, agent, sandbox, settings, cancellationToken);
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

    private async Task<MatrixRow> RunOneAsync(SelectedDeployment entry, ShowcaseTask definition, string agent, SandboxSpec sandbox, AzureAIClientSettings? settings, CancellationToken cancellationToken)
    {
        var name = entry.Deployment.Name;
        Write($"{name}: started ({entry.Route} route, {entry.Deployment.Format})");
        var watch = Stopwatch.StartNew();
        try
        {
            var config = options.Run.GenerateConfig;
            var model = options.Run.Fake
                ? new Model(FakeScripts.For(definition.Name, agent), config)
                : FoundryModels.Create(name, config, entry.Route, modelArgs: ModelArgsFor(entry), settings: settings);
            var task = definition.Build(new TaskBuildContext(AgentChoice.Solver(agent, options.Run.Attempts, options.Run.Debug), sandbox));
            var evalOptions = new EvalOptions
            {
                Model = model,
                Limit = options.Limit,
                SampleIds = options.Run.SampleIds.Count > 0 ? options.Run.SampleIds : null,
                Epochs = options.Run.Epochs,
                MaxSamples = options.Run.MaxSamples,
                LogDir = options.Run.LogDir,
                Cleanup = options.Run.Cleanup,
            };
            var log = await Eval.RunAsync(task, evalOptions, cancellationToken);
            var row = MatrixRow.FromLog(entry, log, watch.Elapsed.TotalSeconds);
            var detail = row.Accuracy is { } accuracy ? $" accuracy {accuracy:0.000}" : "";
            var note = row.IsError ? $" {row.Note}" : "";
            Write($"{name}: {row.Status}{detail}{note} ({row.Tokens} tokens, {row.Seconds:0.0}s)");
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
