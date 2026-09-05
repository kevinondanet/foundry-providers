using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Sample;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The demos of the eval engine's model-level features on top of the provider: the prompt cache, cost accounting
/// and structured output. Each is one command (<c>cache</c>, <c>cost</c>, <c>structured</c>) and works with
/// <c>--fake</c>, so the mechanics can be seen without a deployment.
/// </summary>
internal static partial class Cli
{
    /// <summary>The structured-output demo's answer type; <see cref="JsonSchemaGenerator.JsonSchemaOf{T}"/> turns it into the response schema.</summary>
    internal sealed record CityFacts(
        [property: Description("The city's name")] string City,
        [property: Description("The country the city is in")] string Country,
        [property: Description("Population of the metropolitan area, in millions")] [property: JsonPropertyName("population_millions")] double PopulationMillions,
        [property: Description("Up to three well-known landmarks")] IReadOnlyList<string> Landmarks);

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// <c>cache</c>: the same prompt generated twice through Inspect's <c>Model</c> wrapper under a <see cref="CachePolicy"/>.
    /// The first call goes to the provider and is stored (<c>cache=write</c> on its model event), the second is served
    /// from <c>$INSPECT_CACHE_DIR</c> (or the user cache directory) without a provider call (<c>cache=read</c>).
    /// </summary>
    public static async Task<int> CacheDemo(IModelApi api, string prompt, string? expiry)
    {
        ArgumentNullException.ThrowIfNull(api);
        var policy = expiry is null
            ? CachePolicy.Default
            : CachePolicy.FromString(expiry) ?? throw new UsageError($"--cache-expiry expects a period such as 1W, 3D or 12h, got '{expiry}'");
        var model = new Model(api, DefaultConfig(api));
        var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt)) };
        Console.WriteLine($"model     : {model.Name}");
        Console.WriteLine($"cache dir : {CacheOps.CachePath(model.Name)}");
        Console.WriteLine($"policy    : expiry {policy.Expiry ?? "never"} ({policy.ExpirySeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"}s), per_epoch {policy.PerEpoch}");

        var sink = new RecordingSink();
        using (ModelEventSinks.Install(sink))
        {
            for (var call = 1; call <= 2; call++)
            {
                var watch = Stopwatch.StartNew();
                var output = await model.GenerateAsync(input, cache: policy);
                var e = sink.Events[^1];
                var mode = e.Cache switch
                {
                    CacheMode.Read => "read (served from the cache, no provider call)",
                    CacheMode.Write => "write (provider called, output stored)",
                    _ => "none",
                };
                Console.WriteLine($"call {call}    : cache={mode}, {watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms, {output.Usage?.TotalTokens.ToString(CultureInfo.InvariantCulture) ?? "?"} tokens");
                Console.WriteLine($"           {FirstLine(output.Completion)}");
            }
        }

        var entries = Directory.Exists(CacheOps.CachePath(model.Name)) ? Directory.EnumerateFiles(CacheOps.CachePath(model.Name)).Count(file => !file.EndsWith(".tmp", StringComparison.Ordinal)) : 0;
        Console.WriteLine($"entries   : {entries} for {model.Name}");
        return 0;
    }

    /// <summary>
    /// <c>cost</c>: what Inspect knows about the model (the model database, plus the prices of a
    /// <c>--model-cost-config</c> file or <c>$INSPECT_AZUREAI_MODEL_COST_CONFIG</c>), then one generation priced the
    /// way the eval runner prices every call (<c>usage.total_cost</c>). Without prices the call is reported unpriced.
    /// </summary>
    public static async Task<int> CostDemo(IModelApi api, string prompt, string? costConfig)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (costConfig is not null)
        {
            try
            {
                ModelCostConfig.Apply(costConfig);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                throw new UsageError($"--model-cost-config: {ex.Message}");
            }
        }

        var info = ModelInfoLookup.GetModelInfo(api.ModelName);
        Console.WriteLine($"model     : {api.ModelName}");
        Console.WriteLine(info is null
            ? "model info: (not in the model database)"
            : $"model info: {info.Organization ?? "?"}/{info.Model ?? "?"}"
              + (info.ContextLength is { } context ? $", context {context.ToString("N0", CultureInfo.InvariantCulture)}" : "")
              + (info.OutputTokens is { } outputTokens ? $", max output {outputTokens.ToString("N0", CultureInfo.InvariantCulture)}" : "")
              + (info.ReleaseDate is { } release ? $", released {release.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : ""));
        if (info?.Cost is { } prices)
        {
            Console.WriteLine($"prices    : input ${Price(prices.Input)}/M, output ${Price(prices.Output)}/M, cache write ${Price(prices.InputCacheWrite)}/M, cache read ${Price(prices.InputCacheRead)}/M");
        }
        else
        {
            Console.WriteLine($"prices    : none for {api.ModelName} (the model database ships no prices; pass --model-cost-config <file> or set {ModelCostConfig.EnvironmentVariable})");
        }

        var model = new Model(api, DefaultConfig(api));
        var output = await model.GenerateAsync([new ChatMessageUser(Prompt(prompt))]);
        Console.WriteLine($"completion: {FirstLine(output.Completion)}");
        if (output.Usage is not { } usage)
        {
            Console.WriteLine("usage     : (not reported)");
            return 0;
        }

        Console.WriteLine($"usage     : input={usage.InputTokens} output={usage.OutputTokens} total={usage.TotalTokens}"
                          + (usage.InputTokensCacheRead is { } read ? $" cache_read={read}" : "")
                          + (usage.InputTokensCacheWrite is { } write ? $" cache_write={write}" : ""));
        if (usage.TotalCost is { } total && info?.Cost is { } cost)
        {
            var cacheRead = usage.InputTokensCacheRead ?? 0;
            var cacheWrite = usage.InputTokensCacheWrite ?? 0;
            var uncached = Math.Max(0, usage.InputTokens - cacheRead - cacheWrite);
            Console.WriteLine($"cost      : ${total.ToString("0.000000", CultureInfo.InvariantCulture)}"
                              + $" = input {uncached} × ${Price(cost.Input)}/M + output {usage.OutputTokens} × ${Price(cost.Output)}/M"
                              + (cacheRead > 0 ? $" + cache read {cacheRead} × ${Price(cost.InputCacheRead)}/M" : "")
                              + (cacheWrite > 0 ? $" + cache write {cacheWrite} × ${Price(cost.InputCacheWrite)}/M" : ""));
        }
        else
        {
            Console.WriteLine("cost      : unpriced (no total_cost on the usage; an eval's cost limit would be a prerequisite error)");
        }

        return 0;
    }

    /// <summary>
    /// <c>structured</c>: a generation constrained to the JSON schema of <see cref="CityFacts"/> through
    /// <c>GenerateConfig.ResponseSchema</c> — sent as <c>response_format</c> (model-inference route) or
    /// <c>output_format</c> (Anthropic route) — then parsed back into the record. A completion that is not valid
    /// JSON for the schema is reported and exits 1.
    /// </summary>
    public static async Task<int> Structured(IModelApi api, string prompt)
    {
        ArgumentNullException.ThrowIfNull(api);
        var schema = JsonSchemaGenerator.JsonSchemaOf<CityFacts>();
        var config = DefaultConfig(api) with
        {
            ResponseSchema = new ResponseSchema("city_facts", schema) { Description = "A few facts about a city.", Strict = true },
        };
        var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt, "Give me a few facts about Paris.")) };
        var result = await api.GenerateAsync(input, [], ToolChoice.Auto, config);
        var request = result.Call.Request;
        var format = request["response_format"] ?? request["output_format"];
        Console.WriteLine("-- response schema on the wire --");
        Console.WriteLine(format?.ToJsonString(Pretty) ?? "(none: the provider sent no schema)");
        if (result.Output is not { } output)
        {
            Console.WriteLine($"-- terminal error: {result.Error!.GetType().Name}: {AzureAIModelApi.AzureErrorMessage(result.Error)}");
            return 1;
        }

        Console.WriteLine($"-- completion (stop_reason={output.StopReason.ToWire()}) --");
        Console.WriteLine(output.Completion);
        CityFacts? facts;
        try
        {
            facts = JsonSerializer.Deserialize<CityFacts>(output.Completion, CaseInsensitive);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"-- the completion is not valid JSON for the schema: {ex.Message}");
            return 1;
        }

        if (facts is null)
        {
            Console.WriteLine("-- the completion was JSON null");
            return 1;
        }

        Console.WriteLine("-- parsed --");
        Console.WriteLine($"city      : {facts.City}");
        Console.WriteLine($"country   : {facts.Country}");
        Console.WriteLine($"population: {facts.PopulationMillions.ToString(CultureInfo.InvariantCulture)} million");
        Console.WriteLine($"landmarks : {string.Join(", ", facts.Landmarks ?? [])}");
        return 0;
    }

    private static string Price(double perMillion) => perMillion.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }

    /// <summary>Collects the model events of the cache demo (outside a sample there is no transcript to read them from).</summary>
    private sealed class RecordingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }
}
