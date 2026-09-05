using System.Globalization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of the <c>Compact</c> protocol of <c>model/_compaction/types.py</c>: the handler an agent loop calls around
/// each generate. Call <see cref="CompactInputAsync"/> with the full history before sending input to the model,
/// send the returned input and append the supplemental message (if any) to the full history; call
/// <see cref="RecordOutputAsync"/> after each generate to calibrate token estimation.
/// </summary>
public interface ICompact
{
    /// <summary>
    /// Compacts messages for input to the model. With <paramref name="force"/> the threshold gate is skipped
    /// (used by overflow recovery after a <c>model_length</c> stop).
    /// </summary>
    Task<CompactionResult> CompactInputAsync(IReadOnlyList<ChatMessage> messages, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the output of a generate call, calibrating the token estimate against the actual input token
    /// count in <c>output.usage</c> (which captures tool definitions, system messages and other API overhead
    /// per-message counting cannot). <paramref name="input"/> must be the messages passed to generate.
    /// </summary>
    Task RecordOutputAsync(IReadOnlyList<ChatMessage> input, ModelOutput output, CancellationToken cancellationToken = default);
}

/// <summary>
/// The seam through which an agent loop obtains its <see cref="ICompact"/>: given the loop's starting messages,
/// its tools and its model, returns the handler the loop then calls around every generate. Port of
/// <c>_agent_compact</c> in <c>agent/_react.py</c>; <see cref="Compaction.Hook"/> builds one from a strategy.
/// </summary>
public delegate ICompact CompactionHook(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools, Model model);

/// <summary>
/// Port of <c>model/_compaction/_compaction.py</c>: the orchestrator that decides when a strategy runs
/// (token-threshold triggering, calibrated by generate usage), iterates the strategy until the compacted input
/// fits, preserves the prefix, collapses the result for the api, records a <see cref="CompactionEvent"/> and
/// issues the memory warning when the memory tool is available.
/// </summary>
public static class Compaction
{
    /// <summary>Port of <c>MAX_ITERATIONS</c> in <c>_perform_compaction</c>: extra passes allowed to get under the threshold.</summary>
    public const int MaxIterations = 3;

    /// <summary>Port of <c>REDACTED_REASONING_TOKENS_METADATA_KEY</c>: per-message redacted reasoning token cost stamped by a provider.</summary>
    public const string RedactedReasoningTokensMetadataKey = "redacted_reasoning_tokens";

    /// <summary>
    /// Port of <c>compaction()</c>: creates a conversation compaction handler.
    /// </summary>
    /// <param name="strategy">Compaction strategy (editing, trimming, summary, ...).</param>
    /// <param name="prefix">Chat messages to always preserve in compacted conversations (snapshotted).</param>
    /// <param name="tools">Tool definitions (included in the token count as they consume context).</param>
    /// <param name="model">Target model for compacted input (defaults to the active model of the sample context).</param>
    public static ICompact Create(ICompactionStrategy strategy, IReadOnlyList<ChatMessage> prefix, IReadOnlyList<ToolInfo>? tools = null, Model? model = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(prefix);
        var targetModel = model ?? SampleContext.Require().ActiveModel;
        return new CompactionHandler(strategy, prefix, tools ?? [], targetModel);
    }

    /// <summary>
    /// Port of <c>_agent_compact</c>: a <see cref="CompactionHook"/> that derives the always-preserve prefix (system
    /// messages plus sample input) from the loop's starting messages rather than treating them all as prefix.
    /// </summary>
    public static CompactionHook Hook(ICompactionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        return (messages, tools, model) =>
        {
            var partitioned = TrimMessages.Partition(messages);
            return Create(strategy, [.. partitioned.System, .. partitioned.Input], tools, model);
        };
    }

    /// <summary>
    /// Port of the forced-compaction branch of <c>_handle_overflow</c> in <c>agent/_react.py</c>. After a
    /// <c>model_length</c> stop the loop drops the failed assistant turn and passes the rest here; on success the
    /// returned list is the new conversation (a distinct supplemental message is appended to it). Returns null,
    /// with a logged warning, when compaction fails so the caller can fall back to its overflow policy.
    /// </summary>
    public static async Task<IReadOnlyList<ChatMessage>?> TryRecoverOverflowAsync(ICompact compact, IReadOnlyList<ChatMessage> previousMessages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compact);
        ArgumentNullException.ThrowIfNull(previousMessages);
        try
        {
            var (compacted, message) = await compact.CompactInputAsync(previousMessages, force: true, cancellationToken).ConfigureAwait(false);
            var messages = compacted.ToList();
            // CompactionSummary returns its summary as compacted[^1] and as the message (same object)
            if (message is not null && (messages.Count == 0 || !ReferenceEquals(messages[^1], message)))
            {
                messages.Add(message);
            }

            return messages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLogger.Warning($"Forced compaction failed during overflow recovery: {ex.Message}; falling back to overflow filter.");
            return null;
        }
    }

    /// <summary>
    /// Port of <c>_resolve_threshold</c>: an absolute threshold as-is; a fraction of the model's context window,
    /// assuming <see cref="ModelCompactionExtensions.DefaultContextWindow"/> (with a warning) when unknown.
    /// </summary>
    public static int ResolveThreshold(Model model, CompactionThreshold threshold)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (threshold.Tokens is { } tokens)
        {
            return tokens;
        }

        var contextWindow = model.ContextWindow();
        if (contextWindow is null)
        {
            ProviderLogger.Warning($"Unable to determine context window for {model.Name} (falling back to default of {ModelCompactionExtensions.DefaultContextWindow})");
            contextWindow = ModelCompactionExtensions.DefaultContextWindow;
        }

        return threshold.Resolve(contextWindow.Value);
    }

    /// <summary>
    /// Port of <c>_redacted_reasoning_tokens_total</c>: the summed <see cref="RedactedReasoningTokensMetadataKey"/>
    /// of assistant messages that still carry redacted reasoning, when the provider declares that its
    /// <c>usage.input_tokens</c> omits that content; otherwise 0. The .NET config has no <c>reasoning_history</c>
    /// mode, so the Python <c>"all"</c> accounting applies.
    /// </summary>
    public static int RedactedReasoningTokensTotal(IReadOnlyList<ChatMessage> messages, Model model)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(model);
        if (!model.ApplyRedactedReasoningTokensToInput())
        {
            return 0;
        }

        var total = 0;
        foreach (var message in messages.OfType<ChatMessageAssistant>())
        {
            if (!HasRedactedReasoning(message))
            {
                continue;
            }

            if (message.Metadata is { } metadata && metadata.TryGetValue(RedactedReasoningTokensMetadataKey, out var value) && value is not null)
            {
                total += Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        return total;
    }

    private static bool HasRedactedReasoning(ChatMessageAssistant message) =>
        !message.Content.IsString && message.Content.Items!.Any(c => c is ContentReasoning { Redacted: true });

    /// <summary>
    /// Port of <c>_perform_compaction</c>: runs the strategy, re-running it up to <see cref="MaxIterations"/> more
    /// times while the compacted input (with tool definitions and hidden reasoning) still exceeds the threshold
    /// and progress is being made.
    /// </summary>
    /// <exception cref="InvalidOperationException">Compaction cannot reduce tokens below the threshold (Python raises <c>RuntimeError</c>).</exception>
    internal static async Task<CompactionResult> PerformCompactionAsync(
        ICompactionStrategy strategy,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        Model model,
        int threshold,
        int toolTokens,
        int prefixTokens,
        CancellationToken cancellationToken)
    {
        var result = await strategy.CompactAsync(model, messages, tools, cancellationToken).ConfigureAwait(false);
        var compactedTokens = await model.CountTokensAsync(result.Input, cancellationToken).ConfigureAwait(false);
        var hiddenTokens = RedactedReasoningTokensTotal(result.Input, model);
        var totalCompacted = toolTokens + compactedTokens + hiddenTokens;

        for (var i = 0; i < MaxIterations; i++)
        {
            if (totalCompacted <= threshold)
            {
                break;
            }

            var previousTotal = totalCompacted;
            result = await strategy.CompactAsync(model, result.Input.ToList(), tools, cancellationToken).ConfigureAwait(false);
            compactedTokens = await model.CountTokensAsync(result.Input, cancellationToken).ConfigureAwait(false);
            hiddenTokens = RedactedReasoningTokensTotal(result.Input, model);
            totalCompacted = toolTokens + compactedTokens + hiddenTokens;

            if (totalCompacted >= previousTotal)
            {
                break;
            }
        }

        if (totalCompacted > threshold)
        {
            throw new InvalidOperationException(
                $"Compaction insufficient: {Format(totalCompacted)} tokens "
                + $"still exceeds threshold of {Format(threshold)} "
                + $"(tools: {Format(toolTokens)}, prefix: {Format(prefixTokens)}, "
                + $"messages: {Format(compactedTokens)}, "
                + $"hidden_reasoning: {Format(hiddenTokens)}). "
                + "Consider using a lower compaction threshold to accommodate "
                + "tool definitions and prefix.");
        }

        return result;
    }

    private static string Format(int tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Port of the <c>_CompactHandler</c> closure: the per-conversation state of <c>compaction()</c>.</summary>
    private sealed class CompactionHandler : ICompact
    {
        private readonly ICompactionStrategy _strategy;

        private readonly IReadOnlyList<ChatMessage> _prefix;

        private readonly IReadOnlyList<ToolInfo> _tools;

        private readonly Model _model;

        // serializes state mutation when one handler is shared across concurrent callers (e.g. via a bridge)
        private readonly SemaphoreSlim _lock = new(1, 1);

        // port of _CompactionState
        private readonly List<ChatMessage> _compactedInput = [];

        private readonly HashSet<string> _processedMessageIds = new(StringComparer.Ordinal);

        private int? _baselineTokens;

        private HashSet<string> _baselineMessageIds = new(StringComparer.Ordinal);

        private bool _memoryWarningIssued;

        // resolved on first use: a fractional threshold depends on the context window, which some providers only learn on their first generate
        private int? _threshold;

        private int _memoryWarningThreshold;

        private int? _toolTokens;

        private int? _prefixTokens;

        public CompactionHandler(ICompactionStrategy strategy, IReadOnlyList<ChatMessage> prefix, IReadOnlyList<ToolInfo> tools, Model model)
        {
            _strategy = strategy;
            _prefix = prefix.ToList();
            _tools = tools;
            _model = model;
        }

        public async Task RecordOutputAsync(IReadOnlyList<ChatMessage> input, ModelOutput output, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            if (output.Usage is not { } usage)
            {
                return;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var inputTokens = usage.InputTokens + (usage.InputTokensCacheRead ?? 0) + (usage.InputTokensCacheWrite ?? 0);
                _baselineTokens = inputTokens;
                _baselineMessageIds = input.Select(MessageId).ToHashSet(StringComparer.Ordinal);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<CompactionResult> CompactInputAsync(IReadOnlyList<ChatMessage> messages, bool force = false, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_threshold is null)
                {
                    _threshold = ResolveThreshold(_model, _strategy.Threshold);
                    _memoryWarningThreshold = (int)(0.9 * _threshold.Value);
                }

                _toolTokens ??= await _model.CountToolTokensAsync(_tools, cancellationToken).ConfigureAwait(false);
                _prefixTokens ??= _prefix.Count > 0 ? await _model.CountTokensAsync(_prefix, cancellationToken).ConfigureAwait(false) : 0;

                var threshold = _threshold.Value;
                var toolTokens = _toolTokens.Value;

                // unprocessed messages accumulate in the input until the threshold is reached
                var unprocessed = messages.Where(m => !_processedMessageIds.Contains(MessageId(m))).ToList();
                var targetMessages = _compactedInput.Concat(unprocessed).ToList();
                var targetMessageIds = targetMessages.Select(MessageId).ToHashSet(StringComparer.Ordinal);
                var hiddenReasoningTokens = RedactedReasoningTokensTotal(targetMessages, _model);

                int totalTokens;
                if (_baselineTokens is { } baseline && _baselineMessageIds.IsSubsetOf(targetMessageIds))
                {
                    // the baseline already includes tools, system messages and API overhead: count only new messages
                    var newSinceBaseline = targetMessages.Where(m => !_baselineMessageIds.Contains(MessageId(m))).ToList();
                    var newTokens = newSinceBaseline.Count > 0 ? await _model.CountTokensAsync(newSinceBaseline, cancellationToken).ConfigureAwait(false) : 0;
                    totalTokens = baseline + newTokens + hiddenReasoningTokens;
                }
                else
                {
                    var messageTokens = await _model.CountTokensAsync(targetMessages, cancellationToken).ConfigureAwait(false);
                    totalTokens = toolTokens + messageTokens + hiddenReasoningTokens;
                }

                if (force || totalTokens > threshold)
                {
                    var (compactedInput, compactedMessage) = await PerformCompactionAsync(
                        _strategy, targetMessages, _tools, _model, threshold, toolTokens, _prefixTokens.Value, cancellationToken).ConfigureAwait(false);

                    foreach (var message in _compactedInput.Concat(unprocessed))
                    {
                        _processedMessageIds.Add(MessageId(message));
                    }

                    if (compactedMessage is not null)
                    {
                        _processedMessageIds.Add(MessageId(compactedMessage));
                    }

                    IEnumerable<ChatMessage> prependPrefix;
                    if (_strategy.PreservePrefix)
                    {
                        var inputIds = compactedInput.Select(MessageId).ToHashSet(StringComparer.Ordinal);
                        prependPrefix = _prefix.Where(m => !inputIds.Contains(MessageId(m)));
                    }
                    else
                    {
                        prependPrefix = _prefix.Where(m => m is ChatMessageSystem);
                    }

                    var preCollapseInput = prependPrefix.Concat(compactedInput).ToList();
                    var messageWasInInput = compactedMessage is not null && preCollapseInput.Any(m => ReferenceEquals(m, compactedMessage));
                    var collapsed = CollapseForApi(preCollapseInput);
                    if (messageWasInInput && !collapsed.Any(m => ReferenceEquals(m, compactedMessage)))
                    {
                        compactedMessage = null;
                    }

                    _compactedInput.Clear();
                    _compactedInput.AddRange(collapsed);

                    var compactedTokens = await _model.CountTokensAsync(_compactedInput, cancellationToken).ConfigureAwait(false);
                    var compactedHidden = RedactedReasoningTokensTotal(_compactedInput, _model);
                    SampleContext.Current?.Transcript.Add(new CompactionEvent
                    {
                        Type = _strategy.Type,
                        Role = null,
                        Source = "inspect",
                        TokensBefore = totalTokens,
                        TokensAfter = compactedTokens + compactedHidden,
                        Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["strategy"] = _strategy.GetType().Name,
                            ["messages_before"] = targetMessages.Count,
                            ["messages_after"] = _compactedInput.Count,
                            ["trigger"] = force ? "forced" : "threshold",
                        },
                    });

                    _memoryWarningIssued = false;
                    _baselineTokens = null;
                    _baselineMessageIds = new HashSet<string>(StringComparer.Ordinal);

                    return new CompactionResult(_compactedInput.ToList(), compactedMessage);
                }

                foreach (var message in unprocessed)
                {
                    _processedMessageIds.Add(MessageId(message));
                }

                _compactedInput.AddRange(unprocessed);

                if (_strategy.Memory
                    && _tools.Any(t => t.Name == CompactionMemory.MemoryTool)
                    && totalTokens > _memoryWarningThreshold
                    && !_memoryWarningIssued)
                {
                    var memoryMessage = CompactionMemory.MemoryWarningMessage();
                    _compactedInput.Add(memoryMessage);
                    _processedMessageIds.Add(MessageId(memoryMessage));
                    _memoryWarningIssued = true;
                }

                return new CompactionResult(_compactedInput.ToList(), null);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Port of <c>collapse_consecutive_messages_for_api</c>: the .NET <see cref="Model"/> collapses consecutive
        /// user messages for apis that require it (the only collapse the port's apis declare).
        /// </summary>
        private IReadOnlyList<ChatMessage> CollapseForApi(IReadOnlyList<ChatMessage> messages) =>
            ModelApiHooks.CollapseUserMessages(_model.Api) ? Model.CollapseUserMessages(messages) : messages;

        private static string MessageId(ChatMessage message) =>
            message.Id ?? throw new InvalidOperationException("Message must have an ID");
    }
}
