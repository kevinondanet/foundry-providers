using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Base of the citation union (port of <c>CitationBase</c> in <c>src/inspect_ai/_util/citation.py</c>):
/// a reference attached to a <see cref="ContentText"/> block. The Python <c>cited_text</c> is either the
/// text itself (<see cref="CitedText"/>) or a start/end range within the containing content
/// (<see cref="CitedRange"/>); at most one of the two is set.
/// </summary>
public abstract record Citation
{
    /// <summary>Discriminator carried in the Python <c>type</c> field (<c>content</c>, <c>document</c> or <c>url</c>).</summary>
    public abstract string Type { get; }

    /// <summary>The cited text, when it is carried as text.</summary>
    public string? CitedText { get; init; }

    /// <summary>The cited text as a start/end range within the container, when it is carried as a range.</summary>
    public CitedRange? CitedRange { get; init; }

    /// <summary>Title of the cited resource.</summary>
    public string? Title { get; init; }

    /// <summary>Model provider specific payload, typically used to aid transformation back to model types.</summary>
    public JsonObject? Internal { get; init; }
}

/// <summary>A start/end range of the cited text within its container (the Python <c>tuple[int, int]</c> form of <c>cited_text</c>).</summary>
public readonly record struct CitedRange(int Start, int End);

/// <summary>A generic content citation (port of <c>ContentCitation</c>).</summary>
public sealed record ContentCitation : Citation
{
    public override string Type => "content";
}

/// <summary>A range specifying a section of a document (port of <c>DocumentRange</c>): <see cref="Type"/> is <c>block</c>, <c>page</c> or <c>char</c>; indexes are 0-based.</summary>
public sealed record DocumentRange(string Type, int StartIndex, int EndIndex);

/// <summary>A citation that refers to a page range in a document (port of <c>DocumentCitation</c>).</summary>
public sealed record DocumentCitation : Citation
{
    public override string Type => "document";

    /// <summary>Range of the document that is cited.</summary>
    public DocumentRange? Range { get; init; }
}

/// <summary>A citation that refers to a URL (port of <c>UrlCitation</c>).</summary>
public sealed record UrlCitation(string Url) : Citation
{
    public override string Type => "url";
}
