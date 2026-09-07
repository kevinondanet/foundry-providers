using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The HTTP face of the sandbox agent bridge: auth, routes, streaming, error bodies and disposal, driven with a real HttpClient against a loopback listener.</summary>
public class SandboxAgentBridgeTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(SandboxAgentBridge server, ScriptedModelApi api, HttpClient client)
        {
            Server = server;
            Api = api;
            Client = client;
        }

        public SandboxAgentBridge Server { get; }

        public ScriptedModelApi Api { get; }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns, "served-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser("Fix the bug in main.py")]), new Model(api));
        var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment());
        var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Add("x-api-key", server.AuthToken);
        return new Harness(server, api, client);
    }

    /// <summary>A model whose generation blocks until released, optionally ignoring the cancellation token.</summary>
    private sealed class BlockingModelApi(bool honourCancellation) : IModelApi
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ModelName => "blocking";

        public int? MaxTokens() => 100;

        public async Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, StreamHandler? onStream, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(honourCancellation ? cancellationToken : CancellationToken.None);
            return new GenerateResult(ModelOutput.FromContent(ModelName, "released"), null, ModelCall.Create(new JsonObject()));
        }
    }

    private static async Task<(SandboxAgentBridge Server, HttpClient Client)> StartBlockingAsync(BlockingModelApi api, CancellationToken cancellationToken = default)
    {
        var bridge = new AgentBridge(new AgentState([]), new Model(api));
        var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment(), cancellationToken: cancellationToken);
        var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        client.DefaultRequestHeaders.Add("x-api-key", server.AuthToken);
        return (server, client);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static string MessagesRequest(bool stream = false, string model = "claude-sonnet-4-6") => $$"""
        {"model": "{{model}}", "max_tokens": 100, "stream": {{(stream ? "true" : "false")}}, "system": "You are Claude Code.",
         "messages": [{"role": "user", "content": "Fix the bug in main.py"}]}
        """;

    private static List<(string? Event, JsonObject? Data, string Raw)> ParseSse(string body)
    {
        var events = new List<(string?, JsonObject?, string)>();
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

            events.Add((name, data is null or "[DONE]" ? null : JsonNode.Parse(data)!.AsObject(), data ?? ""));
        }

        return events;
    }

    [Fact]
    public async Task starts_on_loopback_with_a_free_port_and_a_random_token()
    {
        await using var harness = await StartAsync();

        Assert.Equal("127.0.0.1", harness.Server.HostAddress);
        Assert.True(harness.Server.Port > 0);
        Assert.Equal($"http://127.0.0.1:{harness.Server.Port}", harness.Server.BaseUrl);
        Assert.Equal(32, harness.Server.AuthToken.Length);
        Assert.Same(harness.Server.State, harness.Server.State);
    }

    [Fact]
    public async Task a_non_loopback_host_address_advertises_the_sandbox_host()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api));
        await using var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment { HostAddress = "host.docker.internal" });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        client.DefaultRequestHeaders.Add("x-api-key", server.AuthToken);

        Assert.Equal($"http://host.docker.internal:{server.Port}", server.BaseUrl);
        var response = await client.PostAsync("v1/messages", Body(MessagesRequest()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task a_request_carrying_the_sandbox_host_header_is_served()
    {
        // A container reaches the host through host.docker.internal, so the CLI's requests arrive with that
        // name in the Host header. HttpListener matches Host against its prefixes: a loopback-only prefix
        // answers 400 and Claude Code reports the model as missing.
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api));
        await using var server = await SandboxAgentBridge.StartAsync(bridge, new FakeSandboxEnvironment { HostAddress = "host.docker.internal" });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = Body(MessagesRequest()) };
        request.Headers.Host = $"host.docker.internal:{server.Port}";
        request.Headers.Add("x-api-key", server.AuthToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task messages_route_answers_a_non_streaming_request_and_tracks_state()
    {
        await using var harness = await StartAsync(ScriptedTurn.ToolCall("bash", new { cmd = "ls" }, id: "toolu_1", text: "Listing", usage: new ModelUsage(12, 4, 16)));

        var response = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var message = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("claude-sonnet-4-6", message["model"]!.GetValue<string>());
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        var blocks = message["content"]!.AsArray();
        Assert.Equal("Listing", blocks[0]!["text"]!.GetValue<string>());
        Assert.Equal("toolu_1", blocks[1]!["id"]!.GetValue<string>());
        Assert.Equal(12, message["usage"]!["input_tokens"]!.GetValue<int>());

        var request = Assert.Single(harness.Api.Requests);
        Assert.Equal(["system", "user"], request.Input.Select(m => m.Role).ToArray());
        Assert.Equal(3, harness.Server.State.Messages.Count);
        Assert.Equal("Listing", harness.Server.State.Output.Completion);
        Assert.Empty(harness.Server.Errors);
    }

    [Fact]
    public async Task messages_route_streams_server_sent_events()
    {
        await using var harness = await StartAsync(ScriptedTurn.Text("Hello from the bridge", new ModelUsage(7, 3, 10)));

        using var response = await harness.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = Body(MessagesRequest(stream: true)) },
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.TransferEncodingChunked);
        var events = ParseSse(await response.Content.ReadAsStringAsync());

        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"], events.Select(e => e.Event!).ToArray());
        Assert.Equal("Hello from the bridge", events[2].Data!["delta"]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", events[4].Data!["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal(3, events[4].Data!["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal("Hello from the bridge", harness.Server.State.Output.Completion);
    }

    [Fact]
    public async Task bearer_authorization_is_accepted_and_a_wrong_or_missing_token_is_401()
    {
        await using var harness = await StartAsync(ScriptedTurn.Text("ok"));
        using var bearer = new HttpClient { BaseAddress = harness.Client.BaseAddress };
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", harness.Server.AuthToken);
        using var wrong = new HttpClient { BaseAddress = harness.Client.BaseAddress };
        wrong.DefaultRequestHeaders.Add("x-api-key", "not-the-token");
        using var missing = new HttpClient { BaseAddress = harness.Client.BaseAddress };

        var accepted = await bearer.PostAsync("v1/messages", Body(MessagesRequest()));
        var rejected = await wrong.PostAsync("v1/messages", Body(MessagesRequest()));
        var anonymous = await missing.PostAsync("v1/messages", Body(MessagesRequest()));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var body = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("authentication_error", body["error"]!["type"]!.GetValue<string>());
        Assert.Single(harness.Api.Requests);
    }

    [Fact]
    public async Task unknown_routes_are_404_with_an_anthropic_error_body()
    {
        await using var harness = await StartAsync();

        var response = await harness.Client.PostAsync("v1/complete", Body("{}"));
        var get = await harness.Client.GetAsync("v1/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("error", body["type"]!.GetValue<string>());
        Assert.Equal("not_found_error", body["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task count_tokens_answers_an_estimate()
    {
        await using var harness = await StartAsync();

        var response = await harness.Client.PostAsync("v1/messages/count_tokens", Body(MessagesRequest()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(body["input_tokens"]!.GetValue<long>() > 0);
        Assert.Empty(harness.Api.Requests);
    }

    [Fact]
    public async Task chat_completions_route_streams_chunks_and_a_done_sentinel()
    {
        await using var harness = await StartAsync(ScriptedTurn.ToolCall("bash", new { cmd = "ls" }, id: "call_1", usage: new ModelUsage(5, 2, 7)));

        var response = await harness.Client.PostAsync("v1/chat/completions", Body("""
            {"model": "gpt-5", "stream": true, "stream_options": {"include_usage": true},
             "messages": [{"role": "system", "content": "Be terse."}, {"role": "user", "content": "Fix the bug in main.py"}],
             "tools": [{"type": "function", "function": {"name": "bash", "description": "Run", "parameters": {"type": "object", "properties": {"cmd": {"type": "string"}}}}}]}
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var events = ParseSse(body);
        Assert.Equal("[DONE]", events[^1].Raw);
        Assert.All(events.SkipLast(1), e => Assert.Equal("chat.completion.chunk", e.Data!["object"]!.GetValue<string>()));
        Assert.Equal("assistant", events[0].Data!["choices"]![0]!["delta"]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", events[1].Data!["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool_calls", events[^3].Data!["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal(7, events[^2].Data!["usage"]!["total_tokens"]!.GetValue<int>());

        var request = Assert.Single(harness.Api.Requests);
        Assert.False(request.Config.ParallelToolCalls);
        Assert.Equal("bash", Assert.Single(request.Tools).Name);
    }

    [Fact]
    public async Task chat_completions_route_answers_a_non_streaming_request()
    {
        await using var harness = await StartAsync(ScriptedTurn.Text("done"));

        var response = await harness.Client.PostAsync("v1/chat/completions", Body("""{"model": "gpt-5", "messages": [{"role": "user", "content": "hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completion = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("chat.completion", completion["object"]!.GetValue<string>());
        Assert.Equal("served-model", completion["model"]!.GetValue<string>());
        Assert.Equal("done", completion["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task a_provider_exception_is_a_500_in_the_client_dialect_and_is_recorded()
    {
        await using var harness = await StartAsync(ScriptedTurn.Throw(new InvalidOperationException("provider exploded")), ScriptedTurn.Throw(new InvalidOperationException("again")));

        var anthropic = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest()));
        var openai = await harness.Client.PostAsync("v1/chat/completions", Body("""{"model": "gpt-5", "messages": [{"role": "user", "content": "hi"}]}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, anthropic.StatusCode);
        var anthropicBody = (await anthropic.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("api_error", anthropicBody["error"]!["type"]!.GetValue<string>());
        Assert.Equal("provider exploded", anthropicBody["error"]!["message"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.InternalServerError, openai.StatusCode);
        var openaiBody = (await openai.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("again", openaiBody["error"]!["message"]!.GetValue<string>());
        Assert.Null(openaiBody["type"]);

        Assert.Equal(2, harness.Server.Errors.Count);
        Assert.IsType<InvalidOperationException>(harness.Server.Errors[0]);
    }

    [Fact]
    public async Task terminal_model_errors_and_bad_requests_are_400()
    {
        await using var harness = await StartAsync(ScriptedTurn.Error(new InvalidOperationException("bad request upstream")));

        var terminal = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest()));
        var invalidJson = await harness.Client.PostAsync("v1/messages", Body("{not json"));
        var noModel = await harness.Client.PostAsync("v1/messages", Body("""{"messages": []}"""));

        Assert.Equal(HttpStatusCode.BadRequest, terminal.StatusCode);
        Assert.Equal("invalid_request_error", (await terminal.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, invalidJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noModel.StatusCode);
        Assert.Contains("Missing required parameter: 'model'.", (await noModel.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(3, harness.Server.Errors.Count);
    }

    [Fact]
    public async Task dispose_stops_serving()
    {
        var harness = await StartAsync(ScriptedTurn.Text("ok"));
        var baseAddress = harness.Client.BaseAddress!;
        var token = harness.Server.AuthToken;
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync("v1/messages", Body(MessagesRequest()))).StatusCode);

        await harness.DisposeAsync();
        await harness.Server.DisposeAsync();

        using var client = new HttpClient { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.Add("x-api-key", token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("v1/messages", Body(MessagesRequest())));
    }

    [Fact]
    public async Task a_sample_limit_hit_by_a_generation_is_kept_and_signalled_instead_of_being_a_provider_error()
    {
        using var scope = new SampleContextScope(limits: new Limits { MessageLimit = 1 });
        await using var harness = await StartAsync(ScriptedTurn.Text("never"));

        var response = await harness.Client.PostAsync("v1/messages", Body(MessagesRequest()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var limit = Assert.IsType<LimitExceededException>(harness.Server.LimitError);
        Assert.Equal("message", limit.Type);
        Assert.True(harness.Server.LimitReached.IsCancellationRequested);
        Assert.Empty(harness.Api.Requests);
        Assert.Contains("Message limit", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task cancelling_the_start_token_stops_an_in_flight_generation_and_disposal_skips_the_grace_period()
    {
        var api = new BlockingModelApi(honourCancellation: true);
        using var cts = new CancellationTokenSource();
        var (server, client) = await StartBlockingAsync(api, cts.Token);
        using (client)
        {
            var pending = client.PostAsync("v1/messages", Body(MessagesRequest()));
            await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await cts.CancelAsync();
            var stopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"disposal took {stopwatch.Elapsed}");
            Assert.Empty(server.Errors);
            await AssertFailedAsync(pending);
        }
    }

    [Fact]
    public async Task dispose_abandons_a_handler_that_ignores_cancellation_after_the_bounded_waits()
    {
        var api = new BlockingModelApi(honourCancellation: false);
        var (server, client) = await StartBlockingAsync(api);
        server.GracePeriod = TimeSpan.FromMilliseconds(200);
        server.AbandonPeriod = TimeSpan.FromMilliseconds(200);
        using (client)
        {
            var pending = client.PostAsync("v1/messages", Body(MessagesRequest()));
            await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"disposal took {stopwatch.Elapsed}");
            Assert.Contains(InspectAzureAI.Provider.Util.ProviderLogger.Warnings, w => w.StartsWith("Abandoned 1 sandbox agent bridge request handler", StringComparison.Ordinal));
            // the abandoned handler finishes on its own; whether its late answer still reaches the client is platform-dependent
            api.Release.TrySetResult();
            try
            {
                await pending;
            }
            catch (HttpRequestException)
            {
            }
        }
    }

    /// <summary>A request cut short by shutdown ends in an error status, or a transport failure when the connection was torn down first.</summary>
    private static async Task AssertFailedAsync(Task<HttpResponseMessage> pending)
    {
        try
        {
            using var response = await pending;
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("shutting down", await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException)
        {
        }
    }

    [Fact]
    public async Task bodies_over_the_limit_are_413_and_chunked_bodies_under_it_are_served()
    {
        await using var harness = await StartAsync(ScriptedTurn.Text("ok"));
        harness.Server.BodyLimit = 1000;
        var small = MessagesRequest();
        var big = MessagesRequest().Replace("Fix the bug in main.py", new string('x', 2000), StringComparison.Ordinal);

        var served = await harness.Client.PostAsync("v1/messages", Chunked(small));
        var rejected = await harness.Client.PostAsync("v1/messages", Chunked(big));
        var openai = await harness.Client.PostAsync("v1/chat/completions", Chunked("{\"model\": \"gpt-5\", \"messages\": [{\"role\": \"user\", \"content\": \"" + new string('y', 2000) + "\"}]}"));

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        var body = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("request_too_large", body["error"]!["type"]!.GetValue<string>());
        Assert.Contains("1000 bytes", body["error"]!["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, openai.StatusCode);
        Assert.Null((await openai.Content.ReadFromJsonAsync<JsonObject>())!["type"]);
        Assert.Single(harness.Api.Requests);
    }

    [Fact]
    public async Task a_wrong_x_api_key_is_rejected_even_when_the_bearer_token_is_right()
    {
        await using var harness = await StartAsync(ScriptedTurn.Text("ok"));
        using var mixed = new HttpClient { BaseAddress = harness.Client.BaseAddress };
        mixed.DefaultRequestHeaders.Add("x-api-key", "wrong");
        mixed.DefaultRequestHeaders.Authorization = new("Bearer", harness.Server.AuthToken);

        var response = await mixed.PostAsync("v1/messages", Body(MessagesRequest()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(harness.Api.Requests);
    }

    [Fact]
    public async Task recorded_errors_are_capped_to_the_most_recent()
    {
        await using var harness = await StartAsync();

        for (var i = 0; i < SandboxAgentBridge.MaxRecordedErrors + 5; i++)
        {
            await harness.Client.PostAsync("v1/messages", Body("{bad json " + i));
        }

        Assert.Equal(SandboxAgentBridge.MaxRecordedErrors, harness.Server.Errors.Count);
    }

    private static StreamContent Chunked(string json)
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return content;
    }
}
