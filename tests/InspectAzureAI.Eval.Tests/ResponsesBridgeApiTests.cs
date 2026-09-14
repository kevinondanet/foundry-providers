using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port-level behaviour of <c>agent/_bridge/responses_impl.py</c>: request parsing and the response body.</summary>
public class ResponsesBridgeApiTests
{
    private const string WebSearchMarker = "__bridge_web_search__";

    private const string CodexRequest = """
        {
          "model": "gpt-5.4-mini",
          "instructions": "You are Codex.",
          "input": [
            {"type": "message", "role": "developer", "content": [{"type": "input_text", "text": "AGENTS.md says hi"}]},
            {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "fix the bug"}]},
            {"type": "reasoning", "id": "rs_1", "summary": [{"type": "summary_text", "text": "plan"}], "encrypted_content": "ENC"},
            {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "Looking."}]},
            {"type": "function_call", "call_id": "call_1", "name": "shell", "arguments": "{\"command\": [\"ls\"]}"},
            {"type": "custom_tool_call", "call_id": "call_2", "name": "apply_patch", "input": "*** Begin Patch"},
            {"type": "function_call_output", "call_id": "call_1", "output": "file.txt"},
            {"type": "custom_tool_call_output", "call_id": "call_2", "output": [{"type": "input_text", "text": "Done"}, {"type": "input_image", "image_url": "data:image/png;base64,AAAA"}]},
            {"type": "additional_tools", "role": "developer", "tools": [
              {"type": "function", "name": "extra_tool", "description": "Extra", "parameters": {"type": "object", "properties": {}}},
              {"type": "function", "name": "shell", "parameters": {"type": "object", "properties": {}}}
            ]}
          ],
          "tools": [
            {"type": "function", "name": "shell", "description": "Run a command", "strict": false, "parameters": {"type": "object", "properties": {"command": {"type": "array", "items": {"type": "string"}}}, "required": ["command"], "additionalProperties": false}},
            {"type": "custom", "name": "apply_patch", "description": "Apply a patch", "format": {"type": "grammar", "syntax": "lark", "definition": "start: patch"}},
            {"type": "namespace", "name": "multi_agent_v1", "description": "Collaboration tools", "tools": [
              {"type": "function", "name": "spawn_agent", "description": "Spawn", "strict": false, "parameters": {"type": "object", "properties": {"message": {"type": "string", "encrypted": true}}}}
            ]},
            {"type": "tool_search", "execution": "client", "description": "Search tools", "parameters": {"type": "object", "properties": {}}},
            {"type": "web_search", "external_web_access": true},
            {"type": "web_search_preview"}
          ],
          "tool_choice": "auto",
          "parallel_tool_calls": false,
          "reasoning": {"effort": "medium", "summary": "auto"},
          "store": false,
          "stream": true,
          "include": ["reasoning.encrypted_content"],
          "prompt_cache_key": "conv-1"
        }
        """;

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void codex_shaped_request_parses_to_messages_tools_and_config()
    {
        ProviderLogger.Reset();

        var parsed = ResponsesBridgeApi.ParseRequest(Json(CodexRequest));

        Assert.Equal("gpt-5.4-mini", parsed.Model);
        Assert.True(parsed.Stream);
        Assert.False(parsed.ParallelToolCalls);
        Assert.False(parsed.Config.ParallelToolCalls);
        Assert.Equal("auto", parsed.ToolChoice.ToString());
        Assert.Equal("auto", parsed.ToolChoiceEcho!.GetValue<string>());

        Assert.Equal(["system", "system", "user", "assistant", "tool", "tool"], parsed.Messages.Select(m => m.Role).ToArray());
        Assert.Equal("You are Codex.", parsed.Messages[0].Text);
        Assert.Equal("AGENTS.md says hi", parsed.Messages[1].Text);
        Assert.Equal("fix the bug", parsed.Messages[2].Text);

        var assistant = Assert.IsType<ChatMessageAssistant>(parsed.Messages[3]);
        Assert.Equal("gpt-5.4-mini", assistant.Model);
        Assert.Equal(2, assistant.ContentList.Count);
        var reasoning = Assert.IsType<ContentReasoning>(assistant.ContentList[0]);
        Assert.Equal(("ENC", "rs_1", true, "plan"), (reasoning.Reasoning, reasoning.Signature, reasoning.Redacted, reasoning.Summary));
        Assert.Equal("Looking.", Assert.IsType<ContentText>(assistant.ContentList[1]).Text);
        Assert.Equal(2, assistant.ToolCalls!.Count);
        var shell = assistant.ToolCalls[0];
        Assert.Equal(("call_1", "shell", "function"), (shell.Id, shell.Function, shell.Type));
        Assert.Equal("""{"command": ["ls"]}""", PythonJson.Dumps(shell.Arguments));
        var patch = assistant.ToolCalls[1];
        Assert.Equal(("call_2", "apply_patch", "custom", "*** Begin Patch"), (patch.Id, patch.Function, patch.Type, patch.Arguments["input"]!.GetValue<string>()));

        var shellOutput = Assert.IsType<ChatMessageTool>(parsed.Messages[4]);
        Assert.True(shellOutput.Content.IsString);
        Assert.Equal(("call_1", "shell", "file.txt"), (shellOutput.ToolCallId, shellOutput.Function, shellOutput.Text));
        var patchOutput = Assert.IsType<ChatMessageTool>(parsed.Messages[5]);
        Assert.Equal(("call_2", "apply_patch"), (patchOutput.ToolCallId, patchOutput.Function));
        Assert.Equal("Done", Assert.IsType<ContentText>(patchOutput.ContentList[0]).Text);
        Assert.Equal("data:image/png;base64,AAAA", Assert.IsType<ContentImage>(patchOutput.ContentList[1]).Image);

        Assert.Equal(["shell", "apply_patch", "spawn_agent", "web_search", "extra_tool"], parsed.Tools.Select(t => t.Name).ToArray());
        Assert.Equal(7, parsed.ToolsEcho.Count);

        var applyPatch = parsed.Tools[1];
        Assert.Equal(["input"], applyPatch.Parameters.Required);
        Assert.Equal(["string"], applyPatch.Parameters.Properties["input"].Type);
        Assert.Equal("Apply a patch\n\nThe input argument is the raw tool input (not JSON) and must match this lark grammar:\nstart: patch", applyPatch.Description);
        Assert.Equal("lark", applyPatch.Options!["custom_format"]!["syntax"]!.GetValue<string>());
        Assert.Equal("custom", applyPatch.Options[ResponsesTools.VerbatimOption]!["type"]!.GetValue<string>());
        Assert.Equal(["apply_patch"], parsed.CustomToolNames.ToArray());

        var spawn = parsed.Tools[2];
        var rawSpawn = Json(CodexRequest)["tools"]![2]!["tools"]![0];
        Assert.Equal(PythonJson.Dumps(rawSpawn), PythonJson.Dumps(spawn.Options![ResponsesTools.VerbatimOption]));
        Assert.Equal("""["multi_agent_v1", "Collaboration tools"]""", PythonJson.Dumps(spawn.Options[ResponsesTools.NamespaceOption]));
        Assert.Equal("multi_agent_v1", parsed.ToolNamespaces["spawn_agent"]);

        Assert.Single(parsed.Tools, t => t.Options?.ContainsKey(WebSearchMarker) == true);
        Assert.Single(ProviderLogger.Warnings, w => w == "ToolParam of type 'web_search_preview' not supported by the agent bridge; ignoring this tool.");
        Assert.Single(ProviderLogger.Warnings, w => w == "ToolParam of type 'tool_search' not supported by the agent bridge; ignoring this tool.");

        Assert.Equal(("medium", "auto"), (parsed.Config.ReasoningEffort, parsed.Config.ReasoningSummary));
        Assert.Equal("""{"prompt_cache_key": "conv-1", "store": false}""", PythonJson.Dumps(parsed.Config.ExtraBody));
    }

    [Fact]
    public void string_input_becomes_one_user_message_after_the_instructions()
    {
        var parsed = ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": "hello", "instructions": "Be brief."}"""));

        Assert.Equal(["system", "user"], parsed.Messages.Select(m => m.Role).ToArray());
        Assert.Equal(("Be brief.", "hello"), (parsed.Messages[0].Text, parsed.Messages[1].Text));
        Assert.False(parsed.Stream);
        Assert.Empty(parsed.Tools);
    }

    [Fact]
    public void namespaces_flatten_with_tags_and_the_last_declaration_wins()
    {
        var (tools, namespaces, custom) = ResponsesBridgeApi.ToolsFromResponsesTools(JsonNode.Parse("""
            [
              {"type": "namespace", "name": "ns_one", "description": "First", "tools": [
                {"type": "function", "name": "send", "parameters": {"type": "object", "properties": {}}},
                {"type": "custom", "name": "raw", "format": {"type": "text"}}
              ]},
              {"type": "namespace", "name": "ns_two", "tools": [
                {"type": "function", "name": "send", "description": "Send it", "parameters": {"type": "object", "properties": {}}}
              ]}
            ]
            """)!.AsArray());

        Assert.Equal(["send", "raw", "send"], tools.Select(t => t.Name).ToArray());
        Assert.Equal("""["ns_one", "First"]""", PythonJson.Dumps(tools[0].Options![ResponsesTools.NamespaceOption]));
        Assert.Equal("""["ns_one", "First"]""", PythonJson.Dumps(tools[1].Options![ResponsesTools.NamespaceOption]));
        Assert.Equal("""["ns_two", "ns_two"]""", PythonJson.Dumps(tools[2].Options![ResponsesTools.NamespaceOption]));
        Assert.Equal(("send", "raw", "Send it"), (tools[0].Description, tools[1].Description, tools[2].Description));
        Assert.Equal(("ns_two", "ns_one"), (namespaces["send"], namespaces["raw"]));
        Assert.Equal(["raw"], custom.ToArray());
    }

    [Fact]
    public void tool_search_output_items_are_harvested_for_namespaces_and_skipped()
    {
        var parsed = ResponsesBridgeApi.ParseRequest(Json("""
            {"model": "m", "input": [
              {"type": "message", "role": "user", "content": "hi"},
              {"type": "tool_search_output", "call_id": "ts_1", "tools": [{"type": "namespace", "name": "deferred", "tools": [{"type": "function", "name": "later"}]}]}
            ]}
            """));

        Assert.Equal("hi", Assert.Single(parsed.Messages).Text);
        Assert.Equal("deferred", parsed.ToolNamespaces["later"]);
        Assert.Empty(parsed.Tools);
    }

    [Theory]
    [InlineData("web_search")]
    [InlineData("web_search_2025_08_26")]
    public void web_search_tools_become_the_marker(string type)
    {
        var raw = Json($$$"""{"type": "{{{type}}}", "filters": {"allowed_domains": ["example.com"]}}""");

        var (tools, _, _) = ResponsesBridgeApi.ToolsFromResponsesTools(new JsonArray(raw.DeepClone()));

        var tool = Assert.Single(tools);
        Assert.Equal(("web_search", "Search the web."), (tool.Name, tool.Description));
        Assert.Equal(PythonJson.Dumps(raw), PythonJson.Dumps(tool.Options![WebSearchMarker]));
    }

    [Theory]
    [InlineData("web_search_preview")]
    [InlineData("web_search_preview_2025_03_11")]
    [InlineData("code_interpreter")]
    [InlineData("mcp")]
    [InlineData("local_shell")]
    [InlineData("image_generation")]
    [InlineData("file_search")]
    [InlineData("tool_search")]
    [InlineData("something_new")]
    public void unsupported_tool_types_are_withheld_with_the_python_warning(string type)
    {
        ProviderLogger.Reset();

        var (tools, _, _) = ResponsesBridgeApi.ToolsFromResponsesTools(new JsonArray(Json($$"""{"type": "{{type}}"}""")));

        Assert.Empty(tools);
        Assert.Equal([$"ToolParam of type '{type}' not supported by the agent bridge; ignoring this tool."], ProviderLogger.Warnings);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("\"auto\"", "auto")]
    [InlineData("\"none\"", "none")]
    [InlineData("\"required\"", "any")]
    [InlineData("{\"type\": \"function\", \"name\": \"shell\"}", "shell")]
    [InlineData("{\"type\": \"function\", \"name\": \"absent\"}", "auto")]
    [InlineData("{\"type\": \"mcp\", \"server_label\": \"s\", \"name\": \"shell\"}", "shell")]
    [InlineData("{\"type\": \"web_search_preview\"}", "web_search")]
    [InlineData("{\"type\": \"web_search_preview_2025_03_11\"}", "web_search")]
    [InlineData("{\"type\": \"code_interpreter\"}", "auto")]
    [InlineData("{\"type\": \"image_generation\"}", "auto")]
    public void tool_choice_maps_and_relaxes_to_auto_for_absent_tools(string? json, string expected)
    {
        IReadOnlyList<ToolInfo> tools = [new ToolInfo("shell", "Run"), new ToolInfo("web_search", "Search the web.")];

        var choice = ResponsesBridgeApi.ToolChoiceFromResponses(json is null ? null : JsonNode.Parse(json), tools);

        Assert.Equal(expected, choice is ToolFunction function ? function.Name : choice.ToString());
    }

    [Theory]
    [InlineData("{\"type\": \"allowed_tools\", \"mode\": \"auto\", \"tools\": []}", "ToolChoiceAllowedParam not supported by agent bridge")]
    [InlineData("{\"type\": \"custom\", \"name\": \"apply_patch\"}", "ToolChoiceCustomParam not supported by agent bridge")]
    [InlineData("{\"type\": \"mcp\", \"server_label\": \"s\"}", "MCP server tool choice requires 'name' field for agent bridge")]
    public void unsupported_tool_choices_are_rejected(string json, string message)
    {
        var ex = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ToolChoiceFromResponses(JsonNode.Parse(json), []));

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("""{"type": "reasoning", "id": "rs_1", "content": [{"type": "reasoning_text", "text": "think"}], "encrypted_content": "ENC", "summary": []}""", "ENC", "think", true, "rs_1")]
    [InlineData("""{"type": "reasoning", "id": "rs_2", "content": [{"type": "reasoning_text", "text": "a"}], "encrypted_content": "ENC", "summary": [{"type": "summary_text", "text": "S1"}, {"type": "summary_text", "text": "S2"}]}""", "ENC", "S1\n\nS2", true, "rs_2")]
    [InlineData("""{"type": "reasoning", "id": "rs_3", "encrypted_content": "ENC", "summary": [{"type": "summary_text", "text": "S"}]}""", "ENC", "S", true, "rs_3")]
    [InlineData("""{"type": "reasoning", "summary": [{"type": "summary_text", "text": "S"}]}""", "", "S", false, null)]
    [InlineData("""{"type": "reasoning", "id": "rs_5", "content": [{"type": "reasoning_text", "text": "a"}, {"type": "reasoning_text", "text": "b"}], "summary": []}""", "a\nb", null, false, "rs_5")]
    public void replay_reasoning_follows_the_bridge_rules(string item, string reasoning, string? summary, bool redacted, string? signature)
    {
        var content = ResponsesBridgeApi.ReplayReasoningFromItem(Json(item));

        Assert.Equal(new ContentReasoning(reasoning, signature, redacted) { Summary = summary }, content);
    }

    [Fact]
    public void assistant_text_strips_content_internal_and_restores_think_tags()
    {
        ProviderLogger.Reset();

        var messages = ResponsesBridgeApi.MessagesFromResponsesInput(JsonNode.Parse("""
            [
              {"role": "assistant", "content": "<think signature=\"sig\">\n<summary>S</summary>\nR\n</think>\nVisible"},
              {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "Answer\n<content-internal>eyJ4IjogMX0=</content-internal>\n"}, {"type": "refusal", "refusal": "No"}]},
              {"type": "web_search_call", "id": "ws_1", "status": "completed", "action": {"type": "search", "query": "q"}}
            ]
            """)!, new Dictionary<string, string>(), "served");

        var assistant = Assert.IsType<ChatMessageAssistant>(Assert.Single(messages));
        Assert.Equal("served", assistant.Model);
        Assert.Null(assistant.ToolCalls);
        Assert.Equal(new ContentReasoning("R", "sig") { Summary = "S" }, assistant.ContentList[0]);
        Assert.Equal(["Visible", "Answer", "No"], assistant.ContentList.Skip(1).Select(c => Assert.IsType<ContentText>(c).Text).ToArray());
        Assert.True(Assert.IsType<ContentText>(assistant.ContentList[3]).Refusal);
        Assert.Contains(ResponsesBridgeApi.WebSearchCallDroppedWarning, ProviderLogger.Warnings);
    }

    [Fact]
    public void replayed_function_calls_with_an_undeclared_namespace_warn_once()
    {
        ProviderLogger.Reset();
        var input = JsonNode.Parse("""[{"type": "function_call", "call_id": "c1", "name": "spawn_agent", "namespace": "multi_agent_v1", "arguments": "{}"}]""")!;

        ResponsesBridgeApi.MessagesFromResponsesInput(input, new Dictionary<string, string> { ["spawn_agent"] = "multi_agent_v1" });
        Assert.Empty(ProviderLogger.Warnings);

        ResponsesBridgeApi.MessagesFromResponsesInput(input, new Dictionary<string, string>());
        Assert.Single(ProviderLogger.Warnings);
    }

    [Fact]
    public void agent_messages_become_attributed_user_messages_with_the_raw_item_in_metadata()
    {
        ProviderLogger.Reset();

        var messages = ResponsesBridgeApi.MessagesFromResponsesInput(JsonNode.Parse("""
            [
              {"type": "agent_message", "author": "worker-1", "content": [{"type": "input_text", "text": "hello"}, {"type": "input_text", "text": "world"}]},
              {"type": "agent_message", "content": [{"type": "encrypted_content", "encrypted_content": "xyz"}]},
              {"type": "agent_message", "author": "worker-2", "content": []}
            ]
            """)!, new Dictionary<string, string>());

        Assert.All(messages, m => Assert.IsType<ChatMessageUser>(m));
        Assert.Equal(
            ["Agent message from worker-1:\nhello\nworld", "Agent message from agent:\n[encrypted content: readable only by OpenAI]", "Agent message from worker-2:\n[no readable content]"],
            messages.Select(m => m.Text).ToArray());
        var raw = Assert.IsAssignableFrom<JsonNode>(messages[0].Metadata!["agent_message"]);
        Assert.Equal("worker-1", raw["author"]!.GetValue<string>());
        Assert.Contains(ResponsesBridgeApi.AgentMessageEncryptedWarning, ProviderLogger.Warnings);
        Assert.Contains(ResponsesBridgeApi.AgentMessageEmptyWarning, ProviderLogger.Warnings);
    }

    [Theory]
    [InlineData("computer_call")]
    [InlineData("computer_call_output")]
    [InlineData("tool_search_call")]
    [InlineData("mcp_call")]
    [InlineData("mcp_list_tools")]
    [InlineData("mcp_approval_request")]
    [InlineData("code_interpreter_call")]
    [InlineData("local_shell_call")]
    [InlineData("image_generation_call")]
    [InlineData("compaction")]
    [InlineData("context_compaction")]
    [InlineData("item_reference")]
    [InlineData("brand_new_type")]
    public void unsupported_input_item_types_are_rejected_naming_the_type(string type)
    {
        var request = Json($$"""{"model": "m", "input": [{"type": "{{type}}", "id": "x", "call_id": "c"}]}""");

        var ex = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ParseRequest(request));

        Assert.Equal($"Type {type} is not supported by the agent bridge", ex.Message);
    }

    [Fact]
    public void non_inline_media_is_rejected_and_data_uris_pass()
    {
        const string Template = """{"model": "m", "input": [{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "look"}, {"type": "input_image", "image_url": "URL"}]}]}""";

        var ex = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ParseRequest(Json(Template.Replace("URL", "https://example.com/cat.png"))));

        Assert.Equal("Bridged image content at message index 0, content index 1 must be an inline 'data:' URI; the agent bridge will not dereference a non-inline reference.", ex.Message);
        var ok = ResponsesBridgeApi.ParseRequest(Json(Template.Replace("URL", "data:image/png;base64,AAAA")));
        Assert.IsType<ContentImage>(ok.Messages[0].ContentList[1]);
    }

    [Theory]
    [InlineData("""{"input": "hi"}""", "model")]
    [InlineData("""{"model": "  ", "input": "hi"}""", "model")]
    [InlineData("""{"model": "m"}""", "input")]
    [InlineData("""{"model": "m", "input": null}""", "input")]
    public void missing_required_parameters_carry_param_and_code(string json, string param)
    {
        var ex = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ParseRequest(Json(json)));

        Assert.Equal($"Missing required parameter: '{param}'.", ex.Message);
        Assert.Equal((param, "missing_required_parameter"), (ex.Param, ex.Code));
    }

    [Fact]
    public void computer_tools_and_non_list_input_are_rejected()
    {
        var computer = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": "hi", "tools": [{"type": "computer", "display_width": 1024}]}""")));
        Assert.Equal("computer use is not supported by the agent bridge", computer.Message);

        var input = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": 5}""")));
        Assert.Equal("invalid request field in bridged request (input: expected a string or a list, got number)", input.Message);
    }

    [Fact]
    public void config_maps_generation_fields_and_keeps_only_the_passthrough_extra_body()
    {
        ProviderLogger.Reset();

        var config = ResponsesBridgeApi.GenerateConfigFromResponses(Json("""
            {
              "model": "m", "input": "hi", "user": "u-1", "instructions": "ignored here",
              "max_output_tokens": 100, "temperature": 0.5, "top_p": 0.9, "top_logprobs": 3,
              "include": ["message.output_text.logprobs"],
              "reasoning": {"effort": "high", "summary": "detailed"},
              "text": {"verbosity": "low", "format": {"type": "json_schema", "name": "answer", "description": "The answer", "strict": true,
                "schema": {"type": "object", "properties": {"value": {"type": "integer"}}, "required": ["value"]}}},
              "service_tier": "flex", "metadata": {"a": "b"}, "previous_response_id": "resp_x", "truncation": "auto", "store": true,
              "background": false, "prompt": {"id": "p"}
            }
            """));

        Assert.Equal(100, config.MaxTokens);
        Assert.Equal(0.5, config.Temperature);
        Assert.Equal(0.9, config.TopP);
        Assert.Equal(3, config.TopLogprobs);
        Assert.True(config.Logprobs);
        Assert.Null(config.ParallelToolCalls);
        Assert.Null(config.SystemMessage);
        Assert.Equal(("high", "detailed", "low"), (config.ReasoningEffort, config.ReasoningSummary, config.Verbosity));
        var schema = config.ResponseSchema!;
        Assert.Equal(("answer", "The answer"), (schema.Name, schema.Description));
        Assert.True(schema.Strict);
        Assert.Equal("integer", schema.JsonSchema.ToJson()["properties"]!["value"]!["type"]!.GetValue<string>());
        Assert.Equal(
            """{"service_tier": "flex", "metadata": {"a": "b"}, "previous_response_id": "resp_x", "truncation": "auto", "store": true}""",
            PythonJson.Dumps(config.ExtraBody));
        Assert.Contains("'background' option not supported for agent bridge", ProviderLogger.Warnings);
        Assert.Contains("'prompt' option not supported for agent bridge", ProviderLogger.Warnings);
    }

    [Fact]
    public void invalid_text_fields_answer_400()
    {
        var schema = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.GenerateConfigFromResponses(Json("""{"text": {"format": {"type": "json_schema", "name": "x", "schema": {"type": 5}}}}""")));
        Assert.StartsWith("invalid response schema in bridged request (text.format.schema: ", schema.Message);

        var text = Assert.Throws<BridgeRequestException>(() => ResponsesBridgeApi.GenerateConfigFromResponses(Json("""{"text": "plain"}""")));
        Assert.Equal("invalid request field in bridged request (text: expected an object, got str)", text.Message);
    }

    [Fact]
    public void parallel_tool_calls_is_forwarded_and_echoed()
    {
        var absent = ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": "hi"}"""));
        Assert.Null(absent.Config.ParallelToolCalls);
        Assert.True(absent.ParallelToolCalls);

        var disabled = ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": "hi", "parallel_tool_calls": false}"""));
        Assert.False(disabled.Config.ParallelToolCalls);
        Assert.False(disabled.ParallelToolCalls);

        var body = ResponsesBridgeApi.ResponseFromOutput(ModelOutput.FromContent("served", MessageContent.FromString("ok"), StopReason.Stop), "served", disabled);
        Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Equal("completed", body["status"]!.GetValue<string>());
        Assert.Null(body["incomplete_details"]);
    }

    [Fact]
    public void for_served_model_keeps_extra_body_only_for_the_responses_provider_and_drops_previous_response_id()
    {
        ProviderLogger.Reset();
        var parsed = ResponsesBridgeApi.ParseRequest(Json("""{"model": "m", "input": "hi", "store": false, "prompt_cache_key": "k", "previous_response_id": "resp_1"}"""));

        var scripted = ResponsesBridgeApi.ForServedModel(parsed, new Model(new ScriptedModelApi()));
        Assert.Null(scripted.Config.ExtraBody);
        Assert.Contains(ResponsesBridgeApi.PreviousResponseIdDroppedWarning, ProviderLogger.Warnings);

        using var responsesApi = new OpenAIResponsesModelApi("gpt-5.6-sol", "https://baseline/models");
        var responses = ResponsesBridgeApi.ForServedModel(parsed, new Model(responsesApi));
        Assert.Equal("""{"prompt_cache_key": "k", "store": false}""", PythonJson.Dumps(responses.Config.ExtraBody));
        Assert.Equal("""{"previous_response_id": "resp_1", "prompt_cache_key": "k", "store": false}""", PythonJson.Dumps(parsed.Config.ExtraBody));
    }

    [Fact]
    public void output_items_render_text_refusals_reasoning_and_calls()
    {
        var message = new ChatMessageAssistant(
            new Content[]
            {
                new ContentReasoning("plan", "rs_9", Redacted: true) { Summary = "sum" },
                new ContentText("Hello"),
                new ContentText("I cannot") { Refusal = true },
                new ContentText(""),
            },
            toolCalls:
            [
                new ToolCall("call_1", "shell", new JsonObject { ["command"] = new JsonArray("ls") }),
                new ToolCall("call_2", "apply_patch", new JsonObject { ["input"] = "*** Begin Patch" }) { Type = "custom" },
                new ToolCall("call_3", "freeform", new JsonObject { ["input"] = "raw" }),
                new ToolCall("call_4", "spawn_agent", new JsonObject { ["message"] = "go" }),
            ]);

        var items = ResponsesBridgeApi.OutputItems(message, new Dictionary<string, string> { ["spawn_agent"] = "multi_agent_v1" }, new HashSet<string> { "freeform" });

        Assert.Equal(
            ["message", "message", "message", "function_call", "custom_tool_call", "custom_tool_call", "function_call"],
            items.Select(i => i!["type"]!.GetValue<string>()).ToArray());
        Assert.All(items, i => Assert.Equal("completed", i!["status"]!.GetValue<string>()));
        Assert.Equal("<think signature=\"rs_9\" redacted=\"true\">\n<summary>sum</summary>\nplan\n</think>", items[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.StartsWith("msg_", items[1]!["id"]!.GetValue<string>());
        Assert.Equal("""{"type": "output_text", "text": "I cannot", "annotations": [], "logprobs": []}""", PythonJson.Dumps(items[2]!["content"]![0]));

        var shell = items[3]!.AsObject();
        Assert.StartsWith("fc_", shell["id"]!.GetValue<string>());
        Assert.Equal(("call_1", "shell", """{"command": ["ls"]}"""), (shell["call_id"]!.GetValue<string>(), shell["name"]!.GetValue<string>(), shell["arguments"]!.GetValue<string>()));
        Assert.False(shell.ContainsKey("namespace"));

        Assert.StartsWith("ctc_", items[4]!["id"]!.GetValue<string>());
        Assert.Equal(("call_2", "apply_patch", "*** Begin Patch"), (items[4]!["call_id"]!.GetValue<string>(), items[4]!["name"]!.GetValue<string>(), items[4]!["input"]!.GetValue<string>()));
        Assert.Equal(("call_3", "freeform", "raw"), (items[5]!["call_id"]!.GetValue<string>(), items[5]!["name"]!.GetValue<string>(), items[5]!["input"]!.GetValue<string>()));

        Assert.Equal(("multi_agent_v1", """{"message": "go"}"""), (items[6]!["namespace"]!.GetValue<string>(), items[6]!["arguments"]!.GetValue<string>()));
    }

    [Fact]
    public void empty_text_without_calls_is_kept_and_other_content_is_skipped_with_a_warning()
    {
        ProviderLogger.Reset();

        var items = ResponsesBridgeApi.OutputItems(
            new ChatMessageAssistant(new Content[] { new ContentText(""), new ContentImage("data:image/png;base64,AAAA"), new ContentToolUse("mcp_call", "mcp_1", "lookup", "{}", "{}") }),
            new Dictionary<string, string>(),
            new HashSet<string>());

        Assert.Equal("", Assert.Single(items)!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, ProviderLogger.Warnings.Count);
    }

    [Fact]
    public void web_search_tool_use_becomes_a_web_search_call_that_fails_on_error()
    {
        var message = new ChatMessageAssistant(new Content[]
        {
            new ContentToolUse("web_search", "srvtoolu_1", "web_search", """{"type": "search", "queries": ["cats"]}""", "[]"),
            new ContentToolUse("web_search", "srvtoolu_2", "web_search", """{"query": "dogs"}""", "") { Error = "max_uses_exceeded" },
        });

        var items = ResponsesBridgeApi.OutputItems(message, new Dictionary<string, string>(), new HashSet<string>());

        Assert.Equal("""{"id": "srvtoolu_1", "type": "web_search_call", "status": "completed", "action": {"type": "search", "queries": ["cats"], "query": "cats"}}""", PythonJson.Dumps(items[0]));
        Assert.Equal("""{"id": "srvtoolu_2", "type": "web_search_call", "status": "failed", "action": {"type": "search", "query": "dogs"}}""", PythonJson.Dumps(items[1]));
    }

    [Theory]
    [InlineData("""{"type": "search", "queries": ["a", "b"]}""", """{"type": "search", "queries": ["a", "b"], "query": "a"}""")]
    [InlineData("""{"type": "search", "query": "q", "sources": null}""", """{"type": "search", "query": "q"}""")]
    [InlineData("""{"type": "search", "queries": []}""", """{"type": "search", "queries": [], "query": ""}""")]
    [InlineData("""{"type": "open_page", "url": "https://example.com"}""", """{"type": "open_page", "url": "https://example.com"}""")]
    [InlineData("""{"type": "find", "pattern": "p", "url": "https://example.com"}""", """{"type": "find_in_page", "pattern": "p", "url": "https://example.com"}""")]
    [InlineData("""{"query": "cats", "type": "web_search_20250305"}""", """{"type": "search", "query": "cats"}""")]
    [InlineData("""{"type":"open_page"}""", """{"type": "search", "query": "{\"type\":\"open_page\"}"}""")]
    [InlineData("not json", """{"type": "search", "query": "not json"}""")]
    [InlineData("""["a"]""", """{"type": "search", "query": "[\"a\"]"}""")]
    public void web_search_action_follows_parse_web_search_action(string arguments, string expected) =>
        Assert.Equal(expected, PythonJson.Dumps(ResponsesBridgeApi.WebSearchAction(arguments)));

    [Fact]
    public void usage_folds_cache_tokens_into_input_tokens()
    {
        Assert.Null(ResponsesBridgeApi.ResponsesUsage(null));
        Assert.Equal(
            """{"input_tokens": 145, "input_tokens_details": {"cached_tokens": 40, "cache_write_tokens": 5}, "output_tokens": 20, "output_tokens_details": {"reasoning_tokens": 7}, "total_tokens": 120}""",
            PythonJson.Dumps(ResponsesBridgeApi.ResponsesUsage(new ModelUsage(100, 20, 120) { InputTokensCacheRead = 40, InputTokensCacheWrite = 5, ReasoningTokens = 7 })));
        Assert.Equal(
            """{"input_tokens": 10, "input_tokens_details": {"cached_tokens": 0, "cache_write_tokens": 0}, "output_tokens": 5, "output_tokens_details": {"reasoning_tokens": 0}, "total_tokens": 15}""",
            PythonJson.Dumps(ResponsesBridgeApi.ResponsesUsage(new ModelUsage(10, 5, 15))));
    }

    [Theory]
    [InlineData(StopReason.ContentFilter, "content_filter")]
    [InlineData(StopReason.MaxTokens, "max_output_tokens")]
    [InlineData(StopReason.ModelLength, null)]
    [InlineData(StopReason.Stop, null)]
    [InlineData(StopReason.ToolCalls, null)]
    public void incomplete_details_follow_the_stop_reason(StopReason stopReason, string? reason) =>
        Assert.Equal(reason, ResponsesBridgeApi.IncompleteDetails(stopReason)?["reason"]!.GetValue<string>());

    [Fact]
    public void response_body_echoes_the_request_and_reports_status()
    {
        var parsed = ResponsesBridgeApi.ParseRequest(Json("""{"model": "alias", "input": "hi", "tools": [{"type": "function", "name": "shell", "parameters": {"type": "object", "properties": {}}}], "tool_choice": "required"}"""));
        var output = new ModelOutput
        {
            Model = "served",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("Done") { Id = "msg-id-1" }, StopReason.MaxTokens)],
            Usage = new ModelUsage(10, 5, 15),
        };

        var body = ResponsesBridgeApi.ResponseFromOutput(output, "gpt-5.4-mini", parsed);

        Assert.Equal(
            ("msg-id-1", "response", "incomplete", "gpt-5.4-mini"),
            (body["id"]!.GetValue<string>(), body["object"]!.GetValue<string>(), body["status"]!.GetValue<string>(), body["model"]!.GetValue<string>()));
        Assert.Equal("""{"reason": "max_output_tokens"}""", PythonJson.Dumps(body["incomplete_details"]));
        Assert.Null(body["error"]);
        Assert.Null(body["instructions"]);
        Assert.True(body["created_at"]!.GetValue<long>() > 0);
        Assert.Equal("required", body["tool_choice"]!.GetValue<string>());
        Assert.Equal(PythonJson.Dumps(parsed.ToolsEcho), PythonJson.Dumps(body["tools"]));
        Assert.True(body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Equal(15, body["usage"]!["total_tokens"]!.GetValue<int>());
        Assert.Equal("Done", body["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
