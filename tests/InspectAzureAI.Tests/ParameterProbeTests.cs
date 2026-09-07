using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>The parameter-probe catalog and classifier behind the sample's <c>params</c> command.</summary>
public class ParameterProbeTests
{
    private static ProbeObservation Observe(int? status, string? error = null, string request = "{\"messages\":[],\"max_tokens\":512}", string? response = null, string? completion = null)
    {
        var responseBody = response is null ? null : JsonNode.Parse(response)!.AsObject();
        var output = status is 200 && completion is not null ? ModelOutput.FromContent("m", completion, StopReason.Stop) : null;
        return new ProbeObservation(status, error, JsonNode.Parse(request)!.AsObject(), responseBody, output);
    }

    private static ProbeObservation Baseline(int? reasoningTokens = null, string? reasoningText = null) =>
        Observe(200, response: OpenAIResponse("no", reasoningTokens, reasoningText), completion: "no");

    private static string OpenAIResponse(string content, int? reasoningTokens = null, string? reasoningText = null, string extraChoice = "", int choices = 1)
    {
        var message = "{\"role\":\"assistant\",\"content\":" + JsonValue.Create(content)!.ToJsonString()
                      + (reasoningText is null ? "" : ",\"reasoning_content\":" + JsonValue.Create(reasoningText)!.ToJsonString()) + "}";
        var choice = "{\"index\":0,\"finish_reason\":\"stop\",\"message\":" + message + extraChoice + "}";
        var usage = "{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15"
                    + (reasoningTokens is null ? "" : ",\"completion_tokens_details\":{\"reasoning_tokens\":" + reasoningTokens + "}") + "}";
        return "{\"id\":\"1\",\"model\":\"m\",\"choices\":[" + string.Join(",", Enumerable.Repeat(choice, choices)) + "],\"usage\":" + usage + "}";
    }

    [Theory]
    [InlineData(ModelFamilyHint.OpenAI, "reasoning_effort=xhigh", true)]
    [InlineData(ModelFamilyHint.OpenAI, "top_k", false)]
    [InlineData(ModelFamilyHint.OpenAI, "thinking.enabled", false)]
    [InlineData(ModelFamilyHint.Router, "thinking.enabled", true)]
    [InlineData(ModelFamilyHint.DeepSeek, "thinking.budget", false)]
    [InlineData(ModelFamilyHint.DeepSeek, "reasoning_effort=none", true)]
    [InlineData(ModelFamilyHint.OpenAILegacy, "reasoning_effort=high", true)]
    [InlineData(ModelFamilyHint.Cohere, "top_k", true)]
    [InlineData(ModelFamilyHint.Microsoft, "reasoning_effort=low", true)]
    [InlineData(ModelFamilyHint.Mistral, "response_format.json_schema", true)]
    [InlineData(ModelFamilyHint.Anthropic, "thinking.adaptive", true)]
    [InlineData(ModelFamilyHint.Anthropic, "n", false)]
    [InlineData(ModelFamilyHint.Anthropic, "stream_options", false)]
    public void catalog_lists_the_candidates_per_family(ModelFamilyHint family, string id, bool expected) =>
        Assert.Equal(expected, ProbeCatalog.For(family).Any(p => p.Id == id));

    [Fact]
    public void catalog_probes_are_unique_and_sampling_extras_split_on_rejection()
    {
        foreach (var family in Enum.GetValues<ModelFamilyHint>())
        {
            var ids = ProbeCatalog.For(family).Select(p => p.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        var extras = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "sampling-extras");
        Assert.Equal(["seed", "frequency_penalty", "presence_penalty"], extras.SplitOnRejection!.Select(p => p.Id));
        var cohereBudget = ProbeCatalog.For(ModelFamilyHint.Cohere).Single(p => p.Id == "thinking.budget");
        Assert.Contains("token_budget", cohereBudget.Value!.ToJsonString());
        Assert.Equal("max_tokens", ProbeCatalog.AlternateTokenField(baselineUsedMaxCompletionTokens: true).Id);
        Assert.Equal("max_completion_tokens", ProbeCatalog.AlternateTokenField(baselineUsedMaxCompletionTokens: false).Id);
    }

    [Fact]
    public void http_400_is_rejected_with_the_service_message_and_other_failures_are_errors()
    {
        var spec = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "temperature");
        var rejected = ProbeClassifier.Classify(spec, Baseline(), null, Observe(400, "Unsupported value: 'temperature' does not support 0.2"));
        Assert.Equal(("rejected", true), (rejected.Verdict, rejected.Observable));
        Assert.Contains("temperature", rejected.Evidence);

        var error = ProbeClassifier.Classify(spec, Baseline(), null, Observe(503, "Service unavailable"));
        Assert.Equal("error", error.Verdict);
        Assert.Equal("error", ProbeClassifier.Classify(spec, Baseline(), null, Observe(null, "timed out after 120s")).Verdict);
    }

    [Fact]
    public void unobservable_parameters_are_accepted_without_a_visible_effect()
    {
        var spec = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "top_p");
        var verdict = ProbeClassifier.Classify(spec, Baseline(), null, Observe(200, request: "{\"messages\":[],\"max_tokens\":64,\"top_p\":0.9}", response: OpenAIResponse("ok"), completion: "ok"));
        Assert.Equal(("accepted", false), (verdict.Verdict, verdict.Observable));
    }

    [Fact]
    public void config_probes_that_change_nothing_on_the_wire_are_not_applicable()
    {
        var spec = ProbeCatalog.For(ModelFamilyHint.Mistral).Single(p => p.Id == "reasoning_tokens");
        var baseline = Baseline();
        var same = Observe(200, request: "{\"messages\":[],\"max_tokens\":1024}", response: OpenAIResponse("no"), completion: "no");   // only the limit differs: nothing derived
        Assert.Equal("n/a", ProbeClassifier.Classify(spec, baseline, null, same).Verdict);
        var empty = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "response_format.json_object");
        var cut = ProbeClassifier.Classify(empty, baseline, null, Observe(200, response: OpenAIResponse(""), completion: ""));
        Assert.Equal("ignored", cut.Verdict);
        Assert.Contains("empty completion", cut.Evidence);
    }

    [Fact]
    public void n_logprobs_and_response_format_are_judged_by_the_response()
    {
        var n = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "n");
        Assert.Equal("ignored", ProbeClassifier.Classify(n, Baseline(), null, Observe(200, response: OpenAIResponse("ok"), completion: "ok")).Verdict);
        Assert.Equal("accepted", ProbeClassifier.Classify(n, Baseline(), null, Observe(200, response: OpenAIResponse("ok", choices: 2), completion: "ok")).Verdict);

        var logprobs = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "logprobs");
        Assert.Equal("ignored", ProbeClassifier.Classify(logprobs, Baseline(), null, Observe(200, response: OpenAIResponse("ok"), completion: "ok")).Verdict);
        Assert.Equal("accepted", ProbeClassifier.Classify(logprobs, Baseline(), null, Observe(200, response: OpenAIResponse("ok", extraChoice: ",\"logprobs\":{\"content\":[]}"), completion: "ok")).Verdict);

        var jsonObject = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "response_format.json_object");
        Assert.Equal("ignored", ProbeClassifier.Classify(jsonObject, Baseline(), null, Observe(200, response: OpenAIResponse("Sure! ok"), completion: "Sure! ok")).Verdict);
        Assert.Equal("accepted", ProbeClassifier.Classify(jsonObject, Baseline(), null, Observe(200, response: OpenAIResponse("{\"answer\":\"ok\"}"), completion: "{\"answer\":\"ok\"}")).Verdict);
        var schema = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "response_format.json_schema");
        Assert.Equal("ignored", ProbeClassifier.Classify(schema, Baseline(), null, Observe(200, response: OpenAIResponse("{\"reply\":\"ok\"}"), completion: "{\"reply\":\"ok\"}")).Verdict);
        Assert.Equal("accepted", ProbeClassifier.Classify(schema, Baseline(), null, Observe(200, response: OpenAIResponse("```json\n{\"answer\":\"ok\"}\n```"), completion: "```json\n{\"answer\":\"ok\"}\n```")).Verdict);
    }

    [Fact]
    public void stop_sequences_strip_cohere_markers_before_judging()
    {
        var stop = ProbeCatalog.For(ModelFamilyHint.Cohere).Single(p => p.Id == "stop");
        const string request = "{\"messages\":[],\"max_tokens\":64,\"stop\":[\"4\"]}";
        Assert.Equal("accepted", ProbeClassifier.Classify(stop, Baseline(), null, Observe(200, request: request, response: OpenAIResponse("<|START_TEXT|>1 2 3<|END_TEXT|>"), completion: "<|START_TEXT|>1 2 3 <|END_TEXT|>")).Verdict);
        Assert.Equal("ignored", ProbeClassifier.Classify(stop, Baseline(), null, Observe(200, request: request, response: OpenAIResponse("1 2 3 4 5 6"), completion: "1 2 3 4 5 6")).Verdict);
    }

    [Fact]
    public void reasoning_switches_are_judged_against_the_baseline_signal()
    {
        var on = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "reasoning_effort=high");
        var quiet = Baseline(reasoningTokens: 0);
        var thinking = Observe(200, request: "{\"messages\":[],\"max_completion_tokens\":1024,\"reasoning_effort\":\"high\"}", response: OpenAIResponse("no", reasoningTokens: 412), completion: "no");
        var accepted = ProbeClassifier.Classify(on, quiet, null, thinking);
        Assert.Equal(("accepted", true), (accepted.Verdict, accepted.Observable));
        Assert.Contains("reasoning_tokens 412", accepted.Evidence);
        var silent = Observe(200, request: "{\"messages\":[],\"max_completion_tokens\":1024,\"reasoning_effort\":\"high\"}", response: OpenAIResponse("no", reasoningTokens: 0), completion: "no");
        Assert.Equal("ignored", ProbeClassifier.Classify(on, quiet, null, silent).Verdict);
        var alreadyReasoning = ProbeClassifier.Classify(on, Baseline(reasoningTokens: 500), null, thinking);
        Assert.Equal(("accepted", false), (alreadyReasoning.Verdict, alreadyReasoning.Observable));

        var off = ProbeCatalog.For(ModelFamilyHint.MoonshotAI).Single(p => p.Id == "thinking.disabled");
        var talkative = Baseline(reasoningText: "Let me think.");
        var gone = Observe(200, request: "{\"messages\":[],\"max_tokens\":512,\"thinking\":{\"type\":\"disabled\"}}", response: OpenAIResponse("no"), completion: "no");
        Assert.Equal(("accepted", true), (ProbeClassifier.Classify(off, talkative, null, gone).Verdict, ProbeClassifier.Classify(off, talkative, null, gone).Observable));
        var still = Observe(200, request: "{\"messages\":[],\"max_tokens\":512,\"thinking\":{\"type\":\"disabled\"}}", response: OpenAIResponse("no", reasoningText: "Let me think."), completion: "no");
        Assert.Equal("ignored", ProbeClassifier.Classify(off, talkative, null, still).Verdict);
        var neither = ProbeClassifier.Classify(off, quiet, null, gone);
        Assert.Equal(("accepted", false), (neither.Verdict, neither.Observable));
    }

    [Fact]
    public void stream_usage_is_judged_against_the_stream_baseline()
    {
        var spec = ProbeCatalog.For(ModelFamilyHint.OpenAI).Single(p => p.Id == "stream_options");
        var withUsage = Observe(200, request: "{\"messages\":[],\"stream\":true,\"stream_options\":{\"include_usage\":true}}", response: OpenAIResponse("no"), completion: "no");
        var noUsage = Observe(200, request: "{\"messages\":[],\"stream\":true}", response: "{\"id\":\"1\",\"model\":\"m\",\"choices\":[]}", completion: "no");
        var gained = ProbeClassifier.Classify(spec, Baseline(), noUsage, withUsage);
        Assert.Equal(("accepted", true), (gained.Verdict, gained.Observable));
        var already = ProbeClassifier.Classify(spec, Baseline(), withUsage, withUsage);
        Assert.Equal(("accepted", false), (already.Verdict, already.Observable));
        Assert.Equal("ignored", ProbeClassifier.Classify(spec, Baseline(), noUsage, noUsage with { RequestBody = withUsage.RequestBody }).Verdict);

        var stream = ProbeCatalog.StreamBaseline;
        Assert.Contains("no usage", ProbeClassifier.Classify(stream, Baseline(), null, noUsage).Evidence);
    }

    [Fact]
    public void reasoning_signal_helpers_read_both_usage_shapes_and_anthropic_blocks()
    {
        Assert.Equal(58, ProbeClassifier.ReasoningTokensOf(JsonNode.Parse("{\"usage\":{\"reasoning_tokens\":58}}")!.AsObject()));
        Assert.Equal(97, ProbeClassifier.ReasoningTokensOf(JsonNode.Parse("{\"usage\":{\"completion_tokens_details\":{\"reasoning_tokens\":97}}}")!.AsObject()));
        Assert.Null(ProbeClassifier.ReasoningTokensOf(JsonNode.Parse("{\"usage\":{\"output_tokens\":5}}")!.AsObject()));
        Assert.True(ProbeClassifier.ReasoningTextOf(null, JsonNode.Parse("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}]}")!.AsObject()));
        Assert.False(ProbeClassifier.ReasoningTextOf(null, JsonNode.Parse("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\"}]}")!.AsObject()));
        var output = new ModelOutput
        {
            Model = "m",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant(MessageContent.FromItems([new ContentReasoning("why"), new ContentText("no")])), StopReason.Stop, null)],
        };
        Assert.True(ProbeClassifier.ReasoningTextOf(output, null));
    }
}
