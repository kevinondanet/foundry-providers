using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Sample;

/// <summary>The <c>params</c> command: probes which request parameters each deployment accepts.</summary>
internal static partial class Cli
{
    /// <summary>One probe call and everything recorded about it.</summary>
    private sealed record ProbeRun(ProbeSpec Spec, ProbeObservation Observation, long Ms, bool PassThroughHeader, IReadOnlyList<HttpExchange> Exchanges);

    public static async Task<int> Params(AzureAIModelApi api, bool fake, string? only, bool includeFailed, string paramsFilter, int parallel, bool json, string? outPath)
    {
        if (fake)
        {
            Console.Error.WriteLine("params needs Azure Resource Manager and live endpoints; it is not available with --fake.");
            return 2;
        }

        using var catalog = new FoundryCatalog(ArmCredential(api));
        var (resource, deployments) = await catalog.DiscoverAsync(api.EndpointUrl);
        var wanted = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filter = ParseParamsFilter(paramsFilter);
        var shared = api.Settings with { TokenCredential = api.Credential.Inner };
        var targets = deployments
            .Where(d => (wanted is null || wanted.Contains(d.Name)) && (includeFailed || (d.IsSucceeded && d.SupportsChat)))
            .ToList();
        if (!json)
        {
            PrintResource(resource, api.EndpointUrl);
            Console.WriteLine($"probing {targets.Count} deployment(s), {parallel} at a time; baseline + stream + {(filter is null ? "every candidate" : string.Join(",", filter))} each");
            Console.WriteLine();
        }

        var results = await ProbeAllAsync(api, shared, targets, filter, parallel, json ? null : Console.Error.WriteLine);

        var report = new JsonObject
        {
            ["capturedAt"] = DateTimeOffset.UtcNow.ToString("u"),
            ["resource"] = new JsonObject { ["name"] = resource.Name, ["kind"] = resource.Kind, ["location"] = resource.Location, ["resourceGroup"] = resource.ResourceGroup },
            ["endpoint"] = api.EndpointUrl,
            ["deployments"] = new JsonArray(results.Select(r => (JsonNode?)new JsonObject
            {
                ["name"] = r.Deployment.Name,
                ["format"] = r.Deployment.Format,
                ["route"] = RouteFor(r.Deployment) == "anthropic" ? "anthropic" : "models",
                ["params"] = r.Params,
            }).ToArray()),
        };
        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, report.ToJsonString(Pretty));
            Console.Error.WriteLine($"wrote {outPath}");
        }

        if (json)
        {
            if (outPath is null) Console.WriteLine(report.ToJsonString(Pretty));
            return 0;
        }

        foreach (var (deployment, entries) in results)
        {
            Console.WriteLine($"{deployment.Name} ({deployment.Format}, {(RouteFor(deployment) == "anthropic" ? "/anthropic/v1/messages" : "/models/chat/completions")})  {Summary(entries)}");
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var verdict = entry["verdict"]!.GetValue<string>();
                var observable = entry["observable"]?.GetValue<bool>() == true;
                Console.WriteLine($"  {entry["id"],-30} {(verdict == "accepted" && !observable ? "ok*" : verdict),-9} {Clip(entry["evidence"]?.ToString() ?? "", 110)}");
            }

            Console.WriteLine();
        }

        PrintMatrix(results);
        Console.WriteLine();
        Console.WriteLine("ok* = accepted, no visible effect (the service tolerated the field; the probe cannot tell whether it acted on it)");
        return 0;
    }

    /// <summary><c>all</c> (or empty) means every candidate; otherwise a comma list of probe ids.</summary>
    internal static HashSet<string>? ParseParamsFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Probes deployments concurrently (at most <paramref name="parallel"/> at a time); probes within one deployment run one after another.</summary>
    internal static async Task<List<(FoundryDeployment Deployment, JsonArray Params)>> ProbeAllAsync(
        AzureAIModelApi api, AzureAIClientSettings shared, IReadOnlyList<FoundryDeployment> deployments, HashSet<string>? filter, int parallel, Action<string>? log)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var tasks = deployments.Select(async deployment =>
        {
            await gate.WaitAsync();
            try
            {
                return (deployment, await ProbeDeploymentAsync(api, shared, deployment, filter, log));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return [.. await Task.WhenAll(tasks)];
    }

    /// <summary>
    /// Baseline, stream baseline, then one call per candidate (each alone on top of the baseline). A baseline
    /// rejected for <c>max_tokens</c> flips to <c>max_completion_tokens</c> for the rest, and both facts are recorded.
    /// </summary>
    internal static async Task<JsonArray> ProbeDeploymentAsync(AzureAIModelApi api, AzureAIClientSettings shared, FoundryDeployment deployment, HashSet<string>? filter, Action<string>? log)
    {
        // TODO(responses route): the catalog probes chat-completions parameters, so OpenAI-format deployments are probed on
        // the model-inference route even when test-all/capture would take them through /openai/v1/responses.
        var anthropic = RouteFor(deployment) == "anthropic";
        var family = anthropic ? ModelFamilyHint.Anthropic : ReasoningParams.FamilyOf(deployment.Format, deployment.Name);
        var args = new Dictionary<string, object?>(ExtraModelArgs);
        var results = new JsonArray();

        var baseline = await RunProbeAsync(api, shared, deployment, anthropic, ProbeCatalog.Baseline, args);
        if (!anthropic && baseline.Observation.Status == 400 && (baseline.Observation.Error ?? "").Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
        {
            // Reasoning models (MAI-Thinking-1) reject max_tokens: record the rejection, then re-baseline with the other field.
            var rejected = baseline with { Spec = baseline.Spec with { Id = "max_tokens", Param = "max_tokens", Value = JsonValue.Create(512), Prompt = ProbeCatalog.PromptReasoning } };
            results.Add(Entry(rejected, new ProbeVerdict("rejected", true, baseline.Observation.Error!)));
            args["max_completion_tokens"] = true;
            baseline = await RunProbeAsync(api, shared, deployment, anthropic, ProbeCatalog.Baseline, args);
        }

        var baselineVerdict = ProbeClassifier.Classify(ProbeCatalog.Baseline, baseline.Observation, null, baseline.Observation);
        results.Add(Entry(baseline, baselineVerdict with { Evidence = baseline.Observation.Output is null ? baselineVerdict.Evidence : "reference call: " + ReasoningEvidence(baseline) }));
        if (baseline.Observation.Output is null)
        {
            log?.Invoke($"{deployment.Name,-30} baseline failed ({baseline.Observation.Error}); nothing else probed");
            return results;
        }

        var usedMaxCompletionTokens = baseline.Observation.RequestBody?.ContainsKey("max_completion_tokens") == true;
        var flipped = args.ContainsKey("max_completion_tokens") && !ExtraModelArgs.ContainsKey("max_completion_tokens");   // both fields already recorded
        var streamBaseline = await RunProbeAsync(api, shared, deployment, anthropic, ProbeCatalog.StreamBaseline, args);
        results.Add(Entry(streamBaseline, ProbeClassifier.Classify(ProbeCatalog.StreamBaseline, baseline.Observation, null, streamBaseline.Observation)));

        var specs = new List<ProbeSpec>(ProbeCatalog.For(family));
        if (!anthropic && !flipped)
        {
            var at = specs.FindIndex(s => s.Id == "stream_options");
            specs.Insert(at < 0 ? specs.Count : at, ProbeCatalog.AlternateTokenField(usedMaxCompletionTokens));
        }

        foreach (var spec in specs)
        {
            if (filter is not null && !filter.Contains(spec.Id))
            {
                continue;
            }

            var run = await RunProbeAsync(api, shared, deployment, anthropic, spec, args);
            var verdict = ProbeClassifier.Classify(spec, baseline.Observation, streamBaseline.Observation, run.Observation);
            if (verdict.Verdict == "rejected" && spec.SplitOnRejection is { Count: > 0 } parts)
            {
                // The grouped probe was rejected: try its members one by one so the rejection is attributable.
                foreach (var part in parts)
                {
                    var partRun = await RunProbeAsync(api, shared, deployment, anthropic, part, args);
                    results.Add(Entry(partRun, ProbeClassifier.Classify(part, baseline.Observation, streamBaseline.Observation, partRun.Observation)));
                }

                continue;
            }

            results.Add(Entry(run, verdict));
        }

        log?.Invoke($"{deployment.Name,-30} {Summary(results)}");
        return results;
    }

    private static async Task<ProbeRun> RunProbeAsync(AzureAIModelApi api, AzureAIClientSettings shared, FoundryDeployment deployment, bool anthropic, ProbeSpec spec, IReadOnlyDictionary<string, object?> baseArgs)
    {
        var args = new Dictionary<string, object?>(baseArgs);
        foreach (var (key, value) in spec.ModelArgs ?? new Dictionary<string, object?>())
        {
            args[key] = value;
        }

        using var target = CreateCapturedTarget(api, shared, deployment, anthropic ? "anthropic" : "models", args);
        var config = new GenerateConfig { MaxTokens = spec.MaxTokens };   // no temperature: gpt-5 deployments reject anything but 1
        if (spec.Configure is not null)
        {
            config = spec.Configure(config);
        }

        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string? error = null;
        ModelOutput? output = null;
        JsonObject? responseBody = null;
        try
        {
            var input = new List<ChatMessage> { new ChatMessageUser(spec.Prompt) };
            IReadOnlyList<ToolInfo> tools = spec.WithTools ? [WeatherTool] : [];
            var result = spec.Stream
                ? await target.Api.GenerateAsync(input, tools, ToolChoice.Auto, config, _ => Task.CompletedTask, cts.Token)
                : await target.Api.GenerateAsync(input, tools, ToolChoice.Auto, config, cts.Token);
            output = result.Output;
            responseBody = result.Call.Response as JsonObject;          // the folded document for streams
            if (output is null)
            {
                error = AzureAIModelApi.AzureErrorMessage(result.Error!);
            }
        }
        catch (OperationCanceledException)
        {
            error = "timed out after 120s";
        }
        catch (Exception ex) when (ex is RequestFailedException or ServiceResponseException or InvalidOperationException)
        {
            error = AzureAIModelApi.AzureErrorMessage(ex);
        }

        watch.Stop();
        var exchanges = target.Exchanges.ToList();
        var last = exchanges.LastOrDefault();
        var requestBody = TryParseObject(last?.RequestBody);
        int? status = last?.Status is > 0 ? last.Status : null;
        var passThrough = last?.RequestHeaders.ContainsKey("extra-parameters") == true;
        return new ProbeRun(spec, new ProbeObservation(status, error, requestBody, responseBody, output), watch.ElapsedMilliseconds, passThrough, exchanges);
    }

    private static JsonObject Entry(ProbeRun run, ProbeVerdict verdict) => new()
    {
        ["id"] = run.Spec.Id,
        ["param"] = run.Spec.Param,
        ["value"] = run.Spec.Value?.DeepClone(),
        ["via"] = run.Spec.Via == ProbeVia.Config ? "config" : "model-arg",
        ["prompt"] = run.Spec.Prompt,
        ["stream"] = run.Spec.Stream,
        ["verdict"] = verdict.Verdict,
        ["observable"] = verdict.Observable,
        ["evidence"] = verdict.Evidence,
        ["status"] = run.Observation.Status,
        ["ms"] = run.Ms,
        ["passThroughHeader"] = run.PassThroughHeader,
        ["error"] = run.Observation.Error,
        ["output"] = run.Observation.Output is { } output ? OutputJson(output, run.Exchanges) : null,
        ["exchanges"] = new JsonArray(run.Exchanges.Select(e => (JsonNode?)e.ToJson()).ToArray()),
    };

    private static string ReasoningEvidence(ProbeRun run)
    {
        var tokens = ProbeClassifier.ReasoningTokensOf(run.Observation.ResponseBody);
        var text = ProbeClassifier.ReasoningTextOf(run.Observation.Output, run.Observation.ResponseBody);
        return $"reasoning_tokens {(tokens?.ToString() ?? "n/a")}, text {(text ? "visible" : "absent")}";
    }

    private static string Summary(JsonArray entries)
    {
        var counts = entries.OfType<JsonObject>().GroupBy(e => e["verdict"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Count());
        return string.Join(" · ", new[] { "accepted", "ignored", "rejected", "error", "n/a" }.Where(counts.ContainsKey).Select(k => $"{counts[k]} {k}"));
    }

    private static void PrintMatrix(List<(FoundryDeployment Deployment, JsonArray Params)> results)
    {
        var columns = results.SelectMany(r => r.Params.OfType<JsonObject>().Select(e => e["id"]!.GetValue<string>())).Distinct().ToList();
        if (columns.Count == 0)
        {
            return;
        }

        static string Cell(JsonObject? entry) => entry is null ? "-" : entry["verdict"]!.GetValue<string>() switch
        {
            "accepted" => entry["observable"]?.GetValue<bool>() == true ? "ok" : "ok*",
            "ignored" => "ign",
            "rejected" => "REJ",
            "error" => "err",
            _ => "n/a",
        };

        var width = Math.Max(10, columns.Max(c => c.Length));
        Console.WriteLine("parameter matrix (rows: deployments, columns: probes)");
        Console.WriteLine($"{"deployment",-30} " + string.Join(" ", columns.Select(c => c.Length > 12 ? c[..12] : c).Select(c => c.PadRight(12))));
        foreach (var (deployment, entries) in results)
        {
            var byId = entries.OfType<JsonObject>().ToDictionary(e => e["id"]!.GetValue<string>(), e => e);
            Console.WriteLine($"{deployment.Name,-30} " + string.Join(" ", columns.Select(c => Cell(byId.GetValueOrDefault(c)).PadRight(12))));
        }

        _ = width;
    }

    private static JsonObject? TryParseObject(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] + "…" : text.Replace('\n', ' ');
}
