using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Provider.Core;
using Microsoft.Extensions.AI;
using ChatMessage = InspectAzureAI.Provider.Core.ChatMessage;
using MafChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace InspectAzureAI.Maf;

/// <summary>
/// Translation between the Microsoft.Extensions.AI chat types that Agent Framework speaks and Inspect's model
/// types, in both directions. Function calls travel as <see cref="FunctionCallContent"/> /
/// <see cref="FunctionResultContent"/> on the Agent Framework side and as <see cref="ToolCall"/> /
/// <see cref="ChatMessageTool"/> on the Inspect side.
/// </summary>
internal static class MafConversion
{
    /// <summary>The model a request names when the caller sets no <see cref="ChatOptions.ModelId"/>; resolves to the bridge's default model.</summary>
    public const string DefaultModelName = "inspect";

    /// <summary>Agent Framework's serializer settings, compact (its defaults indent, which only pads what a model reads).</summary>
    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>Agent Framework messages to Inspect messages; <paramref name="instructions"/> (the agent's system instructions) lead as a system message.</summary>
    public static IReadOnlyList<ChatMessage> ToInspectMessages(IEnumerable<MafChatMessage> messages, string? instructions)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            result.Add(new ChatMessageSystem(instructions));
        }

        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                result.Add(new ChatMessageSystem(ToContent(message.Contents)));
            }
            else if (message.Role == ChatRole.Assistant)
            {
                result.Add(ToAssistant(message, callNames));
            }
            else if (message.Role == ChatRole.Tool)
            {
                result.AddRange(ToToolResults(message, callNames));
            }
            else
            {
                result.Add(new ChatMessageUser(ToContent(message.Contents)));
            }
        }

        return result;
    }

    private static ChatMessageAssistant ToAssistant(MafChatMessage message, Dictionary<string, string> callNames)
    {
        var toolCalls = new List<ToolCall>();
        foreach (var call in message.Contents.OfType<FunctionCallContent>())
        {
            callNames[call.CallId] = call.Name;
            toolCalls.Add(new ToolCall(call.CallId, call.Name, ToJsonObject(call.Arguments)) { ParseError = call.Exception?.Message });
        }

        return new ChatMessageAssistant(ToContent(message.Contents), toolCalls.Count == 0 ? null : toolCalls);
    }

    private static IEnumerable<ChatMessageTool> ToToolResults(MafChatMessage message, Dictionary<string, string> callNames)
    {
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
        {
            yield return ToToolResult(result, callNames.GetValueOrDefault(result.CallId));
        }
    }

    /// <summary>
    /// A function result as an Inspect tool message. A result that is itself a <see cref="ChatMessageTool"/> (an
    /// Inspect tool's error or non-text content, see <see cref="ToolDefFunction"/>) is taken as is; otherwise an
    /// exception recorded on the result becomes the message's error.
    /// </summary>
    public static ChatMessageTool ToToolResult(FunctionResultContent result, string? function)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Result is ChatMessageTool tool)
        {
            return tool with { ToolCallId = result.CallId, Function = function ?? tool.Function };
        }

        var error = result.Exception is null ? null : new ToolCallError("unknown", result.Exception.Message);
        return new ChatMessageTool(ResultText(result.Result), toolCallId: result.CallId, function: function, error: error);
    }

    /// <summary>
    /// Message contents to Inspect content: text, reasoning and media are carried over; function calls and results
    /// travel separately (<see cref="ToAssistant"/>, <see cref="ToToolResults"/>); usage and error items are not
    /// model-visible and are dropped. Any other content type is refused rather than silently lost.
    /// </summary>
    public static MessageContent ToContent(IList<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var items = new List<Content>();
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent text:
                    items.Add(new ContentText(text.Text ?? ""));
                    break;
                case TextReasoningContent reasoning:
                    // a provider's opaque reasoning blob (a signature, or a redacted block) rides in ProtectedData
                    items.Add(new ContentReasoning(reasoning.Text ?? "", reasoning.ProtectedData, Redacted: string.IsNullOrEmpty(reasoning.Text) && reasoning.ProtectedData is not null));
                    break;
                case DataContent data:
                    items.Add(ToMedia(data.Uri, data.MediaType));
                    break;
                case UriContent uri:
                    items.Add(ToMedia(uri.Uri.ToString(), uri.MediaType));
                    break;
                case FunctionCallContent or FunctionResultContent or UsageContent or ErrorContent:
                    break;
                default:
                    throw new NotSupportedException($"Agent Framework content of type {content.GetType().Name} cannot be sent to an Inspect model.");
            }
        }

        return items switch
        {
            [] => "",
            [ContentText only] => only.Text,
            _ => MessageContent.FromItems(items),
        };
    }

    private static Content ToMedia(string uri, string mediaType)
    {
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentImage(uri);
        }

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentAudio(uri, MediaFormat(mediaType));
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentVideo(uri, MediaFormat(mediaType));
        }

        return new ContentDocument(uri, "", mediaType);
    }

    /// <summary>The subtype of a media type as Inspect's format name (<c>audio/mpeg</c> is <c>mp3</c>).</summary>
    private static string MediaFormat(string mediaType)
    {
        var format = mediaType[(mediaType.IndexOf('/') + 1)..];
        return format.Equals("mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3" : format;
    }

    /// <summary>Function-call arguments as the JSON object Inspect tools receive.</summary>
    public static JsonObject ToJsonObject(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return new JsonObject();
        }

        return JsonSerializer.SerializeToNode(arguments, AIJsonUtilities.DefaultOptions) as JsonObject ?? new JsonObject();
    }

    /// <summary>A function result as the text a model sees: strings verbatim, anything else as JSON.</summary>
    public static string ResultText(object? result) => result switch
    {
        null => "",
        string text => text,
        ChatMessageTool tool => tool.Error is { } error ? $"Error: {error.Message}" : tool.Text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
        JsonNode node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString(),
        _ => JsonSerializer.Serialize(result, CompactJson),
    };

    /// <summary>Agent Framework tools as Inspect tool descriptions. Only function tools have a schema a model can call; any other tool kind is refused.</summary>
    public static IReadOnlyList<ToolInfo> ToToolInfos(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        var result = new List<ToolInfo>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is not AIFunction function)
            {
                throw new NotSupportedException(
                    $"Agent Framework tool '{tool.Name}' ({tool.GetType().Name}) is not a function tool; only AIFunction tools can be bridged to an Inspect model.");
            }

            var schema = function.JsonSchema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(function.JsonSchema.GetRawText()) as JsonObject : null;
            result.Add(new ToolInfo(function.Name, function.Description) { Parameters = BridgeJson.ToolParamsFromSchema(schema) });
        }

        return result;
    }

    public static ToolChoice ToToolChoice(ChatToolMode? mode) => mode switch
    {
        null => ToolChoice.Auto,
        AutoChatToolMode => ToolChoice.Auto,
        NoneChatToolMode => ToolChoice.None,
        RequiredChatToolMode { RequiredFunctionName: { } name } => new ToolFunction(name),
        RequiredChatToolMode => ToolChoice.Any,
        _ => throw new NotSupportedException($"Tool mode {mode.GetType().Name} is not supported."),
    };

    /// <summary>
    /// Generation parameters of a request. The bridge only forwards them when built with
    /// <c>forwardGenerationConfig</c>. A JSON-schema response format becomes a <see cref="ResponseSchema"/>;
    /// schema-less JSON mode and a seed outside 32 bits have no Inspect form and are refused.
    /// </summary>
    public static GenerateConfig ToGenerateConfig(ChatOptions? options) => options is null
        ? new GenerateConfig()
        : new GenerateConfig
        {
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            MaxTokens = options.MaxOutputTokens,
            StopSeqs = options.StopSequences is { Count: > 0 } stops ? [.. stops] : null,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed is { } seed
                ? seed is >= int.MinValue and <= int.MaxValue ? (int)seed : throw new NotSupportedException($"Seed {seed} does not fit Inspect's 32-bit seed.")
                : null,
            ParallelToolCalls = options.AllowMultipleToolCalls,
            ResponseSchema = ToResponseSchema(options.ResponseFormat),
        };

    private static ResponseSchema? ToResponseSchema(ChatResponseFormat? format) => format switch
    {
        null or ChatResponseFormatText => null,
        ChatResponseFormatJson { Schema: { } schema } json => new ResponseSchema(json.SchemaName ?? "response", JsonSchema.FromJson(JsonNode.Parse(schema.GetRawText())))
        {
            Description = json.SchemaDescription,
        },
        ChatResponseFormatJson => throw new NotSupportedException("A JSON response format without a schema has no Inspect equivalent; use ChatResponseFormat.ForJsonSchema."),
        _ => throw new NotSupportedException($"Response format {format.GetType().Name} is not supported."),
    };

    /// <summary>An Inspect model output as an Agent Framework response; tool calls become <see cref="FunctionCallContent"/> for the framework to invoke.</summary>
    public static ChatResponse ToChatResponse(ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Choices.Count == 0)
        {
            throw new InvalidOperationException(output.Error is { } error ? $"The Inspect model returned an error: {error}" : "The Inspect model returned no choices.");
        }

        var message = output.Message;
        var contents = ToAIContents(message.ContentList);
        foreach (var call in message.ToolCalls ?? [])
        {
            contents.Add(new FunctionCallContent(call.Id, call.Function, ToArguments(call.Arguments))
            {
                Exception = call.ParseError is null ? null : new JsonException(call.ParseError),
            });
        }

        var response = new MafChatMessage(ChatRole.Assistant, contents) { MessageId = message.Id, RawRepresentation = message };
        return new ChatResponse(response)
        {
            ResponseId = message.Id ?? Guid.NewGuid().ToString("N"),
            ModelId = output.Model,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ToFinishReason(output.StopReason),
            Usage = ToUsage(output.Usage),
            RawRepresentation = output,
        };
    }

    /// <summary>Tool-call arguments as the dictionary a <see cref="FunctionCallContent"/> carries (values as <see cref="JsonElement"/>, the shape provider clients produce).</summary>
    public static Dictionary<string, object?> ToArguments(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            result[name] = value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);
        }

        return result;
    }

    public static ChatFinishReason? ToFinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.Stop => ChatFinishReason.Stop,
        StopReason.MaxTokens or StopReason.ModelLength => ChatFinishReason.Length,
        StopReason.ToolCalls => ChatFinishReason.ToolCalls,
        StopReason.ContentFilter => ChatFinishReason.ContentFilter,
        _ => null,
    };

    public static UsageDetails? ToUsage(ModelUsage? usage) => usage is null
        ? null
        : new UsageDetails
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalTokens,
            CachedInputTokenCount = usage.InputTokensCacheRead,
            ReasoningTokenCount = usage.ReasoningTokens,
        };

    /// <summary>Inspect messages as Agent Framework messages (the sample's conversation seeds the agent's session).</summary>
    public static List<MafChatMessage> ToMafMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<MafChatMessage>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case ChatMessageSystem system:
                    result.Add(new MafChatMessage(ChatRole.System, ToAIContents(system.ContentList)) { MessageId = system.Id });
                    break;
                case ChatMessageUser user:
                    result.Add(new MafChatMessage(ChatRole.User, ToAIContents(user.ContentList)) { MessageId = user.Id });
                    break;
                case ChatMessageAssistant assistant:
                    var contents = ToAIContents(assistant.ContentList);
                    foreach (var call in assistant.ToolCalls ?? [])
                    {
                        contents.Add(new FunctionCallContent(call.Id, call.Function, ToArguments(call.Arguments)));
                    }

                    result.Add(new MafChatMessage(ChatRole.Assistant, contents) { MessageId = assistant.Id });
                    break;
                case ChatMessageTool tool:
                    // plain text travels as text; an error or non-text content travels as the message itself, which ToToolResult unwraps
                    var functionResult = new FunctionResultContent(tool.ToolCallId ?? "", tool.Error is null && tool.Content.IsString ? tool.Text : tool);
                    result.Add(new MafChatMessage(ChatRole.Tool, [functionResult]) { MessageId = tool.Id });
                    break;
                default:
                    throw new NotSupportedException($"Inspect message of type {message.GetType().Name} cannot be sent to an Agent Framework agent.");
            }
        }

        return result;
    }

    private static List<AIContent> ToAIContents(IReadOnlyList<Content> contents)
    {
        var result = new List<AIContent>();
        foreach (var content in contents)
        {
            switch (content)
            {
                case ContentText { Text.Length: > 0 } text:
                    result.Add(new TextContent(text.Text));
                    break;
                case ContentText:
                    break;
                case ContentReasoning reasoning:
                    result.Add(new TextReasoningContent(reasoning.Redacted ? "" : reasoning.Reasoning) { ProtectedData = reasoning.Signature });
                    break;
                case ContentImage image:
                    result.Add(ToAIMedia(image.Image, "image/*"));
                    break;
                case ContentAudio audio:
                    result.Add(ToAIMedia(audio.Audio, $"audio/{audio.Format}"));
                    break;
                case ContentVideo video:
                    result.Add(ToAIMedia(video.Video, $"video/{video.Format}"));
                    break;
                case ContentDocument document:
                    result.Add(ToAIMedia(document.Document, document.MimeType.Length > 0 ? document.MimeType : "application/octet-stream"));
                    break;
                default:
                    throw new NotSupportedException($"Inspect content of type {content.GetType().Name} cannot be sent to an Agent Framework agent.");
            }
        }

        return result;
    }

    /// <summary>A data URI becomes inline data (its own media type wins); anything else is a reference by URI.</summary>
    private static AIContent ToAIMedia(string uri, string mediaType) =>
        uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? new DataContent(uri, mediaType.EndsWith("/*", StringComparison.Ordinal) ? null : mediaType)
            : new UriContent(uri, mediaType);
}
