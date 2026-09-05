using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Claude's server-side web search on the Anthropic Messages route: tool param, response parsing, replay and streaming.</summary>
public class AnthropicWebSearchTests
{
    private const string Inference = "https://res.services.ai.azure.com/models";

    private static ToolInfo WebSearchTool(JsonNode? anthropicOptions = null, bool includeAnthropic = true)
    {
        var options = new JsonObject { [AnthropicWebSearch.InternalToolType] = "web_search", ["tavily"] = new JsonObject() };
        if (includeAnthropic)
        {
            options["anthropic"] = anthropicOptions;
        }

        return new ToolInfo("web_search", "Use the web_search tool to perform keyword searches of the web.")
        {
            Parameters = new ToolParams { Properties = new Dictionary<string, ToolParam> { ["query"] = ToolParam.Of("string", "Search query.") }, Required = ["query"] },
            Options = options,
        };
    }

    private const string SearchResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[
          {"type":"text","text":"Let me search."},
          {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"capital of France"}},
          {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://en.wikipedia.org/wiki/Paris","title":"Paris","encrypted_content":"ENC","page_age":"2024-01-01"}]},
          {"type":"text","text":"Paris is the capital of France.","citations":[{"type":"web_search_result_location","url":"https://en.wikipedia.org/wiki/Paris","title":"Paris","cited_text":"Paris is the capital","encrypted_index":"IDX"}]}
        ],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5,"server_tool_use":{"web_search_requests":1}}}
        """;

    private static (AnthropicFoundryModelApi Api, FakeArmHandler Handler) Build(string body, string deployment = "claude-sonnet-4-6")
    {
        var handler = new FakeArmHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var api = new AnthropicFoundryModelApi(deployment, settings: Fixtures.Entra(), handler: handler);
        return (api, handler);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-4-20250514", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData("claude-3-7-sonnet-20250219", true)]
    [InlineData("claude-3-5-sonnet-latest", true)]
    [InlineData("claude-3-5-haiku-latest", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-3-5-sonnet-20240620", false)]
    [InlineData("claude-3-opus-20240229", false)]
    [InlineData("my-custom-deployment", false)]
    public void supports_web_search_follows_the_python_model_list(string model, bool expected)
    {
        Assert.Equal(expected, AnthropicWebSearch.SupportsWebSearch(model));
    }

    [Fact]
    public void server_tool_param_copies_the_supported_anthropic_options()
    {
        var options = new JsonObject
        {
            ["allowed_domains"] = new JsonArray("example.com"),
            ["max_uses"] = 3,
            ["user_location"] = new JsonObject { ["type"] = "approximate", ["city"] = "Oslo" },
            ["unknown_option"] = 1,
        };

        var param = AnthropicWebSearch.ServerToolParam(WebSearchTool(options), "claude-sonnet-4-6")!;

        Assert.Equal("{\"name\":\"web_search\",\"type\":\"web_search_20250305\",\"allowed_domains\":[\"example.com\"],\"max_uses\":3,\"user_location\":{\"type\":\"approximate\",\"city\":\"Oslo\"}}", param.ToJsonString());
        Assert.Equal("{\"name\":\"web_search\",\"type\":\"web_search_20250305\"}", AnthropicWebSearch.ServerToolParam(WebSearchTool(), "claude-sonnet-4-6")!.ToJsonString());
        Assert.Null(AnthropicWebSearch.ServerToolParam(WebSearchTool(), "claude-3-5-sonnet-20240620"));
        Assert.Null(AnthropicWebSearch.ServerToolParam(WebSearchTool(includeAnthropic: false), "claude-sonnet-4-6"));
        Assert.Null(AnthropicWebSearch.ServerToolParam(Fixtures.WeatherTool, "claude-sonnet-4-6"));
        Assert.Throws<ArgumentException>(() => AnthropicWebSearch.ServerToolParam(WebSearchTool(JsonValue.Create("bad")), "claude-sonnet-4-6"));
    }

    [Fact]
    public async Task the_request_carries_the_server_tool_instead_of_a_function_tool()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse);

        var result = await api.GenerateAsync([new ChatMessageUser("Capital of France?")], [WebSearchTool(new JsonObject { ["max_uses"] = 2 }), Fixtures.WeatherTool], ToolChoice.Auto, new GenerateConfig());

        var tools = result.Call.Request["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("{\"name\":\"web_search\",\"type\":\"web_search_20250305\",\"max_uses\":2}", tools[0]!.ToJsonString());
        Assert.Equal("get_weather", tools[1]!["name"]!.ToString());
        Assert.NotNull(tools[1]!["input_schema"]);
        Assert.Equal("auto", result.Call.Request["tool_choice"]!["type"]!.ToString());
    }

    [Fact]
    public async Task an_unsupported_model_still_gets_the_function_tool()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse, "claude-3-5-sonnet-20240620");

        var result = await api.GenerateAsync([new ChatMessageUser("hi")], [WebSearchTool()], ToolChoice.Auto, new GenerateConfig());

        var tool = Assert.Single(result.Call.Request["tools"]!.AsArray());
        Assert.Equal("web_search", tool!["name"]!.ToString());
        Assert.NotNull(tool["input_schema"]);
        Assert.Null(tool["type"]);
    }

    [Fact]
    public void the_response_pairs_server_tool_use_with_its_result_and_reads_citations()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse);

        var output = api.ParseMessage(JsonNode.Parse(SearchResponse)!.AsObject());

        var assistant = output.Choices[0].Message;
        Assert.Null(assistant.ToolCalls);
        Assert.Equal(StopReason.Stop, output.Choices[0].StopReason);
        var items = assistant.Content.Items!;
        Assert.Equal(3, items.Count);
        Assert.Equal("Let me search.", Assert.IsType<ContentText>(items[0]).Text);
        var toolUse = Assert.IsType<ContentToolUse>(items[1]);
        Assert.Equal("web_search", toolUse.ToolType);
        Assert.Equal("srvtoolu_1", toolUse.Id);
        Assert.Equal("web_search", toolUse.Name);
        Assert.Equal("{\"query\": \"capital of France\"}", toolUse.Arguments);
        Assert.Equal("[{\"type\": \"web_search_result\", \"url\": \"https://en.wikipedia.org/wiki/Paris\", \"title\": \"Paris\", \"encrypted_content\": \"ENC\", \"page_age\": \"2024-01-01\"}]", toolUse.Result);
        Assert.Null(toolUse.Error);
        var cited = Assert.IsType<ContentText>(items[2]);
        Assert.Equal("Paris is the capital of France.", cited.Text);
        var citation = Assert.IsType<UrlCitation>(Assert.Single(cited.Citations!));
        Assert.Equal("https://en.wikipedia.org/wiki/Paris", citation.Url);
        Assert.Equal("Paris", citation.Title);
        Assert.Equal("Paris is the capital", citation.CitedText);
        Assert.Equal("IDX", citation.Internal!["encrypted_index"]!.GetValue<string>());
        Assert.Equal("Let me search.\nParis is the capital of France.", assistant.Text);
    }

    [Fact]
    public void a_failed_search_carries_its_error_code_and_an_orphan_result_is_rejected()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse);
        var failed = JsonNode.Parse("""
            {"content":[
              {"type":"server_tool_use","id":"srvtoolu_2","name":"web_search","input":{"query":"q"}},
              {"type":"web_search_tool_result","tool_use_id":"srvtoolu_2","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}
            ],"stop_reason":"end_turn"}
            """)!.AsObject();
        var orphan = JsonNode.Parse("""{"content":[{"type":"web_search_tool_result","tool_use_id":"srvtoolu_9","content":[]}],"stop_reason":"end_turn"}""")!.AsObject();

        var toolUse = Assert.IsType<ContentToolUse>(Assert.Single(api.ParseMessage(failed).Choices[0].Message.Content.Items!));
        Assert.Equal("max_uses_exceeded", toolUse.Error);
        Assert.Equal("{\"type\": \"web_search_tool_result_error\", \"error_code\": \"max_uses_exceeded\"}", toolUse.Result);
        Assert.Throws<ServiceResponseException>(() => api.ParseMessage(orphan));
    }

    [Fact]
    public async Task the_assistant_turn_replays_the_server_tool_blocks_and_citations()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse);
        var previous = api.ParseMessage(JsonNode.Parse(SearchResponse)!.AsObject()).Choices[0].Message;
        ProviderLogger.Reset();

        var result = await api.GenerateAsync([new ChatMessageUser("Capital of France?"), previous, new ChatMessageUser("And Germany?")], [WebSearchTool()], ToolChoice.Auto, new GenerateConfig());

        var messages = result.Call.Request["messages"]!.AsArray();
        var blocks = messages[1]!["content"]!.AsArray();
        Assert.Equal(["text", "server_tool_use", "web_search_tool_result", "text"], blocks.Select(b => b!["type"]!.ToString()));
        Assert.Equal("{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_1\",\"name\":\"web_search\",\"input\":{\"query\":\"capital of France\"}}", blocks[1]!.ToJsonString());
        Assert.Equal("ENC", blocks[2]!["content"]![0]!["encrypted_content"]!.ToString());
        Assert.Equal("srvtoolu_1", blocks[2]!["tool_use_id"]!.ToString());
        Assert.Equal("{\"type\":\"web_search_result_location\",\"cited_text\":\"Paris is the capital\",\"title\":\"Paris\",\"url\":\"https://en.wikipedia.org/wiki/Paris\",\"encrypted_index\":\"IDX\"}", blocks[3]!["citations"]![0]!.ToJsonString());
        Assert.Null(blocks[0]!["citations"]);
        Assert.Empty(ProviderLogger.Warnings);
    }

    [Fact]
    public void replay_reconstructs_foreign_results_and_drops_what_cannot_be_expressed()
    {
        ProviderLogger.Reset();
        var foreign = new ContentToolUse("web_search", "ws_1", "web_search", "{\"query\": \"q\"}", "plain text from another system");
        var mcp = new ContentToolUse("mcp_call", "mcp_1", "lookup", "{}", "{}");

        var blocks = AnthropicWebSearch.ReplayBlocks(foreign);
        var textBlock = new JsonObject { ["type"] = "text", ["text"] = "t" };
        AnthropicWebSearch.AddCitations(textBlock, [new UrlCitation("https://tavily.example") { CitedText = "x" }]);

        Assert.Equal("{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"ws_1\",\"content\":{\"type\":\"web_search_tool_result_error\",\"error_code\":\"unavailable\"}}", blocks[1].ToJsonString());
        Assert.Empty(AnthropicWebSearch.ReplayBlocks(mcp));
        Assert.Null(textBlock["citations"]);
        Assert.Equal(2, ProviderLogger.Warnings.Count);
    }

    [Fact]
    public void citations_map_both_ways_including_document_locations()
    {
        var longTitle = new string('t', 300);
        var web = AnthropicWebSearch.ToInspectCitation(JsonNode.Parse($$"""{"type":"web_search_result_location","url":"https://e.com","title":"{{longTitle}}","cited_text":"c","encrypted_index":"i"}""")!.AsObject());
        var page = AnthropicWebSearch.ToInspectCitation(JsonNode.Parse("""{"type":"page_location","cited_text":"c","document_index":2,"document_title":"Doc","start_page_number":3,"end_page_number":4}""")!.AsObject());
        var other = AnthropicWebSearch.ToInspectCitation(JsonNode.Parse("""{"type":"search_result_location","cited_text":"c","source":"s","search_result_index":0}""")!.AsObject());

        var url = Assert.IsType<UrlCitation>(web);
        Assert.Equal(255, url.Title!.Length);
        Assert.EndsWith("…", url.Title);
        var document = Assert.IsType<DocumentCitation>(page);
        Assert.Equal(new DocumentRange("page", 3, 4), document.Range);
        Assert.Equal("Doc", document.Title);
        Assert.Equal("{\"type\":\"page_location\",\"cited_text\":\"c\",\"document_index\":2,\"document_title\":\"Doc\",\"start_page_number\":3,\"end_page_number\":4}", AnthropicWebSearch.ToAnthropicCitation(document)!.ToJsonString());
        var content = Assert.IsType<ContentCitation>(other);
        Assert.Equal("search_result_location", content.Internal!["type"]!.ToString());
    }

    [Fact]
    public async Task streaming_accumulates_server_tool_blocks_and_citation_deltas()
    {
        using var env = EnvScope.Clean().Set(AzureAIModelApi.AzureAIBaseUrlVar, Inference);
        var (api, _) = Build(SearchResponse);
        var events = new[]
        {
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[],"stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\": \"capi"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"tal of France\"}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://en.wikipedia.org/wiki/Paris","title":"Paris","encrypted_content":"ENC","page_age":null}]}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Paris is the capital"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://en.wikipedia.org/wiki/Paris","title":"Paris","cited_text":"Paris is the capital","encrypted_index":"IDX"}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":" of France."}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":20}}""",
            """{"type":"message_stop"}""",
        };

        var message = await AnthropicFoundryModelApi.AccumulateAsync(Events(events));
        var output = api.ParseMessage(message);

        var items = output.Choices[0].Message.Content.Items!;
        var toolUse = Assert.IsType<ContentToolUse>(items[0]);
        Assert.Equal("{\"query\": \"capital of France\"}", toolUse.Arguments);
        Assert.Contains("\"encrypted_content\": \"ENC\"", toolUse.Result);
        var text = Assert.IsType<ContentText>(items[1]);
        Assert.Equal("Paris is the capital of France.", text.Text);
        Assert.Equal("IDX", Assert.IsType<UrlCitation>(Assert.Single(text.Citations!)).Internal!["encrypted_index"]!.ToString());
        Assert.Equal(StopReason.Stop, output.Choices[0].StopReason);
    }

    private static async IAsyncEnumerable<JsonObject> Events(IEnumerable<string> events)
    {
        foreach (var evt in events)
        {
            yield return JsonNode.Parse(evt)!.AsObject();
            await Task.Yield();
        }
    }
}
