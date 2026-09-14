using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>
/// Scaffold tools on the Responses provider (port of <c>openai_responses_tools</c>, <c>model/_openai_responses.py:567-622</c>):
/// verbatim function params, namespace regrouping and <c>namespace</c> on replayed function calls.
/// </summary>
public class ResponsesNamespaceToolsTests
{
    private const string SpawnAgentParam =
        """{"type": "function", "name": "spawn_agent", "description": "Spawn a sub-agent", "strict": false, "parameters": {"type": "object", "properties": {"message": {"type": "string", "encrypted": true}, "items": {"type": "array"}}}}""";

    private static ToolInfo Function(string name, JsonObject? options = null) => new(name, $"{name} tool")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["x"] = ToolParam.Of("string") },
            Required = ["x"],
        },
        Options = options,
    };

    private static JsonObject Tag(string name, string description) =>
        new() { [ResponsesTools.NamespaceOption] = new JsonArray(name, description) };

    private static IReadOnlyList<ChatMessage> NamespacedConversation() =>
    [
        new ChatMessageUser("delegate this"),
        new ChatMessageAssistant("", toolCalls:
        [
            new ToolCall("call_1", "spawn_agent", new JsonObject { ["message"] = "go" }),
            new ToolCall("call_2", "bash", new JsonObject { ["x"] = "ls" }),
        ]),
        new ChatMessageTool("spawned", "call_1", "spawn_agent"),
        new ChatMessageTool("listed", "call_2", "bash"),
    ];

    private static IReadOnlyList<ToolInfo> NamespacedTools() =>
    [
        Function("spawn_agent", new JsonObject
        {
            [ResponsesTools.VerbatimOption] = JsonNode.Parse(SpawnAgentParam),
            [ResponsesTools.NamespaceOption] = new JsonArray("multi_agent_v1", "Collaboration"),
        }),
        Function("bash"),
    ];

    private static void AssertNamespacedReplay(JsonArray input)
    {
        var calls = input.Select(i => i!.AsObject()).Where(i => i["type"]!.GetValue<string>() == "function_call").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(("spawn_agent", "multi_agent_v1"), (calls[0]["name"]!.GetValue<string>(), calls[0]["namespace"]!.GetValue<string>()));
        Assert.False(calls[1].ContainsKey("namespace"));
    }

    [Fact]
    public void verbatim_function_params_are_resent_byte_for_byte()
    {
        var tool = Function("spawn_agent", new JsonObject { [ResponsesTools.VerbatimOption] = JsonNode.Parse(SpawnAgentParam) });
        var python = Function("python", new JsonObject { [ResponsesTools.VerbatimOption] = JsonNode.Parse("""{"type": "function", "name": "python", "parameters": {"type": "object"}}""") });

        var parameters = ResponsesTools.ToolParams([tool, python]);

        Assert.Equal(SpawnAgentParam, PythonJson.Dumps(parameters[0]));
        Assert.Equal("""{"type": "function", "name": "python", "parameters": {"type": "object"}}""", PythonJson.Dumps(parameters[1]));
    }

    [Fact]
    public void verbatim_custom_params_are_sent_as_functions()
    {
        var tool = new ToolInfo("apply_patch", "Apply a patch")
        {
            Parameters = new ToolParams
            {
                Properties = new Dictionary<string, ToolParam> { ["input"] = ToolParam.Of("string", "Input.") },
                Required = ["input"],
            },
            Options = new JsonObject
            {
                ["custom_format"] = new JsonObject { ["type"] = "grammar", ["syntax"] = "lark", ["definition"] = "start: x" },
                [ResponsesTools.VerbatimOption] = new JsonObject { ["type"] = "custom", ["name"] = "apply_patch", ["format"] = new JsonObject { ["type"] = "grammar" } },
            },
        };

        var param = Assert.Single(ResponsesTools.ToolParams([tool]))!;

        Assert.Equal("function", param["type"]!.GetValue<string>());
        Assert.Equal(PythonJson.Dumps(ResponsesTools.ToolParam(tool)), PythonJson.Dumps(param));
    }

    [Fact]
    public void namespaced_tools_are_regrouped_after_the_ungrouped_tools_in_first_seen_order()
    {
        var tools = new[]
        {
            Function("a", Tag("ns_one", "First")),
            Function("b"),
            Function("c", Tag("ns_two", "Second")),
            Function("d", Tag("ns_one", "First")),
            Function("e"),
        };

        var parameters = ResponsesTools.ToolParams(tools);

        Assert.Equal(["b", "e", "ns_one", "ns_two"], parameters.Select(p => p!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(["function", "function", "namespace", "namespace"], parameters.Select(p => p!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal(("First", "Second"), (parameters[2]!["description"]!.GetValue<string>(), parameters[3]!["description"]!.GetValue<string>()));
        Assert.Equal(["a", "d"], parameters[2]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(["c"], parameters[3]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(PythonJson.Dumps(ResponsesTools.ToolParam(tools[0])), PythonJson.Dumps(parameters[2]!["tools"]![0]));
    }

    [Fact]
    public void untagged_tools_produce_exactly_the_flat_function_params()
    {
        var tools = new[] { Function("python"), Function("bash", new JsonObject { ["custom_format"] = "x" }) };

        var parameters = ResponsesTools.ToolParams(tools);

        Assert.Equal(PythonJson.Dumps(new JsonArray(tools.Select(t => (JsonNode?)ResponsesTools.ToolParam(t)).ToArray())), PythonJson.Dumps(parameters));
        Assert.Equal("python_exec", parameters[0]!["name"]!.GetValue<string>());
        Assert.Empty(ResponsesTools.Namespaces(tools));
    }

    [Fact]
    public void a_json_array_tag_read_back_from_a_log_still_groups_and_malformed_tags_do_not()
    {
        var logged = JsonNode.Parse("""{"name": "spawn_agent", "options": {"__responses_namespace__": ["multi_agent_v1", "Collaboration"]}}""")!;
        var tool = Function("spawn_agent", logged["options"]!.DeepClone().AsObject());
        var oneElement = Function("other", JsonNode.Parse("""{"__responses_namespace__": ["only_name"]}""")!.AsObject());
        var numeric = Function("third", JsonNode.Parse("""{"__responses_namespace__": ["ns", 5]}""")!.AsObject());

        var parameters = ResponsesTools.ToolParams([tool, oneElement, numeric]);

        Assert.Equal(["other", "third", "multi_agent_v1"], parameters.Select(p => p!["name"]!.GetValue<string>()).ToArray());
        var namespaces = ResponsesTools.Namespaces([tool, oneElement, numeric]);
        Assert.Equal(("spawn_agent", "multi_agent_v1"), (Assert.Single(namespaces).Key, namespaces["spawn_agent"]));
    }

    [Fact]
    public void namespaces_map_tool_names_and_the_last_tag_wins()
    {
        var namespaces = ResponsesTools.Namespaces([Function("send", Tag("ns_one", "First")), Function("plain"), Function("send", Tag("ns_two", "Second"))]);

        var entry = Assert.Single(namespaces);
        Assert.Equal(("send", "ns_two"), (entry.Key, entry.Value));
    }

    [Fact]
    public void function_call_items_carry_a_namespace_only_when_given()
    {
        var call = new ToolCall("c", "spawn_agent", new JsonObject());

        Assert.Equal(
            """{"type": "function_call", "call_id": "c", "name": "spawn_agent", "arguments": "{}", "namespace": "multi_agent_v1"}""",
            PythonJson.Dumps(ResponsesInput.FunctionCallItem(call, "multi_agent_v1")));
        Assert.False(ResponsesInput.FunctionCallItem(call).ContainsKey("namespace"));
    }

    [Fact]
    public void build_request_replays_namespaced_function_calls_with_their_namespace()
    {
        var request = new ResponsesProtocol("gpt-5.6-sol", new Dictionary<string, object?>())
            .BuildRequest(NamespacedConversation(), NamespacedTools(), ToolChoice.Auto, new GenerateConfig(), streaming: false);

        AssertNamespacedReplay(request["input"]!.AsArray());
        Assert.Equal(["bash", "multi_agent_v1"], request["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(SpawnAgentParam, PythonJson.Dumps(request["tools"]![1]!["tools"]![0]));
    }

    [Fact]
    public void direct_openai_input_replays_namespaced_function_calls_with_their_namespace()
    {
        AssertNamespacedReplay(OpenAIModelApi.DirectInput(NamespacedConversation(), synthesizePhase: false, ResponsesTools.Namespaces(NamespacedTools())));
        Assert.DoesNotContain(
            OpenAIModelApi.DirectInput(NamespacedConversation(), synthesizePhase: false),
            item => item!.AsObject().ContainsKey("namespace"));

        using var api = new OpenAIModelApi("gpt-5.6-sol", baseUrl: "https://api.openai.com/v1", apiKey: "test");
        var request = api.BuildRequest(NamespacedConversation(), NamespacedTools(), ToolChoice.Auto, new GenerateConfig(), streaming: false);

        AssertNamespacedReplay(request["input"]!.AsArray());
        Assert.Equal("namespace", request["tools"]![1]!["type"]!.GetValue<string>());
    }
}
