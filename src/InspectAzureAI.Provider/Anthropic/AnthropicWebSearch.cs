using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Anthropic;

/// <summary>
/// Claude's server-side web search on the Anthropic Messages route (port of the <c>web_search</c> handling of
/// <c>model/_providers/anthropic.py</c> and <c>_anthropic_citations.py</c>, basic version). A <c>web_search</c>
/// tool whose <c>options</c> carry an <c>anthropic</c> entry is sent as the <c>web_search_20250305</c> server
/// tool instead of a function tool when the model supports it (<see cref="SupportsWebSearch"/>); the response's
/// <c>server_tool_use</c> / <c>web_search_tool_result</c> pairs become <see cref="ContentToolUse"/> items and
/// the text blocks' citations become <see cref="Citation"/>s, both replayed on later turns. Foundry supports
/// only this basic tool version (no dynamic filtering) and the companion <c>web_fetch</c> tool needs a beta
/// header, so neither the <c>web_search_20260209</c> form nor <c>web_fetch</c> is emitted.
/// </summary>
public static partial class AnthropicWebSearch
{
    /// <summary>The <c>ToolInfo.options</c> key marking a tool a model provider may execute server-side.</summary>
    public const string InternalToolType = "__internal_tool_type__";

    /// <summary>The server tool type Foundry accepts.</summary>
    public const string ServerToolType = "web_search_20250305";

    /// <summary>The error block type of a failed search.</summary>
    public const string ResultErrorType = "web_search_tool_result_error";

    private static readonly string[] OptionKeys = ["allowed_domains", "blocked_domains", "cache_control", "max_uses", "user_location"];

    /// <summary>Whether <paramref name="tool"/> is the built-in <c>web_search</c> tool with the "anthropic" provider enabled.</summary>
    public static bool IsAnthropicWebSearchTool(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name == "web_search" && tool.Options is { } options && options.ContainsKey("anthropic");
    }

    /// <summary>
    /// Port of <c>_supports_web_search</c> plus the frontier check of <c>web_search_tool_params</c>: Claude 3.7
    /// Sonnet, the 3.5 <c>-latest</c> aliases, every Claude 4.x and every Claude 5 model.
    /// </summary>
    public static bool SupportsWebSearch(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var name = modelName.ToLowerInvariant();
        return name.StartsWith("claude-opus-4", StringComparison.Ordinal)
            || name.StartsWith("claude-sonnet-4", StringComparison.Ordinal)
            || name.StartsWith("claude-haiku-4", StringComparison.Ordinal)
            || name.StartsWith("claude-3-7-sonnet", StringComparison.Ordinal)
            || name is "claude-3-5-sonnet-latest" or "claude-3-5-haiku-latest"
            || Claude5().IsMatch(name);
    }

    /// <summary>
    /// Port of <c>_web_search_tool_params</c> (basic version): the server tool param for
    /// <paramref name="tool"/> on <paramref name="modelName"/>, or null when the tool is not the Anthropic web
    /// search or the model cannot run it (the caller then sends an ordinary function tool). The "anthropic"
    /// options must be an object or null (<see cref="ArgumentException"/> otherwise, Python's TypeError);
    /// <c>allowed_domains</c>, <c>blocked_domains</c>, <c>cache_control</c>, <c>max_uses</c> and
    /// <c>user_location</c> are copied onto the tool.
    /// </summary>
    public static JsonObject? ServerToolParam(ToolInfo tool, string modelName)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(modelName);
        if (!IsAnthropicWebSearchTool(tool) || !SupportsWebSearch(modelName))
        {
            return null;
        }

        var options = tool.Options!["anthropic"];
        if (options is not null && options is not JsonObject)
        {
            throw new ArgumentException($"Expected a dictionary for anthropic_options, got {options.GetValueKind()}", nameof(tool));
        }

        var param = new JsonObject { ["name"] = "web_search", ["type"] = ServerToolType };
        if (options is JsonObject anthropicOptions)
        {
            foreach (var key in OptionKeys)
            {
                if (anthropicOptions.TryGetPropertyValue(key, out var value))
                {
                    param[key] = value?.DeepClone();
                }
            }
        }

        return param;
    }

    /// <summary>
    /// A <c>server_tool_use</c> block and its <c>web_search_tool_result</c> as one <see cref="ContentToolUse"/>:
    /// the input as JSON arguments, the result content (the search results with their encrypted payloads, or
    /// the error block) as the JSON result, and the error code when the search failed.
    /// </summary>
    public static ContentToolUse ToContentToolUse(JsonObject serverToolUse, JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(serverToolUse);
        ArgumentNullException.ThrowIfNull(result);
        var content = result["content"];
        var error = content is JsonObject { } errorBlock && errorBlock["type"]?.ToString() == ResultErrorType
            ? errorBlock["error_code"]?.ToString() ?? "unavailable"
            : null;
        return new ContentToolUse(
            "web_search",
            serverToolUse["id"]?.ToString() ?? "",
            serverToolUse["name"]?.ToString() ?? "web_search",
            PythonJson.Dumps(serverToolUse["input"] ?? new JsonObject()),
            PythonJson.Dumps(content))
        { Error = error };
    }

    /// <summary>
    /// The blocks that replay <paramref name="content"/> on a later turn (port of the <c>ContentToolUse</c>
    /// reconstruction in <c>anthropic.py</c>): <c>server_tool_use</c> with the parsed arguments and
    /// <c>web_search_tool_result</c> with the parsed result, or an <c>unavailable</c> error block when the
    /// result is not valid JSON (a result from another system). Only <c>web_search</c> tool uses are replayed;
    /// other tool types are left out with a one-time warning.
    /// </summary>
    public static IReadOnlyList<JsonObject> ReplayBlocks(ContentToolUse content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.ToolType != "web_search")
        {
            ProviderLogger.WarnOnce($"Anthropic on Azure: server-side '{content.ToolType}' tool uses cannot be replayed and were left out of the conversation.");
            return [];
        }

        JsonNode? input;
        try
        {
            input = JsonNode.Parse(content.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            input = new JsonObject();
        }

        JsonNode resultContent;
        try
        {
            var parsed = JsonNode.Parse(content.Result);
            resultContent = parsed is JsonArray or JsonObject ? parsed : UnavailableResult();
        }
        catch (JsonException)
        {
            resultContent = UnavailableResult();
        }

        return
        [
            new JsonObject { ["type"] = "server_tool_use", ["id"] = content.Id, ["name"] = "web_search", ["input"] = input },
            new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = content.Id, ["content"] = resultContent },
        ];
    }

    /// <summary>
    /// Port of <c>to_inspect_citation</c>: a <c>web_search_result_location</c> becomes a <see cref="UrlCitation"/>
    /// (title capped at 255 characters, <c>encrypted_index</c> kept in <see cref="Citation.Internal"/>); the
    /// document locations (<c>char_location</c>, <c>page_location</c>, <c>content_block_location</c>) become
    /// <see cref="DocumentCitation"/>s with their range and <c>document_index</c>. Any other citation type is
    /// kept as a <see cref="ContentCitation"/> carrying the whole payload in <see cref="Citation.Internal"/>.
    /// </summary>
    public static Citation ToInspectCitation(JsonObject citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        var citedText = citation["cited_text"]?.ToString();
        switch (citation["type"]?.ToString())
        {
            case "web_search_result_location":
                var title = citation["title"]?.ToString();
                return new UrlCitation(citation["url"]?.ToString() ?? "")
                {
                    CitedText = citedText,
                    Title = title is null || title.Length <= 255 ? title : title[..254] + "…",
                    Internal = new JsonObject { ["encrypted_index"] = citation["encrypted_index"]?.DeepClone() },
                };
            case "char_location":
                return DocumentLocation(citation, citedText, "char", "start_char_index", "end_char_index");
            case "page_location":
                return DocumentLocation(citation, citedText, "page", "start_page_number", "end_page_number");
            case "content_block_location":
                return DocumentLocation(citation, citedText, "block", "start_block_index", "end_block_index");
            default:
                return new ContentCitation { CitedText = citedText, Title = citation["title"]?.ToString(), Internal = citation.DeepClone().AsObject() };
        }
    }

    /// <summary>
    /// Port of <c>to_anthropic_citation</c> for replay: a <see cref="UrlCitation"/> with an
    /// <c>encrypted_index</c> becomes a <c>web_search_result_location</c>, a <see cref="DocumentCitation"/> with
    /// a range and <c>document_index</c> its location block. Null when the citation cannot be expressed (Python
    /// asserts there; here it is left out of the replay with a one-time warning).
    /// </summary>
    public static JsonObject? ToAnthropicCitation(Citation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        switch (citation)
        {
            case UrlCitation url when url.Internal is { } urlInternal && urlInternal["encrypted_index"] is JsonValue encrypted:
                return new JsonObject
                {
                    ["type"] = "web_search_result_location",
                    ["cited_text"] = citation.CitedText ?? "",
                    ["title"] = citation.Title,
                    ["url"] = url.Url,
                    ["encrypted_index"] = encrypted.DeepClone(),
                };
            case DocumentCitation { Range: { } range } document when document.Internal is { } documentInternal && documentInternal["document_index"] is JsonValue index:
                var (type, start, end) = range.Type switch
                {
                    "char" => ("char_location", "start_char_index", "end_char_index"),
                    "page" => ("page_location", "start_page_number", "end_page_number"),
                    _ => ("content_block_location", "start_block_index", "end_block_index"),
                };
                return new JsonObject
                {
                    ["type"] = type,
                    ["cited_text"] = citation.CitedText ?? "",
                    ["document_index"] = index.DeepClone(),
                    ["document_title"] = citation.Title,
                    [start] = range.StartIndex,
                    [end] = range.EndIndex,
                };
            default:
                ProviderLogger.WarnOnce($"Anthropic on Azure: a '{citation.Type}' citation without provider payload cannot be replayed and was left out of the conversation.");
                return null;
        }
    }

    /// <summary>Adds the replayable <paramref name="citations"/> to a <c>text</c> block (nothing when none can be expressed).</summary>
    public static void AddCitations(JsonObject textBlock, IReadOnlyList<Citation>? citations)
    {
        ArgumentNullException.ThrowIfNull(textBlock);
        if (citations is not { Count: > 0 })
        {
            return;
        }

        var array = new JsonArray();
        foreach (var citation in citations)
        {
            if (ToAnthropicCitation(citation) is { } param)
            {
                array.Add(param);
            }
        }

        if (array.Count > 0)
        {
            textBlock["citations"] = array;
        }
    }

    /// <summary>The citations of a response <c>text</c> block, or null when it has none.</summary>
    public static IReadOnlyList<Citation>? ReadCitations(JsonObject textBlock)
    {
        ArgumentNullException.ThrowIfNull(textBlock);
        if (textBlock["citations"] is not JsonArray { Count: > 0 } array)
        {
            return null;
        }

        return array.OfType<JsonObject>().Select(ToInspectCitation).ToList();
    }

    private static DocumentCitation DocumentLocation(JsonObject citation, string? citedText, string rangeType, string startKey, string endKey) => new()
    {
        CitedText = citedText,
        Title = citation["document_title"]?.ToString(),
        Range = new DocumentRange(rangeType, citation[startKey]?.GetValue<int>() ?? 0, citation[endKey]?.GetValue<int>() ?? 0),
        Internal = new JsonObject { ["document_index"] = citation["document_index"]?.DeepClone() },
    };

    private static JsonObject UnavailableResult() => new() { ["type"] = ResultErrorType, ["error_code"] = "unavailable" };

    [GeneratedRegex("claude-[a-z]+-5")]
    private static partial Regex Claude5();
}
