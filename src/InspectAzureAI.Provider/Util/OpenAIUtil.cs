using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Port of the helpers in <c>src/inspect_ai/model/_openai.py</c> that the azureai provider imports.</summary>
public static partial class OpenAIUtil
{
    /// <summary>Sentinel replacing base64 payloads in recorded calls (<c>BASE_64_DATA_REMOVED</c>, <c>constants.py</c>).</summary>
    public const string Base64DataRemoved = "<base64-data-removed>";

    [GeneratedRegex(@"^o\d+")]
    private static partial Regex OSeriesPrefix();

    [GeneratedRegex(@"o\d+")]
    private static partial Regex OSeriesAnywhere();

    /// <summary>Port of <c>is_gpt_5_model</c>.</summary>
    public static bool IsGpt5Model(string modelName) => modelName.ToLowerInvariant().Contains("gpt-5");

    /// <summary>Port of <c>is_o_series_model</c>.</summary>
    public static bool IsOSeriesModel(string modelName)
    {
        var name = modelName.ToLowerInvariant();
        if (OSeriesPrefix().IsMatch(name))
        {
            return true;
        }

        return !name.Contains("gpt") && OSeriesAnywhere().IsMatch(name);
    }

    /// <summary>Port of <c>needs_max_completion_tokens</c>.</summary>
    public static bool NeedsMaxCompletionTokens(string modelName) => IsGpt5Model(modelName) || IsOSeriesModel(modelName);

    [GeneratedRegex(@"^gpt-(\d+)(?:\.(\d+))?")]
    private static partial Regex GptVersion();

    /// <summary>Port of <c>is_gpt_5_plus_model</c>: a gpt-5.x name (gpt-5.1, gpt-5.4-mini, gpt-5.6-sol, ...).</summary>
    public static bool IsGpt5Plus(string modelName) => modelName.ToLowerInvariant().Contains("gpt-5.");

    /// <summary>Port of <c>supports_native_max_reasoning_effort</c>: gpt-5.6 and later take <c>reasoning.effort: max</c> verbatim.</summary>
    public static bool SupportsMaxReasoningEffort(string modelName)
    {
        var match = GptVersion().Match(modelName.ToLowerInvariant());
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups[1].Value);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        return major > 5 || (major == 5 && minor >= 6);
    }

    /// <summary>Port of <c>has_reasoning_options</c>: the o-series, gpt-5 except the <c>-chat</c> variants, and codex.</summary>
    public static bool HasReasoningOptions(string modelName)
    {
        var name = modelName.ToLowerInvariant();
        return IsOSeriesModel(name) || (IsGpt5Model(name) && !name.Contains("-chat")) || name.Contains("codex");
    }

    /// <summary>
    /// Whether a deployment name should default to the Responses route: Foundry serves the gpt-5.6 family, the
    /// <c>-pro</c> models (<c>chatCompletion: false</c> in ARM), codex and the o-series there; chat completions
    /// refuses them function tools with reasoning, or everything. An explicit route always wins.
    /// </summary>
    public static bool PrefersResponsesRoute(string modelName)
    {
        var name = modelName.ToLowerInvariant();
        return name.Contains("gpt-5.6") || name.Contains("-pro") || name.Contains("codex") || OSeriesPrefix().IsMatch(name);
    }

    /// <summary>
    /// Port of <c>openai_stop_details</c> over the raw JSON of a choice: every
    /// <c>content_filter_results</c> entry with <c>filtered: true</c> becomes a category (detected-only
    /// entries are ignored). Python reads <c>message.refusal</c> with <c>getattr</c>, and the
    /// azure.ai.inference <c>ChatResponseMessage</c> is dict-backed with no <c>refusal</c> field, so for
    /// this provider the explanation is always <c>None</c> — a refusal without filtered categories
    /// yields no stop details, which the port reproduces by not reading the raw <c>refusal</c> key.
    /// </summary>
    public static StopDetails? OpenAIStopDetails(JsonObject choice)
    {
        string? explanation = null;
        var categories = new List<StopCategory>();
        if (choice["content_filter_results"] is JsonObject filterResults)
        {
            foreach (var (name, info) in filterResults)
            {
                if (info is JsonObject infoObj && PythonSemantics.Truthy(infoObj["filtered"]))
                {
                    var level = infoObj["severity"];
                    categories.Add(new StopCategory(name, level is null ? null : level.ToString()));
                }
            }
        }

        if (categories.Count == 0 && string.IsNullOrEmpty(explanation))
        {
            return null;
        }

        var finishReason = choice["finish_reason"]?.GetValue<string>();
        return new StopDetails
        {
            Type = finishReason == "content_filter" ? "content_filter" : "refusal",
            Explanation = explanation,
            Categories = categories,
        };
    }

    /// <summary>
    /// Port of <c>openai_media_filter</c>: redacts <c>data:</c> URLs under <c>image_url.url</c>,
    /// <c>input_audio.data</c> and image outputs in recorded requests/responses.
    /// </summary>
    public static JsonNode? OpenAIMediaFilter(string? key, JsonNode? value)
    {
        if (key == "output" && value is JsonObject output && output.ContainsKey("image_url"))
        {
            var copy = output.DeepClone().AsObject();
            copy["image_url"] = Base64DataRemoved;
            return copy;
        }

        if (key == "output" && value is JsonArray outputs)
        {
            var copy = outputs.DeepClone().AsArray();
            foreach (var item in copy)
            {
                if (item is JsonObject obj && obj.ContainsKey("image_url"))
                {
                    obj["image_url"] = Base64DataRemoved;
                }
            }

            return copy;
        }

        if (key == "image_url" && value is JsonObject imageUrl && imageUrl.ContainsKey("url"))
        {
            var url = imageUrl["url"]?.ToString() ?? "";
            if (url.StartsWith("data:", StringComparison.Ordinal))
            {
                var copy = imageUrl.DeepClone().AsObject();
                copy["url"] = Base64DataRemoved;
                return copy;
            }
        }
        else if (key == "input_audio" && value is JsonObject audio && audio.ContainsKey("data"))
        {
            var copy = audio.DeepClone().AsObject();
            copy["data"] = Base64DataRemoved;
            return copy;
        }

        return value;
    }
}
