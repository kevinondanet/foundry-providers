using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Framework-contract helpers the provider relies on (stop details, redaction, output shaping).</summary>
public class CoreContractTests
{
    [Fact]
    public void openai_stop_details_ignores_detected_but_not_filtered()
    {
        var choice = JsonNode.Parse("""{"finish_reason":"stop","content_filter_results":{"jailbreak":{"filtered":false,"detected":true}}}""")!.AsObject();
        Assert.Null(OpenAIUtil.OpenAIStopDetails(choice));

        var refusal = JsonNode.Parse("""{"finish_reason":"stop","message":{"refusal":"nope"}}""")!.AsObject();
        var details = OpenAIUtil.OpenAIStopDetails(refusal)!;
        Assert.Equal("refusal", details.Type);
        Assert.Equal("nope", details.Explanation);
        Assert.Empty(details.Categories);
        Assert.Null(details.Category);

        var filtered = JsonNode.Parse("""{"finish_reason":"content_filter","content_filter_results":{"hate":{"filtered":true},"self_harm":{"filtered":true,"severity":"medium"}}}""")!.AsObject();
        var collected = ModelOutputUtil.CollectStopDetails("azureai", () => OpenAIUtil.OpenAIStopDetails(filtered));
        Assert.Equal("content_filter", collected!.Type);
        Assert.Equal("hate", collected.Category);
        Assert.Equal("Content filtered: hate, self_harm (medium)", collected.Explanation);
    }

    [Fact]
    public void collect_stop_details_swallows_extractor_exceptions()
    {
        ProviderLogger.Reset();
        Assert.Null(ModelOutputUtil.CollectStopDetails("azureai", () => throw new KeyNotFoundException("missing")));
        Assert.Null(ModelOutputUtil.CollectStopDetails("azureai", () => new StopDetails()));
        Assert.Contains(ProviderLogger.Warnings, w => w.StartsWith("Unexpected data shape collecting stop_details from azureai:"));
    }

    [Fact]
    public void media_filter_redacts_data_urls_only()
    {
        var request = JsonNode.Parse(
            """{"messages":[{"content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}},{"type":"image_url","image_url":{"url":"https://x/y.png"}},{"type":"input_audio","input_audio":{"data":"AAAA","format":"wav"}}]}]}""")!.AsObject();
        var call = ModelCall.Create(request, OpenAIUtil.OpenAIMediaFilter);
        var content = call.Request["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("<base64-data-removed>", content[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("https://x/y.png", content[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("<base64-data-removed>", content[2]!["input_audio"]!["data"]!.GetValue<string>());
        Assert.Null(call.Error);

        call.SetError(new JsonObject { ["error"] = new JsonObject { ["message"] = "boom" } });
        Assert.True(call.Error);
        Assert.Equal("boom", call.Response!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void model_output_completion_auto_fills_from_first_choice()
    {
        var output = new ModelOutput
        {
            Model = "m",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant(new Content[] { new ContentText("a"), new ContentText("b") }))],
        };
        Assert.Equal("a\nb", output.Completion);
        Assert.Equal(StopReason.Unknown, output.StopReason);
        Assert.False(output.Empty);
        Assert.Equal("", new ModelOutput().Completion);
        Assert.Equal("explicit", (output with { Completion = "explicit" }).Completion);

        var fromContent = ModelOutput.FromContent("m", "text", StopReason.ModelLength);
        Assert.Equal("generate", fromContent.Message.Source);
        Assert.Equal("m", fromContent.Message.Model);
        Assert.Equal("model_length", fromContent.StopReason.ToWire());
    }

    [Fact]
    public void model_usage_addition()
    {
        var sum = new ModelUsage(1, 2, 3) { ReasoningTokens = 4 } + new ModelUsage(10, 20, 30) { InputTokensCacheRead = 5 };
        Assert.Equal(new ModelUsage(11, 22, 33) { ReasoningTokens = 4, InputTokensCacheRead = 5 }, sum);
    }

    [Fact]
    public void warn_once_dedupes_by_exact_message()
    {
        ProviderLogger.Reset();
        ProviderLogger.WarnOnce("m");
        ProviderLogger.WarnOnce("m");
        ProviderLogger.WarnOnce("m2");
        Assert.Equal(["m", "m2"], ProviderLogger.Warnings);
    }

    [Fact]
    public void python_json_dumps_matches_python_defaults()
    {
        var node = JsonNode.Parse("""{"a": 1.0, "b": [1, "x"], "c": {"d": null, "e": true}, "u": "é\n"}""");
        Assert.Equal("{\"a\": 1.0, \"b\": [1, \"x\"], \"c\": {\"d\": null, \"e\": true}, \"u\": \"\\u00e9\\n\"}", PythonJson.Dumps(node));
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": {}\n}", PythonJson.Dumps(JsonNode.Parse("""{"a":[1,2],"b":{}}"""), indent: 2));
        Assert.Equal("{\"n\": 1.0, \"m\": 2, \"f\": 0.5}", PythonJson.Dumps(new JsonObject { ["n"] = 1.0, ["m"] = 2, ["f"] = 0.5 }));
    }

    [Fact]
    public void python_truthiness()
    {
        Assert.False(PythonSemantics.Truthy(null));
        Assert.False(PythonSemantics.Truthy(false));
        Assert.False(PythonSemantics.Truthy(""));
        Assert.False(PythonSemantics.Truthy(0));
        Assert.True(PythonSemantics.Truthy("false"));
        Assert.True(PythonSemantics.Truthy(JsonValue.Create(true)));
        Assert.False(PythonSemantics.Truthy(JsonValue.Create(false)));
        Assert.False(PythonSemantics.Truthy(new JsonArray()));
    }
}
