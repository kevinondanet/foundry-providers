namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Base of the content union (port of <c>ContentBase</c> in
/// <c>src/inspect_ai/_util/content.py</c>). Only the members the providers touch are carried
/// over: text, reasoning, image, audio and video.
/// </summary>
public abstract record Content
{
    /// <summary>Discriminator carried in the Python <c>type</c> field.</summary>
    public abstract string Type { get; }
}

/// <summary>Text content (port of <c>ContentText</c>, <c>content.py</c>).</summary>
public sealed record ContentText(string Text) : Content
{
    public override string Type => "text";

    /// <summary>Whether the text is a refusal (ignored by the azureai provider).</summary>
    public bool? Refusal { get; init; }
}

/// <summary>
/// Reasoning content (port of <c>ContentReasoning</c>, <c>content.py</c>): thinking the model chose to
/// expose. <see cref="Signature"/> carries Anthropic's opaque signature (or the <c>redacted_thinking</c>
/// payload when <see cref="Redacted"/>), which must travel back unchanged on later turns. The Foundry
/// model-inference route exposes reasoning as plain text (<c>reasoning_content</c>) with no signature.
/// </summary>
public sealed record ContentReasoning(string Reasoning, string? Signature = null, bool Redacted = false) : Content
{
    public override string Type => "reasoning";
}

/// <summary>
/// Image content (port of <c>ContentImage</c>, <c>content.py</c>). <see cref="Image"/> must be an
/// inline base64 data URI by the time it reaches the provider; <see cref="Detail"/> is one of
/// <c>auto</c>, <c>low</c>, <c>high</c> or <c>original</c> and is passed to the wire verbatim.
/// </summary>
public sealed record ContentImage(string Image, string Detail = "auto") : Content
{
    public override string Type => "image";
}

/// <summary>Audio content (port of <c>ContentAudio</c>, <c>content.py</c>); rejected by azureai.</summary>
public sealed record ContentAudio(string Audio, string Format) : Content
{
    public override string Type => "audio";
}

/// <summary>Video content (port of <c>ContentVideo</c>, <c>content.py</c>); rejected by azureai.</summary>
public sealed record ContentVideo(string Video, string Format) : Content
{
    public override string Type => "video";
}

/// <summary>
/// Document content, e.g. a PDF (port of <c>ContentDocument</c>, <c>content.py</c>): a file path or base64 data
/// URI with an optional filename and mime type. Neither Foundry route sends documents; it exists for token
/// estimation and log fidelity.
/// </summary>
public sealed record ContentDocument(string Document, string Filename = "", string MimeType = "") : Content
{
    public override string Type => "document";

    /// <summary>Enable model-generated citations for text or PDF documents (ignored by providers without citation support).</summary>
    public bool Citations { get; init; }
}
