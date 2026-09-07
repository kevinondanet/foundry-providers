using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Compaction;

/// <summary>
/// Port of <c>model/_tokens.py</c>: the token estimate compaction uses when a provider has no native counter.
/// Text is estimated from its length (Python uses tiktoken's <c>o200k_base</c>, which this port does not ship)
/// with the same 10% safety buffer; media follows Python's size/detail heuristics. Every estimate deliberately
/// over-counts, since under-counting is the worse failure for a compaction trigger.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Approximate characters per token for the text heuristic.</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>Port of the 10% buffer applied to text counts.</summary>
    public const double TextBuffer = 1.1;

    public const int FallbackImageTokens = 1600;

    public const int FallbackAudioTokens = 2000;

    public const int FallbackVideoTokens = 8000;

    public const int AudioBytesPerSecMp3 = 16_000;

    public const int AudioBytesPerSecWav = 176_000;

    public const int VideoBytesPerSec = 500_000;

    public const int AudioTokensPerSec = 50;

    public const int VideoTokensPerSec = 400;

    /// <summary>
    /// Port of <c>count_tokens</c>: gathers the text of every message (string content, text parts, tool call
    /// names and JSON arguments) into one newline-joined string counted once, adds each media part, and never
    /// returns less than 1. Reasoning parts contribute nothing, as in Python (it counts only the reasoning
    /// <c>summary</c>, which the .NET content model does not carry).
    /// </summary>
    public static int CountTokens(
        IReadOnlyList<ChatMessage> messages,
        Func<string, int>? countText = null,
        Func<Content, int>? countMedia = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var textParts = new List<string>();
        var mediaItems = new List<Content>();
        foreach (var message in messages)
        {
            if (message.Content.IsString)
            {
                textParts.Add(message.Content.Text!);
            }
            else
            {
                foreach (var content in message.Content.Items!)
                {
                    switch (content)
                    {
                        case ContentText text:
                            textParts.Add(text.Text);
                            break;
                        case ContentImage or ContentAudio or ContentVideo:
                            mediaItems.Add(content);
                            break;
                    }
                }
            }

            if (message is ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls })
            {
                foreach (var toolCall in toolCalls)
                {
                    textParts.Add(toolCall.Function);
                    textParts.Add(PythonJson.Dumps(toolCall.Arguments));
                }
            }
        }

        var total = 0;
        if (textParts.Count > 0)
        {
            total += (countText ?? CountTextTokens)(string.Join("\n", textParts));
        }

        foreach (var media in mediaItems)
        {
            total += (countMedia ?? CountMediaTokens)(media);
        }

        return Math.Max(1, total);
    }

    /// <summary>
    /// Port of <c>count_text_tokens</c> without tiktoken: one token per <see cref="CharsPerToken"/> characters
    /// (rounded up) plus the 10% buffer, minimum 1.
    /// </summary>
    public static int CountTextTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var estimate = Math.Ceiling(text.Length / CharsPerToken);
        return Math.Max(1, (int)(estimate * TextBuffer));
    }

    /// <summary>
    /// Port of <c>count_media_tokens</c>: images by detail level (85 low, 765 otherwise); audio and video from
    /// the decoded size of a data URI at conservative token rates (with Python's minimums), or the fixed
    /// fallbacks for URLs and file paths.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="media"/> is not an image, audio or video part.</exception>
    public static int CountMediaTokens(Content media)
    {
        ArgumentNullException.ThrowIfNull(media);
        switch (media)
        {
            case ContentImage image:
                return image.Detail == "low" ? 85 : 765;
            case ContentAudio audio:
                if (!IsDataUri(audio.Audio))
                {
                    return FallbackAudioTokens;
                }

                var audioBytes = (long)audio.Audio.Length * 3 / 4;
                var bytesPerSecond = audio.Format == "wav" ? AudioBytesPerSecWav : AudioBytesPerSecMp3;
                return Math.Max(50, (int)(audioBytes / (double)bytesPerSecond * AudioTokensPerSec));
            case ContentVideo video:
                if (!IsDataUri(video.Video))
                {
                    return FallbackVideoTokens;
                }

                var videoBytes = (long)video.Video.Length * 3 / 4;
                return Math.Max(100, (int)(videoBytes / (double)VideoBytesPerSec * VideoTokensPerSec));
            default:
                throw new ArgumentException($"{media.GetType().Name} is not a media content part.", nameof(media));
        }
    }

    /// <summary>Port of <c>is_data_uri</c> as used by the token estimates.</summary>
    public static bool IsDataUri(string value) => value.StartsWith("data:", StringComparison.Ordinal);
}
