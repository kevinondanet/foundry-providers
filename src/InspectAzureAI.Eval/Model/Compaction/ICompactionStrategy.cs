using System.Globalization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of the <c>threshold: int | float</c> parameter of <c>CompactionStrategy</c>: an absolute token count, or a
/// fraction of the model's context window. An <see langword="int"/> converts to an absolute count and a
/// <see langword="double"/> to a fraction unless it exceeds 1.0 (then it is an absolute count too), exactly as
/// <c>_resolve_threshold</c> reads the Python union. The <see langword="default"/> value is the Python default
/// of 0.9.
/// </summary>
public readonly record struct CompactionThreshold
{
    private const double DefaultFraction = 0.9;

    private readonly int? _tokens;

    private readonly double? _fraction;

    private CompactionThreshold(int? tokens, double? fraction)
    {
        _tokens = tokens;
        _fraction = fraction;
    }

    /// <summary>The Python default: 90% of the context window.</summary>
    public static CompactionThreshold Default => FromFraction(DefaultFraction);

    /// <summary>Absolute token count, or null when the threshold is a fraction of the context window.</summary>
    public int? Tokens => _tokens;

    /// <summary>Fraction of the context window, or null when the threshold is an absolute token count.</summary>
    public double? Fraction => _tokens is null ? _fraction ?? DefaultFraction : null;

    public static CompactionThreshold FromTokens(int tokens) => new(tokens, null);

    public static CompactionThreshold FromFraction(double fraction) => new(null, fraction);

    public static implicit operator CompactionThreshold(int tokens) => FromTokens(tokens);

    public static implicit operator CompactionThreshold(double value) => value > 1.0 ? FromTokens((int)value) : FromFraction(value);

    /// <summary>Port of the arithmetic of <c>_resolve_threshold</c> once the context window is known.</summary>
    public int Resolve(int contextWindow) => Tokens ?? (int)(Fraction!.Value * contextWindow);

    /// <summary>The Python repr value: the int, or the float.</summary>
    public object Value => Tokens is { } tokens ? tokens : Fraction!.Value;

    public override string ToString() =>
        Tokens is { } tokens ? tokens.ToString(CultureInfo.InvariantCulture) : Fraction!.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Port of the <c>tuple[list[ChatMessage], ChatMessageUser | None]</c> a strategy (and <see cref="ICompact"/>)
/// returns: the input to present to the model and, optionally, a message to append to the full history (e.g. a
/// summary).
/// </summary>
public sealed record CompactionResult(IReadOnlyList<ChatMessage> Input, ChatMessageUser? Message);

/// <summary>Port of <c>model/_compaction/types.py</c> <c>CompactionStrategy</c>: how a conversation is reduced.</summary>
public interface ICompactionStrategy
{
    /// <summary>Type of compaction performed: <c>summary</c>, <c>edit</c> or <c>trim</c> (recorded on the <see cref="CompactionEvent"/>).</summary>
    string Type { get; }

    /// <summary>Token count or fraction of the context window that triggers compaction.</summary>
    CompactionThreshold Threshold { get; }

    /// <summary>Whether to warn the model to save critical content to memory before compaction when the memory tool is available.</summary>
    bool Memory { get; }

    /// <summary>
    /// Instruction to the orchestrator: when true (the default) any prefix messages missing from the compacted
    /// output are prepended; when false (native compaction) only system messages are, since user content is
    /// either preserved by the provider or encoded in the compaction block.
    /// </summary>
    bool PreservePrefix { get; }

    /// <summary>Compacts the full message history for <paramref name="model"/> given the available <paramref name="tools"/>.</summary>
    Task<CompactionResult> CompactAsync(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        CancellationToken cancellationToken = default);
}

/// <summary>Port of the <c>CompactionStrategy</c> abstract base: the shared parameters and their <c>_repr_params_</c>.</summary>
public abstract class CompactionStrategy : ICompactionStrategy
{
    private static readonly HashSet<string> KnownTypes = new(["summary", "edit", "trim"], StringComparer.Ordinal);

    private readonly bool _memory;

    /// <param name="type">Type of compaction performed (<c>summary</c>, <c>edit</c> or <c>trim</c>).</param>
    /// <param name="threshold">Token count or fraction of the context window that triggers compaction.</param>
    /// <param name="memory">Warn the model to save critical content to memory before compaction when the memory tool is available.</param>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not one of the Python literals.</exception>
    protected CompactionStrategy(string type, CompactionThreshold threshold, bool memory)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!KnownTypes.Contains(type))
        {
            throw new ArgumentException($"Compaction type must be one of 'summary', 'edit' or 'trim', got '{type}'.", nameof(type));
        }

        Type = type;
        Threshold = threshold;
        _memory = memory;
    }

    public string Type { get; }

    public CompactionThreshold Threshold { get; }

    public virtual bool Memory => _memory;

    public virtual bool PreservePrefix => true;

    /// <summary>Port of <c>_repr_params_</c>: the strategy's parameters by their Python names.</summary>
    public virtual IReadOnlyDictionary<string, object?> ReprParams() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["type"] = Type,
        ["threshold"] = Threshold.Value,
        ["memory"] = Memory,
    };

    public abstract Task<CompactionResult> CompactAsync(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        CancellationToken cancellationToken = default);
}
