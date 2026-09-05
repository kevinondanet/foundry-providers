using System.Text;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port of <c>model/_tokens.py</c>: conservative token estimates used to trigger context compaction, so every
/// estimate intentionally overcounts. Text is counted by <see cref="CountTextTokens"/> (Python: tiktoken
/// <c>o200k_base</c> plus a 10% buffer; this port has no tokenizer package, so a character heuristic stands in
/// unless a tokenizer is supplied); media by size for data URIs and by fixed fallbacks for URLs and paths.
/// </summary>
public static class TokenEstimation
{
    /// <summary>Port of <c>FALLBACK_IMAGE_TOKENS</c> (max high-detail).</summary>
    public const int FallbackImageTokens = 1600;

    /// <summary>Port of <c>FALLBACK_AUDIO_TOKENS</c> (~40 seconds at 50 tok/sec).</summary>
    public const int FallbackAudioTokens = 2000;

    /// <summary>Port of <c>FALLBACK_VIDEO_TOKENS</c> (~20 seconds at 400 tok/sec).</summary>
    public const int FallbackVideoTokens = 8000;

    /// <summary>Port of <c>FALLBACK_DOCUMENT_TOKENS</c> (~5 pages at 1000 tok/page).</summary>
    public const int FallbackDocumentTokens = 5000;

    /// <summary>Port of <c>AUDIO_BYTES_PER_SEC_MP3</c> (~128kbps).</summary>
    public const int AudioBytesPerSecMp3 = 16_000;

    /// <summary>Port of <c>AUDIO_BYTES_PER_SEC_WAV</c> (44.1kHz/16bit stereo).</summary>
    public const int AudioBytesPerSecWav = 176_000;

    /// <summary>Port of <c>VIDEO_BYTES_PER_SEC</c> (~4Mbps).</summary>
    public const int VideoBytesPerSec = 500_000;

    /// <summary>Port of <c>DOCUMENT_BYTES_PER_PAGE</c> (~100KB/page for PDF).</summary>
    public const int DocumentBytesPerPage = 100_000;

    /// <summary>Port of <c>AUDIO_TOKENS_PER_SEC</c> (above Gemini's 32 tok/sec).</summary>
    public const int AudioTokensPerSec = 50;

    /// <summary>Port of <c>VIDEO_TOKENS_PER_SEC</c> (above Gemini's 300 tok/sec).</summary>
    public const int VideoTokensPerSec = 400;

    /// <summary>Port of <c>DOCUMENT_TOKENS_PER_PAGE</c>.</summary>
    public const int DocumentTokensPerPage = 1000;

    /// <summary>Low-detail image tokens (OpenAI's vision formula).</summary>
    public const int LowDetailImageTokens = 85;

    /// <summary>High/auto-detail image tokens (~1024x1024, OpenAI's vision formula).</summary>
    public const int HighDetailImageTokens = 765;

    /// <summary>
    /// Port of <c>count_tokens</c>: text parts (message text, reasoning summaries — which this port's
    /// <see cref="ContentReasoning"/> does not carry, so reasoning contributes nothing — tool call names and
    /// JSON arguments) are joined with newlines and counted once by <paramref name="countText"/>; each media item
    /// is counted by <paramref name="countMedia"/>. Never less than 1.
    /// </summary>
    public static async Task<int> CountTokensAsync(
        IReadOnlyList<ChatMessage> messages,
        Func<string, Task<int>> countText,
        Func<Content, Task<int>> countMedia)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(countText);
        ArgumentNullException.ThrowIfNull(countMedia);

        var textParts = new List<string>();
        var mediaItems = new List<Content>();
        foreach (var message in messages)
        {
            if (message.Content.IsString)
            {
                textParts.Add(message.Text);
            }
            else
            {
                foreach (var content in message.ContentList)
                {
                    switch (content)
                    {
                        case ContentText text:
                            textParts.Add(text.Text);
                            break;
                        case ContentImage or ContentAudio or ContentVideo or ContentDocument:
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
            total += await countText(string.Join("\n", textParts)).ConfigureAwait(false);
        }

        foreach (var media in mediaItems)
        {
            total += await countMedia(media).ConfigureAwait(false);
        }

        return Math.Max(1, total);
    }

    /// <summary>
    /// Port of <c>count_text_tokens</c>: the tokenizer's count with a 10% buffer (undercounting is worse than
    /// overcounting for a compaction trigger), never less than 1. Python uses tiktoken <c>o200k_base</c>; without
    /// a <paramref name="tokenizer"/> this port approximates one token per four ASCII characters plus one per
    /// other character (CJK, emoji), which overcounts typical English prose as the buffer intends.
    /// </summary>
    public static int CountTextTokens(string text, Func<string, int>? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = tokenizer?.Invoke(text) ?? ApproximateTokens(text);
        return Math.Max(1, (int)(count * 1.1));
    }

    /// <summary>
    /// Port of <c>count_media_tokens</c>: images by detail level; audio, video and documents by decoded size
    /// (base64 length * 3 / 4) for data URIs, else the fixed fallback; anything else counts as an image.
    /// </summary>
    public static int CountMediaTokens(Content media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media switch
        {
            ContentImage image => image.Detail == "low" ? LowDetailImageTokens : HighDetailImageTokens,
            ContentAudio audio => CountAudioTokens(audio),
            ContentVideo video => CountVideoTokens(video),
            ContentDocument document => CountDocumentTokens(document),
            _ => FallbackImageTokens,
        };
    }

    private static int CountAudioTokens(ContentAudio audio)
    {
        if (!InlineMedia.IsDataUri(audio.Audio))
        {
            return FallbackAudioTokens;
        }

        var rawBytes = audio.Audio.Length * 3 / 4;
        var bytesPerSecond = audio.Format == "wav" ? AudioBytesPerSecWav : AudioBytesPerSecMp3;
        var tokens = (int)(rawBytes / (double)bytesPerSecond * AudioTokensPerSec);
        return Math.Max(50, tokens);
    }

    private static int CountVideoTokens(ContentVideo video)
    {
        if (!InlineMedia.IsDataUri(video.Video))
        {
            return FallbackVideoTokens;
        }

        var rawBytes = video.Video.Length * 3 / 4;
        var tokens = (int)(rawBytes / (double)VideoBytesPerSec * VideoTokensPerSec);
        return Math.Max(100, tokens);
    }

    private static int CountDocumentTokens(ContentDocument document)
    {
        if (!InlineMedia.IsDataUri(document.Document))
        {
            return FallbackDocumentTokens;
        }

        var rawBytes = document.Document.Length * 3 / 4;
        var tokens = (int)(rawBytes / (double)DocumentBytesPerPage * DocumentTokensPerPage);
        return Math.Max(100, tokens);
    }

    private static int ApproximateTokens(string text)
    {
        var ascii = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 128)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }

        return (ascii + 3) / 4 + other;
    }
}
