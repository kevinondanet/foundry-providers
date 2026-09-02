namespace InspectAzureAI.Provider.Core;

/// <summary>Reason the model stopped (port of the <c>StopReason</c> literal in <c>src/inspect_ai/model/_model_output.py</c>).</summary>
public enum StopReason
{
    Stop,
    MaxTokens,
    ModelLength,
    ToolCalls,
    ContentFilter,
    Unknown,
}

/// <summary>Wire-name helpers for <see cref="StopReason"/>.</summary>
public static class StopReasonExtensions
{
    /// <summary>The Python literal value (<c>stop</c>, <c>max_tokens</c>, ...).</summary>
    public static string ToWire(this StopReason reason) => reason switch
    {
        StopReason.Stop => "stop",
        StopReason.MaxTokens => "max_tokens",
        StopReason.ModelLength => "model_length",
        StopReason.ToolCalls => "tool_calls",
        StopReason.ContentFilter => "content_filter",
        _ => "unknown",
    };
}

/// <summary>A category that triggered filtering (port of <c>StopCategory</c>).</summary>
public sealed record StopCategory(string Category, string? Level = null);

/// <summary>Details about why generation stopped (port of <c>StopDetails</c>).</summary>
public sealed record StopDetails
{
    public string? Type { get; init; }

    public string? Category { get; init; }

    public string? Explanation { get; init; }

    public IReadOnlyList<StopCategory> Categories { get; init; } = [];
}

/// <summary>Token usage for a completion (port of <c>ModelUsage</c>).</summary>
public sealed record ModelUsage(int InputTokens = 0, int OutputTokens = 0, int TotalTokens = 0)
{
    public int? InputTokensCacheWrite { get; init; }

    public int? InputTokensCacheRead { get; init; }

    public int? ReasoningTokens { get; init; }

    public double? TotalCost { get; init; }

    /// <summary>Port of <c>ModelUsage.__add__</c>.</summary>
    public static ModelUsage operator +(ModelUsage a, ModelUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.TotalTokens + b.TotalTokens)
    {
        InputTokensCacheWrite = OptionalSum(a.InputTokensCacheWrite, b.InputTokensCacheWrite),
        InputTokensCacheRead = OptionalSum(a.InputTokensCacheRead, b.InputTokensCacheRead),
        ReasoningTokens = OptionalSum(a.ReasoningTokens, b.ReasoningTokens),
        TotalCost = OptionalSum(a.TotalCost, b.TotalCost),
    };

    private static int? OptionalSum(int? a, int? b) => (a, b) switch
    {
        (not null, not null) => a + b,
        (not null, null) => a,
        (null, not null) => b,
        _ => null,
    };

    private static double? OptionalSum(double? a, double? b) => (a, b) switch
    {
        (not null, not null) => a + b,
        (not null, null) => a,
        (null, not null) => b,
        _ => null,
    };
}

/// <summary>Choice generated for a completion (port of <c>ChatCompletionChoice</c>).</summary>
public sealed record ChatCompletionChoice(
    ChatMessageAssistant Message,
    StopReason StopReason = StopReason.Unknown,
    StopDetails? StopDetails = null);

/// <summary>Output from a model generation (port of <c>ModelOutput</c>).</summary>
public sealed record ModelOutput
{
    private readonly string? _completion;

    public string Model { get; init; } = "";

    public IReadOnlyList<ChatCompletionChoice> Choices { get; init; } = [];

    /// <summary>
    /// Text of the first choice unless explicitly set (port of the <c>set_completion</c> validator).
    /// </summary>
    public string Completion
    {
        get => !string.IsNullOrEmpty(_completion) ? _completion : Choices.Count > 0 ? Choices[0].Message.Text : "";
        init => _completion = value;
    }

    public ModelUsage? Usage { get; init; }

    public double? Time { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    public string? Error { get; init; }

    /// <summary>True when there are no choices.</summary>
    public bool Empty => Choices.Count == 0;

    /// <summary>Stop reason of the first choice.</summary>
    public StopReason StopReason => Choices[0].StopReason;

    /// <summary>Message of the first choice.</summary>
    public ChatMessageAssistant Message => Choices[0].Message;

    /// <summary>Port of <c>ModelOutput.from_content</c>.</summary>
    public static ModelOutput FromContent(
        string model,
        MessageContent content,
        StopReason stopReason = StopReason.Stop,
        string? error = null,
        StopDetails? stopDetails = null) =>
        new()
        {
            Model = model,
            Choices =
            [
                new ChatCompletionChoice(
                    new ChatMessageAssistant(content, model: model, source: "generate"),
                    stopReason,
                    stopDetails),
            ],
            Error = error,
        };
}
