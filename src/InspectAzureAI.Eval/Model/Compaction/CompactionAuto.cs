using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/auto.py</c> <c>CompactionAuto</c>: automatic compaction that tries provider-native
/// compaction first and falls back to summary compaction for unsupported providers or models. This is the
/// recommended default, as it adapts to the capabilities of the underlying provider.
/// </summary>
public sealed class CompactionAuto : CompactionStrategy
{
    /// <param name="threshold">Token count or fraction of the context window that triggers compaction (default 0.9).</param>
    /// <param name="instructions">Additional instructions to give the model about compaction (e.g. "Focus on preserving code snippets, variable names, and technical decisions.").</param>
    /// <param name="memory">
    /// Whether to warn the model to save critical content to memory before compaction. Null (Python
    /// <c>"auto"</c>, the default) enables warnings for all compaction paths.
    /// </param>
    public CompactionAuto(CompactionThreshold threshold = default, string? instructions = null, bool? memory = null)
        : base("summary", threshold, false)
    {
        Instructions = instructions;
        MemorySetting = memory;

        // determine memory settings for each strategy
        var nativeMemory = memory ?? false;
        var summaryMemory = memory ?? true;

        Native = new CompactionNative(threshold, instructions, nativeMemory) { SuggestAuto = false };
        Summary = new CompactionSummary(threshold, summaryMemory, instructions: instructions);
    }

    /// <summary>Additional instructions forwarded to both strategies.</summary>
    public string? Instructions { get; }

    /// <summary>The <c>memory</c> argument as given; null is Python's <c>"auto"</c>.</summary>
    public bool? MemorySetting { get; }

    /// <summary>The native strategy tried first.</summary>
    public CompactionNative Native { get; }

    /// <summary>The summary strategy used as the fallback.</summary>
    public CompactionSummary Summary { get; }

    /// <summary>Whether to warn the model to save content to memory before compaction (true for <c>"auto"</c>).</summary>
    public override bool Memory => MemorySetting ?? true;

    public override IReadOnlyDictionary<string, object?> ReprParams()
    {
        var parameters = new Dictionary<string, object?>(base.ReprParams(), StringComparer.Ordinal)
        {
            ["instructions"] = Instructions,
            ["memory"] = MemorySetting is { } memory ? memory : "auto",
        };
        return parameters;
    }

    /// <summary>
    /// Attempts native compaction; falls back to summary compaction when the provider does not support it
    /// (<see cref="NotSupportedException"/>), and also, with a logged warning, when native compaction fails for
    /// any other reason. Cancellation propagates.
    /// </summary>
    public override async Task<CompactionResult> CompactAsync(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        try
        {
            return await Native.CompactAsync(model, messages, tools, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return await Summary.CompactAsync(model, messages, tools, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Native compaction failed: {ex.Message}. Falling back to summary compaction.");
            return await Summary.CompactAsync(model, messages, tools, cancellationToken).ConfigureAwait(false);
        }
    }
}
