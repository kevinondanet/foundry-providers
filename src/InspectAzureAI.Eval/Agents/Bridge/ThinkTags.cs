using System.Net;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of the think-tag helpers of <c>model/_reasoning.py</c> (<c>reasoning_to_think_tag</c> and
/// <c>parse_content_with_reasoning</c>, lines 106-235). Reasoning travels through text-only scaffolds as
/// <c>&lt;think signature="…" redacted="true"&gt;</c>, an optional <c>&lt;summary&gt;…&lt;/summary&gt;</c> line, the
/// reasoning text and <c>&lt;/think&gt;</c>. Attribute values are HTML-escaped as Python's
/// <c>html.escape(quote=True)</c> does. The <c>internal</c> attribute is neither written nor read, because this
/// port's content types have no <c>internal</c> slot.
/// </summary>
public static partial class ThinkTags
{
    /// <summary>Port of <c>reasoning_to_think_tag</c>.</summary>
    public static string ToThinkTag(ContentReasoning reasoning)
    {
        ArgumentNullException.ThrowIfNull(reasoning);
        var attribs = "";
        if (reasoning.Signature is not null)
        {
            attribs = $"{attribs} signature=\"{HtmlEscape(reasoning.Signature)}\"";
        }

        if (reasoning.Redacted)
        {
            attribs = $"{attribs} redacted=\"true\"";
        }

        var inner = reasoning.Summary is not null ? $"<summary>{reasoning.Summary}</summary>\n" : "";
        inner = $"{inner}{reasoning.Reasoning}";
        return $"<think{attribs}>\n{inner}\n</think>";
    }

    /// <summary>
    /// Port of <c>parse_content_with_reasoning</c>. It extracts the first <c>&lt;think&gt;</c> block: a nesting-aware
    /// scan first, then the first lazy match. Attributes are HTML-unescaped and a nested <c>&lt;summary&gt;</c> is split
    /// out. A redacted block whose signature starts with <c>rs_</c> (an OpenAI encrypted reasoning item) has all
    /// whitespace removed from its body, which undoes line wrapping added by text scaffolds. Returns the content with
    /// the block removed and trimmed, plus zero or one reasoning item (Python extracts a single block). Content
    /// without a block is returned unchanged.
    /// </summary>
    public static (string Remaining, IReadOnlyList<ContentReasoning> Reasoning) Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var match = FindNestedThinkBlock(content);
        if (match is null || !match.Success)
        {
            match = LazyThinkBlockRegex().Match(content);
        }

        if (!match.Success)
        {
            return (content, Array.Empty<ContentReasoning>());
        }

        var attrs = match.Groups[1].Value;
        var signature = ParseAttr(SignatureAttrRegex(), attrs);
        var redacted = ParseAttr(RedactedAttrRegex(), attrs) == "true";
        var (reasoning, summary) = ParseSummary(match.Groups[2].Value.Trim());
        if (redacted && signature is not null && signature.StartsWith("rs_", StringComparison.Ordinal))
        {
            reasoning = WhitespaceRegex().Replace(reasoning, "");
        }

        var remaining = (content[..match.Index] + content[(match.Index + match.Length)..]).Trim();
        return (remaining, new[] { new ContentReasoning(reasoning, signature, redacted) { Summary = summary } });
    }

    /// <summary>Port of <c>_find_nested_think_block</c>: the span from the first opening tag to its balancing closing tag.</summary>
    private static Match? FindNestedThinkBlock(string content)
    {
        int? start = null;
        var depth = 0;
        foreach (Match tag in ThinkTagBoundaryRegex().Matches(content))
        {
            if (tag.Value.StartsWith("<think", StringComparison.Ordinal))
            {
                if (start is null)
                {
                    start = tag.Index;
                    depth = 1;
                }
                else
                {
                    depth++;
                }
            }
            else if (start is not null)
            {
                depth--;
                if (depth == 0)
                {
                    var end = tag.Index + tag.Length;
                    return GreedyThinkBlockRegex().Match(content, start.Value, end - start.Value);
                }
            }
        }

        return null;
    }

    private static string? ParseAttr(Regex attribute, string attrs)
    {
        var match = attribute.Match(attrs);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static (string Remaining, string? Summary) ParseSummary(string content)
    {
        var match = SummaryRegex().Match(content);
        if (!match.Success)
        {
            return (content, null);
        }

        return ((content[..match.Index] + content[(match.Index + match.Length)..]).Trim(), match.Groups[1].Value);
    }

    /// <summary>Python's <c>html.escape(s, quote=True)</c>.</summary>
    private static string HtmlEscape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#x27;", StringComparison.Ordinal);

    [GeneratedRegex("<think([^>]*)>|</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkTagBoundaryRegex();

    [GeneratedRegex("<think([^>]*)>(.*)</think>", RegexOptions.Singleline)]
    private static partial Regex GreedyThinkBlockRegex();

    [GeneratedRegex("<think([^>]*)>(.*?)</think>", RegexOptions.Singleline)]
    private static partial Regex LazyThinkBlockRegex();

    [GeneratedRegex("signature=\"([^\"]*)\"")]
    private static partial Regex SignatureAttrRegex();

    [GeneratedRegex("redacted=\"([^\"]*)\"")]
    private static partial Regex RedactedAttrRegex();

    [GeneratedRegex("<summary>(.*?)</summary>\\s*", RegexOptions.Singleline)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
