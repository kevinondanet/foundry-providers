using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// A parsed OpenAI Responses request: what <c>AgentBridge.GenerateAsync</c> needs, plus the state the response
/// is built from.
/// </summary>
/// <param name="Model">The requested model name.</param>
/// <param name="Messages">The conversation, with <c>instructions</c> at index 0 as a system message.</param>
/// <param name="Tools">The tools the model sees. Namespaced tools are flattened and tagged; web search is a marker.</param>
/// <param name="ToolChoice">The tool choice, relaxed to <c>auto</c> when it names an absent tool.</param>
/// <param name="Config">The generation config taken from the request.</param>
/// <param name="Stream">Whether the client asked for a stream.</param>
/// <param name="ToolNamespaces">Inner tool name to namespace name; the last declaration wins.</param>
/// <param name="CustomToolNames">Freeform tools, answered as <c>custom_tool_call</c>.</param>
/// <param name="ToolsEcho">The declared tools, raw (echoed in the response).</param>
/// <param name="ToolChoiceEcho">The raw <c>tool_choice</c> (echoed in the response; <c>auto</c> when null).</param>
/// <param name="ParallelToolCalls">Echo only: the request's <c>parallel_tool_calls</c>, true when absent (<c>responses_impl.py:221</c>).</param>
public sealed record ResponsesBridgeRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolInfo> Tools,
    ToolChoice ToolChoice,
    GenerateConfig Config,
    bool Stream,
    IReadOnlyDictionary<string, string> ToolNamespaces,
    IReadOnlySet<string> CustomToolNames,
    JsonArray ToolsEcho,
    JsonNode? ToolChoiceEcho,
    bool ParallelToolCalls);

/// <summary>
/// Port of <c>agent/_bridge/responses_impl.py</c> (<c>inspect_responses_api_request_impl</c> and its conversions)
/// and of the proxy's Responses stream synthesis (<c>proxy.py:697-1415</c>). This class is pure: it parses an
/// OpenAI Responses request into Inspect messages, tools, tool choice and config, and turns a
/// <see cref="ModelOutput"/> back into a response object and its SSE events. The HTTP handler lives in
/// <see cref="SandboxAgentBridge"/>.
/// </summary>
public static partial class ResponsesBridgeApi
{
    /// <summary>
    /// The options key of a declared web search tool. It is the same literal as <c>BridgeBuiltinTools.WebSearchMarker</c>,
    /// which applies the web-search grant; that constant replaces this one after the wave-1 merge.
    /// </summary>
    private const string WebSearchMarker = "__bridge_web_search__";

    internal const string PreviousResponseIdDroppedWarning =
        "The bridged agent sent 'previous_response_id', which the agent bridge does not support (its response ids are synthetic); the field was dropped.";

    private static readonly string[] ExtraBodyFields =
    [
        "service_tier", "max_tool_calls", "metadata", "previous_response_id", "prompt_cache_key",
        "prompt_cache_retention", "safety_identifier", "truncation", "store",
    ];

    /// <summary>
    /// The request half of <c>inspect_responses_api_request_impl</c>, which runs these steps in order:
    /// <list type="number">
    /// <item>Check the required parameters.</item>
    /// <item>Merge <c>additional_tools</c> into the tools, then convert and tag them.</item>
    /// <item>Map the tool choice and harvest namespaces.</item>
    /// <item>Convert the messages and check their media.</item>
    /// <item>Build the config and insert <c>instructions</c>.</item>
    /// </list>
    /// A request the bridge cannot serve throws <see cref="BridgeRequestException"/>.
    /// </summary>
    public static ResponsesBridgeRequest ParseRequest(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = BridgeJson.GetString(request, "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            throw MissingParameter("model");
        }

        var input = request["input"];
        if (input is null)
        {
            throw MissingParameter("input");
        }

        if (input is not JsonArray && !(input is JsonValue inputValue && inputValue.TryGetValue<string>(out _)))
        {
            throw new BridgeRequestException($"invalid request field in bridged request (input: expected a string or a list, got {BridgeJson.Describe(input)})");
        }

        // tools: the request's tools plus the tools of any additional_tools input item, deduplicated on (type, name)
        var declared = new List<JsonObject>();
        if (request["tools"] is not null)
        {
            declared.AddRange(BridgeJson.RequireArray(request["tools"], "tools").OfType<JsonObject>());
        }

        var declaredKeys = declared.Select(ToolKey).ToHashSet();
        foreach (var item in InputItemsOfType(input, "additional_tools"))
        {
            foreach (var tool in (item["tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (declaredKeys.Add(ToolKey(tool)))
                {
                    declared.Add(tool);
                }
            }
        }

        if (declared.Any(tool => BridgeJson.GetString(tool, "type") == "computer"))
        {
            throw new BridgeRequestException("computer use is not supported by the agent bridge");
        }

        var toolsEcho = new JsonArray(declared.Select(tool => (JsonNode?)tool.DeepClone()).ToArray());
        var (tools, declaredNamespaces, customToolNames) = ToolsFromResponsesTools(toolsEcho);
        var toolChoice = ToolChoiceFromResponses(request["tool_choice"], tools);

        // namespaces of deferred tools discovered through tool_search (responses_impl.py:285-290)
        var toolNamespaces = new Dictionary<string, string>(declaredNamespaces, StringComparer.Ordinal);
        foreach (var item in InputItemsOfType(input, "tool_search_output"))
        {
            foreach (var discovered in (item["tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (BridgeJson.GetString(discovered, "type") == "namespace")
                {
                    HarvestNamespace(discovered, toolNamespaces);
                }
            }
        }

        var messages = MessagesFromResponsesInput(input, toolNamespaces, model).ToList();
        BridgeMedia.RequireInline(messages);

        var config = GenerateConfigFromResponses(request);
        if (BridgeJson.GetString(request, "instructions") is { Length: > 0 } instructions)
        {
            messages.Insert(0, new ChatMessageSystem(instructions));
        }

        return new ResponsesBridgeRequest(
            model,
            messages,
            tools,
            toolChoice,
            config,
            BridgeJson.GetBool(request, "stream") ?? false,
            toolNamespaces,
            customToolNames,
            toolsEcho,
            request["tool_choice"]?.DeepClone(),
            BridgeJson.GetBool(request, "parallel_tool_calls") ?? true);
    }

    /// <summary>
    /// Adapts a parsed request to the model that serves it. <c>extra_body</c> (<c>store</c>, <c>prompt_cache_key</c>, …)
    /// is kept only for an <see cref="OpenAIResponsesModelApi"/>; other providers would forward it wholesale.
    /// <c>previous_response_id</c> is always dropped, with a one-time warning, because bridge response ids are synthetic.
    /// </summary>
    public static ResponsesBridgeRequest ForServedModel(ResponsesBridgeRequest parsed, Model served)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(served);
        var extraBody = parsed.Config.ExtraBody;
        if (extraBody is not null && extraBody.ContainsKey("previous_response_id"))
        {
            ProviderLogger.WarnOnce(PreviousResponseIdDroppedWarning);
            extraBody = extraBody.DeepClone().AsObject();
            extraBody.Remove("previous_response_id");
        }

        if (served.Api is not OpenAIResponsesModelApi || extraBody is { Count: 0 })
        {
            extraBody = null;
        }

        return ReferenceEquals(extraBody, parsed.Config.ExtraBody)
            ? parsed
            : parsed with { Config = parsed.Config with { ExtraBody = extraBody } };
    }

    /// <summary>
    /// Port of <c>tools_from_responses_tool</c> and <c>tool_from_responses_tool</c> (<c>responses_impl.py:458-595</c>),
    /// with this port's supported subset.
    /// <list type="bullet">
    /// <item><c>function</c> keeps its original param under <see cref="ResponsesTools.VerbatimOption"/>.</item>
    /// <item><c>custom</c> becomes a function with one required string <c>input</c>, plus its grammar in the description.</item>
    /// <item><c>namespace</c> is flattened; each inner function or custom tool is tagged with <see cref="ResponsesTools.NamespaceOption"/>.</item>
    /// <item><c>web_search</c> and <c>web_search_2025_08_26</c> become the web-search marker tool.</item>
    /// <item>Every other type is skipped with Python's one-time warning.</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<ToolInfo> Tools, IReadOnlyDictionary<string, string> Namespaces, IReadOnlySet<string> Custom) ToolsFromResponsesTools(JsonArray tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = new List<ToolInfo>();
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var custom = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (BridgeJson.GetString(tool, "type") != "namespace")
            {
                if (ToolFromResponsesTool(tool, custom) is { } info)
                {
                    result.Add(info);
                }

                continue;
            }

            HarvestNamespace(tool, namespaces);
            var namespaceName = BridgeJson.GetString(tool, "name");
            var namespaceDescription = BridgeJson.GetString(tool, "description") is { Length: > 0 } description ? description : namespaceName;
            foreach (var inner in (tool["tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (ToolFromResponsesTool(inner, custom) is not { } innerInfo)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(namespaceName) && BridgeJson.GetString(inner, "type") is "function" or "custom")
                {
                    var options = innerInfo.Options?.DeepClone().AsObject() ?? new JsonObject();
                    options[ResponsesTools.NamespaceOption] = new JsonArray(namespaceName, namespaceDescription);
                    innerInfo = innerInfo with { Options = options };
                }

                result.Add(innerInfo);
            }
        }

        return (result, namespaces, custom);
    }

    /// <summary>
    /// Port of <c>tool_choice_from_responses_tool_choice</c> followed by <c>relax_tool_choice_for_withheld</c>.
    /// <list type="bullet">
    /// <item><c>auto</c> and <c>none</c> pass through; <c>required</c> becomes <c>any</c>.</item>
    /// <item>A <c>function</c> or <c>mcp</c> object becomes a function choice.</item>
    /// <item><c>allowed_tools</c> and <c>custom</c> are rejected.</item>
    /// <item>Any other typed object becomes a function choice named by its type.</item>
    /// </list>
    /// A function choice naming a tool that is not in <paramref name="tools"/> becomes <c>auto</c>, as does an absent choice.
    /// </summary>
    public static ToolChoice ToolChoiceFromResponses(JsonNode? toolChoice, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var choice = toolChoice switch
        {
            JsonValue value when value.TryGetValue<string>(out var preset) => preset switch
            {
                "auto" => ToolChoice.Auto,
                "none" => ToolChoice.None,
                "required" => ToolChoice.Any,
                _ => null,
            },
            JsonObject obj => TypedToolChoice(obj),
            _ => null,
        };
        if (choice is ToolFunction function && !tools.Any(tool => tool.Name == function.Name))
        {
            return ToolChoice.Auto;
        }

        return choice ?? ToolChoice.Auto;
    }

    /// <summary>
    /// Port of <c>generate_config_from_openai_responses</c> (<c>responses_impl.py:651-712</c>) without
    /// <c>instructions</c>, which <see cref="ParseRequest"/> inserts as a system message. <c>parallel_tool_calls</c> is
    /// structural, not a generation parameter, so the bridge forwards it even when it does not forward generation config.
    /// <c>ExtraBody</c> takes only the Responses passthrough fields that are present.
    /// </summary>
    public static GenerateConfig GenerateConfigFromResponses(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var param in new[] { "background", "prompt" })
        {
            if (request.ContainsKey(param))
            {
                ProviderLogger.WarnOnce($"'{param}' option not supported for agent bridge");
            }
        }

        var include = BridgeJson.GetStringList(request, "include") ?? [];
        var reasoning = request["reasoning"] as JsonObject;
        ResponseSchema? responseSchema = null;
        string? verbosity = null;
        if (BridgeJson.RequestObject(request["text"], "text") is { } text)
        {
            if (BridgeJson.RequestObject(text["format"], "text.format") is { } format && BridgeJson.GetString(format, "type") == "json_schema")
            {
                responseSchema = ResponseSchemaFromFormat(format);
            }

            verbosity = BridgeJson.GetString(text, "verbosity");
        }

        JsonObject? extraBody = null;
        foreach (var field in ExtraBodyFields)
        {
            if (request.TryGetPropertyValue(field, out var value))
            {
                (extraBody ??= new JsonObject())[field] = value?.DeepClone();
            }
        }

        return new GenerateConfig
        {
            MaxTokens = BridgeJson.GetInt(request, "max_output_tokens"),
            Logprobs = include.Contains("message.output_text.logprobs") ? true : null,
            TopLogprobs = BridgeJson.GetInt(request, "top_logprobs"),
            ParallelToolCalls = BridgeJson.GetBool(request, "parallel_tool_calls"),
            ReasoningEffort = reasoning is null ? null : BridgeJson.GetString(reasoning, "effort"),
            ReasoningSummary = reasoning is null ? null : BridgeJson.GetString(reasoning, "summary"),
            Temperature = BridgeJson.GetDouble(request, "temperature"),
            TopP = BridgeJson.GetDouble(request, "top_p"),
            ResponseSchema = responseSchema,
            Verbosity = verbosity,
            ExtraBody = extraBody,
        };
    }

    internal static string UnsupportedToolWarning(string? type) =>
        $"ToolParam of type '{type ?? "None"}' not supported by the agent bridge; ignoring this tool.";

    private static BridgeRequestException MissingParameter(string name) =>
        new($"Missing required parameter: '{name}'.") { Param = name, Code = "missing_required_parameter" };

    private static (string? Type, string? Name) ToolKey(JsonObject tool) =>
        (BridgeJson.GetString(tool, "type"), BridgeJson.GetString(tool, "name"));

    private static IEnumerable<JsonObject> InputItemsOfType(JsonNode input, string type) =>
        input is JsonArray items
            ? items.OfType<JsonObject>().Where(item => BridgeJson.GetString(item, "type") == type)
            : [];

    /// <summary>Port of <c>_harvest_tool_namespaces</c>: every named inner tool maps to the namespace (last wins).</summary>
    private static void HarvestNamespace(JsonObject namespaceTool, Dictionary<string, string> namespaces)
    {
        if (BridgeJson.GetString(namespaceTool, "name") is not { } namespaceName)
        {
            return;
        }

        foreach (var inner in (namespaceTool["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (BridgeJson.GetString(inner, "name") is { } innerName)
            {
                namespaces[innerName] = namespaceName;
            }
        }
    }

    private static ToolInfo? ToolFromResponsesTool(JsonObject tool, HashSet<string> customToolNames)
    {
        var type = BridgeJson.GetString(tool, "type");
        switch (type)
        {
            case "function":
            {
                var name = RequireToolName(tool);
                return new ToolInfo(name, BridgeJson.GetString(tool, "description") is { Length: > 0 } description ? description : name)
                {
                    Parameters = BridgeJson.ToolParamsFromSchema(BridgeJson.RequestObject(tool["parameters"], "tools[].parameters")),
                    Options = new JsonObject { [ResponsesTools.VerbatimOption] = tool.DeepClone() },
                };
            }

            case "custom":
            {
                var name = RequireToolName(tool);
                var description = BridgeJson.GetString(tool, "description") is { Length: > 0 } given ? given : name;
                if (tool["format"] is JsonObject format && BridgeJson.GetString(format, "type") == "grammar")
                {
                    description += $"\n\nThe input argument is the raw tool input (not JSON) and must match this {BridgeJson.GetString(format, "syntax")} grammar:\n{BridgeJson.GetString(format, "definition")}";
                }

                customToolNames.Add(name);
                return new ToolInfo(name, description)
                {
                    Parameters = new ToolParams
                    {
                        Properties = new Dictionary<string, ToolParam>(StringComparer.Ordinal) { ["input"] = ToolParam.Of("string", "Input.") },
                        Required = ["input"],
                    },
                    Options = new JsonObject
                    {
                        ["custom_format"] = tool["format"]?.DeepClone(),
                        [ResponsesTools.VerbatimOption] = tool.DeepClone(),
                    },
                };
            }

            case "web_search" or "web_search_2025_08_26":
                return new ToolInfo("web_search", "Search the web.")
                {
                    Options = new JsonObject { [WebSearchMarker] = tool.DeepClone() },
                };

            default:
                ProviderLogger.WarnOnce(UnsupportedToolWarning(type));
                return null;
        }
    }

    private static string RequireToolName(JsonObject tool) =>
        BridgeJson.GetString(tool, "name") ?? throw new BridgeRequestException("invalid request field in bridged request (tools[].name: expected a string)");

    private static ToolChoice? TypedToolChoice(JsonObject choice)
    {
        var type = BridgeJson.GetString(choice, "type");
        return type switch
        {
            null => null,
            "function" => new ToolFunction(BridgeJson.GetString(choice, "name")
                ?? throw new BridgeRequestException("invalid request field in bridged request (tool_choice.name: expected a string)")),
            "mcp" => new ToolFunction(BridgeJson.GetString(choice, "name")
                ?? throw new BridgeRequestException("MCP server tool choice requires 'name' field for agent bridge")),
            "allowed_tools" => throw new BridgeRequestException("ToolChoiceAllowedParam not supported by agent bridge"),
            "custom" => throw new BridgeRequestException("ToolChoiceCustomParam not supported by agent bridge"),
            "web_search_preview" or "web_search_preview_2025_03_11" => new ToolFunction("web_search"),
            "code_interpreter" => new ToolFunction("code_execution"),
            _ => new ToolFunction(type),
        };
    }

    /// <summary>Port of <c>client_response_schema</c> and <c>client_json_schema</c> for <c>text.format</c>: an invalid value answers 400.</summary>
    private static ResponseSchema ResponseSchemaFromFormat(JsonObject format)
    {
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromJson(format["schema"] ?? new JsonObject());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new BridgeRequestException($"invalid response schema in bridged request (text.format.schema: {ex.Message})");
        }

        var name = format.ContainsKey("name") ? BridgeJson.GetString(format, "name") : "schema";
        if (string.IsNullOrEmpty(name))
        {
            throw new BridgeRequestException("invalid response schema in bridged request (text.format.name: expected a non-empty string)");
        }

        return new ResponseSchema(name, schema)
        {
            Description = BridgeJson.GetString(format, "description"),
            Strict = BridgeJson.GetBool(format, "strict"),
        };
    }
}
