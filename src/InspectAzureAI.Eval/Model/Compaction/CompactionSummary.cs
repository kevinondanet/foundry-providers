using System.Text;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_compaction/summary.py</c> <c>CompactionSummary</c>: conversation summary compaction. Compacts
/// messages by asking a model to summarize the conversation; the summary (a user message tagged with
/// <c>metadata["summary"] = true</c>) replaces everything after the sample input.
/// </summary>
public sealed class CompactionSummary : CompactionStrategy
{
    /// <summary>Port of <c>DEFAULT_SUMMARY_PROMPT</c> (the dedented text, verbatim); <c>{addendums}</c> receives the instructions and memory addendum.</summary>
    public const string DefaultSummaryPrompt =
        "\nYou have been working on the task described above but have not yet completed it. Write a continuation summary that will allow you (or another instance of yourself) to resume work efficiently in a future context window where the conversation history will be replaced with this summary. Your summary should be structured, concise, and actionable. Include:\n"
        + "\n"
        + "- Task Overview\n"
        + "The user's core request and success criteria\n"
        + "Any clarifications or constraints they specified\n"
        + "\n"
        + "- Current State\n"
        + "What has been completed so far\n"
        + "Files created, modified, or analyzed (with paths if relevant)\n"
        + "Key outputs or artifacts produced\n"
        + "\n"
        + "- Important Discoveries\n"
        + "Technical constraints or requirements uncovered\n"
        + "Decisions made and their rationale\n"
        + "Errors encountered and how they were resolved\n"
        + "What approaches were tried that didn't work (and why)\n"
        + "\n"
        + "- Next Steps\n"
        + "Specific actions needed to complete the task\n"
        + "Any blockers or open questions to resolve\n"
        + "Priority order if multiple steps remain\n"
        + "\n"
        + "- Context to Preserve\n"
        + "User preferences or style requirements\n"
        + "Domain-specific details that aren't obvious\n"
        + "Any promises made to the user\n"
        + "{addendums}\n"
        + "\n"
        + "Be concise but complete—err on the side of including information that would prevent duplicate work or repeated mistakes. Write in a way that enables immediate resumption of the task.\n";

    /// <summary>Port of <c>MEMORY_SUMMARY_ADDENDUM</c> (the dedented text, verbatim).</summary>
    public const string MemorySummaryAddendum =
        "\n- Memory Files\n"
        + "List any files you saved to memory during this conversation.\n"
        + "For each file, include the path and a brief description of what\n"
        + "information it contains and when to reference it.\n";

    /// <summary>Port of <c>_MAX_TRUNCATION_ITERATIONS</c>: passes allowed when shrinking oversized tool output to fit the window.</summary>
    public const int MaxTruncationIterations = 12;

    /// <summary>Port of <c>_TRUNCATION_MARKER</c>: left in place of tool output elided to fit the summarization window.</summary>
    public const string TruncationMarker = "\n\n...[tool output truncated for summarization]...\n\n";

    /// <summary>Port of <c>_DEFAULT_OUTPUT_RESERVE</c>: output headroom reserved when the model's <c>max_tokens</c> cannot be determined.</summary>
    public const int DefaultOutputReserve = 4096;

    /// <summary>Port of <c>_CHARS_PER_TOKEN</c>: weighs media against text when choosing what to shrink.</summary>
    public const int CharsPerToken = 4;

    /// <param name="threshold">Token count or fraction of the context window that triggers compaction (default 0.9).</param>
    /// <param name="memory">Warn the model to save critical content to memory before compaction when the memory tool is available.</param>
    /// <param name="model">Model to use for summarization (defaults to the compaction target model).</param>
    /// <param name="instructions">
    /// Additional instructions to give the model about compaction (e.g. "Focus on preserving code snippets,
    /// variable names, and technical decisions."). Inserted into the <paramref name="prompt"/>.
    /// </param>
    /// <param name="prompt">
    /// Prompt to use for summarization (fully replaces the summarization prompt). Include an <c>{addendums}</c>
    /// placeholder to receive the custom <paramref name="instructions"/> and the memory addendum.
    /// </param>
    public CompactionSummary(
        CompactionThreshold threshold = default,
        bool memory = true,
        Model? model = null,
        string? instructions = null,
        string? prompt = null)
        : base("summary", threshold, memory)
    {
        Model = model;
        Instructions = instructions;
        Prompt = prompt ?? DefaultSummaryPrompt;
    }

    /// <summary>Model used for summarization, or null for the compaction target model.</summary>
    public Model? Model { get; }

    /// <summary>Additional instructions inserted into the prompt.</summary>
    public string? Instructions { get; }

    /// <summary>The summarization prompt template.</summary>
    public string Prompt { get; }

    public override IReadOnlyDictionary<string, object?> ReprParams()
    {
        var parameters = new Dictionary<string, object?>(base.ReprParams(), StringComparer.Ordinal)
        {
            ["model"] = Model?.Name,
            ["instructions"] = Instructions,
            ["prompt"] = Prompt,
        };
        return parameters;
    }

    /// <summary>
    /// Summarizes the conversation (from the most recent summary onward, if any) and returns the system messages,
    /// the input messages and the summary as the new input, with the summary as the message to append.
    /// </summary>
    /// <exception cref="InvalidOperationException">The summary generation itself overflowed the model's context window (Python raises <c>RuntimeError</c>).</exception>
    public override async Task<CompactionResult> CompactAsync(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var partitioned = TrimMessages.Partition(messages);

        // an existing summary in the conversation: summarize only from it onward
        var conversationStartIndex = partitioned.Conversation.FindLastIndex(TrimMessages.IsSummaryMessage);
        if (conversationStartIndex < 0)
        {
            conversationStartIndex = 0;
        }

        var addendums = new List<string>();
        if (Instructions is not null)
        {
            addendums.Add(Instructions);
        }

        if (Memory && CompactionMemory.HasMemoryCalls(partitioned.Conversation))
        {
            addendums.Add(MemorySummaryAddendum);
        }

        var prompt = Prompt.Replace("{addendums}", string.Join("\n\n", addendums), StringComparison.Ordinal);
        var summarizationInput = new List<ChatMessage>(partitioned.System);
        summarizationInput.AddRange(partitioned.Input);
        summarizationInput.AddRange(partitioned.Conversation.Skip(conversationStartIndex));
        summarizationInput.Add(new ChatMessageUser(prompt));

        var summarizer = Model ?? model;

        // a long tool output right before compaction can push the summarization input past the window
        summarizationInput = await FitSummarizationInputAsync(summarizer, summarizationInput, cancellationToken).ConfigureAwait(false);

        var output = await summarizer.GenerateAsync(summarizationInput, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (output.StopReason == StopReason.ModelLength)
        {
            throw new InvalidOperationException(
                "Compaction summary generation exceeded the model's context "
                + "window (tool output is truncated automatically to fit, so the "
                + "overflow comes from content that truncation cannot reach). "
                + "Consider lowering the compaction threshold.");
        }

        var summary = new ChatMessageUser(
            "[CONTEXT COMPACTION SUMMARY]\n\n"
            + "The following is a summary of work completed on this task so far:\n\n"
            + "<summary>\n"
            + $"{output.Completion}\n"
            + "</summary>\n\n"
            + "Please continue working on this task from where you left off.")
        {
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { [TrimMessages.SummaryMetadataKey] = true },
        };

        var input = new List<ChatMessage>(partitioned.System);
        input.AddRange(partitioned.Input);
        input.Add(summary);
        return new CompactionResult(input, summary);
    }

    /// <summary>
    /// Port of <c>_fit_summarization_input</c>: shrinks the largest pieces of tool output (middle-truncating text,
    /// replacing media with a placeholder) until the input fits the summarization model's window less its output
    /// reserve. Returns the input unchanged when the window is unknown or the input already fits; edited messages
    /// are replaced with copies so the live transcript is untouched.
    /// </summary>
    internal static async Task<List<ChatMessage>> FitSummarizationInputAsync(Model model, List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var contextWindow = model.ContextWindow();
        if (contextWindow is null)
        {
            return messages;
        }

        var target = contextWindow.Value - OutputReserve(model, contextWindow.Value);
        var tokens = await model.CountTokensAsync(messages, cancellationToken).ConfigureAwait(false);
        if (tokens <= target)
        {
            return messages;
        }

        messages = new List<ChatMessage>(messages);
        for (var i = 0; i < MaxTruncationIterations; i++)
        {
            var candidate = LargestShrinkable(messages);
            if (candidate is null)
            {
                break;
            }

            Shrink(messages, candidate.Value);
            var shrunkTokens = await model.CountTokensAsync(messages, cancellationToken).ConfigureAwait(false);
            if (shrunkTokens >= tokens)
            {
                break;
            }

            tokens = shrunkTokens;
            if (tokens <= target)
            {
                break;
            }
        }

        return messages;
    }

    /// <summary>
    /// Port of <c>_output_reserve</c>: the <c>max_tokens</c> generate would resolve (explicit config, then the
    /// provider default, then <see cref="DefaultOutputReserve"/>), capped at half the window.
    /// </summary>
    private static int OutputReserve(Model model, int contextWindow)
    {
        var maxTokens = model.Config.MaxTokens ?? model.Api.MaxTokens() ?? DefaultOutputReserve;
        return Math.Min(maxTokens, contextWindow / 2);
    }

    /// <summary>A piece of tool output (one content part) that shrinking would reduce.</summary>
    private readonly record struct ShrinkCandidate(int MessageIndex, int PartIndex, int Weight);

    /// <summary>
    /// Port of <c>_largest_shrinkable</c>: media parts are always shrinkable, text parts only when middle-truncation
    /// can make progress; media is weighed by its token estimate times <see cref="CharsPerToken"/>. Ties keep the
    /// first candidate, as Python's <c>max</c> does.
    /// </summary>
    private static ShrinkCandidate? LargestShrinkable(List<ChatMessage> messages)
    {
        ShrinkCandidate? best = null;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            if (messages[messageIndex] is not ChatMessageTool tool)
            {
                continue;
            }

            var parts = tool.ContentList;
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                ShrinkCandidate? candidate = parts[partIndex] switch
                {
                    ContentText text when text.Text.Length / 2 > TruncationMarker.Length => new ShrinkCandidate(messageIndex, partIndex, text.Text.Length),
                    ContentImage or ContentAudio or ContentVideo => new ShrinkCandidate(messageIndex, partIndex, TokenEstimator.CountMediaTokens(parts[partIndex]) * CharsPerToken),
                    _ => null,
                };
                if (candidate is { } found && (best is null || found.Weight > best.Value.Weight))
                {
                    best = found;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Port of <c>_shrink</c>: text parts are middle-truncated to half their size, media parts become a text
    /// placeholder; parts are edited in place so the message structure is preserved, and the message is replaced
    /// with a copy rather than mutated.
    /// </summary>
    private static void Shrink(List<ChatMessage> messages, ShrinkCandidate candidate)
    {
        var message = messages[candidate.MessageIndex];
        MessageContent content;
        if (message.Content.IsString)
        {
            var text = message.Content.Text!;
            content = TruncateMiddle(text, text.Length / 2);
        }
        else
        {
            var parts = message.Content.Items!.ToList();
            var part = parts[candidate.PartIndex];
            parts[candidate.PartIndex] = part is ContentText text
                ? new ContentText(TruncateMiddle(text.Text, text.Text.Length / 2))
                : new ContentText($"[{part.Type} elided for summarization]");
            content = MessageContent.FromItems(parts);
        }

        messages[candidate.MessageIndex] = message with { Content = content };
    }

    /// <summary>
    /// Port of <c>_truncate_middle</c>: keeps the head and tail of <paramref name="text"/> within about
    /// <paramref name="maxBytes"/> UTF-8 bytes with <see cref="TruncationMarker"/> at the seam. Returns the text
    /// unchanged when it already fits or when the budget leaves no room for the marker.
    /// </summary>
    internal static string TruncateMiddle(string text, int maxBytes)
    {
        var budget = maxBytes - TruncationMarker.Length;
        if (budget <= 0)
        {
            return text;
        }

        var encoded = Encoding.UTF8.GetBytes(text);
        if (encoded.Length <= budget)
        {
            return text;
        }

        var front = Encoding.UTF8.GetString(encoded, 0, budget / 2);
        var backLength = budget - budget / 2;
        var back = Encoding.UTF8.GetString(encoded, encoded.Length - backLength, backLength);
        return front + TruncationMarker + back;
    }
}
