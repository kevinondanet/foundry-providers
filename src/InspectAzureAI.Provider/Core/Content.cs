namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Base of the content union (port of <c>ContentBase</c> in
/// <c>src/inspect_ai/_util/content.py</c>). Only the members the providers touch are carried
/// over: text, reasoning, image, audio, video and server-side tool use.
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

    /// <summary>Citations supporting the text block (port of <c>ContentText.citations</c>); carried into the log, not sent by the azureai providers.</summary>
    public IReadOnlyList<Citation>? Citations { get; init; }
}

/// <summary>
/// Reasoning content (port of <c>ContentReasoning</c>, <c>content.py</c>): thinking the model chose to
/// expose. <see cref="Signature"/> carries Anthropic's opaque signature (or the <c>redacted_thinking</c>
/// payload when <see cref="Redacted"/>), which must travel back unchanged on later turns. The Foundry
/// model-inference route exposes reasoning as plain text (<c>reasoning_content</c>) with no signature. On the
/// OpenAI Responses route <see cref="Signature"/> is the reasoning item id and, when <see cref="Redacted"/>,
/// <see cref="Reasoning"/> is the item's <c>encrypted_content</c>; both are replayed on later turns.
/// </summary>
public sealed record ContentReasoning(string Reasoning, string? Signature = null, bool Redacted = false) : Content
{
    public override string Type => "reasoning";

    /// <summary>Provider summary of the reasoning (the Responses API <c>summary</c> parts); null when none was returned.</summary>
    public string? Summary { get; init; }
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

/// <summary>
/// Server-side tool use (port of <c>ContentToolUse</c>, <c>content.py</c>): a tool the model provider ran itself,
/// such as Anthropic's web search. <see cref="Arguments"/> and <see cref="Result"/> are JSON text; the Anthropic
/// route replays the pair as <c>server_tool_use</c> / <c>web_search_tool_result</c> blocks on later turns.
/// </summary>
/// <param name="ToolType">The type of the tool call: <c>web_search</c>, <c>mcp_call</c> or <c>code_execution</c>.</param>
/// <param name="Id">The unique ID of the tool call.</param>
/// <param name="Name">Name of the tool.</param>
/// <param name="Arguments">Arguments passed to the tool (JSON).</param>
/// <param name="Result">Result from the tool call (JSON or text).</param>
public sealed record ContentToolUse(string ToolType, string Id, string Name, string Arguments, string Result) : Content
{
    public override string Type => "tool_use";

    /// <summary>Tool context (e.g. MCP server).</summary>
    public string? Context { get; init; }

    /// <summary>The error from the tool call (if any).</summary>
    public string? Error { get; init; }
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
