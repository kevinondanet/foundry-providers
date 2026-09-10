using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>POST /v1/responses</c> route on a real loopback listener: JSON and streamed replies, the Codex-shaped request,
/// OpenAI-shaped errors with <c>param</c>/<c>code</c>, <c>parallel_tool_calls</c>, state tracking across replayed turns,
/// the served model name, and deviation D-R10 (a limit or termination is a 400).
/// </summary>
public class SandboxAgentBridgeResponsesTests
{
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
              {"type": "function", "name": "extra_tool", "description": "Extra", "parameters": {"type": "object", "properties": {}}}
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

    private sealed class Harness(SandboxAgentBridge server, AgentBridge bridge, HttpClient client) : IAsyncDisposable
    {
        public SandboxAgentBridge Server { get; } = server;

        public AgentBridge Bridge { get; } = bridge;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(AgentBridge bridge)
    {
        var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment());
        var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.AuthToken);
        return new Harness(server, bridge, client);
    }

    private static Task<Harness> StartAsync(ScriptedModelApi api) =>
        StartAsync(new AgentBridge(new AgentState([new ChatMessageUser("Fix the bug")]), new Model(api)));

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonObject> JsonOf(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonObject>())!;

    private static List<(string? Event, JsonObject Data)> ParseSse(string body)
    {
        var events = new List<(string?, JsonObject)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }

            events.Add((name, JsonNode.Parse(data!)!.AsObject()));
        }

        return events;
    }

    [Fact]
    public async Task a_non_stream_request_is_a_responses_object_named_after_the_served_api()
    {
        var api = new ScriptedModelApi([ScriptedTurn.Text("Hi there", new ModelUsage(7, 3, 10))], "served-model");
        await using var harness = await StartAsync(api);

        var response = await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4-mini", "input": "hello", "instructions": "Be brief."}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await JsonOf(response);
        Assert.Equal(("response", "completed", "served-model"), (body["object"]!.GetValue<string>(), body["status"]!.GetValue<string>(), body["model"]!.GetValue<string>()));
        var item = Assert.Single(body["output"]!.AsArray())!;
        Assert.Equal(("message", "Hi there"), (item["type"]!.GetValue<string>(), item["content"]![0]!["text"]!.GetValue<string>()));
        Assert.Equal((7, 3, 10), (body["usage"]!["input_tokens"]!.GetValue<int>(), body["usage"]!["output_tokens"]!.GetValue<int>(), body["usage"]!["total_tokens"]!.GetValue<int>()));
        var request = Assert.Single(api.Requests);
        Assert.Equal(["system", "user"], request.Input.Select(m => m.Role));
        Assert.Equal(("Be brief.", "hello"), (request.Input[0].Text, request.Input[1].Text));
    }

    [Fact]
    public async Task the_response_model_is_the_api_model_name_of_the_model_the_request_resolves_to()
    {
        var served = new ScriptedModelApi([ScriptedTurn.Text("main")], "served-model");
        var guardian = new ScriptedModelApi([ScriptedTurn.Text("approved")], "guardian-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser("hi")]), new Model(served), new Dictionary<string, Model> { ["codex-auto-review"] = new Model(guardian) });
        await using var harness = await StartAsync(bridge);

        var review = await JsonOf(await harness.Client.PostAsync("v1/responses", Body("""{"model": "codex-auto-review", "input": "review this"}""")));
        var main = await JsonOf(await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi"}""")));

        Assert.Equal("guardian-model", review["model"]!.GetValue<string>());
        Assert.Equal("served-model", main["model"]!.GetValue<string>());
        Assert.Single(guardian.Requests);
        Assert.Single(served.Requests);
    }

    [Fact]
    public async Task a_stream_request_is_named_events_ending_in_response_completed_without_done()
    {
        var api = new ScriptedModelApi([ScriptedTurn.ToolCall("exec_command", new { cmd = "ls" }, id: "call_1", text: "Listing", usage: new ModelUsage(5, 2, 7))], "served-model");
        await using var harness = await StartAsync(api);

        var response = await harness.Client.PostAsync("v1/responses", Body("""
            {"model": "gpt-5.4", "stream": true, "input": [{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Fix the bug"}]}],
             "tools": [{"type": "function", "name": "exec_command", "parameters": {"type": "object", "properties": {"cmd": {"type": "string"}}, "required": ["cmd"]}}]}
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("[DONE]", raw, StringComparison.Ordinal);
        var events = ParseSse(raw);
        Assert.All(events, e => Assert.Equal(e.Event, e.Data["type"]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Data["sequence_number"]!.GetValue<int>()));
        Assert.Equal(("response.created", "response.completed"), (events[0].Event, events[^1].Event));
        var completed = events[^1].Data["response"]!;
        Assert.Equal(("completed", "served-model", 7), (completed["status"]!.GetValue<string>(), completed["model"]!.GetValue<string>(), completed["usage"]!["total_tokens"]!.GetValue<int>()));
        var call = events.Where(e => e.Event == "response.output_item.done").Select(e => e.Data["item"]!).Single(item => item["type"]!.GetValue<string>() == "function_call");
        Assert.Equal(("call_1", "exec_command", """{"cmd": "ls"}"""), (call["call_id"]!.GetValue<string>(), call["name"]!.GetValue<string>(), call["arguments"]!.GetValue<string>()));
    }

    [Fact]
    public async Task the_codex_shaped_request_is_answered_with_a_namespace_a_custom_tool_call_and_completion()
    {
        ProviderLogger.Reset();
        var output = new ModelOutput
        {
            Model = "served-model",
            Choices =
            [
                new ChatCompletionChoice(
                    new ChatMessageAssistant("Delegating.", [
                        new ToolCall("call_spawn", "spawn_agent", new JsonObject { ["message"] = "Survey the repo" }),
                        new ToolCall("call_patch", "apply_patch", new JsonObject { ["input"] = "*** Begin Patch\n*** End Patch" }),
                    ]),
                    StopReason.ToolCalls),
            ],
            Usage = new ModelUsage(10, 5, 15),
        };
        var api = new ScriptedModelApi([ScriptedTurn.From(output)], "served-model");
        await using var harness = await StartAsync(api);

        var response = await harness.Client.PostAsync("v1/responses", Body(CodexRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = ParseSse(await response.Content.ReadAsStringAsync());
        var items = events.Where(e => e.Event == "response.output_item.done").Select(e => e.Data["item"]!.AsObject()).ToList();
        Assert.Equal(["message", "function_call", "custom_tool_call"], items.Select(item => item["type"]!.GetValue<string>()));
        Assert.Equal(("spawn_agent", "multi_agent_v1"), (items[1]["name"]!.GetValue<string>(), items[1]["namespace"]!.GetValue<string>()));
        Assert.Equal(("apply_patch", "call_patch", "*** Begin Patch\n*** End Patch"), (items[2]["name"]!.GetValue<string>(), items[2]["call_id"]!.GetValue<string>(), items[2]["input"]!.GetValue<string>()));
        var completed = events[^1];
        Assert.Equal("response.completed", completed.Event);
        Assert.Equal(("completed", false, 3), (completed.Data["response"]!["status"]!.GetValue<string>(), completed.Data["response"]!["parallel_tool_calls"]!.GetValue<bool>(), completed.Data["response"]!["output"]!.AsArray().Count));
        var request = Assert.Single(api.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == "spawn_agent");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "web_search");
        Assert.False(request.Config.ParallelToolCalls);
        Assert.Empty(harness.Server.Errors);
    }

    [Theory]
    [InlineData("""{"input": "hi"}""", "model")]
    [InlineData("""{"model": "gpt-5.4"}""", "input")]
    public async Task a_missing_model_or_input_is_a_400_missing_required_parameter_with_param(string json, string param)
    {
        ProviderLogger.Reset();
        await using var harness = await StartAsync(new ScriptedModelApi());

        var response = await harness.Client.PostAsync("v1/responses", Body(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await JsonOf(response))["error"]!;
        Assert.Equal($"Missing required parameter: '{param}'.", error["message"]!.GetValue<string>());
        Assert.Equal(("invalid_request_error", param, "missing_required_parameter"), (error["type"]!.GetValue<string>(), error["param"]!.GetValue<string>(), error["code"]!.GetValue<string>()));
        Assert.Contains("agent bridge answered 400 to POST /v1/responses", ProviderLogger.Warnings);
    }

    [Fact]
    public async Task a_request_error_without_param_or_code_writes_nulls()
    {
        await using var harness = await StartAsync(new ScriptedModelApi());

        var response = await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi", "tools": [{"type": "computer"}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await JsonOf(response))["error"]!.AsObject();
        Assert.Equal("computer use is not supported by the agent bridge", error["message"]!.GetValue<string>());
        Assert.True(error.ContainsKey("param") && error["param"] is null);
        Assert.True(error.ContainsKey("code") && error["code"] is null);
    }

    [Fact]
    public async Task a_provider_exception_is_an_openai_error_body()
    {
        await using var harness = await StartAsync(new ScriptedModelApi([ScriptedTurn.Throw(new InvalidOperationException("provider exploded"))], "served-model"));

        var response = await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi", "stream": true}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var error = (await JsonOf(response))["error"]!;
        Assert.Equal(("provider exploded", "api_error"), (error["message"]!.GetValue<string>(), error["type"]!.GetValue<string>()));
        Assert.IsType<InvalidOperationException>(Assert.Single(harness.Server.Errors));
    }

    [Fact]
    public async Task parallel_tool_calls_reaches_the_served_model_as_sent()
    {
        var api = new ScriptedModelApi([ScriptedTurn.Text("one"), ScriptedTurn.Text("two")], "served-model");
        await using var harness = await StartAsync(api);

        var disabled = await JsonOf(await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi", "parallel_tool_calls": false}""")));
        var absent = await JsonOf(await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi"}""")));

        Assert.False(api.Requests[0].Config.ParallelToolCalls);
        Assert.False(disabled["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Null(api.Requests[1].Config.ParallelToolCalls);
        Assert.True(absent["parallel_tool_calls"]!.GetValue<bool>());
    }

    [Fact]
    public async Task state_is_tracked_across_two_replayed_turns()
    {
        var api = new ScriptedModelApi([ScriptedTurn.ToolCall("exec_command", new { cmd = "ls" }, id: "call_1"), ScriptedTurn.Text("Done")], "served-model");
        await using var harness = await StartAsync(api);
        const string User = """{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "Fix the bug"}]}""";
        const string Tools = """[{"type": "function", "name": "exec_command", "parameters": {"type": "object", "properties": {"cmd": {"type": "string"}}, "required": ["cmd"]}}]""";

        var first = await JsonOf(await harness.Client.PostAsync("v1/responses", Body($$"""{"model": "gpt-5.4", "input": [{{User}}], "tools": {{Tools}}}""")));
        var call = first["output"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "function_call")!.ToJsonString();
        var second = await JsonOf(await harness.Client.PostAsync("v1/responses", Body($$"""
            {"model": "gpt-5.4", "tools": {{Tools}},
             "input": [{{User}}, {{call}}, {"type": "function_call_output", "call_id": "call_1", "output": "file.txt"}]}
            """)));

        Assert.Equal("Done", second["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        var messages = harness.Server.State.Messages;
        Assert.Equal(["user", "assistant", "tool", "assistant"], messages.Select(m => m.Role));
        Assert.Equal("call_1", Assert.IsType<ChatMessageAssistant>(messages[1]).ToolCalls![0].Id);
        Assert.Equal(("call_1", "file.txt"), (Assert.IsType<ChatMessageTool>(messages[2]).ToolCallId, messages[2].Text));
        Assert.Equal("Done", messages[3].Text);
        Assert.Equal("Done", harness.Server.State.Output.Completion);
    }

    [Fact]
    public async Task a_limit_on_the_responses_dialect_is_a_400_and_signals_the_limit()
    {
        using var scope = new SampleContextScope(limits: new Limits { MessageLimit = 1 });
        var api = new ScriptedModelApi([ScriptedTurn.Text("never")], "served-model");
        await using var harness = await StartAsync(api);

        var response = await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await JsonOf(response))["error"]!;
        Assert.Contains("Message limit", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Equal("message", Assert.IsType<LimitExceededException>(harness.Server.LimitError).Type);
        Assert.True(harness.Server.LimitReached.IsCancellationRequested);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task a_termination_on_the_responses_dialect_is_a_400_and_signals_it()
    {
        var api = new ScriptedModelApi([ScriptedTurn.Text("never")], "served-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser("hi")]), new Model(api), filter: (_, _, _, _, _, _) => throw new TerminateSampleException("approver said stop"));
        await using var harness = await StartAsync(bridge);

        var response = await harness.Client.PostAsync("v1/responses", Body("""{"model": "gpt-5.4", "input": "hi"}"""));
        var messages = await harness.Client.PostAsync("v1/chat/completions", Body("""{"model": "gpt-5.4", "messages": [{"role": "user", "content": "hi"}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("approver said stop", (await JsonOf(response))["error"]!["message"]!.GetValue<string>());
        Assert.Equal("approver said stop", harness.Server.TerminateError!.Message);
        Assert.True(harness.Server.TerminateRequested.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.InternalServerError, messages.StatusCode);
        Assert.Empty(api.Requests);
    }
}
