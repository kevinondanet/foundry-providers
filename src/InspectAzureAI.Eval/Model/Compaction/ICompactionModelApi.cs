using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>Port of the <c>tuple[list[ChatMessage], ModelUsage | None]</c> returned by <c>ModelAPI.compact</c>.</summary>
public sealed record NativeCompactionResult(IReadOnlyList<ChatMessage> Messages, ModelUsage? Usage);

/// <summary>
/// The <c>ModelAPI</c> members compaction consults that <see cref="IModelApi"/> does not declare
/// (<c>count_tokens</c>, <c>compact</c>, <c>compact_reasoning_history</c>,
/// <c>apply_redacted_reasoning_tokens_to_input</c> and the context window from <c>get_model_input_tokens</c>).
/// An api opts in by implementing this interface alongside <see cref="IModelApi"/>; every other api, the Azure
/// providers included, gets the Python defaults: heuristic counting, an unknown context window, reasoning
/// history eligible for compaction, and no native compaction.
/// </summary>
public interface ICompactionModelApi
{
    /// <summary>Input token capacity of the model, or null when unknown.</summary>
    int? ContextWindow => null;

    /// <summary>Port of <c>compact_reasoning_history()</c>: whether reasoning blocks may be cleared by <see cref="CompactionEdit"/>.</summary>
    bool CompactReasoningHistory => true;

    /// <summary>
    /// Port of <c>apply_redacted_reasoning_tokens_to_input()</c>: whether the provider's <c>usage.input_tokens</c>
    /// omits redacted reasoning content, so the per-message <c>redacted_reasoning_tokens</c> metadata must be
    /// added back when estimating context use.
    /// </summary>
    bool ApplyRedactedReasoningTokensToInput => false;

    /// <summary>Port of <c>count_tokens</c>; the default is the <see cref="TokenEstimator"/> heuristic.</summary>
    Task<int> CountTokensAsync(IReadOnlyList<ChatMessage> input, CancellationToken cancellationToken = default) =>
        Task.FromResult(TokenEstimator.CountTokens(input));

    /// <summary>Port of <c>ModelAPI.compact</c>: provider-native compaction.</summary>
    /// <exception cref="NotSupportedException">The provider has no native compaction (Python raises <c>NotImplementedError</c>).</exception>
    Task<NativeCompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        GenerateConfig config,
        string? instructions,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support native compaction.");
}

/// <summary>
/// Port of the <c>set_model_info(context_length=...)</c> registry consulted by <c>get_model_input_tokens</c>: an
/// explicit context window registered for a model name wins over whatever the api reports.
/// </summary>
public static class CompactionModelInfo
{
    private static readonly ConcurrentDictionary<string, int> ContextWindows = new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) the input token capacity of <paramref name="modelName"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputTokens"/> is not positive.</exception>
    public static void SetContextWindow(string modelName, int inputTokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputTokens);
        ContextWindows[modelName] = inputTokens;
    }

    /// <summary>The registered capacity of <paramref name="modelName"/>, or null.</summary>
    public static int? GetContextWindow(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return ContextWindows.TryGetValue(modelName, out var tokens) ? tokens : null;
    }

    /// <summary>Removes a registration; returns whether one existed.</summary>
    public static bool RemoveContextWindow(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return ContextWindows.TryRemove(modelName, out _);
    }
}

/// <summary>
/// The compaction-facing members of Python's <c>Model</c> wrapper (<c>count_tokens</c>, <c>count_tool_tokens</c>,
/// <c>compact</c>) and <c>get_model_input_tokens</c>, as extensions over <see cref="Model"/> that dispatch to an
/// <see cref="ICompactionModelApi"/> when the api implements it.
/// </summary>
public static class ModelCompactionExtensions
{
    /// <summary>Port of <c>DEFAULT_CONTEXT_WINDOW</c>: assumed when a fractional threshold meets an unknown window.</summary>
    public const int DefaultContextWindow = 128_000;

    /// <summary>
    /// Port of <c>get_model_input_tokens</c>: an explicit <see cref="CompactionModelInfo"/> registration, else the
    /// api's <see cref="ICompactionModelApi.ContextWindow"/>, else <see cref="DefaultContextWindow"/> for the
    /// <see cref="ScriptedModelApi"/> (Python's <c>mockllm</c>), else null.
    /// </summary>
    public static int? ContextWindow(this Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (CompactionModelInfo.GetContextWindow(model.Name) is { } registered)
        {
            return registered;
        }

        if (model.Api is ICompactionModelApi api && api.ContextWindow is { } window)
        {
            return window;
        }

        return model.Api is ScriptedModelApi ? DefaultContextWindow : null;
    }

    /// <summary>
    /// Port of <c>Model.count_tool_tokens</c>: the tool definitions rendered as their OpenAI function JSON,
    /// concatenated into one user message and counted.
    /// </summary>
    public static Task<int> CountToolTokensAsync(this Model model, IReadOnlyList<ToolInfo> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        var toolJson = new StringBuilder();
        foreach (var tool in tools)
        {
            var function = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters.ToJson(),
                },
            };
            toolJson.Append(PythonJson.Dumps(function));
        }

        return model.CountTokensAsync([new ChatMessageUser(toolJson.ToString())], cancellationToken);
    }

    /// <summary>Port of <c>model.api.compact_reasoning_history()</c> (true unless the api opts out).</summary>
    public static bool CompactReasoningHistory(this Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Api is not ICompactionModelApi api || api.CompactReasoningHistory;
    }

    /// <summary>Port of <c>model.api.apply_redacted_reasoning_tokens_to_input()</c> (false unless the api opts in).</summary>
    public static bool ApplyRedactedReasoningTokensToInput(this Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Api is ICompactionModelApi api && api.ApplyRedactedReasoningTokensToInput;
    }

    /// <summary>
    /// Port of <c>Model.compact</c>: provider-native compaction with the same <c>max_tokens</c> defaulting as
    /// generate, recording the usage against the sample limits.
    /// </summary>
    /// <exception cref="NotSupportedException">The api has no native compaction (the Azure providers, and any api that is not an <see cref="ICompactionModelApi"/>).</exception>
    public static async Task<NativeCompactionResult> CompactAsync(
        this Model model,
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        string? instructions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tools);
        if (model.Api is not ICompactionModelApi api)
        {
            throw new NotSupportedException($"{model.Api.GetType().Name} does not support native compaction.");
        }

        var config = model.Config;
        if (config.MaxTokens is null)
        {
            config = config with { MaxTokens = model.Api.MaxTokens() };
        }

        var result = await api.CompactAsync(input, tools, config, instructions, cancellationToken).ConfigureAwait(false);
        if (result.Usage is { } usage)
        {
            SampleContext.Current?.Limits.AddUsage(usage, model.Name);
        }

        return result;
    }
}
