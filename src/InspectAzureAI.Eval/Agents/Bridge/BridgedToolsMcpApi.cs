using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>An MCP HTTP reply: the status and the JSON-RPC body (null for a bare <c>202 Accepted</c>).</summary>
public sealed record McpHttpReply(int Status, JsonObject? Body);

/// <summary>
/// The bridged-tools MCP endpoint as pure logic: port of the proxy's streamable-HTTP JSON-RPC server
/// (<c>inspect_sandbox_tools/_agent_bridge/proxy.py</c> <c>mcp_endpoint</c>) together with the host service it relays to
/// (<c>agent/_bridge/sandbox/service.py</c> <c>list_tools</c> / <c>call_tool</c>). <see cref="SandboxAgentBridge"/>
/// serves it at <c>POST /mcp/{server}</c>.
/// </summary>
/// <remarks>
/// Beyond Python: notifications and id-less messages get <c>202</c>, <c>ping</c> is answered, batches are refused,
/// a non-object <c>arguments</c> is <c>-32602</c>, arguments are validated against the tool schema before the tool
/// runs, and text-only content lists become text blocks rather than one JSON string.
/// </remarks>
public static class BridgedToolsMcpApi
{
    /// <summary>The MCP protocol version announced (the proxy's <c>MCP_PROTOCOL_VERSION</c>).</summary>
    public const string ProtocolVersion = "2025-03-26";

    private const string PathPrefix = "/mcp/";

    /// <summary>The server name in <c>/mcp/{server}[/...]</c>, URL-unescaped; null when the path names none.</summary>
    public static string? ServerFromPath(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        if (!absolutePath.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = absolutePath[PathPrefix.Length..];
        var slash = rest.IndexOf('/');
        var segment = slash >= 0 ? rest[..slash] : rest;
        return segment.Length == 0 ? null : Uri.UnescapeDataString(segment);
    }

    /// <summary>
    /// Answers one JSON-RPC message posted to <paramref name="server"/>: <c>initialize</c>, notifications (202),
    /// <c>ping</c>, <c>tools/list</c> and <c>tools/call</c>; unknown methods are <c>-32601</c> and any other failure
    /// <c>-32603</c> with its message. A sample limit, an approver's termination and cancellation of
    /// <paramref name="cancellationToken"/> propagate to the caller.
    /// </summary>
    public static async Task<McpHttpReply> HandleAsync(
        BridgedToolRegistry registry,
        IReadOnlyList<ApprovalPolicy>? approval,
        string? server,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (server is null)
        {
            return Reply(JsonRpcError(null, -32600, "Invalid path: expected /mcp/{server_name}"));
        }

        if (body is JsonArray)
        {
            return Reply(JsonRpcError(null, -32600, "Batch requests are not supported"));
        }

        if (body is not JsonObject message)
        {
            return Reply(JsonRpcError(null, -32600, "Invalid request: expected a JSON-RPC message object"));
        }

        var id = message["id"];
        var method = message["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var name) ? name : null;
        try
        {
            if (method == "initialize")
            {
                return Reply(JsonRpcResult(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = server, ["version"] = "1.0.0" },
                }));
            }

            if (!message.ContainsKey("id") || method?.StartsWith("notifications/", StringComparison.Ordinal) == true)
            {
                return new McpHttpReply(202, null);
            }

            switch (method)
            {
                case "ping":
                    return Reply(JsonRpcResult(id, new JsonObject()));

                case "tools/list":
                    return Reply(JsonRpcResult(id, new JsonObject { ["tools"] = ListTools(registry, server) }));

                case "tools/call":
                    {
                        var parameters = message["params"] as JsonObject;
                        var tool = parameters?["name"] is JsonValue toolValue && toolValue.TryGetValue<string>(out var toolName) ? toolName : null;
                        if (string.IsNullOrEmpty(tool))
                        {
                            return Reply(JsonRpcError(id, -32602, "Missing 'name' in params"));
                        }

                        JsonObject arguments;
                        switch (parameters!["arguments"])
                        {
                            case null:
                                arguments = new JsonObject();
                                break;
                            case JsonObject provided:
                                arguments = provided;
                                break;
                            default:
                                return Reply(JsonRpcError(id, -32602, "Invalid 'arguments' in params: expected an object"));
                        }

                        var result = await CallToolAsync(registry, approval, server, tool, arguments, cancellationToken).ConfigureAwait(false);
                        return Reply(JsonRpcResult(id, result));
                    }

                default:
                    return Reply(JsonRpcError(id, -32601, $"Unknown method: {method ?? "None"}"));
            }
        }
        catch (LimitExceededException)
        {
            throw;
        }
        catch (TerminateSampleException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Reply(JsonRpcError(id, -32603, ex.Message));
        }
    }

    /// <summary>Port of the service's <c>list_tools</c>: <c>[{name, description, inputSchema}]</c> in spec order; an unknown server throws.</summary>
    public static JsonArray ListTools(BridgedToolRegistry registry, string server)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(server);
        if (!registry.TryGetServer(server, out var tools))
        {
            throw new ArgumentException($"Unknown bridged tools server: {server}");
        }

        return new JsonArray(tools.Values.Select(tool => (JsonNode?)new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.Parameters.ToJson(),
        }).ToArray());
    }

    /// <summary>
    /// Port of the service's <c>call_tool</c>: looks up the tool; under active tool approval requires (and consumes) a
    /// matching execution grant, throwing <see cref="BridgedToolDeniedException"/> otherwise; validates the arguments
    /// against the tool schema; runs the tool and returns <c>{content}</c> (<see cref="ResultContent"/>).
    /// </summary>
    public static async Task<JsonObject> CallToolAsync(
        BridgedToolRegistry registry,
        IReadOnlyList<ApprovalPolicy>? approval,
        string server,
        string tool,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!registry.TryGetServer(server, out var tools))
        {
            throw new ArgumentException($"Unknown bridged tools server: {server}");
        }

        if (!tools.TryGetValue(tool, out var definition))
        {
            throw new ArgumentException($"Unknown tool '{tool}' in server '{server}'");
        }

        bool approvalRequired;
        using (ToolApproval.BeginIfAny(approval))
        {
            approvalRequired = ToolApproval.HaveToolApproval;
        }

        if (approvalRequired && !registry.ConsumeToolExecutionGrant(server, tool, arguments))
        {
            ProviderLogger.WarnOnce($"Denied host tool call '{server}/{tool}': no approved execution grant matched it.");
            throw new BridgedToolDeniedException($"Host tool call '{server}/{tool}' was not approved for execution");
        }

        foreach (var required in definition.Parameters.Required)
        {
            if (!arguments.ContainsKey(required))
            {
                throw new ToolParsingError($"Required parameter {required} not provided to tool call.");
            }
        }

        ToolInputValidator.Validate(arguments, definition.Parameters);
        var result = await definition.Execute(arguments, cancellationToken).ConfigureAwait(false) ?? ToolResult.Empty;
        return new JsonObject { ["content"] = ResultContent(result) };
    }

    /// <summary>
    /// A tool result as MCP content blocks: text becomes one text block; in a content list, text stays text, a
    /// data-URI image becomes an image block, an image URL a text block carrying the URL, and anything else a text
    /// block holding its eval-log JSON.
    /// </summary>
    public static JsonArray ResultContent(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Contents is not { } contents)
        {
            return new JsonArray(TextBlock(result.Text ?? ""));
        }

        var blocks = new JsonArray();
        foreach (var content in contents)
        {
            switch (content)
            {
                case ContentText text:
                    blocks.Add(TextBlock(text.Text));
                    break;
                case ContentImage image when InlineMedia.IsDataUri(image.Image):
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = InlineMedia.DataUriToBase64(image.Image),
                        ["mimeType"] = InlineMedia.DataUriMimeType(image.Image) ?? "image/png",
                    });
                    break;
                case ContentImage image:
                    blocks.Add(TextBlock(image.Image));
                    break;
                case null:
                    break;
                default:
                    blocks.Add(TextBlock(PythonJson.Dumps(JsonSerializer.SerializeToNode<Content>(content, EvalLogWriter.Options))));
                    break;
            }
        }

        return blocks;
    }

    /// <summary>A JSON-RPC 2.0 success response (the proxy's <c>_jsonrpc_response</c> body).</summary>
    public static JsonObject JsonRpcResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    /// <summary>A JSON-RPC 2.0 error response (the proxy's <c>_jsonrpc_error</c> body).</summary>
    public static JsonObject JsonRpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static McpHttpReply Reply(JsonObject body) => new(200, body);

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
}
