using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// Port of <c>model/_cache.py</c> <c>CacheEntry</c>: everything that identifies one generate call in the prompt
/// cache. <see cref="Key"/> is computed once, on construction, by <see cref="CacheKey.Compute"/>. Python reads the
/// epoch from a context variable; here it is an explicit constructor argument (<see cref="Model"/> passes the
/// sample state's epoch).
/// </summary>
public sealed class CacheEntry
{
    public CacheEntry(
        string? baseUrl,
        GenerateConfig config,
        IReadOnlyList<ChatMessage> input,
        string model,
        CachePolicy policy,
        ToolChoice? toolChoice,
        IReadOnlyList<ToolInfo> tools,
        int? epoch = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(tools);
        BaseUrl = baseUrl;
        Config = config;
        Input = input;
        Model = model;
        Policy = policy;
        ToolChoice = toolChoice;
        Tools = tools;
        Epoch = epoch;
        Key = CacheKey.Compute(this);
    }

    /// <summary>The base URL of the model API, if any.</summary>
    public string? BaseUrl { get; }

    /// <summary>The configuration used to generate the output.</summary>
    public GenerateConfig Config { get; }

    /// <summary>The messages sent to the model.</summary>
    public IReadOnlyList<ChatMessage> Input { get; }

    /// <summary>The model name; also the cache subdirectory the entry is stored under.</summary>
    public string Model { get; }

    /// <summary>The policy: expiry, per-epoch keying and extra scopes.</summary>
    public CachePolicy Policy { get; }

    /// <summary>The tool choice, if any.</summary>
    public ToolChoice? ToolChoice { get; }

    /// <summary>The tools provided to the model.</summary>
    public IReadOnlyList<ToolInfo> Tools { get; }

    /// <summary>The epoch of the sample being run (Python's <c>epoch</c> context variable); part of the key when <see cref="CachePolicy.PerEpoch"/>.</summary>
    public int? Epoch { get; }

    /// <summary>The MD5 hex digest identifying this entry (its file name under the model directory).</summary>
    public string Key { get; }
}

/// <summary>
/// Port of <c>_cache_key</c>. The key covers the same components as Python — the config minus the fields that do
/// not affect the output, the messages minus their ids, the base url, the tool choice, the tools, the expiry in
/// seconds, the scopes and (per epoch) the epoch — but hashes their canonical JSON rather than Python's
/// <c>str()</c> reprs, so the digests differ from Python's for the same call.
/// </summary>
public static class CacheKey
{
    /// <summary>
    /// The Python exclusion set: connection, retry, timeout, cache and batch settings. The .NET <see cref="GenerateConfig"/>
    /// carries only four of them; the rest are listed so the set stays identical to Python's.
    /// </summary>
    public static IReadOnlyList<string> ExcludedConfigFields { get; } =
        ["max_connections", "adaptive_connections", "max_retries", "timeout", "stream_idle_timeout", "cache", "batch"];

    /// <summary>The lowercase MD5 hex digest of <see cref="CanonicalJson"/> over <see cref="Components"/>.</summary>
    public static string Compute(CacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var json = CanonicalJson(Components(entry));
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// The key components in Python's order: config, messages, base url, tool choice, tools, expiry seconds, scopes,
    /// and the epoch when the policy is per epoch. Exposed so callers can see why two calls key differently.
    /// </summary>
    public static JsonArray Components(CacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var options = EvalLogWriter.Options;

        var config = JsonSerializer.SerializeToNode(entry.Config, options)?.AsObject() ?? new JsonObject();
        foreach (var field in ExcludedConfigFields)
        {
            config.Remove(field);
        }

        var messages = new JsonArray();
        foreach (var message in entry.Input)
        {
            var node = JsonSerializer.SerializeToNode(message, options)?.AsObject()
                ?? throw new InvalidOperationException($"A {message.GetType().Name} did not serialize to a JSON object.");
            node.Remove("id");
            messages.Add(node);
        }

        var tools = new JsonArray();
        foreach (var tool in entry.Tools)
        {
            tools.Add(tool.ToJson());
        }

        var scopes = new JsonObject();
        foreach (var (name, value) in entry.Policy.Scopes)
        {
            scopes[name] = value;
        }

        var components = new JsonArray
        {
            config,
            messages,
            entry.BaseUrl is { } baseUrl ? JsonValue.Create(baseUrl) : null,
            entry.ToolChoice is { } choice ? JsonSerializer.SerializeToNode(choice, options) : null,
            tools,
            entry.Policy.ExpirySeconds is { } seconds ? JsonValue.Create(seconds) : null,
            scopes,
        };
        if (entry.Policy.PerEpoch)
        {
            components.Add(entry.Epoch is { } epoch ? JsonValue.Create(epoch) : null);
        }

        return components;
    }

    /// <summary>
    /// Canonical JSON: object keys sorted ordinally at every level, <c>json.dumps</c> default separators and ASCII
    /// escaping (via <see cref="PythonJson"/>), so the same content always hashes the same regardless of insertion order.
    /// </summary>
    public static string CanonicalJson(JsonNode? node) => PythonJson.Dumps(SortKeys(node));

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, SortKeys(p.Value)))),
        JsonArray array => new JsonArray(array.Select(SortKeys).ToArray()),
        _ => node.DeepClone(),
    };
}
