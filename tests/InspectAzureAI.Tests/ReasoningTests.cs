using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Reasoning ("thinking") controls and outputs across the Foundry families and the Anthropic route.</summary>
public class ReasoningTests
{
    private const string Inference = "https://res.services.ai.azure.com/models";

    // ---------- family mapping (model-inference route) ----------

    [Theory]
    [InlineData("OpenAI", "gpt-5.4-mini", ModelFamilyHint.OpenAI)]
    [InlineData("OpenAI", "model-router", ModelFamilyHint.Router)]
    [InlineData("xAI", "grok-4.6", ModelFamilyHint.XAI)]
    [InlineData("Microsoft", "MAI-Thinking-1", ModelFamilyHint.Microsoft)]
    [InlineData("DeepSeek", "DeepSeek-V4-Flash", ModelFamilyHint.DeepSeek)]
    [InlineData("MoonshotAI", "Kimi-K2.6", ModelFamilyHint.MoonshotAI)]
    [InlineData("Cohere", "Cohere-command-a-plus-05-2026", ModelFamilyHint.Cohere)]
    [InlineData("Mistral AI", "Ministral-3B", ModelFamilyHint.Mistral)]
    [InlineData("Anthropic", "claude-sonnet-4-6", ModelFamilyHint.Anthropic)]
    [InlineData("Black Forest Labs", "FLUX.2-pro", ModelFamilyHint.Unknown)]
    [InlineData("OpenAI", "gpt-4o", ModelFamilyHint.OpenAILegacy)]
    [InlineData(null, "gpt-5.6-terra", ModelFamilyHint.OpenAI)]
    [InlineData(null, "gpt-4o", ModelFamilyHint.OpenAILegacy)]
    [InlineData(null, "o3-mini", ModelFamilyHint.OpenAI)]
    [InlineData(null, "model-router", ModelFamilyHint.Router)]
    [InlineData(null, "grok-4.6", ModelFamilyHint.XAI)]
    [InlineData(null, "MAI-Thinking-1", ModelFamilyHint.Microsoft)]
    [InlineData(null, "DeepSeek-V4-Pro", ModelFamilyHint.DeepSeek)]
    [InlineData(null, "Kimi-K2.7-Code", ModelFamilyHint.MoonshotAI)]
    [InlineData(null, "Cohere-command-a-plus-05-2026", ModelFamilyHint.Cohere)]
    [InlineData(null, "claude-sonnet-4-6", ModelFamilyHint.Anthropic)]
    [InlineData(null, "Mistral-Large-3", ModelFamilyHint.Mistral)]
    [InlineData(null, "Ministral-3B", ModelFamilyHint.Unknown)]
    [InlineData(null, "Llama-3.3-70B-Instruct", ModelFamilyHint.Unknown)]
    public void family_of_prefers_the_arm_format_then_the_name(string? format, string name, ModelFamilyHint expected) =>
        Assert.Equal(expected, ReasoningParams.FamilyOf(format, name));

    [Fact]
    public void reasoning_params_by_family()
    {
        static string Params(string model, GenerateConfig config, string? format = null) =>
            Fixtures.Api(model, modelArgs: format is null ? null : new Dictionary<string, object?> { ["model_format"] = format })
                .ReasoningRequestParams(config).ToJsonString();

        Assert.Equal("""{"reasoning_effort":"high"}""", Params("gpt-5.4-mini", new() { ReasoningEffort = "high" }));
        Assert.Equal("""{"reasoning_effort":"none"}""", Params("MAI-Thinking-1", new() { ReasoningEffort = "none" }));
        Assert.Equal("""{"reasoning_effort":"low"}""", Params("model-router", new() { ReasoningEffort = "LOW " }));
        Assert.Equal("""{"thinking":{"type":"enabled"}}""", Params("Kimi-K2.6", new() { ReasoningEffort = "medium" }));
        Assert.Equal("""{"thinking":{"type":"disabled"}}""", Params("Kimi-K2.6", new() { ReasoningEffort = "none" }));
        Assert.Equal("""{"reasoning_effort":"high"}""", Params("DeepSeek-V4-Flash", new() { ReasoningEffort = "high" }));      // DeepSeek ignores `thinking`, honours reasoning_effort
        Assert.Equal("""{"thinking":{"type":"enabled","token_budget":512}}""", Params("Cohere-command-a-plus-05-2026", new() { ReasoningTokens = 512 }));
        Assert.Equal("""{"thinking":{"type":"enabled"}}""", Params("Kimi-K2.6", new() { ReasoningTokens = 512 }));            // no budget field for Kimi
        Assert.Equal("{}", Params("gpt-4o", new() { ReasoningEffort = "high" }));                                              // gpt-4o rejects reasoning_effort
        Assert.Equal("{}", Params("Mistral-Large-3", new() { ReasoningEffort = "high" }));
        Assert.Equal("{}", Params("gpt-5.4-mini", new() { ReasoningTokens = 512 }));                                          // effort families ignore budgets
        Assert.Equal("{}", Params("gpt-5.4-mini", new()));
        Assert.Equal("{}", Params("Llama-3.3-70B-Instruct", new() { ReasoningEffort = "high" }));                              // unknown family: nothing derived
        Assert.Equal("""{"thinking":{"type":"enabled"}}""", Params("my-deploy", new() { ReasoningEffort = "high" }, format: "MoonshotAI"));
    }

    [Fact]
    public void model_format_arg_is_popped_and_reasoning_keys_land_in_completion_params()
    {
        var api = Fixtures.Api("my-deploy", modelArgs: new Dictionary<string, object?> { ["model_format"] = "Cohere", ["safe_mode"] = true });
        Assert.Equal("Cohere", api.ModelFormat);
        Assert.Equal(ModelFamilyHint.Cohere, api.FamilyHint);
        Assert.Equal(new[] { "safe_mode" }, api.ModelArgs.Keys);
        Assert.Equal(
            """{"max_tokens":5,"thinking":{"type":"enabled","token_budget":100}}""",
            api.CompletionParams(new GenerateConfig { MaxTokens = 5, ReasoningTokens = 100 }).ToJsonString());
        Assert.Equal(ThinkingToggle.EnabledDisabled, ReasoningParams.Describe(api.FamilyHint).Toggle);
    }

    [Fact]
    public async Task reasoning_effort_reaches_the_wire_and_a_model_arg_overrides_it()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("ok")) };
        var api = Fixtures.Api("gpt-5.4-mini", transport: transport, modelArgs: new Dictionary<string, object?> { ["reasoning_effort"] = "low" });
        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { MaxTokens = 5, ReasoningEffort = "high" });

        var body = transport.LastRequest!.BodyJson;
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());                       // the model arg wins on the wire
        Assert.Equal("high", result.Call.Request["reasoning_effort"]!.GetValue<string>());       // the snapshot records the derived value
        Assert.Equal(5, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("pass-through", transport.LastRequest.Headers["extra-parameters"]);
    }

    [Fact]
    public async Task streamed_extras_carry_the_pass_through_header_but_plain_requests_do_not()
    {
        var chunk = Fixtures.Update("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""").ToJsonString();
        var transport = new CannedTransport { Responder = _ => CannedResponse.Sse([chunk]) };
        var api = Fixtures.Api("Kimi-K2.6", streaming: true, transport: transport);

        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { MaxTokens = 5, ReasoningEffort = "high" });
        Assert.True(transport.LastRequest!.BodyJson["stream"]!.GetValue<bool>());
        Assert.Equal("""{"type":"enabled"}""", transport.LastRequest.BodyJson["thinking"]!.ToJsonString());
        Assert.Equal("pass-through", transport.LastRequest.Headers["extra-parameters"]);

        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { MaxTokens = 5 });
        Assert.False(transport.LastRequest!.Headers.ContainsKey("extra-parameters"));
    }

    // ---------- responses (model-inference route) ----------

    private const string KimiCompletion = """
        {"id":"k1","created":1,"model":"Kimi-K2.6","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok","reasoning_content":"The user wants ok."}}],
         "usage":{"prompt_tokens":13,"completion_tokens":31,"total_tokens":44,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":25}}}
        """;

    [Fact]
    public async Task reasoning_content_becomes_leading_content_reasoning_and_usage_details_are_mapped()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, KimiCompletion) };
        var output = (await Fixtures.Api("Kimi-K2.6", transport: transport).GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();

        var reasoning = Assert.IsType<ContentReasoning>(output.Message.ContentList[0]);
        Assert.Equal("The user wants ok.", reasoning.Reasoning);
        Assert.Null(reasoning.Signature);
        Assert.False(reasoning.Redacted);
        Assert.Equal("ok", output.Completion);
        Assert.Equal("ok", output.Message.Text);
        Assert.Equal(new ModelUsage(13, 31, 44) { ReasoningTokens = 25, InputTokensCacheRead = 4 }, output.Usage);
    }

    [Fact]
    public async Task hidden_reasoning_models_yield_no_content_reasoning_but_report_the_count()
    {
        const string mai = """
            {"id":"m1","created":1,"model":"MAI-Thinking-1","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok","thinking":null,"reasoning":null,"encrypted_content":null}}],
             "usage":{"prompt_tokens":11,"completion_tokens":108,"total_tokens":119,"prompt_tokens_details":{"cached_tokens":2},"completion_tokens_details":{"reasoning_tokens":100}}}
            """;
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, mai) };
        var output = (await Fixtures.Api("MAI-Thinking-1", transport: transport).GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();

        Assert.True(output.Message.Content.IsString);
        Assert.Equal("ok", output.Completion);
        Assert.Equal(100, output.Usage!.ReasoningTokens);
        Assert.Equal(2, output.Usage.InputTokensCacheRead);

        // A plain completion reports neither field, so both stay null (a 0 would mean "reported as 0").
        var plain = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("ok")) };
        var plainOutput = (await Fixtures.Api(transport: plain).GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();
        Assert.Null(plainOutput.Usage!.ReasoningTokens);
        Assert.Null(plainOutput.Usage.InputTokensCacheRead);
    }

    [Theory]
    [InlineData("<|START_TEXT|>ok<|END_TEXT|>", "ok")]
    [InlineData("<|START_TEXT|>cut off", "cut off")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void cohere_text_markers_are_stripped(string wire, string expected) =>
        Assert.Equal(expected, AzureAIModelApi.StripCohereTextMarkers(wire));

    [Fact]
    public async Task cohere_markers_are_stripped_from_the_output_but_kept_in_the_model_call()
    {
        var transport = new CannedTransport { Responder = _ => CannedResponse.Json(200, Fixtures.Completion("<|START_TEXT|>ok<|END_TEXT|>")) };
        var result = await Fixtures.Api("Cohere-command-a-plus-05-2026", transport: transport).GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());
        Assert.Equal("ok", result.OutputOrThrow().Completion);
        Assert.Equal("<|START_TEXT|>ok<|END_TEXT|>", result.Call.Response!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task reasoning_deltas_are_accumulated_and_reported()
    {
        var updates = new[]
        {
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"reasoning_content":null,"role":"assistant","content":""},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"reasoning_content":"The user "},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"reasoning_content":"wants ok."},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"reasoning_content":null,"content":"ok"},"finish_reason":"stop"}]}"""),
            Fixtures.Update("""{"choices":[],"usage":{"prompt_tokens":19,"completion_tokens":44,"total_tokens":63,"prompt_tokens_details":null,"reasoning_tokens":38}}"""),
        };
        var collector = new StreamCollector();
        AzureChatCompletions response;
        using (ModelStreamObserver.Install(new ModelStreamObserver("test", collector.Collect)))
        {
            response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        }

        Assert.Equal("The user wants ok.", response.Choices[0].Message.ReasoningContent);
        Assert.Equal("ok", response.Choices[0].Message.Content);
        Assert.Equal(38, response.Usage!.ReasoningTokens);
        Assert.Equal([typeof(StreamReasoningEvent), typeof(StreamReasoningEvent), typeof(StreamTextEvent)], collector.Events.Select(e => e.GetType()));
        Assert.Equal("The user ", ((StreamReasoningEvent)collector.Events[0]).Reasoning);

        var assistant = AzureAIModelApi.ChatCompletionAssistantMessage("m", response.Choices[0].Message);
        Assert.IsType<ContentReasoning>(assistant.ContentList[0]);
        Assert.Equal("ok", assistant.Text);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("thinking")]
    public async Task reasoning_fallback_keys_are_accumulated(string key)
    {
        var updates = new[]
        {
            Fixtures.Update($$"""{"choices":[{"index":0,"delta":{"role":"assistant","{{key}}":"hmm","content":""},"finish_reason":null}]}"""),
            Fixtures.Update("""{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""),
        };
        var response = await AzureAIStreamAccumulator.CompletionFromStreamAsync(Fixtures.Updates(updates));
        Assert.Equal("hmm", response.Choices[0].Message.ReasoningContent);
        Assert.Equal("ok", response.Choices[0].Message.Content);
    }

    [Fact]
    public void content_reasoning_is_excluded_from_text_and_completion()
    {
        var assistant = new ChatMessageAssistant(MessageContent.FromItems([new ContentReasoning("why", "sig"), new ContentText("answer")]));
        Assert.Equal("answer", assistant.Text);
        Assert.Equal("reasoning", assistant.ContentList[0].Type);
        var output = new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(assistant, StopReason.Stop, null)] };
        Assert.Equal("answer", output.Completion);
    }

    // ---------- Anthropic route ----------

    private static (AnthropicFoundryModelApi Api, FakeArmHandler Handler) Claude(
        string body, string contentType = "application/json", IReadOnlyDictionary<string, object?>? modelArgs = null)
    {
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        var api = new AnthropicFoundryModelApi("claude-sonnet-4-6", modelArgs: modelArgs,
            settings: new AzureAIClientSettings { TokenCredential = new FakeTokenCredential("entra-token") }, handler: handler);
        return (api, handler);
    }

    private const string ClaudeThinkingMessage = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[
          {"type":"thinking","thinking":"391 = 17 x 23.","signature":"sig-abc"},
          {"type":"redacted_thinking","data":"opaque-blob"},
          {"type":"text","text":"No."},
          {"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Oslo"}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":50}}
        """;

    [Fact]
    public void thinking_params_follow_the_config()
    {
        static string Params(GenerateConfig config, int maxTokens = 4096) => AnthropicFoundryModelApi.ThinkingParams(config, maxTokens).ToJsonString();

        Assert.Equal("{}", Params(new GenerateConfig()));
        Assert.Equal("{}", Params(new GenerateConfig { ReasoningEffort = "none", ReasoningTokens = 1024 }));
        Assert.Equal("""{"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}""", Params(new GenerateConfig { ReasoningEffort = "high" }));
        Assert.Equal("""{"thinking":{"type":"adaptive"},"output_config":{"effort":"low"}}""", Params(new GenerateConfig { ReasoningEffort = "minimal" }));
        Assert.Equal("""{"thinking":{"type":"enabled","budget_tokens":1024}}""", Params(new GenerateConfig { ReasoningTokens = 1024 }));
        Assert.Equal(
            """{"thinking":{"type":"enabled","budget_tokens":4096},"max_tokens":6144,"output_config":{"effort":"medium"}}""",
            Params(new GenerateConfig { ReasoningTokens = 4096, ReasoningEffort = "medium" }));
    }

    [Fact]
    public async Task thinking_blocks_are_parsed_first_and_replayed_with_signature_before_text_and_tool_use()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler) = Claude(ClaudeThinkingMessage);
        var config = new GenerateConfig { ReasoningEffort = "medium" };
        var input = new List<ChatMessage> { new ChatMessageUser("Is 391 prime? Use the tool.") };
        var output = (await api.GenerateAsync(input, [Fixtures.WeatherTool], ToolChoice.Auto, config)).OutputOrThrow();

        var first = JsonNode.Parse(handler.Bodies[0]!)!.AsObject();
        Assert.Equal("""{"type":"adaptive"}""", first["thinking"]!.ToJsonString());
        Assert.Equal("medium", first["output_config"]!["effort"]!.GetValue<string>());

        var items = output.Message.ContentList;
        Assert.Equal(["reasoning", "reasoning", "text"], items.Select(i => i.Type));
        var thinking = Assert.IsType<ContentReasoning>(items[0]);
        Assert.Equal(("391 = 17 x 23.", "sig-abc", false), (thinking.Reasoning, thinking.Signature, thinking.Redacted));
        var redacted = Assert.IsType<ContentReasoning>(items[1]);
        Assert.Equal(("", "opaque-blob", true), (redacted.Reasoning, redacted.Signature, redacted.Redacted));
        Assert.Equal("No.", output.Completion);
        Assert.Null(output.Usage!.ReasoningTokens);   // Anthropic counts thinking inside output_tokens

        // Second turn: the assistant message is replayed with its thinking blocks first, unchanged.
        input.Add(output.Message);
        input.Add(new ChatMessageTool("{\"temperature_c\":5}", "toolu_1", "get_weather"));
        await api.GenerateAsync(input, [Fixtures.WeatherTool], ToolChoice.Auto, config);
        var second = JsonNode.Parse(handler.Bodies[1]!)!.AsObject();
        var assistantTurn = second["messages"]!.AsArray().First(m => m!["role"]!.GetValue<string>() == "assistant")!["content"]!.AsArray();
        Assert.Equal(["thinking", "redacted_thinking", "text", "tool_use"], assistantTurn.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("sig-abc", assistantTurn[0]!["signature"]!.GetValue<string>());
        Assert.Equal("391 = 17 x 23.", assistantTurn[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("opaque-blob", assistantTurn[1]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task streamed_thinking_deltas_are_reported_and_the_signature_is_reassembled()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var sse = string.Join("\n\n",
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_s\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"usage\":{\"input_tokens\":7,\"output_tokens\":1}}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"17 x 23 \"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"= 391\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"xyz\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"No.\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":30}}",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}") + "\n\n";
        var (api, _) = Claude(sse, "text/event-stream");
        var events = new List<StreamEvent>();
        var output = (await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "low" }, e =>
        {
            events.Add(e);
            return Task.CompletedTask;
        })).OutputOrThrow();

        Assert.Equal([typeof(StreamReasoningEvent), typeof(StreamReasoningEvent), typeof(StreamTextEvent)], events.Select(e => e.GetType()));
        Assert.Equal("17 x 23 = 391", string.Concat(events.OfType<StreamReasoningEvent>().Select(e => e.Reasoning)));
        var reasoning = Assert.IsType<ContentReasoning>(output.Message.ContentList[0]);
        Assert.Equal(("17 x 23 = 391", "sig-xyz"), (reasoning.Reasoning, reasoning.Signature));
        Assert.Equal("No.", output.Completion);
    }

    [Fact]
    public async Task model_args_become_top_level_fields_and_anthropic_beta_becomes_a_header()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler) = Claude(ClaudeThinkingMessage, modelArgs: new Dictionary<string, object?>
        {
            ["anthropic_beta"] = "interleaved-thinking-2025-05-14",
            ["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048}"""),
            ["top_k"] = 40,
        });
        Assert.Equal("interleaved-thinking-2025-05-14", api.AnthropicBeta);
        Assert.Equal(new[] { "thinking", "top_k" }, api.ModelArgs.Keys.Order());

        await api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "high" });
        var body = JsonNode.Parse(handler.Bodies[0]!)!.AsObject();
        Assert.Equal("""{"type":"enabled","budget_tokens":2048}""", body["thinking"]!.ToJsonString());   // the model arg wins over adaptive
        Assert.Equal(40, body["top_k"]!.GetValue<int>());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("interleaved-thinking-2025-05-14", handler.Requests[0].Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public void model_arg_values_parse_scalars_and_json()
    {
        Assert.Equal(true, ProviderUtil.ParseModelArgValue("true"));
        Assert.Equal(42, ProviderUtil.ParseModelArgValue("42"));
        Assert.Equal(0.5, ProviderUtil.ParseModelArgValue("0.5"));
        Assert.Equal("low", ProviderUtil.ParseModelArgValue("low"));
        Assert.Null(ProviderUtil.ParseModelArgValue("null"));
        var thinking = Assert.IsType<JsonObject>(ProviderUtil.ParseModelArgValue("""{"type":"enabled","budget_tokens":512}"""));
        Assert.Equal(512, thinking["budget_tokens"]!.GetValue<int>());
        Assert.IsType<JsonArray>(ProviderUtil.ParseModelArgValue("[1, 2]"));
        Assert.Throws<ArgumentException>(() => ProviderUtil.ParseModelArgValue("{not json"));
    }
}
