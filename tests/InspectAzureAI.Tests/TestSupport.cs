using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InspectAzureAI.Tests;

/// <summary>Sets environment variables for the duration of a test and restores them afterwards.</summary>
internal sealed class EnvScope : IDisposable
{
    private static readonly string[] Vars =
    [
        AzureAIModelApi.AzureApiKeyVar, AzureAIModelApi.AzureAIApiKeyVar, AzureAIModelApi.AzureEndpointUrlVar,
        AzureAIModelApi.AzureAIEndpointUrlVar, AzureAIModelApi.AzureAIBaseUrlVar, "INSPECT_EVAL_MODEL_BASE_URL",
        AzureAIModelApi.AzureAIAudienceVar, AzureHosting.AzureAICredential, AzureHosting.AzureTenantId, AzureHosting.AzureClientId,
    ];

    private readonly Dictionary<string, string?> _saved = new();

    private EnvScope()
    {
        foreach (var name in Vars)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Clears every provider env var (restored on dispose).</summary>
    public static EnvScope Clean() => new();

    public EnvScope Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

/// <summary>A token credential returning a fixed token (stands in for DefaultAzureCredential).</summary>
internal sealed class FakeTokenCredential(string token) : TokenCredential
{
    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Scopes.Add(requestContext.Scopes);
        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}

internal static class Fixtures
{
    public const string BaseUrl = "https://example.com/models";

    public static readonly ToolInfo WeatherTool = new("get_weather", "Get weather.")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["city"] = new() { Type = ["string"], Description = "City", MinLength = 1 },
            },
            Required = ["city"],
        },
    };

    public static readonly ToolInfo TestingTool = new("testing_tool", "A tool")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["param1"] = ToolParam.Of("string") },
            Required = ["param1"],
        },
    };

    public static readonly ToolInfo TestingToolBool = new("testing_tool", "A tool")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["param1"] = ToolParam.Of("boolean") },
            Required = ["param1"],
        },
    };

    public static AzureAIModelApi Api(
        string modelName = "test-model",
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        CannedTransport? transport = null,
        string? apiKey = "test",
        int sdkRetries = 0) =>
        new(modelName, BaseUrl, apiKey, streaming: streaming, modelArgs: modelArgs,
            settings: new AzureAIClientSettings
            {
                Transport = transport,
                ConfigureClientOptions = o =>
                {
                    o.Retry.MaxRetries = sdkRetries;
                    o.Retry.Delay = TimeSpan.Zero;
                    o.Retry.MaxDelay = TimeSpan.Zero;
                },
            });

    public static string Completion(string content, string finishReason = "stop", string? extraChoiceJson = null, string model = "test-model") =>
        "{\"id\":\"cmpl-1\",\"created\":123,\"model\":\"" + model + "\",\"choices\":[{\"index\":0,\"finish_reason\":\"" + finishReason
        + "\",\"message\":{\"role\":\"assistant\",\"content\":" + JsonValue.Create(content)!.ToJsonString() + "}"
        + (extraChoiceJson is null ? "" : "," + extraChoiceJson)
        + "}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":7,\"total_tokens\":10}}";

    public static string ToolCallCompletion(string id, string name, string arguments) =>
        "{\"id\":\"cmpl-2\",\"created\":123,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\""
        + id + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":" + JsonValue.Create(arguments)!.ToJsonString()
        + "}}]}}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":9,\"total_tokens\":14}}";

    /// <summary>Builds a streaming update like the Python <c>_update()</c> helper.</summary>
    public static JsonObject Update(string payloadJson)
    {
        var update = new JsonObject { ["id"] = "cmpl-1", ["created"] = 123, ["model"] = "test-model" };
        foreach (var (key, value) in JsonNode.Parse(payloadJson)!.AsObject())
        {
            update[key] = value?.DeepClone();
        }

        return update;
    }

    public static async IAsyncEnumerable<JsonObject> Updates(IEnumerable<JsonObject> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    public static RequestFailedException Http(int status, string message, IReadOnlyDictionary<string, string>? headers = null) =>
        new(CannedResponse.Error(status, message, headers));
}

internal sealed class StreamCollector
{
    public List<StreamEvent> Events { get; } = [];

    public Task Collect(StreamEvent streamEvent)
    {
        Events.Add(streamEvent);
        return Task.CompletedTask;
    }
}
