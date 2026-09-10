using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.OpenAI;

internal sealed class ResponsesProtocol(string DeploymentName, IReadOnlyDictionary<string, object?> ModelArgs)
{
    private bool IsReasoningModel => OpenAIUtil.HasReasoningOptions(DeploymentName);
    private static readonly string[] ExtraBodyFields =
    [
        "store", "include", "service_tier", "max_tool_calls", "metadata", "previous_response_id",
        "prompt_cache_key", "prompt_cache_retention", "safety_identifier", "truncation", "background",
    ];

    private const string EncryptedReasoningInclude = OpenAIResponsesModelApi.EncryptedReasoningInclude;
    private const string TemperatureIgnoredWarning = OpenAIResponsesModelApi.TemperatureIgnoredWarning;
    private const string TopPIgnoredWarning = OpenAIResponsesModelApi.TopPIgnoredWarning;
    private const string FallbackModelsIgnoredWarning = OpenAIResponsesModelApi.FallbackModelsIgnoredWarning;
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolChoice);
        ArgumentNullException.ThrowIfNull(config);
        var request = new JsonObject
        {
            ["model"] = DeploymentName,
            ["input"] = ResponsesInput.InputItems(input),
        };

        if (tools.Count > 0)
        {
            request["tools"] = ResponsesTools.ToolParams(tools);
            if (ResponsesTools.ToolChoiceParam(toolChoice) is { } choice)
            {
                request["tool_choice"] = choice;
            }

            if (config.ParallelToolCalls is not null && !OpenAIUtil.IsOSeriesModel(DeploymentName))
            {
                request["parallel_tool_calls"] = config.ParallelToolCalls;
            }
        }

        var store = IsTrue(config.ExtraBody?["store"]) || (ModelArgs.TryGetValue("store", out var storeArg) && IsTrue(storeArg));
        request["store"] = store;
        var reasoningRequested = !string.IsNullOrEmpty(config.ReasoningEffort) || !string.IsNullOrEmpty(config.ReasoningMode) || !string.IsNullOrEmpty(config.ReasoningSummary);
        if (!store && (IsReasoningModel || reasoningRequested))
        {
            request["include"] = new JsonArray(EncryptedReasoningInclude);
        }

        if (config.MaxTokens is not null) request["max_output_tokens"] = config.MaxTokens;

        var reasoningOn = ReasoningEnabled(DeploymentName, config);
        if (config.Temperature is not null)
        {
            if (reasoningOn) ProviderLogger.WarnOnce(TemperatureIgnoredWarning);
            else request["temperature"] = config.Temperature;
        }

        if (config.TopP is not null)
        {
            if (reasoningOn) ProviderLogger.WarnOnce(TopPIgnoredWarning);
            else request["top_p"] = config.TopP;
        }

        if (config.FrequencyPenalty is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("frequency_penalty"));
        if (config.PresencePenalty is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("presence_penalty"));
        if (config.StopSeqs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("stop_seqs"));
        if (config.Seed is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("seed"));
        if (config.LogitBias is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("logit_bias"));
        if (config.NumChoices is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("num_choices"));
        if (config.Logprobs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("logprobs"));
        if (config.TopLogprobs is not null) ProviderLogger.WarnOnce(UnsupportedParamWarning("top_logprobs"));

        if (ReasoningParams(DeploymentName, config) is { Count: > 0 } reasoning)
        {
            request["reasoning"] = reasoning;
        }

        var text = new JsonObject();
        if (config.ResponseSchema is { } responseSchema)
        {
            text["format"] = ResponsesTools.TextFormat(responseSchema);
        }

        if (config.Verbosity is not null)
        {
            text["verbosity"] = config.Verbosity;
        }

        if (text.Count > 0)
        {
            request["text"] = text;
        }

        if (config.FallbackModels is { Count: > 0 })
        {
            ProviderLogger.WarnOnce(FallbackModelsIgnoredWarning);
        }

        foreach (var field in ExtraBodyFields)
        {
            if (config.ExtraBody?[field] is { } value && !request.ContainsKey(field))
            {
                request[field] = value.DeepClone();
            }
        }

        if (streaming) request["stream"] = true;
        foreach (var (key, value) in ModelArgs)
        {
            request[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        }

        return request;
    }

    /// <summary>
    /// The <c>reasoning</c> object: <c>effort</c> verbatim (<c>max</c> becomes <c>xhigh</c> for models before
    /// gpt-5.6), <c>mode</c> verbatim (<c>pro</c>), and <c>summary</c> only when the config asks for one (and not
    /// <c>none</c>). Empty when no reasoning setting is given. Not gated on the model name: Azure deployment
    /// names are arbitrary, and a deployment that does not reason answers with a clear 400.
    /// </summary>
    public static JsonObject ReasoningParams(string deploymentName, GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var reasoning = new JsonObject();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(effort))
        {
            if (effort == "max" && !OpenAIUtil.SupportsMaxReasoningEffort(deploymentName))
            {
                effort = "xhigh";
            }

            reasoning["effort"] = effort;
        }

        if (!string.IsNullOrEmpty(config.ReasoningMode))
        {
            reasoning["mode"] = config.ReasoningMode;
        }

        var summary = config.ReasoningSummary?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(summary) && summary != "none")
        {
            reasoning["summary"] = summary;
        }

        return reasoning;
    }

    /// <summary>
    /// Port of the "reasoning enabled" test that decides whether sampling parameters are sent: the o-series
    /// always reason; gpt-5 (not gpt-5.x) reasons unless the effort is <c>none</c>; gpt-5.x reasons when an
    /// effort other than <c>none</c> or the <c>pro</c> mode is set; any other name follows the config.
    /// </summary>
    public static bool ReasoningEnabled(string deploymentName, GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var name = deploymentName.ToLowerInvariant();
        var effort = config.ReasoningEffort?.Trim().ToLowerInvariant();
        var explicitReasoning = (!string.IsNullOrEmpty(effort) && effort != "none") || config.ReasoningMode == "pro";
        if (OpenAIUtil.IsOSeriesModel(name))
        {
            return true;
        }

        if (OpenAIUtil.IsGpt5Model(name) && !name.Contains("-chat") && !OpenAIUtil.IsGpt5Plus(name))
        {
            return effort != "none";
        }

        return explicitReasoning;
    }

    public static string UnsupportedParamWarning(string param) =>
        $"OpenAI Responses on Azure: the '{param}' parameter is not supported by the Responses API and was ignored.";

    /// <summary>A boolean-ish <c>store</c> value from <c>extra_body</c> or a model arg (<c>true</c>, <c>"true"</c>, or a JSON true).</summary>
    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        JsonValue json => (json.TryGetValue<bool>(out var jb) && jb) || (json.TryGetValue<string>(out var js) && js.Equals("true", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

}
