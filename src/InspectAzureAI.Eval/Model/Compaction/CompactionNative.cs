using System.Globalization;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/native.py</c> <c>CompactionNative</c>: compaction delegated to the provider's native
/// compaction API. Compaction happens server-side, the compacted representation is opaque and provider-specific,
/// and token savings may be more aggressive while preserving semantic meaning. Neither Azure provider offers
/// native compaction, so with them (and any api that is not an <see cref="ICompactionModelApi"/>) this strategy
/// fails with <see cref="NotSupportedException"/>; use <see cref="CompactionAuto"/> for an automatic fallback to
/// summary compaction.
/// </summary>
public sealed class CompactionNative : CompactionStrategy
{
    /// <param name="threshold">Token count or fraction of the context window that triggers compaction (default 0.9).</param>
    /// <param name="instructions">Additional instructions to give the model about compaction (e.g. "Focus on preserving code snippets, variable names, and technical decisions.").</param>
    /// <param name="memory">Warn the model to save critical content to memory before compaction. Default false.</param>
    public CompactionNative(CompactionThreshold threshold = default, string? instructions = null, bool memory = false)
        : base("summary", threshold, memory)
    {
        Instructions = instructions;
    }

    /// <summary>Additional instructions passed to the provider.</summary>
    public string? Instructions { get; }

    /// <summary>Port of <c>_suggest_auto</c>: whether the not-supported error suggests switching to <see cref="CompactionAuto"/>.</summary>
    internal bool SuggestAuto { get; set; } = true;

    /// <summary>Native compaction preserves only system prefix messages (see <see cref="ICompactionStrategy.PreservePrefix"/>).</summary>
    public override bool PreservePrefix => false;

    public override IReadOnlyDictionary<string, object?> ReprParams()
    {
        var parameters = new Dictionary<string, object?>(base.ReprParams(), StringComparer.Ordinal) { ["instructions"] = Instructions };
        return parameters;
    }

    /// <summary>Compacts through <see cref="ModelCompactionExtensions.CompactAsync"/>; produces no supplemental message.</summary>
    /// <exception cref="NotSupportedException">
    /// The model's provider has no native compaction. The message names the provider, the input token count
    /// (when it could be counted) and, unless created by <see cref="CompactionAuto"/>, the suggestion to use it.
    /// </exception>
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
            var compacted = await model.CompactAsync(messages, tools, Instructions, cancellationToken).ConfigureAwait(false);
            return new CompactionResult(compacted.Messages, null);
        }
        catch (NotSupportedException ex)
        {
            var message = ex.Message;
            try
            {
                var tokenCount = await model.CountTokensAsync(messages, cancellationToken).ConfigureAwait(false);
                message = $"{message} Messages input had {tokenCount.ToString("N0", CultureInfo.InvariantCulture)} tokens.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception countException)
            {
                ProviderLogger.Warning($"Error attempting to count tokens: {countException.Message}");
            }

            if (SuggestAuto)
            {
                message = $"{message} You may want to switch to CompactionAuto for automatic fallback to CompactionSummary when native compaction fails.";
            }

            throw new NotSupportedException(message, ex);
        }
    }
}
