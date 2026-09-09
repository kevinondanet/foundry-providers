using Azure;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port-level behaviour of <c>Model.generate</c> (<c>model/_model.py</c>, <c>_retry.py</c>): defaults, retries, limits, events and collapsing.</summary>
public class ModelTests
{
    private sealed class CountingDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }

    private static RequestFailedException Http(int status, IReadOnlyDictionary<string, string>? headers = null) =>
        new(CannedResponse.Error(status, $"http {status}", headers));

    private static (Model Model, ScriptedModelApi Api, CountingDelay Delay) Build(int maxRetries = 5, params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns);
        var delay = new CountingDelay();
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: maxRetries, Delay: delay.Delay));
        return (model, api, delay);
    }

    [Theory]
    [InlineData(null, null, 0, 1)]
    [InlineData(0, null, 5, 1)]
    [InlineData(2, 0, 5, 1)]
    [InlineData(0, 2, 0, 3)]
    public async Task effective_config_controls_retry_count(int? modelRetries, int? callRetries, int defaultRetries, int expectedAttempts)
    {
        var api = new ScriptedModelApi(Enumerable.Range(0, 6).Select(_ => ScriptedTurn.Throw(Http(503))));
        var delay = new CountingDelay();
        var model = new Model(api, new GenerateConfig { MaxRetries = modelRetries }, new ModelRetryOptions(MaxRetries: defaultRetries, Delay: delay.Delay));

        await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi", config: new GenerateConfig { MaxRetries = callRetries }));

        Assert.Equal(expectedAttempts, api.Requests.Count);
        Assert.Equal(expectedAttempts - 1, delay.Delays.Count);
    }

    [Theory]
    [InlineData(null, null, 0, 1)]
    [InlineData(0, null, 60, 1)]
    [InlineData(60, 0, 60, 1)]
    [InlineData(0, 60, 0, 2)]
    public async Task effective_config_controls_retry_timeout(int? modelTimeout, int? callTimeout, int defaultTimeout, int expectedAttempts)
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(503)), ScriptedTurn.Throw(Http(503)));
        var delay = new CountingDelay();
        var model = new Model(api, new GenerateConfig { Timeout = modelTimeout },
            new ModelRetryOptions(MaxRetries: 1, Timeout: TimeSpan.FromSeconds(defaultTimeout), Delay: delay.Delay));

        await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi", config: new GenerateConfig { Timeout = callTimeout }));

        Assert.Equal(expectedAttempts, api.Requests.Count);
        Assert.Equal(expectedAttempts - 1, delay.Delays.Count);
    }

    [Fact]
    public async Task cancelled_retry_wait_records_only_elapsed_waiting_time()
    {
        using var context = new SampleContextScope();
        var working = new WorkingLimit(TimeSpan.FromSeconds(100));
        using var limit = working.Enter();
        using var cancellation = new CancellationTokenSource();
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(503))) { ShouldRetry = _ => RetryDecision.Transient(60) };
        var model = new Model(api, retry: new ModelRetryOptions(Delay: (_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.GenerateAsync("hi", cancellationToken: cancellation.Token));

        Assert.InRange(working.WaitingTime.TotalSeconds, 0, 1);
        Assert.Equal(working.WaitingTime, context.Context.Limits.WaitingTime);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task max_tokens_defaults_from_the_api_unless_configured()
    {
        var (model, api, _) = Build(5, ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"));
        var configured = new Model(api, new GenerateConfig { MaxTokens = 7 });

        await model.GenerateAsync("hi");
        await model.GenerateAsync("hi", config: new GenerateConfig { MaxTokens = 5 });
        await configured.GenerateAsync("hi");

        Assert.Equal(2048, api.Requests[0].Config.MaxTokens);
        Assert.Equal(5, api.Requests[1].Config.MaxTokens);
        Assert.Equal(7, api.Requests[2].Config.MaxTokens);
    }

    [Fact]
    public async Task call_config_is_merged_over_the_model_config()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"));
        var model = new Model(api, new GenerateConfig { Temperature = 0.1, TopP = 0.5 });

        await model.GenerateAsync("hi", config: new GenerateConfig { Temperature = 0.9 });

        Assert.Equal(0.9, api.Requests[0].Config.Temperature);
        Assert.Equal(0.5, api.Requests[0].Config.TopP);
    }

    [Fact]
    public async Task retries_a_retryable_exception_then_succeeds()
    {
        var (model, api, delay) = Build(5, ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("ok"));

        var output = await model.GenerateAsync("hi");

        Assert.Equal("ok", output.Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Single(delay.Delays);
        Assert.True(delay.Delays[0] <= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task retry_after_is_honoured_for_the_delay()
    {
        var headers = new Dictionary<string, string> { ["Retry-After"] = "7" };
        var (model, _, delay) = Build(5, ScriptedTurn.Throw(Http(429, headers)), ScriptedTurn.Text("ok"));

        await model.GenerateAsync("hi");

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delay.Delays));
    }

    [Fact]
    public async Task backoff_grows_exponentially_with_full_jitter_up_to_the_cap()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(Http(500)), ScriptedTurn.Throw(Http(500)), ScriptedTurn.Throw(Http(500)), ScriptedTurn.Text("ok"));
        var delay = new CountingDelay();
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 5, InitialBackoffSeconds: 2, MaxBackoffSeconds: 5, Delay: delay.Delay));

        await model.GenerateAsync("hi");

        Assert.Equal(3, delay.Delays.Count);
        Assert.True(delay.Delays[0] <= TimeSpan.FromSeconds(2));
        Assert.True(delay.Delays[1] <= TimeSpan.FromSeconds(4));
        Assert.True(delay.Delays[2] <= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task non_retryable_exception_propagates_without_delay()
    {
        var (model, api, delay) = Build(5, ScriptedTurn.Throw(new InvalidOperationException("nope")), ScriptedTurn.Text("never"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync("hi"));

        Assert.Equal("nope", ex.Message);
        Assert.Single(api.Requests);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task a_400_is_not_retried()
    {
        var (model, api, delay) = Build(5, ScriptedTurn.Throw(Http(400)), ScriptedTurn.Text("never"));

        await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi"));

        Assert.Single(api.Requests);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task max_retries_bounds_the_attempts()
    {
        var (model, api, delay) = Build(2, ScriptedTurn.Throw(Http(503)), ScriptedTurn.Throw(Http(503)), ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("never"));

        await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi"));

        Assert.Equal(3, api.Requests.Count);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task scripted_should_retry_override_drives_the_decision()
    {
        var api = new ScriptedModelApi([ScriptedTurn.Throw(new InvalidOperationException("flaky")), ScriptedTurn.Text("ok")])
        {
            ShouldRetry = ex => ex is InvalidOperationException ? RetryDecision.Transient(0.25) : RetryDecision.No(),
        };
        var delay = new CountingDelay();
        var model = new Model(api, retry: new ModelRetryOptions(Delay: delay.Delay));

        var output = await model.GenerateAsync("hi");

        Assert.Equal("ok", output.Completion);
        Assert.Equal(TimeSpan.FromSeconds(0.25), Assert.Single(delay.Delays));
    }

    [Fact]
    public async Task a_terminal_error_result_becomes_a_model_generate_exception()
    {
        var (model, api, delay) = Build(5, ScriptedTurn.Error(Http(400)), ScriptedTurn.Text("never"));
        using var scope = new SampleContextScope();

        var ex = await Assert.ThrowsAsync<ModelGenerateException>(() => model.GenerateAsync("hi"));

        Assert.IsType<RequestFailedException>(ex.InnerException);
        Assert.NotNull(ex.Call);
        Assert.True(ex.Call.Error);
        Assert.Single(api.Requests);
        Assert.Empty(delay.Delays);
        var recorded = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Equal(ex.Message, recorded.Error);
    }

    [Fact]
    public async Task a_model_event_is_recorded_per_attempt()
    {
        var (model, _, _) = Build(5, ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("ok", new ModelUsage(3, 4, 7)));
        using var scope = new SampleContextScope();

        await model.GenerateAsync("hi", tools: [SandboxTools.Bash().ToInfo()]);

        var events = scope.Transcript.Events.OfType<ModelEvent>().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("model", events[0].Event);
        Assert.StartsWith("http 503", events[0].Error);
        Assert.Equal(0, events[0].Retries);
        Assert.Equal("", events[0].Output.Completion);
        Assert.Null(events[1].Error);
        Assert.Equal(1, events[1].Retries);
        Assert.Equal("ok", events[1].Output.Completion);
        Assert.NotNull(events[1].Call);
        Assert.Equal("bash", Assert.Single(events[1].Tools).Name);
        Assert.Equal("hi", Assert.Single(events[1].Input).Text);
        Assert.Equal(new ModelUsage(3, 4, 7), scope.Context.Limits.TotalUsage);
        Assert.Equal(new ModelUsage(3, 4, 7), scope.Context.Limits.UsageByModel["scripted"]);
    }

    [Fact]
    public async Task events_reach_the_instance_sink_and_the_ambient_sink()
    {
        var (model, _, _) = Build(5, ScriptedTurn.Text("a"), ScriptedTurn.Text("b"));
        var instanceSink = new CollectingSink();
        var ambientSink = new CollectingSink();

        using (ModelEventSinks.Install(ambientSink))
        {
            await model.WithEventSink(instanceSink).GenerateAsync("one");
        }

        await model.GenerateAsync("two");

        Assert.Equal("a", Assert.Single(instanceSink.Events).Output.Completion);
        Assert.Equal("a", Assert.Single(ambientSink.Events).Output.Completion);
        Assert.Null(ModelEventSinks.Current);
    }

    [Fact]
    public async Task output_message_source_defaults_to_generate()
    {
        var api = new ScriptedModelApi(ScriptedTurn.From((_, _) => new ModelOutput
        {
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("raw"))],
        }));
        var model = new Model(api);

        var output = await model.GenerateAsync("hi");

        Assert.Equal("generate", output.Message.Source);
        Assert.Equal("raw", output.Completion);
    }

    [Fact]
    public async Task message_limit_is_checked_before_the_call()
    {
        var (model, api, _) = Build(5, ScriptedTurn.Text("never"));
        using var scope = new SampleContextScope(limits: new Limits { MessageLimit = 2 });

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => model.GenerateAsync([new ChatMessageUser("a"), new ChatMessageAssistant("b")]));

        Assert.Equal("message", ex.Type);
        Assert.Equal("Message limit reached. count: 2; limit: 2", ex.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task token_limit_is_checked_after_the_call()
    {
        var (model, api, _) = Build(5, ScriptedTurn.Text("ok", new ModelUsage(10, 5, 15)));
        using var scope = new SampleContextScope(limits: new Limits { TokenLimit = 10 });

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => model.GenerateAsync("hi"));

        Assert.Equal("token", ex.Type);
        Assert.Equal("10", ex.LimitStr);
        Assert.Single(api.Requests);
        Assert.Equal(15, scope.Context.Limits.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task system_message_config_is_prepended_and_tool_choice_is_normalised()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"));
        var model = new Model(api, new GenerateConfig { SystemMessage = "be brief" });
        var tools = new[] { SandboxTools.Bash().ToInfo(), SandboxTools.Python().ToInfo() };

        await model.GenerateAsync("hi", tools);
        await model.GenerateAsync("hi", tools, new ToolFunction("python"));
        await model.GenerateAsync("hi", tools, ToolChoice.None);

        var first = api.Requests[0];
        Assert.Equal(["system", "user"], first.Input.Select(m => m.Role));
        Assert.Equal("be brief", first.Input[0].Text);
        Assert.Same(ToolChoice.Auto, first.ToolChoice);
        Assert.Equal(2, first.Tools.Count);
        Assert.Equal("python", Assert.Single(api.Requests[1].Tools).Name);
        Assert.Empty(api.Requests[2].Tools);
        Assert.Same(ToolChoice.None, api.Requests[2].ToolChoice);
    }

    [Fact]
    public void collapse_merges_consecutive_user_messages_only()
    {
        var a = new ChatMessageUser("a") { ToolCallId = ["t1"] };
        var b = new ChatMessageUser("b");
        var c = new ChatMessageUser(new Content[] { new ContentText("c"), new ContentImage("data:image/png;base64,AA==") });

        var collapsed = Model.CollapseUserMessages([new ChatMessageSystem("s"), a, b, new ChatMessageAssistant("x"), b, c]);

        Assert.Equal(["system", "user", "assistant", "user"], collapsed.Select(m => m.Role));
        var merged = Assert.IsType<ChatMessageUser>(collapsed[1]);
        Assert.Equal("a\nb", merged.Text);
        Assert.Equal(["t1"], merged.ToolCallId);
        Assert.Equal(new[] { a.Id, b.Id }, merged.Metadata!["combined_from"]);
        var items = Assert.IsType<ChatMessageUser>(collapsed[3]);
        Assert.False(items.Content.IsString);
        Assert.Equal(3, items.ContentList.Count);
    }

    [Fact]
    public async Task azureai_api_collapses_consecutive_user_messages_on_the_wire()
    {
        var transport = new CannedTransport
        {
            Responder = _ => CannedResponse.Json(200,
                "{\"id\":\"cmpl-1\",\"created\":123,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":7,\"total_tokens\":10}}"),
        };
        var api = new AzureAIModelApi("test-model", "https://example.com/models", settings: new AzureAIClientSettings
        {
            Transport = transport,
            TokenCredential = new FakeTokenCredential("token"),
            ConfigureClientOptions = o => o.Retry.MaxRetries = 0,
        });
        var model = new Model(api);

        var output = await model.GenerateAsync([new ChatMessageUser("first"), new ChatMessageUser("second")]);

        Assert.Equal("ok", output.Completion);
        var messages = transport.LastRequest!.BodyJson["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("first\nsecond", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task scripted_api_does_not_collapse_unless_asked()
    {
        var plain = new ScriptedModelApi(ScriptedTurn.Text("a"));
        var collapsing = new ScriptedModelApi([ScriptedTurn.Text("a")]) { CollapseUserMessages = true };

        await new Model(plain).GenerateAsync([new ChatMessageUser("x"), new ChatMessageUser("y")]);
        await new Model(collapsing).GenerateAsync([new ChatMessageUser("x"), new ChatMessageUser("y")]);

        Assert.Equal(2, plain.Requests[0].Input.Count);
        Assert.Single(collapsing.Requests[0].Input);
    }

    [Fact]
    public async Task stream_handler_is_told_about_retries()
    {
        var (model, _, _) = Build(5, ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("ok"));
        var events = new List<StreamEvent>();

        await model.GenerateAsync("hi", onStream: e => { events.Add(e); return Task.CompletedTask; });

        Assert.Equal(1, Assert.IsType<StreamRetryEvent>(events[0]).Attempt);
        Assert.Equal("ok", Assert.IsType<StreamTextEvent>(events[1]).Text);
    }

    [Fact]
    public void foundry_models_pick_the_route_from_the_name_or_the_route_argument()
    {
        using var env = new EnvVarScope().Set(AzureAIModelApi.AzureAIBaseUrlVar, "https://example.com/models").Set(FoundryModels.ModelVar, null);
        var settings = new AzureAIClientSettings { TokenCredential = new FakeTokenCredential("token") };

        using var claude = Assert.IsType<AnthropicFoundryModelApi>(FoundryModels.CreateApi("Claude-sonnet-4-6", settings: settings));
        var gpt = Assert.IsType<AzureAIModelApi>(FoundryModels.CreateApi("gpt-4o", settings: settings));
        using var routed = Assert.IsType<AnthropicFoundryModelApi>(FoundryModels.CreateApi("my-deployment", route: "anthropic", settings: settings));
        var defaulted = Assert.IsType<AzureAIModelApi>(FoundryModels.CreateApi(null, route: "models", settings: settings));
        var model = FoundryModels.Create("gpt-4o", new GenerateConfig { MaxTokens = 3 }, settings: settings);

        Assert.Equal("gpt-4o", gpt.ModelName);
        Assert.Equal(FoundryModels.DefaultModel, defaulted.ModelName);
        Assert.Equal(3, model.Config.MaxTokens);
        using var responses = Assert.IsType<InspectAzureAI.Provider.OpenAI.OpenAIResponsesModelApi>(FoundryModels.CreateApi("gpt-5.6-sol", settings: settings));
        using var pro = Assert.IsType<InspectAzureAI.Provider.OpenAI.OpenAIResponsesModelApi>(FoundryModels.CreateApi("gpt-5.4-pro", settings: settings));
        var overridden = Assert.IsType<AzureAIModelApi>(FoundryModels.CreateApi("gpt-5.6-sol", route: "models", settings: settings));
        var mini = Assert.IsType<AzureAIModelApi>(FoundryModels.CreateApi("gpt-5.4-mini", settings: settings));
        using var explicitResponses = Assert.IsType<InspectAzureAI.Provider.OpenAI.OpenAIResponsesModelApi>(FoundryModels.CreateApi("gpt-4o", route: "Responses", settings: settings));
        Assert.Equal("https://example.com/openai/v1", responses.BaseUrl);
        Assert.Equal("gpt-5.6-sol", overridden.ModelName);
        Assert.Equal("gpt-5.4-mini", mini.ModelName);
        Assert.Throws<ArgumentException>(() => FoundryModels.CreateApi("gpt-4o", route: "completions", settings: settings));
    }

    [Fact]
    public async Task a_config_system_message_does_not_count_against_the_message_limit()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var model = new Model(api, new GenerateConfig { SystemMessage = "be brief" });
        using var scope = new SampleContextScope(limits: new Limits { MessageLimit = 2 });

        await model.GenerateAsync("hi");

        var request = Assert.Single(api.Requests);
        Assert.Equal(["system", "user"], request.Input.Select(m => m.Role));
    }
}
