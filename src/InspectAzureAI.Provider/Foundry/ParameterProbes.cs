using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Foundry;

/// <summary>How a probe puts its parameter on the wire.</summary>
public enum ProbeVia
{
    /// <summary>Through <see cref="GenerateConfig"/> (the provider maps it, so the mapping itself is verified).</summary>
    Config,

    /// <summary>As a model arg, i.e. a raw pass-through body field.</summary>
    ModelArg,
}

/// <summary>What observable effect a probe is expected to have when the parameter is honoured.</summary>
public enum ProbeExpect
{
    /// <summary>Nothing observable: an HTTP 200 is "accepted, no visible effect".</summary>
    None,

    /// <summary>The completion stops before the stop sequence ("4" in a 1..6 count).</summary>
    Stop,

    /// <summary>Two choices come back (<c>n = 2</c>).</summary>
    TwoChoices,

    /// <summary>The first choice carries <c>logprobs</c>.</summary>
    Logprobs,

    /// <summary>The completion is a JSON object.</summary>
    JsonObject,

    /// <summary>The completion is a JSON object with an <c>answer</c> key (strict schema).</summary>
    JsonSchemaAnswer,

    /// <summary>A streamed call: accepted when it completes; the evidence says whether usage was reported.</summary>
    Stream,

    /// <summary>Usage appears on a stream that had none without the parameter.</summary>
    StreamUsage,

    /// <summary>A reasoning signal appears (reasoning tokens or text).</summary>
    ReasoningOn,

    /// <summary>The reasoning signal the baseline showed disappears.</summary>
    ReasoningOff,
}

/// <summary>One candidate request parameter to try against a deployment, sent alone on top of the baseline.</summary>
public sealed record ProbeSpec(
    string Id,
    string Param,
    JsonNode? Value,
    ProbeVia Via,
    string Prompt,
    ProbeExpect Expect = ProbeExpect.None,
    bool Stream = false,
    bool WithTools = false,
    int? MaxTokens = 64)
{
    /// <summary>For <see cref="ProbeVia.Config"/>: how the parameter is applied to the config.</summary>
    public Func<GenerateConfig, GenerateConfig>? Configure { get; init; }

    /// <summary>For <see cref="ProbeVia.ModelArg"/>: the model args to add (may be several, e.g. <c>logprobs</c> + <c>top_logprobs</c>).</summary>
    public IReadOnlyDictionary<string, object?>? ModelArgs { get; init; }

    /// <summary>Probes to run one by one when this (grouped) probe is rejected, so the rejection is attributable.</summary>
    public IReadOnlyList<ProbeSpec>? SplitOnRejection { get; init; }
}

/// <summary>What one probe call produced, as far as the classifier needs.</summary>
public sealed record ProbeObservation(int? Status, string? Error, JsonObject? RequestBody, JsonObject? ResponseBody, ModelOutput? Output);

/// <summary>The classification of one probe: <c>accepted</c>, <c>ignored</c>, <c>rejected</c>, <c>error</c> or <c>n/a</c>.</summary>
public sealed record ProbeVerdict(string Verdict, bool Observable, string Evidence);

/// <summary>The candidate parameters per model family (the sample's <c>params</c> command runs them).</summary>
public static class ProbeCatalog
{
    public const string PromptOk = "Reply with exactly: ok";

    public const string PromptCount = "Count from 1 to 6 separated by spaces.";

    public const string PromptJson = "Return a JSON object with one key \"answer\" whose value is \"ok\".";

    /// <summary>10403 = 101 × 103: enough to make a model think without needing a long answer.</summary>
    public const string PromptReasoning = "Is 10403 a prime number? Answer yes or no.";

    public const string PromptTool = "What is the weather in Oslo right now? Use the get_weather tool.";

    /// <summary>The reference call every deployment gets first: the reasoning prompt, no parameters beyond <c>max_tokens</c>.</summary>
    public static readonly ProbeSpec Baseline = new("baseline", "(none)", null, ProbeVia.Config, PromptReasoning, ProbeExpect.None, MaxTokens: 512);

    /// <summary>The same call streamed: the reference for <c>stream_options</c>.</summary>
    public static readonly ProbeSpec StreamBaseline = new("stream", "stream", JsonValue.Create(true), ProbeVia.Config, PromptReasoning, ProbeExpect.Stream, Stream: true, MaxTokens: 512);

    /// <summary>The candidates for a family, in display order (the baseline and stream baseline are not included).</summary>
    public static IReadOnlyList<ProbeSpec> For(ModelFamilyHint family)
    {
        var probes = new List<ProbeSpec>
        {
            Config("temperature", "temperature", 0.2, c => c with { Temperature = 0.2 }),
            Config("top_p", "top_p", 0.9, c => c with { TopP = 0.9 }),
        };

        if (family == ModelFamilyHint.Anthropic)
        {
            probes.Add(Config("stop", "stop_sequences", JsonNode.Parse("""["4"]"""), c => c with { StopSeqs = ["4"] }, PromptCount, ProbeExpect.Stop));
            probes.Add(ModelArg("top_k", "top_k", 40));
            probes.Add(ModelArg("metadata", "metadata", JsonNode.Parse("""{"user_id":"probe"}""")));
            probes.Add(ModelArg("thinking.adaptive", "thinking", JsonNode.Parse("""{"type":"adaptive"}"""), PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 1024));
            probes.Add(ModelArg("thinking.enabled", "thinking", JsonNode.Parse("""{"type":"enabled","budget_tokens":1024}"""), PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 2048));
            probes.Add(ModelArg("thinking.disabled", "thinking", JsonNode.Parse("""{"type":"disabled"}"""), PromptReasoning, ProbeExpect.ReasoningOff, maxTokens: 512));
            probes.Add(ModelArg("output_config.effort=low", "output_config", JsonNode.Parse("""{"effort":"low"}"""), PromptReasoning, ProbeExpect.None, maxTokens: 512));
            probes.Add(Config("reasoning_effort=high", "reasoning_effort", "high", c => c with { ReasoningEffort = "high" }, PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 1024));
            probes.Add(Config("reasoning_tokens", "reasoning_tokens", 1024, c => c with { ReasoningTokens = 1024 }, PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 2048));
            return probes;
        }

        var extras = new ProbeSpec("sampling-extras", "seed + frequency_penalty + presence_penalty", JsonNode.Parse("""{"seed":42,"frequency_penalty":0.5,"presence_penalty":0.5}"""), ProbeVia.Config, PromptOk, MaxTokens: 512)
        {
            Configure = c => c with { Seed = 42, FrequencyPenalty = 0.5, PresencePenalty = 0.5 },
            SplitOnRejection =
            [
                Config("seed", "seed", 42, c => c with { Seed = 42 }),
                Config("frequency_penalty", "frequency_penalty", 0.5, c => c with { FrequencyPenalty = 0.5 }),
                Config("presence_penalty", "presence_penalty", 0.5, c => c with { PresencePenalty = 0.5 }),
            ],
        };
        probes.Add(extras);
        probes.Add(Config("stop", "stop", JsonNode.Parse("""["4"]"""), c => c with { StopSeqs = ["4"] }, PromptCount, ProbeExpect.Stop));
        probes.Add(ModelArg("n", "n", 2, expect: ProbeExpect.TwoChoices));
        probes.Add(new ProbeSpec("logprobs", "logprobs + top_logprobs", JsonNode.Parse("""{"logprobs":true,"top_logprobs":1}"""), ProbeVia.ModelArg, PromptOk, ProbeExpect.Logprobs, MaxTokens: 512)
        {
            ModelArgs = new Dictionary<string, object?> { ["logprobs"] = true, ["top_logprobs"] = 1 },
        });
        if (family is not (ModelFamilyHint.OpenAI or ModelFamilyHint.Router))
        {
            probes.Add(ModelArg("top_k", "top_k", 40));
        }

        probes.Add(new ProbeSpec("parallel_tool_calls", "parallel_tool_calls", JsonValue.Create(false), ProbeVia.ModelArg, PromptTool, ProbeExpect.None, WithTools: true, MaxTokens: 256)
        {
            ModelArgs = new Dictionary<string, object?> { ["parallel_tool_calls"] = false },
        });
        probes.Add(ModelArg("response_format.json_object", "response_format", JsonNode.Parse("""{"type":"json_object"}"""), PromptJson, ProbeExpect.JsonObject));
        probes.Add(ModelArg("response_format.json_schema", "response_format",
            JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"answer","strict":true,"schema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}}}"""),
            PromptJson, ProbeExpect.JsonSchemaAnswer));
        probes.Add(new ProbeSpec("stream_options", "stream_options", JsonNode.Parse("""{"include_usage":true}"""), ProbeVia.ModelArg, PromptReasoning, ProbeExpect.StreamUsage, Stream: true, MaxTokens: 512)
        {
            ModelArgs = new Dictionary<string, object?> { ["stream_options"] = JsonNode.Parse("""{"include_usage":true}""") },
        });

        switch (family)
        {
            case ModelFamilyHint.OpenAI or ModelFamilyHint.Router:
                probes.Add(Effort("none", ProbeExpect.ReasoningOff));
                probes.Add(Effort("low", ProbeExpect.ReasoningOn));
                probes.Add(Effort("high", ProbeExpect.ReasoningOn));
                probes.Add(Effort("xhigh", ProbeExpect.ReasoningOn));
                probes.Add(ModelArg("verbosity=low", "verbosity", "low", PromptReasoning, ProbeExpect.None, maxTokens: 512));
                if (family == ModelFamilyHint.Router)
                {
                    probes.Add(Thinking("enabled", ProbeExpect.ReasoningOn));
                    probes.Add(Thinking("disabled", ProbeExpect.ReasoningOff));
                }

                break;
            case ModelFamilyHint.XAI or ModelFamilyHint.Microsoft or ModelFamilyHint.DeepSeek:
                probes.Add(Effort("none", ProbeExpect.ReasoningOff));
                probes.Add(Effort("low", ProbeExpect.ReasoningOn));
                probes.Add(Effort("high", ProbeExpect.ReasoningOn));
                probes.Add(Thinking("enabled", ProbeExpect.ReasoningOn));
                probes.Add(Thinking("disabled", ProbeExpect.ReasoningOff));
                break;
            default:
                // MoonshotAI, Cohere, Mistral, OpenAILegacy, Unknown: the config maps effort to `thinking` (or to
                // nothing), so the raw reasoning_effort field is tried as a model arg, and the thinking object in
                // its three forms.
                probes.Add(ModelArg("reasoning_effort=high", "reasoning_effort", "high", PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 512));
                probes.Add(Thinking("enabled", ProbeExpect.ReasoningOn));
                probes.Add(Thinking("disabled", ProbeExpect.ReasoningOff));
                probes.Add(ModelArg("thinking.budget", "thinking",
                    JsonNode.Parse(family == ModelFamilyHint.Cohere ? """{"type":"enabled","token_budget":512}""" : """{"type":"enabled","budget_tokens":512}"""),
                    PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 512));
                break;
        }

        probes.Add(Config("reasoning_tokens", "reasoning_tokens", 512, c => c with { ReasoningTokens = 512 }, PromptReasoning, ProbeExpect.ReasoningOn, maxTokens: 1024));
        return probes;

        static JsonNode? Node(object? value) => value is JsonNode node ? node : value is null ? null : JsonValue.Create(value);

        static ProbeSpec Config(string id, string param, object? value, Func<GenerateConfig, GenerateConfig> configure, string prompt = PromptOk, ProbeExpect expect = ProbeExpect.None, int maxTokens = 512) =>
            new(id, param, Node(value), ProbeVia.Config, prompt, expect, MaxTokens: maxTokens) { Configure = configure };

        static ProbeSpec ModelArg(string id, string param, object? value, string prompt = PromptOk, ProbeExpect expect = ProbeExpect.None, int maxTokens = 512) =>
            new(id, param, Node(value), ProbeVia.ModelArg, prompt, expect, MaxTokens: maxTokens)
            {
                ModelArgs = new Dictionary<string, object?> { [param] = value },
            };

        static ProbeSpec Effort(string level, ProbeExpect expect) =>
            Config($"reasoning_effort={level}", "reasoning_effort", level, c => c with { ReasoningEffort = level }, PromptReasoning, expect, maxTokens: 1024);

        static ProbeSpec Thinking(string type, ProbeExpect expect) =>
            ModelArg($"thinking.{type}", "thinking", JsonNode.Parse($$"""{"type":"{{type}}"}""")!, PromptReasoning, expect, maxTokens: 512);
    }

    /// <summary>
    /// The token-limit field the baseline did not use (<c>max_tokens</c> after a <c>max_completion_tokens</c>
    /// baseline and vice versa), sent as a raw body field with the config's limit removed.
    /// </summary>
    public static ProbeSpec AlternateTokenField(bool baselineUsedMaxCompletionTokens)
    {
        var field = baselineUsedMaxCompletionTokens ? "max_tokens" : "max_completion_tokens";
        return new ProbeSpec(field, field, JsonValue.Create(64), ProbeVia.ModelArg, PromptOk, ProbeExpect.None, MaxTokens: null)
        {
            ModelArgs = new Dictionary<string, object?> { [field] = 64 },
            Configure = c => c with { MaxTokens = null },
        };
    }
}

/// <summary>Turns a probe's observation into a verdict, using the baselines as the reference.</summary>
public static class ProbeClassifier
{
    public static ProbeVerdict Classify(ProbeSpec spec, ProbeObservation baseline, ProbeObservation? streamBaseline, ProbeObservation probe)
    {
        if (probe.Status is 400 or 422)
        {
            return new("rejected", true, probe.Error ?? $"HTTP {probe.Status}");
        }

        if (probe.Output is null || probe.Status is null or >= 400)
        {
            return new("error", false, probe.Error ?? (probe.Status is null ? "no response" : $"HTTP {probe.Status}"));
        }

        if (spec.Via == ProbeVia.Config && spec.Configure is not null && spec.Expect != ProbeExpect.Stream
            && !RequestChanged(baseline.RequestBody, probe.RequestBody))
        {
            return new("n/a", false, "the provider derives no request field for this family; nothing was sent");
        }

        var completion = AzureAIModelApi.StripCohereTextMarkers(probe.Output.Completion).Trim();
        if (completion.Length == 0 && spec.Expect is ProbeExpect.Stop or ProbeExpect.JsonObject or ProbeExpect.JsonSchemaAnswer)
        {
            return new("ignored", true, $"empty completion (stop_reason {probe.Output.StopReason.ToWire()}); nothing to judge");
        }

        var probeTokens = ReasoningTokensOf(probe.ResponseBody);
        var probeText = ReasoningTextOf(probe.Output, probe.ResponseBody);
        var baseTokens = ReasoningTokensOf(baseline.ResponseBody);
        var baseText = ReasoningTextOf(baseline.Output, baseline.ResponseBody);
        var probeSignal = probeTokens > 0 || probeText;
        var baseSignal = baseTokens > 0 || baseText;
        string Signal(int? tokens, bool text) => $"reasoning_tokens {(tokens?.ToString() ?? "n/a")}, text {(text ? "visible" : "absent")}";

        switch (spec.Expect)
        {
            case ProbeExpect.Stop:
                return completion.Contains('4')
                    ? new("ignored", true, $"completion still contains \"4\": {Short(completion)}")
                    : new("accepted", true, $"stopped before \"4\": {Short(completion)}");
            case ProbeExpect.TwoChoices:
                var choices = (probe.ResponseBody?["choices"] as JsonArray)?.Count ?? 0;
                return choices == 2 ? new("accepted", true, "2 choices returned") : new("ignored", true, $"{choices} choice(s) returned");
            case ProbeExpect.Logprobs:
                var logprobs = (probe.ResponseBody?["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["logprobs"];
                return logprobs is not null ? new("accepted", true, "choices[0].logprobs present") : new("ignored", true, "no logprobs in the response");
            case ProbeExpect.JsonObject:
            case ProbeExpect.JsonSchemaAnswer:
                var parsed = TryParseObject(completion);
                if (parsed is null)
                {
                    return new("ignored", true, $"completion is not a JSON object: {Short(completion)}");
                }

                return spec.Expect == ProbeExpect.JsonSchemaAnswer && !parsed.ContainsKey("answer")
                    ? new("ignored", true, $"JSON object without an \"answer\" key: {Short(completion)}")
                    : new("accepted", true, $"JSON object returned: {Short(completion)}");
            case ProbeExpect.Stream:
                return new("accepted", true, probe.ResponseBody?["usage"] is JsonObject ? "streamed; usage reported" : "streamed; no usage in the stream");
            case ProbeExpect.StreamUsage:
                var usageNow = probe.ResponseBody?["usage"] is JsonObject;
                var usageBefore = streamBaseline?.ResponseBody?["usage"] is JsonObject;
                if (!usageNow)
                {
                    return new("ignored", true, "still no usage in the stream");
                }

                return usageBefore
                    ? new("accepted", false, "usage was already reported without it")
                    : new("accepted", true, "usage now reported in the stream");
            case ProbeExpect.ReasoningOn:
                if (!probeSignal)
                {
                    return new("ignored", true, $"no reasoning signal (baseline: {Signal(baseTokens, baseText)})");
                }

                return baseSignal
                    ? new("accepted", false, $"reasoning present, but the baseline reasons without it: {Signal(probeTokens, probeText)} (baseline: {Signal(baseTokens, baseText)})")
                    : new("accepted", true, $"{Signal(probeTokens, probeText)} (baseline: {Signal(baseTokens, baseText)})");
            case ProbeExpect.ReasoningOff:
                if (probeSignal)
                {
                    return new("ignored", true, $"reasoning still present: {Signal(probeTokens, probeText)}");
                }

                return baseSignal
                    ? new("accepted", true, $"reasoning gone (baseline: {Signal(baseTokens, baseText)})")
                    : new("accepted", false, "no reasoning signal, but the baseline had none either");
            default:
                return new("accepted", false, "HTTP 200; this parameter has no effect the probe can observe");
        }
    }

    /// <summary>Whether the probe put anything new on the wire, ignoring the fields every call carries anyway.</summary>
    private static bool RequestChanged(JsonObject? baseline, JsonObject? probe)
    {
        static JsonObject Strip(JsonObject? body)
        {
            var copy = body?.DeepClone().AsObject() ?? new JsonObject();
            foreach (var key in new[] { "max_tokens", "max_completion_tokens", "stream", "messages" })
            {
                copy.Remove(key);
            }

            return copy;
        }

        return !JsonNode.DeepEquals(Strip(baseline), Strip(probe));
    }

    /// <summary>
    /// The reasoning token count a response reports: <c>usage.completion_tokens_details.reasoning_tokens</c> or the
    /// top-level <c>usage.reasoning_tokens</c> (model-inference route); null when absent (Anthropic reports none).
    /// </summary>
    public static int? ReasoningTokensOf(JsonObject? responseBody)
    {
        if (responseBody?["usage"] is not JsonObject usage)
        {
            return null;
        }

        return IntOf(usage["completion_tokens_details"]?["reasoning_tokens"]) ?? IntOf(usage["reasoning_tokens"]);
    }

    /// <summary>Whether reasoning text came back: <see cref="ContentReasoning"/> items, or raw <c>reasoning_content</c> / Anthropic <c>thinking</c> blocks.</summary>
    public static bool ReasoningTextOf(ModelOutput? output, JsonObject? responseBody)
    {
        if (output?.Message.ContentList.OfType<ContentReasoning>().Any(r => r.Reasoning.Length > 0) == true)
        {
            return true;
        }

        var message = (responseBody?["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["message"] as JsonObject;
        if (message?["reasoning_content"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return true;
        }

        return (responseBody?["content"] as JsonArray)?.OfType<JsonObject>()
            .Any(b => b["type"]?.ToString() == "thinking" && (b["thinking"]?.ToString().Length ?? 0) > 0) == true;
    }

    private static int? IntOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static JsonObject? TryParseObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[start..(end + 1)];
            }
        }

        try
        {
            return JsonNode.Parse(trimmed) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Short(string text) => text.Length > 80 ? text[..80] + "…" : text;
}
