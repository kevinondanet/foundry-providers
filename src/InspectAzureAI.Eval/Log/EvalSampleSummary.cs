using System.Text.Json;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log;

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalSampleSummary</c>: the fast-to-load view of a sample (no messages, output,
/// events or store) that the <c>.eval</c> recorder writes to <c>summaries.json</c>. Built with
/// <see cref="EvalSampleSummaries.Summary"/>, which thins the input, target, metadata and scores like Python's
/// <c>thin_data</c> validator.
/// </summary>
public sealed record EvalSampleSummary
{
    public required object Id { get; init; }

    public required int Epoch { get; init; }

    /// <summary>Sample input, text only (media replaced by a placeholder, long text truncated).</summary>
    public required SampleInput Input { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public Target Target { get; init; } = Target.Empty;

    /// <summary>Sample metadata: fields under 1k, strings shortened to 1k.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Scores with answer, explanation and metadata thinned; reason and history are dropped.</summary>
    public IReadOnlyDictionary<string, Score>? Scores { get; init; }

    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelUsage> RoleUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public IReadOnlyList<ModelFallback>? ModelFallbacks { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public double? TotalTime { get; init; }

    public double? WorkingTime { get; init; }

    public string? Uuid { get; init; }

    /// <summary>Message of the error that halted the sample.</summary>
    public string? Error { get; init; }

    /// <summary>Type of the limit that halted the sample.</summary>
    public string? Limit { get; init; }

    public string? LimitReason { get; init; }

    /// <summary>Number of retried attempts.</summary>
    public int? Retries { get; init; }

    public bool Completed { get; init; }

    public int? MessageCount { get; init; }

    public int? TurnCount { get; init; }

    public int? TokenLimit { get; init; }

    public string? TokenLimitType { get; init; }

    public int? TokenLimitUsage { get; init; }

    public int? MessageLimit { get; init; }

    public int? TimeLimit { get; init; }
}

/// <summary>Port of <c>EvalSample.summary()</c>.</summary>
public static class EvalSampleSummaries
{
    /// <summary>Port of <c>EvalSample.summary()</c> plus <c>EvalSampleSummary.thin_data</c>.</summary>
    public static EvalSampleSummary Summary(this EvalSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new EvalSampleSummary
        {
            Id = sample.Id,
            Epoch = sample.Epoch,
            Input = LogThinning.ThinInput(sample.Input),
            Choices = sample.Choices,
            Target = LogThinning.ThinTarget(sample.Target),
            Metadata = LogThinning.ThinMetadata(sample.Metadata),
            Scores = sample.Scores?.ToDictionary(
                pair => pair.Key,
                pair => new Score(pair.Value.Value)
                {
                    Answer = pair.Value.Answer is { } answer ? LogThinning.ThinText(answer) : null,
                    Explanation = pair.Value.Explanation is { } explanation ? LogThinning.ThinText(explanation) : null,
                    Metadata = pair.Value.Metadata is { } metadata ? LogThinning.ThinMetadata(metadata) : null,
                },
                StringComparer.Ordinal),
            ModelUsage = sample.ModelUsage,
            RoleUsage = sample.RoleUsage,
            ModelFallbacks = sample.ModelFallbacks,
            StartedAt = sample.StartedAt,
            CompletedAt = sample.CompletedAt,
            TotalTime = sample.TotalTime,
            WorkingTime = sample.WorkingTime,
            Uuid = sample.Uuid,
            Error = sample.Error?.Message,
            Limit = sample.Limit?.Type,
            LimitReason = sample.Limit?.Reason,
            Retries = sample.ErrorRetries?.Count,
            Completed = true,
            MessageCount = sample.Messages.Count,
            TurnCount = sample.TurnCount,
            TokenLimit = sample.TokenLimit,
            TokenLimitType = sample.TokenLimitType,
            TokenLimitUsage = sample.TokenLimitUsage,
            MessageLimit = sample.MessageLimit,
            TimeLimit = sample.TimeLimit,
        };
    }
}

/// <summary>Port of <c>log/_util.py</c>: the thinning applied to sample summaries.</summary>
public static class LogThinning
{
    /// <summary>Port of <c>MAX_TEXT_LENGTH</c>: the longest text kept by <see cref="TruncateText"/>.</summary>
    public const int MaxTextLength = 5120;

    /// <summary>Port of <c>THIN_TEXT_WIDTH</c>: the width <see cref="ThinText"/> shortens to.</summary>
    public const int ThinTextWidth = 1024;

    /// <summary>Port of <c>THIN_TEXT_SCAN</c>: the input prefix <see cref="ThinText"/> considers.</summary>
    public const int ThinTextScan = ThinTextWidth * 8;

    /// <summary>Port of <c>thin_metadata</c>'s placeholder for values whose JSON exceeds 1k.</summary>
    public const string KeyRemoved = "Key removed from summary (> 1k)";

    private const string Placeholder = "...";

    private const int MetadataBudget = 1024;

    /// <summary>Port of <c>truncate_text</c>: cuts at <paramref name="maxLength"/> characters and appends a truncation marker.</summary>
    public static string TruncateText(string text, int maxLength = MaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > maxLength ? text[..maxLength] + "...\n(content truncated)" : text;
    }

    /// <summary>
    /// Port of <c>thin_text</c>: <c>textwrap.shorten(text[:8192], width=1024, placeholder="...")</c> — whitespace
    /// collapsed to single spaces, then the longest prefix of whole words that fits with the placeholder, or the
    /// bare placeholder when not even the first word fits. (Python also breaks on hyphens; this port breaks on
    /// whitespace only.)
    /// </summary>
    public static string ThinText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text[..Math.Min(text.Length, ThinTextScan)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var collapsed = string.Join(' ', words);
        if (collapsed.Length <= ThinTextWidth)
        {
            return collapsed;
        }

        var kept = 0;
        var length = -1;
        while (kept < words.Length && length + 1 + words[kept].Length + Placeholder.Length <= ThinTextWidth)
        {
            length += 1 + words[kept].Length;
            kept++;
        }

        return kept == 0 ? Placeholder : string.Join(' ', words, 0, kept) + Placeholder;
    }

    /// <summary>
    /// Port of <c>thin_metadata</c>: numbers, booleans and dates pass through, strings are shortened with
    /// <see cref="ThinText"/>, and any other value whose indented JSON exceeds 1k is replaced by <see cref="KeyRemoved"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ThinMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var thinned = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            thinned[key] = value switch
            {
                int or long or double or float or decimal or short or byte or bool or DateTime or DateTimeOffset or DateOnly or TimeOnly => value,
                string text => ThinText(text),
                _ => JsonSerializer.Serialize(value, EvalLogWriter.Options).Length <= MetadataBudget ? value : KeyRemoved,
            };
        }

        return thinned;
    }

    /// <summary>Port of <c>thin_input</c>: text truncated with <see cref="TruncateText"/>, media replaced by a <c>(Image)</c>-style placeholder.</summary>
    public static SampleInput ThinInput(SampleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsText)
        {
            return TruncateText(input.Text ?? "");
        }

        return input.Messages!.Select(ThinMessage).ToArray();
    }

    /// <summary>Port of <c>thin_target</c>.</summary>
    public static Target ThinTarget(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new Target(target.Values.Select(value => TruncateText(value)).ToList());
    }

    private static ChatMessage ThinMessage(ChatMessage message)
    {
        if (message.Content.IsString)
        {
            var truncated = TruncateText(message.Content.Text!);
            return truncated == message.Content.Text ? message : message with { Content = truncated };
        }

        var changed = false;
        var items = new List<Content>(message.Content.Items!.Count);
        foreach (var content in message.Content.Items!)
        {
            if (content is ContentText text)
            {
                var truncated = TruncateText(text.Text);
                if (truncated != text.Text)
                {
                    items.Add(text with { Text = truncated });
                    changed = true;
                }
                else
                {
                    items.Add(text);
                }
            }
            else
            {
                items.Add(new ContentText($"({Capitalize(content.Type)})"));
                changed = true;
            }
        }

        return changed ? message with { Content = MessageContent.FromItems(items) } : message;
    }

    /// <summary>Python <c>str.capitalize()</c>: first character upper-cased, the rest lower-cased.</summary>
    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
}
