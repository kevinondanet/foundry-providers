using System.Text;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using ModelContextProtocol.Protocol;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>
/// Port of the content mapping in <c>tool/_mcp/sampling.py</c> (<c>as_inspect_content</c>, <c>as_mcp_content</c>)
/// and <c>tool_result_as_text</c> in <c>_local.py</c>: MCP content blocks to and from Inspect <see cref="Content"/>.
/// </summary>
public static class McpContent
{
    /// <summary>Port of <c>as_inspect_content_list</c>.</summary>
    public static IReadOnlyList<Content> AsInspectContentList(IEnumerable<ContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Select(AsInspectContent).ToArray();
    }

    /// <summary>
    /// Port of <c>as_inspect_content</c>: text, image (as a base64 data URI), audio (wav or mp3), a resource link
    /// (rendered as text) and an embedded text resource. Any other block is an <see cref="ArgumentException"/>.
    /// </summary>
    public static Content AsInspectContent(ContentBlock content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            TextContentBlock text => new ContentText(text.Text),
            ImageContentBlock image => new ContentImage(InlineMedia.AsDataUri(image.MimeType, Base64(image.Data))),
            AudioContentBlock audio => new ContentAudio(InlineMedia.AsDataUri(audio.MimeType, Base64(audio.Data)), AudioFormat(audio.MimeType)),
            ResourceLinkBlock link => new ContentText(ResourceLinkText(link)),
            EmbeddedResourceBlock { Resource: TextResourceContents resource } => new ContentText(resource.Text),
            _ => throw new ArgumentException($"Unexpected content: {content.Type}", nameof(content)),
        };
    }

    /// <summary>Port of <c>as_mcp_content</c>: text or an image data URI back to an MCP block (used by Python for sampling replies).</summary>
    public static ContentBlock AsMcpContent(Content content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            ContentText text => new TextContentBlock { Text = text.Text },
            ContentImage image => new ImageContentBlock
            {
                MimeType = InlineMedia.DataUriMimeType(image.Image) ?? "image/png",
                Data = Encoding.UTF8.GetBytes(InlineMedia.DataUriToBase64(image.Image)),
            },
            _ => throw new ArgumentException($"Unexpected content: {content.Type}", nameof(content)),
        };
    }

    /// <summary>
    /// Port of <c>tool_result_as_text</c>: the text fed back to the model for an <c>isError</c> result. Blocks are
    /// joined by blank lines; binary blocks are replaced by a placeholder and unknown blocks are skipped.
    /// </summary>
    public static string ToolResultAsText(IEnumerable<ContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parts = new List<string>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    parts.Add(text.Text);
                    break;
                case ImageContentBlock:
                    parts.Add("(base64 encoded image omitted)");
                    break;
                case AudioContentBlock:
                    parts.Add("(base64 encoded audio omitted)");
                    break;
                case ResourceLinkBlock link:
                    parts.Add(ResourceLinkText(link));
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    parts.Add(resource.Text);
                    break;
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Port of <c>_get_audio_format</c>: <c>wav</c> or <c>mp3</c> from the mime type, anything else is an <see cref="ArgumentException"/>.</summary>
    internal static string AudioFormat(string mimeType) => mimeType switch
    {
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/mpeg" => "mp3",
        _ => throw new ArgumentException($"Unsupported audio mime type: {mimeType}", nameof(mimeType)),
    };

    // Python renders `f"{description} ({uri})"`, which prints "None" for a missing description; the link's
    // required name is used instead here.
    private static string ResourceLinkText(ResourceLinkBlock link) => $"{link.Description ?? link.Name} ({link.Uri})";

    // ImageContentBlock.Data / AudioContentBlock.Data hold the UTF-8 bytes of the base64 text on the wire.
    private static string Base64(ReadOnlyMemory<byte> data) => Encoding.UTF8.GetString(data.Span);
}
