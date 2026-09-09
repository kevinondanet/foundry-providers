using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>The OpenAI Responses route for the gpt-5.6 family (and other Responses-only deployments) on Foundry, against canned responses.</summary>
public class ResponsesRouteTests
{
    private const string Inference = "https://res.services.ai.azure.com/models";

    private const string DefaultUsage =
        "\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens\":5,\"output_tokens_details\":{\"reasoning_tokens\":3},\"total_tokens\":15";

    private const string ReasoningItem =
        "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"ENC\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"thinking...\"}]}";

    private const string TextItem =
        "{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello from gpt\",\"annotations\":[]}]}";

    private const string CallItem =
        "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Oslo\\\"}\",\"status\":\"completed\"}";

    private const string RefusalItem =
        "{\"type\":\"message\",\"id\":\"msg_2\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"I cannot help with that.\"}]}";

    private static string ResponseJson(string outputItems, string? incompleteReason = null, string status = "completed", string usage = DefaultUsage) =>
        "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":123,\"status\":\"" + status + "\",\"model\":\"gpt-5.6-sol\",\"output\":[" + outputItems + "],"
        + (incompleteReason is null ? "\"incomplete_details\":null," : "\"incomplete_details\":{\"reason\":\"" + incompleteReason + "\"},")
        + "\"usage\":{" + usage + "}}";

    private static string Sse(string type, string data) => $"event: {type}\ndata: {data}\n\n";

    private static (OpenAIResponsesModelApi Api, FakeArmHandler Handler, FakeTokenCredential Credential) Build(
        string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json", string model = "gpt-5.6-sol")
    {
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        var credential = new FakeTokenCredential("entra-token");
        var api = new OpenAIResponsesModelApi(model, settings: new AzureAIClientSettings { TokenCredential = credential }, handler: handler);
        return (api, handler, credential);
    }

    private static Task<GenerateResult> Hi(OpenAIResponsesModelApi api, GenerateConfig? config = null, IReadOnlyList<ToolInfo>? tools = null) =>
        api.GenerateAsync([new ChatMessageUser("hi")], tools ?? [], ToolChoice.Auto, config ?? new GenerateConfig());

    [Fact]
    public void derive_base_url_and_env_precedence()
    {
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x/models"));
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x/anthropic/"));
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x"));
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x/openai"));
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x/openai/v1/"));
        Assert.Equal("https://x/openai/v1", OpenAIResponsesModelApi.DeriveBaseUrl("https://x/openai/v1/responses"));

        using var env = EnvScope.Clean()
            .Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference)
            .Set(OpenAIResponsesModelApi.AzureAIOpenAIBaseUrlVar, "https://other.openai.azure.com/");
        using var api = new OpenAIResponsesModelApi("azure/gpt-5.6-sol", settings: Fixtures.Entra());
        Assert.Equal("gpt-5.6-sol", api.DeploymentName);
        Assert.Equal("https://other.openai.azure.com/openai/v1", api.BaseUrl);            // the OpenAI vars win over the inference endpoint
        Assert.Equal("https://other.openai.azure.com/openai/v1/responses", api.ResponsesUrl);
        Assert.Null(api.MaxTokens());
        Assert.True(api.IsReasoningModel);
        Assert.Equal(ModelFamilyHint.OpenAI, api.FamilyHint);

        env.Set(OpenAIResponsesModelApi.AzureAIOpenAIBaseUrlVar, null);
        using var derived = new OpenAIResponsesModelApi("gpt-5.6-sol", settings: Fixtures.Entra());
        Assert.Equal("https://res.services.ai.azure.com/openai/v1", derived.BaseUrl);

        env.Set(AzureAIModelApi.AzureAIBaseUrlVar, null);
        var missing = Assert.Throws<PrerequisiteError>(() => new OpenAIResponsesModelApi("gpt-5.6-sol", settings: Fixtures.Entra()));
        Assert.StartsWith("ERROR: Unable to initialise OpenAI on Azure client", missing.Message);
        Assert.Contains("AZUREAI_OPENAI_BASE_URL", missing.Message);
        Assert.Contains("AZURE_OPENAI_BASE_URL", missing.Message);
        Assert.Contains("AZUREAI_BASE_URL", missing.Message);
    }

    [Fact]
    public void name_heuristics_pick_the_responses_route_for_the_deployments_foundry_serves_there()
    {
        Assert.True(OpenAIUtil.PrefersResponsesRoute("gpt-5.6-sol"));
        Assert.True(OpenAIUtil.PrefersResponsesRoute("GPT-5.6-luna-2"));
        Assert.True(OpenAIUtil.PrefersResponsesRoute("gpt-5.4-pro"));
        Assert.True(OpenAIUtil.PrefersResponsesRoute("gpt-5.3-codex"));
        Assert.True(OpenAIUtil.PrefersResponsesRoute("o4-mini"));
        Assert.False(OpenAIUtil.PrefersResponsesRoute("gpt-5.4-mini"));
        Assert.False(OpenAIUtil.PrefersResponsesRoute("gpt-4o"));
        Assert.False(OpenAIUtil.PrefersResponsesRoute("claude-sonnet-4-6"));
        Assert.False(OpenAIUtil.PrefersResponsesRoute("model-router"));

        Assert.True(OpenAIUtil.SupportsMaxReasoningEffort("gpt-5.6-sol"));
        Assert.True(OpenAIUtil.SupportsMaxReasoningEffort("gpt-6-astra"));
        Assert.False(OpenAIUtil.SupportsMaxReasoningEffort("gpt-5.4-mini"));
        Assert.False(OpenAIUtil.SupportsMaxReasoningEffort("gpt-5"));
        Assert.False(OpenAIUtil.SupportsMaxReasoningEffort("o3"));

        Assert.True(OpenAIUtil.HasReasoningOptions("gpt-5.6-sol"));
        Assert.True(OpenAIUtil.HasReasoningOptions("o3-mini"));
        Assert.False(OpenAIUtil.HasReasoningOptions("gpt-5-chat"));
        Assert.False(OpenAIUtil.HasReasoningOptions("gpt-4o"));
    }

    [Fact]
    public async Task request_has_bearer_and_the_responses_shape()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler, credential) = Build(ResponseJson(TextItem));
        using (api)
        {
            var input = new List<ChatMessage>
            {
                new ChatMessageSystem("Be terse."),
                new ChatMessageUser("Weather in Oslo?"),
                new ChatMessageAssistant(
                    MessageContent.FromItems([new ContentReasoning("ENC0", "rs_0", Redacted: true), new ContentText("Let me check.")]),
                    [new ToolCall("call_1", "get_weather", new JsonObject { ["city"] = "Oslo" })]),
                new ChatMessageTool("21C", "call_1", "get_weather"),
                new ChatMessageUser("Thanks, and in Bergen?"),
            };
            var result = await api.GenerateAsync(input, [Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "medium", MaxTokens = 16384 });

            Assert.Equal("hello from gpt", result.OutputOrThrow().Completion);
            var request = handler.Requests.Single();
            Assert.Equal("https://res.services.ai.azure.com/openai/v1/responses", request.RequestUri!.ToString());
            Assert.Equal("Bearer entra-token", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("api-key"));
            Assert.Equal([AzureHosting.DefaultAzureAudience], Assert.Single(credential.Scopes));

            var body = result.Call.Request;
            Assert.Equal("gpt-5.6-sol", body["model"]!.ToString());
            Assert.False(body["store"]!.GetValue<bool>());
            Assert.Equal(["reasoning.encrypted_content"], body["include"]!.AsArray().Select(n => n!.ToString()));
            Assert.Equal(16384, body["max_output_tokens"]!.GetValue<int>());
            Assert.Equal("medium", body["reasoning"]!["effort"]!.ToString());
            Assert.False(body["reasoning"]!.AsObject().ContainsKey("summary"));
            Assert.False(body.ContainsKey("reasoning_effort"));                          // the chat-completions field never appears
            Assert.False(body.ContainsKey("max_completion_tokens"));
            Assert.False(body.ContainsKey("stream"));
            Assert.False(body.ContainsKey("tool_choice"));                               // auto is the API default

            var items = body["input"]!.AsArray();
            Assert.Equal(["message", "message", "reasoning", "message", "function_call", "function_call_output", "message"], items.Select(i => i!["type"]!.ToString()));
            Assert.Equal("developer", items[0]!["role"]!.ToString());
            Assert.Equal("input_text", items[0]!["content"]![0]!["type"]!.ToString());
            Assert.Equal("Be terse.", items[0]!["content"]![0]!["text"]!.ToString());
            Assert.Equal("user", items[1]!["role"]!.ToString());
            Assert.Equal("rs_0", items[2]!["id"]!.ToString());
            Assert.Equal("ENC0", items[2]!["encrypted_content"]!.ToString());
            Assert.Empty(items[2]!["summary"]!.AsArray());
            Assert.False(items[2]!.AsObject().ContainsKey("content"));
            Assert.Equal("assistant", items[3]!["role"]!.ToString());
            Assert.Equal("completed", items[3]!["status"]!.ToString());
            Assert.False(items[3]!.AsObject().ContainsKey("id"));                       // ids are optional with store: false
            Assert.Equal("output_text", items[3]!["content"]![0]!["type"]!.ToString());
            Assert.Equal("Let me check.", items[3]!["content"]![0]!["text"]!.ToString());
            Assert.Equal("call_1", items[4]!["call_id"]!.ToString());
            Assert.Equal("get_weather", items[4]!["name"]!.ToString());
            Assert.False(items[4]!.AsObject().ContainsKey("id"));
            Assert.Equal("Oslo", JsonNode.Parse(items[4]!["arguments"]!.ToString())!["city"]!.ToString());
            Assert.Equal("call_1", items[5]!["call_id"]!.ToString());
            Assert.Equal("21C", items[5]!["output"]!.ToString());

            var tool = body["tools"]![0]!;
            Assert.Equal("function", tool["type"]!.ToString());
            Assert.Equal("get_weather", tool["name"]!.ToString());
            Assert.Equal("Get weather.", tool["description"]!.ToString());
            Assert.False(tool["strict"]!.GetValue<bool>());
            Assert.Null(tool["function"]);                                               // flat, not the chat-completions nesting
            Assert.Equal("string", tool["parameters"]!["properties"]!["city"]!["type"]!.ToString());
            Assert.Null(tool["parameters"]!["properties"]!["city"]!["minLength"]);       // extended validation fields stripped
        }
    }

    [Fact]
    public void completion_params_follow_the_config()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        ProviderLogger.Reset();
        using var api = new OpenAIResponsesModelApi("gpt-5.6-sol", settings: Fixtures.Entra());
        var schema = new ResponseSchema("answer", new JsonSchema { Type = ["object"], Properties = new Dictionary<string, JsonSchema> { ["a"] = new() { Type = ["string"], Examples = [JsonValue.Create("x")] } } })
        {
            Description = "the answer",
            Strict = true,
        };
        var config = new GenerateConfig
        {
            MaxTokens = 100, Temperature = 0.2, TopP = 0.9,
            ReasoningEffort = "max", ReasoningMode = "pro", ReasoningSummary = "auto", Verbosity = "low",
            ParallelToolCalls = false, FrequencyPenalty = 0.1, PresencePenalty = 0.1, StopSeqs = ["END"], Seed = 7,
            NumChoices = 2, Logprobs = true, TopLogprobs = 3, LogitBias = new Dictionary<int, double> { [1] = 1 },
            ResponseSchema = schema, ExtraBody = new JsonObject { ["truncation"] = "auto", ["store"] = false, ["model"] = "ignored" },
        };

        var body = api.BuildRequest([new ChatMessageUser("hi")], [Fixtures.WeatherTool], ToolChoice.Any, config, streaming: true);

        Assert.Equal("max", body["reasoning"]!["effort"]!.ToString());                    // gpt-5.6 takes max verbatim
        Assert.Equal("pro", body["reasoning"]!["mode"]!.ToString());
        Assert.Equal("auto", body["reasoning"]!["summary"]!.ToString());
        Assert.Equal(100, body["max_output_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("temperature"));                                     // dropped while reasoning is on
        Assert.False(body.ContainsKey("top_p"));
        Assert.Equal("required", body["tool_choice"]!.ToString());
        Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("json_schema", body["text"]!["format"]!["type"]!.ToString());
        Assert.Equal("answer", body["text"]!["format"]!["name"]!.ToString());
        Assert.Equal("the answer", body["text"]!["format"]!["description"]!.ToString());
        Assert.True(body["text"]!["format"]!["strict"]!.GetValue<bool>());
        Assert.Null(body["text"]!["format"]!["schema"]!["properties"]!["a"]!["examples"]);
        Assert.Null(body["text"]!["format"]!["json_schema"]);
        Assert.Equal("low", body["text"]!["verbosity"]!.ToString());
        Assert.Equal("auto", body["truncation"]!.ToString());                              // whitelisted extra_body field
        Assert.Equal("gpt-5.6-sol", body["model"]!.ToString());                            // extra_body cannot override the model
        Assert.True(body["stream"]!.GetValue<bool>());
        foreach (var unsupported in new[] { "frequency_penalty", "presence_penalty", "stop", "seed", "n", "logprobs", "top_logprobs", "logit_bias" })
        {
            Assert.False(body.ContainsKey(unsupported), unsupported);
        }

        Assert.Contains(OpenAIResponsesModelApi.TemperatureIgnoredWarning, ProviderLogger.Warnings);
        Assert.Contains(OpenAIResponsesModelApi.TopPIgnoredWarning, ProviderLogger.Warnings);
        Assert.Contains(OpenAIResponsesModelApi.UnsupportedParamWarning("seed"), ProviderLogger.Warnings);
        Assert.Contains(OpenAIResponsesModelApi.UnsupportedParamWarning("num_choices"), ProviderLogger.Warnings);
        api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, config, streaming: false);
        Assert.Single(ProviderLogger.Warnings, OpenAIResponsesModelApi.UnsupportedParamWarning("seed"));   // warned once

        // max becomes xhigh before gpt-5.6; the include is sent for any reasoning model
        using var mini = new OpenAIResponsesModelApi("gpt-5.4-mini", settings: Fixtures.Entra());
        var miniBody = mini.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "max" }, streaming: false);
        Assert.Equal("xhigh", miniBody["reasoning"]!["effort"]!.ToString());
        Assert.Equal(["reasoning.encrypted_content"], miniBody["include"]!.AsArray().Select(n => n!.ToString()));

        // sampling parameters are sent when reasoning is off; the effort none still travels
        var off = api.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "none", Temperature = 0.3, TopP = 0.5 }, streaming: false);
        Assert.Equal(0.3, off["temperature"]!.GetValue<double>());
        Assert.Equal(0.5, off["top_p"]!.GetValue<double>());
        Assert.Equal("none", off["reasoning"]!["effort"]!.ToString());
        Assert.False(off.ContainsKey("summary"));

        // extra_body store: true switches the include off; model args are applied last and override derived fields
        using var stored = new OpenAIResponsesModelApi("gpt-5.6-sol", settings: Fixtures.Entra(), modelArgs: new Dictionary<string, object?> { ["reasoning"] = JsonNode.Parse("{\"effort\":\"low\"}"), ["model_format"] = "OpenAI" });
        var storedBody = stored.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "high", ExtraBody = new JsonObject { ["store"] = true } }, streaming: false);
        Assert.True(storedBody["store"]!.GetValue<bool>());
        Assert.False(storedBody.ContainsKey("include"));
        Assert.Equal("low", storedBody["reasoning"]!["effort"]!.ToString());
        Assert.False(storedBody.ContainsKey("model_format"));
        using var storedByArg = new OpenAIResponsesModelApi("gpt-5.6-sol", settings: Fixtures.Entra(), modelArgs: new Dictionary<string, object?> { ["store"] = true });
        var storedByArgBody = storedByArg.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { ReasoningEffort = "high" }, streaming: false);
        Assert.True(storedByArgBody["store"]!.GetValue<bool>());
        Assert.False(storedByArgBody.ContainsKey("include"));

        // a non-reasoning name with no reasoning settings: nothing reasoning-related, sampling sent, no max_output_tokens default
        using var legacy = new OpenAIResponsesModelApi("gpt-4o", settings: Fixtures.Entra());
        var legacyBody = legacy.BuildRequest([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig { Temperature = 0.5 }, streaming: false);
        Assert.False(legacyBody.ContainsKey("reasoning"));
        Assert.False(legacyBody.ContainsKey("include"));
        Assert.False(legacyBody.ContainsKey("max_output_tokens"));
        Assert.Equal(0.5, legacyBody["temperature"]!.GetValue<double>());
        Assert.False(legacyBody.ContainsKey("tools"));
        Assert.False(legacyBody.ContainsKey("parallel_tool_calls"));
    }

    [Fact]
    public async Task tool_choice_and_the_python_alias_map_both_ways()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var python = new ToolInfo("python", "Run Python.") { Parameters = Fixtures.WeatherTool.Parameters };
        var pythonCall = "{\"type\":\"function_call\",\"id\":\"fc_2\",\"call_id\":\"call_2\",\"name\":\"python_exec\",\"arguments\":\"{\\\"city\\\":\\\"x\\\"}\"}";
        var (api, _, _) = Build(ResponseJson(pythonCall));
        using (api)
        {
            var none = api.BuildRequest([new ChatMessageUser("hi")], [python], ToolChoice.None, new GenerateConfig(), streaming: false);
            Assert.Equal("none", none["tool_choice"]!.ToString());
            Assert.Equal("python_exec", none["tools"]![0]!["name"]!.ToString());
            var named = api.BuildRequest([new ChatMessageUser("hi")], [python], new ToolFunction("python"), new GenerateConfig(), streaming: false);
            Assert.Equal("function", named["tool_choice"]!["type"]!.ToString());
            Assert.Equal("python_exec", named["tool_choice"]!["name"]!.ToString());

            var output = (await Hi(api, tools: [python])).OutputOrThrow();
            var call = Assert.Single(output.Message.ToolCalls!);
            Assert.Equal(("call_2", "python"), (call.Id, call.Function));
            Assert.Equal(StopReason.ToolCalls, output.StopReason);
        }
    }

    [Fact]
    public async Task output_items_stop_reasons_and_usage_are_mapped()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _, _) = Build(ResponseJson(ReasoningItem + "," + TextItem + "," + CallItem));
        using (api)
        {
            var output = (await Hi(api, tools: [Fixtures.WeatherTool])).OutputOrThrow();

            Assert.Equal(StopReason.ToolCalls, output.StopReason);
            Assert.Equal("gpt-5.6-sol", output.Model);
            Assert.Equal("hello from gpt", output.Completion);
            var reasoning = Assert.IsType<ContentReasoning>(output.Message.ContentList[0]);
            Assert.Equal(new ContentReasoning("ENC", "rs_1", Redacted: true) { Summary = "thinking..." }, reasoning);
            Assert.IsType<ContentText>(output.Message.ContentList[1]);
            var call = Assert.Single(output.Message.ToolCalls!);
            Assert.Equal(("call_1", "get_weather", "Oslo"), (call.Id, call.Function, call.Arguments["city"]!.ToString()));
            Assert.Null(call.ParseError);
            Assert.Equal(new ModelUsage(8, 5, 15) { InputTokensCacheRead = 2, ReasoningTokens = 3 }, output.Usage);   // cached tokens taken out of input
            Assert.Null(output.Choices[0].StopDetails);
        }

        var truncated = Build(ResponseJson(TextItem, incompleteReason: "max_output_tokens", status: "incomplete")).Api;
        Assert.Equal(StopReason.MaxTokens, (await Hi(truncated)).OutputOrThrow().StopReason);

        var filtered = (await Hi(Build(ResponseJson(RefusalItem, incompleteReason: "content_filter", status: "incomplete")).Api)).OutputOrThrow();
        Assert.Equal(StopReason.ContentFilter, filtered.StopReason);
        Assert.Equal("content_filter", filtered.Choices[0].StopDetails!.Type);
        Assert.Equal("I cannot help with that.", filtered.Choices[0].StopDetails!.Explanation);
        Assert.True(Assert.IsType<ContentText>(filtered.Message.ContentList[0]).Refusal);

        var refused = (await Hi(Build(ResponseJson(RefusalItem)).Api)).OutputOrThrow();
        Assert.Equal(StopReason.Stop, refused.StopReason);
        Assert.Equal("refusal", refused.Choices[0].StopDetails!.Type);

        // unknown output item types are skipped, not fatal; a readable reasoning item is not redacted
        ProviderLogger.Reset();
        var exotic = "{\"type\":\"web_search_call\",\"id\":\"ws_1\",\"status\":\"completed\"},{\"type\":\"reasoning\",\"id\":\"rs_2\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"visible\"}],\"summary\":[]}," + TextItem;
        var mixed = (await Hi(Build(ResponseJson(exotic)).Api)).OutputOrThrow();
        Assert.Equal("hello from gpt", mixed.Completion);
        Assert.Equal(new ContentReasoning("visible", "rs_2"), Assert.IsType<ContentReasoning>(mixed.Message.ContentList[0]));
        Assert.Contains(ProviderLogger.Warnings, w => w.Contains("web_search_call"));

        var noUsage = (await Hi(Build("{\"id\":\"resp_2\",\"status\":\"completed\",\"output\":[]}").Api)).OutputOrThrow();
        Assert.Equal("", noUsage.Completion);
        Assert.Equal(StopReason.Stop, noUsage.StopReason);
        Assert.Null(noUsage.Usage);
        Assert.Equal("gpt-5.6-sol", noUsage.Model);
    }

    [Fact]
    public async Task reasoning_items_round_trip_encrypted_and_are_dropped_without_a_blob()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        ProviderLogger.Reset();
        var (api, handler, _) = Build(ResponseJson(TextItem));
        using (api)
        {
            var history = new List<ChatMessage>
            {
                new ChatMessageUser("hi"),
                new ChatMessageAssistant(MessageContent.FromItems([new ContentReasoning("ENC", "rs_1", Redacted: true) { Summary = "thinking..." }, new ContentText("first")]), model: "gpt-5.6-sol"),
                new ChatMessageUser("more"),
                new ChatMessageAssistant(MessageContent.FromItems([new ContentReasoning("plain text reasoning"), new ContentText("second")])),
                new ChatMessageUser("again"),
            };
            await api.GenerateAsync(history, [], ToolChoice.Auto, new GenerateConfig());

            var items = JsonNode.Parse(handler.Bodies.Single()!)!["input"]!.AsArray();
            Assert.Equal(["message", "reasoning", "message", "message", "message", "message"], items.Select(i => i!["type"]!.ToString()));
            var reasoning = items[1]!;
            Assert.Equal("rs_1", reasoning["id"]!.ToString());
            Assert.Equal("ENC", reasoning["encrypted_content"]!.ToString());
            Assert.Equal("summary_text", reasoning["summary"]![0]!["type"]!.ToString());
            Assert.Equal("thinking...", reasoning["summary"]![0]!["text"]!.ToString());
            Assert.Equal("first", items[2]!["content"]![0]!["text"]!.ToString());
            Assert.Equal("second", items[4]!["content"]![0]!["text"]!.ToString());
            Assert.Single(ProviderLogger.Warnings, ResponsesInput.ReasoningNotReplayableWarning);
        }
    }

    [Fact]
    public async Task streaming_events_are_folded_and_delivered()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var sse = string.Concat(
            Sse("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\",\"output\":[]}}"),
            Sse("response.output_item.added", "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[]}}"),
            Sse("response.reasoning_summary_text.delta", "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_1\",\"delta\":\"think\"}"),
            Sse("response.output_item.added", "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[]}}"),
            Sse("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"hello \"}"),
            Sse("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"delta\":\"from gpt\"}"),
            Sse("response.output_text.done", "{\"type\":\"response.output_text.done\",\"item_id\":\"msg_1\",\"text\":\"hello from gpt\"}"),
            Sse("response.output_item.added", "{\"type\":\"response.output_item.added\",\"output_index\":2,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"get_weather\",\"arguments\":\"\"}}"),
            Sse("response.function_call_arguments.delta", "{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"delta\":\"{\\\"city\\\":\"}"),
            Sse("response.function_call_arguments.delta", "{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"delta\":\"\\\"Oslo\\\"}\"}"),
            Sse("response.completed", "{\"type\":\"response.completed\",\"response\":" + ResponseJson(ReasoningItem + "," + TextItem + "," + CallItem) + "}"));
        var (api, _, _) = Build(sse, contentType: "text/event-stream");
        using (api)
        {
            var collector = new StreamCollector();
            var result = await api.GenerateAsync([new ChatMessageUser("hi")], [Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig(), collector.Collect);

            Assert.True(result.Call.Request["stream"]!.GetValue<bool>());
            var output = result.OutputOrThrow();
            Assert.Equal("hello from gpt", output.Completion);
            Assert.Equal(StopReason.ToolCalls, output.StopReason);
            Assert.Equal(new ModelUsage(8, 5, 15) { InputTokensCacheRead = 2, ReasoningTokens = 3 }, output.Usage);
            Assert.Equal("hello from gpt", string.Concat(collector.Events.OfType<StreamTextEvent>().Select(e => e.Text)));
            Assert.Equal("think", string.Concat(collector.Events.OfType<StreamReasoningEvent>().Select(e => e.Reasoning)));
            var toolEvents = collector.Events.OfType<StreamToolCallEvent>().ToList();
            Assert.Equal(2, toolEvents.Count);
            Assert.Equal(("call_1", "get_weather"), (toolEvents[0].Id, toolEvents[0].Function));
            Assert.Equal("{\"city\":\"Oslo\"}", string.Concat(toolEvents.Select(e => e.Arguments)));
            Assert.Equal("resp_1", result.Call.Response!["id"]!.ToString());            // the terminal event's response object is what is recorded
            Assert.Equal("completed", result.Call.Response!["status"]!.ToString());
            Assert.Equal(3, result.Call.Response!["output"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task stream_without_a_terminal_event_or_with_an_error_event()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var collector = new StreamCollector();

        var (dangling, _, _) = Build(Sse("response.created", "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}"), contentType: "text/event-stream");
        var noTerminal = await Assert.ThrowsAsync<ServiceResponseException>(() => dangling.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig(), collector.Collect));
        Assert.Equal(ResponsesStreamAccumulator.NoTerminalEventError, noTerminal.Message);

        var (invalid, _, _) = Build(Sse("error", "{\"type\":\"error\",\"code\":\"invalid_request\",\"message\":\"bad input\"}"), contentType: "text/event-stream");
        var failed = await Assert.ThrowsAsync<RequestFailedException>(() => invalid.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig(), collector.Collect));
        Assert.Equal("bad input", failed.Message);
        Assert.False(invalid.ShouldRetry(failed).Retry);

        var (transient, _, _) = Build(Sse("error", "{\"type\":\"error\",\"error\":{\"code\":\"server_error\",\"message\":\"upstream hiccup\"}}"), contentType: "text/event-stream");
        var hiccup = await Assert.ThrowsAsync<ServiceResponseException>(() => transient.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig(), collector.Collect));
        Assert.Equal("upstream hiccup", hiccup.Message);
        Assert.True(transient.ShouldRetry(hiccup).Retry);

        var (filtered, _, _) = Build(Sse("error", "{\"type\":\"error\",\"code\":\"content_filter\",\"message\":\"blocked by policy\"}"), contentType: "text/event-stream");
        var result = await filtered.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig(), collector.Collect);
        Assert.Equal(StopReason.ContentFilter, result.OutputOrThrow().StopReason);
        Assert.True(result.Call.Error);

        // a completed-status body carrying `error` (response.failed) takes the same path without streaming
        var (failedBody, _, _) = Build("{\"id\":\"resp_3\",\"status\":\"failed\",\"output\":[],\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"This model's maximum context length is 400000 tokens.\"}}");
        Assert.Equal(StopReason.ModelLength, (await Hi(failedBody)).OutputOrThrow().StopReason);
    }

    [Fact]
    public async Task http_400_is_returned_429_thrown_and_refusal_codes_become_output()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _, _) = Build("{\"error\":{\"message\":\"Unsupported parameter: 'seed'\",\"type\":\"invalid_request_error\",\"param\":\"seed\",\"code\":\"unsupported_parameter\"}}", HttpStatusCode.BadRequest);
        using (api)
        {
            var result = await Hi(api);
            Assert.Null(result.Output);
            var error = Assert.IsType<RequestFailedException>(result.Error);
            Assert.Equal(400, error.Status);
            Assert.Equal("Unsupported parameter: 'seed'", error.Message);
            Assert.True(result.Call.Error);
            Assert.Equal("unsupported_parameter", result.Call.Response!["error"]!["code"]!.ToString());
            Assert.False(api.ShouldRetry(error).Retry);
            Assert.False(api.IsAuthFailure(error));
            Assert.True(api.IsAuthFailure(new RequestFailedException(401, "no")));
        }

        var (context, _, _) = Build("{\"error\":{\"message\":\"This model's maximum context length is 400000 tokens.\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}", HttpStatusCode.BadRequest);
        var contextOutput = (await Hi(context)).OutputOrThrow();
        Assert.Equal(StopReason.ModelLength, contextOutput.StopReason);
        Assert.Contains("maximum context length", contextOutput.Completion);

        var azureFilter = "{\"error\":{\"message\":\"The response was filtered due to the prompt triggering Azure OpenAI's content management policy.\",\"type\":null,\"param\":\"prompt\",\"code\":\"content_filter\",\"status\":400,\"innererror\":{\"code\":\"ResponsibleAIPolicyViolation\"}}}";
        var (filtered, _, _) = Build(azureFilter, HttpStatusCode.BadRequest);
        var filteredResult = await Hi(filtered);
        var filteredOutput = filteredResult.OutputOrThrow();
        Assert.Equal(StopReason.ContentFilter, filteredOutput.StopReason);
        Assert.Equal("refusal", filteredOutput.Choices[0].StopDetails!.Type);
        Assert.Contains("content management policy", filteredOutput.Choices[0].StopDetails!.Explanation);
        Assert.True(filteredResult.Call.Error);

        var (limited, _, _) = Build("{\"error\":{\"message\":\"slow down\",\"code\":\"rate_limit_exceeded\"}}", HttpStatusCode.TooManyRequests);
        var thrown = await Assert.ThrowsAsync<RequestFailedException>(() => Hi(limited));
        Assert.Equal(429, thrown.Status);
        Assert.Equal("slow down", thrown.Message);
        Assert.Equal(RetryKind.RateLimit, limited.ShouldRetry(thrown).Kind);

        var (broken, _, _) = Build("<html>bad gateway</html>", HttpStatusCode.BadGateway, "text/html");
        var gateway = await Assert.ThrowsAsync<RequestFailedException>(() => Hi(broken));
        Assert.Equal(502, gateway.Status);
        Assert.Equal("<html>bad gateway</html>", gateway.Message);
        Assert.Equal(RetryKind.Transient, broken.ShouldRetry(gateway).Kind);
    }

    [Fact]
    public async Task images_are_inlined_and_redacted_in_the_model_call()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var png = "data:image/png;base64," + new string('A', 200);
        var (api, handler, _) = Build(ResponseJson(TextItem));
        using (api)
        {
            var input = new List<ChatMessage>
            {
                new ChatMessageUser(MessageContent.FromItems([new ContentText("look"), new ContentImage(png, "high"), new ContentImage("https://example.com/a.png")])),
                new ChatMessageAssistant("ok", [new ToolCall("call_1", "screenshot", new JsonObject())]),
                new ChatMessageTool(MessageContent.FromItems([new ContentText("captured"), new ContentImage(png)]), "call_1", "screenshot"),
            };
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, new GenerateConfig());

            var wire = JsonNode.Parse(handler.Bodies.Single()!)!["input"]!.AsArray();
            Assert.Equal(["message", "message", "function_call", "function_call_output"], wire.Select(i => i!["type"]!.ToString()));
            var userParts = wire[0]!["content"]!.AsArray();
            Assert.Equal("input_image", userParts[1]!["type"]!.ToString());
            Assert.Equal(png, userParts[1]!["image_url"]!.ToString());
            Assert.Equal("high", userParts[1]!["detail"]!.ToString());
            Assert.Equal("https://example.com/a.png", userParts[2]!["image_url"]!.ToString());
            var toolOutput = wire[3]!["output"]!.AsArray();
            Assert.Equal(["input_text", "input_image"], toolOutput.Select(p => p!["type"]!.ToString()));
            Assert.Equal(png, toolOutput[1]!["image_url"]!.ToString());

            var recorded = result.Call.Request["input"]!.AsArray();
            Assert.Equal(OpenAIUtil.Base64DataRemoved, recorded[0]!["content"]![1]!["image_url"]!.ToString());
            Assert.Equal("https://example.com/a.png", recorded[0]!["content"]![2]!["image_url"]!.ToString());
            Assert.Equal(OpenAIUtil.Base64DataRemoved, recorded[3]!["output"]![1]!["image_url"]!.ToString());
        }

        Assert.Throws<InvalidOperationException>(() => ResponsesInput.ContentParts([new ContentAudio("data:audio/wav;base64,AAAA", "wav")]));
    }

    [Fact]
    public async Task tool_errors_refusals_and_empty_assistant_text_are_shaped_like_python()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, handler, _) = Build(ResponseJson(TextItem));
        using (api)
        {
            var input = new List<ChatMessage>
            {
                new ChatMessageUser("hi"),
                new ChatMessageAssistant("", [new ToolCall("call_1", "python", new JsonObject { ["code"] = "1+1" })]),
                new ChatMessageTool("partial output", "call_1", "python", new ToolCallError("timeout", "Command timed out")),
                new ChatMessageAssistant(MessageContent.FromItems([new ContentText("I cannot help with that.") { Refusal = true }])),
                new ChatMessageUser("ok"),
            };
            await api.GenerateAsync(input, [], ToolChoice.Auto, new GenerateConfig());

            var items = JsonNode.Parse(handler.Bodies.Single()!)!["input"]!.AsArray();
            Assert.Equal(["message", "function_call", "function_call_output", "message", "message"], items.Select(i => i!["type"]!.ToString()));   // no empty assistant message
            Assert.Equal("python_exec", items[1]!["name"]!.ToString());
            Assert.Equal("Command timed out", items[2]!["output"]!.ToString());                   // the error message verbatim, no prefix
            Assert.Equal("refusal", items[3]!["content"]![0]!["type"]!.ToString());
            Assert.Equal("I cannot help with that.", items[3]!["content"]![0]!["refusal"]!.ToString());
        }
    }

    [Fact]
    public void function_call_arguments_are_middle_truncated_at_one_mib()
    {
        var call = new ToolCall("call_1", "write", new JsonObject { ["text"] = new string('x', 1_500_000) });
        var item = ResponsesInput.FunctionCallItem(call);
        var arguments = item["arguments"]!.ToString();
        Assert.True(Encoding.UTF8.GetByteCount(arguments) <= ResponsesInput.MaxFunctionCallArguments);
        Assert.StartsWith("{\"text\": \"xxx", arguments);
        Assert.Equal("{\"a\": 1}", ResponsesInput.FunctionCallItem(new ToolCall("c", "f", new JsonObject { ["a"] = 1 }))["arguments"]!.ToString());
    }
}
