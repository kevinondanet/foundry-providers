using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Port of <c>inline_media_data_uri</c> (<c>src/inspect_ai/_util/images.py</c>) and the data-URI helpers in
/// <c>src/inspect_ai/_util/url.py</c>. No I/O is performed: file paths and http(s) URLs are rejected.
/// </summary>
public static partial class InlineMedia
{
    [GeneratedRegex(@"^data:[^,]*;base64,")]
    private static partial Regex DataUriRegex();

    [GeneratedRegex(@"^data:([^;]+);.*")]
    private static partial Regex DataUriMimeRegex();

    [GeneratedRegex(@"^data:[^,]*,")]
    private static partial Regex DataUriPrefixRegex();

    /// <summary>Port of <c>is_data_uri</c>.</summary>
    public static bool IsDataUri(string url) => DataUriRegex().IsMatch(url);

    /// <summary>Port of <c>data_uri_mime_type</c>.</summary>
    public static string? DataUriMimeType(string dataUri)
    {
        var match = DataUriMimeRegex().Match(dataUri);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Port of <c>data_uri_to_base64</c>.</summary>
    public static string DataUriToBase64(string dataUri) => DataUriPrefixRegex().Replace(dataUri, "", 1);

    /// <summary>Port of <c>as_data_uri</c>.</summary>
    public static string AsDataUri(string mimeType, string data) => $"data:{mimeType};base64,{data}";

    /// <summary>
    /// Validates and returns a typed inline media data URI. <paramref name="expectedKind"/> is
    /// <c>image</c>, <c>audio</c>, <c>video</c>, <c>document</c> or null.
    /// </summary>
    public static string InlineMediaDataUri(string file, string? expectedKind = null, string? mimeTypeHint = null)
    {
        var mimeType = InlineMediaMimeType(file, expectedKind, mimeTypeHint);
        if (DataUriMimeType(file) is null)
        {
            return AsDataUri(mimeType, DataUriToBase64(file));
        }

        return file;
    }

    private static string InlineMediaMimeType(string file, string? expectedKind, string? mimeTypeHint)
    {
        if (!IsDataUri(file))
        {
            throw new UnresolvedMediaError(
                "Media references must be materialized before model submission. Trusted code can call inspect_ai.util.materialize_media().");
        }

        var mimeType = NormalizeMimeType(DataUriMimeType(file)) ?? NormalizeMimeType(mimeTypeHint);
        if (mimeType is null && expectedKind == "image")
        {
            mimeType = SniffImageMimeType(DecodeInlineMedia(file)) ?? "image/png";
        }

        if (mimeType is null)
        {
            throw new ArgumentException(
                "Inline media data URI does not declare a MIME type and its content type could not be inferred from the media metadata.");
        }

        if (expectedKind is not null && !MimeMatchesKind(mimeType, expectedKind))
        {
            throw new ArgumentException($"Inline {expectedKind} media has incompatible MIME type '{mimeType}'.");
        }

        return mimeType;
    }

    private static byte[] DecodeInlineMedia(string file)
    {
        try
        {
            return Convert.FromBase64String(DataUriToBase64(file));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Inline media data URI contains invalid base64 data.", ex);
        }
    }

    private static string? NormalizeMimeType(string? mimeType)
    {
        if (mimeType is null)
        {
            return null;
        }

        var normalized = mimeType.Split(';')[0].Trim().ToLowerInvariant();
        return normalized.Contains('/') ? normalized : null;
    }

    private static bool MimeMatchesKind(string mimeType, string kind) =>
        kind == "document" || mimeType.StartsWith(kind + "/", StringComparison.Ordinal);

    private static string? SniffImageMimeType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("\x89PNG\r\n\x1a\n"u8))
        {
            return "image/png";
        }

        if (data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return "image/jpeg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        return null;
    }
}
