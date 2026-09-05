using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Stream stall scopes and attempt timeouts (<c>tests/model/test_model_stream.py</c>), model fallbacks
/// (<c>tests/model/test_model_fallbacks.py</c>), model roles (<c>tests/model/test_model_roles.py</c>), token
/// estimation (<c>model/_tokens.py</c>) and the log shape of the new fields.
/// </summary>
public sealed class ModelExtrasTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    private sealed class CollectingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }

    /// <summary>The port of the Python tests' <c>ScriptedStreamAPI</c>: each attempt reports through the observer and returns an output.</summary>
    private sealed class ScriptedStreamApi(params Func<CancellationToken, Task<ModelOutput>>[] attempts) : IModelApi
    {
        public string ModelName => "mockstream";

        public int Attempts { get; private set; }

        public List<bool> Requested { get; } = [];

        public int? MaxTokens() => 2048;

        public async Task<GenerateResult> GenerateAsync(
            IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config,
            StreamHandler? onStream, CancellationToken cancellationToken = default)
        {
            Requested.Add(ModelStreamObserver.ModelStreamRequested());
            var attempt = attempts[Math.Min(Attempts, attempts.Length - 1)];
            Attempts++;
            // the real routes install their own observer for a handler; it inherits the wrapper's stall scope
            using var scope = onStream is null ? null : ModelStreamObserver.Install(new ModelStreamObserver(ModelName, onStream));
            var output = await attempt(cancellationToken);
            return new GenerateResult(output, null, ModelCall.Create(new JsonObject()));
        }

        public static ModelOutput Output(string text) => ModelOutput.FromContent("mockstream", text);
    }

    private static RequestFailedException Http(int status) => new(CannedResponse.Error(status, $"http {status}"));

    private static async Task<ModelOutput> Stall(CancellationToken cancellationToken)
    {
        ModelStreamObserver.ReportModelStreamStart();
        await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent("stall"));
        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        return ScriptedStreamApi.Output("too late");
    }

    private static async Task<ModelOutput> Done(CancellationToken cancellationToken)
    {
        ModelStreamObserver.ReportModelStreamStart();
        await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent("done"));
        return ScriptedStreamApi.Output("done");
    }

    // --- stream idle timeout (design/stream-idle-timeout.md) ---------------------------------------------

    [Fact]
    public async Task stream_idle_timeout_fires_and_retries()
    {
        var api = new ScriptedStreamApi(Stall, Done);
        var sink = new CollectingSink();
        var model = new Model(api, new GenerateConfig { StreamIdleTimeout = 1 }, new ModelRetryOptions(MaxRetries: 2, Delay: NoDelay)) { EventSink = sink };
        var events = new List<StreamEvent>();

        var output = await model.GenerateAsync("hello", onStream: e => { events.Add(e); return Task.CompletedTask; });

        Assert.Equal("done", output.Completion);
        Assert.Equal(2, api.Attempts);
        Assert.Equal([typeof(StreamTextEvent), typeof(StreamRetryEvent), typeof(StreamTextEvent)], events.Select(e => e.GetType()));
        // the stalled attempt's event carries the error, not its partial output
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("stream_idle_timeout '1' exceeded (streaming response stalled).", sink.Events[0].Error);
        Assert.Equal("", sink.Events[0].Output.Completion);
        Assert.Null(sink.Events[1].Error);
        Assert.Equal(1, sink.Events[1].Retries);
    }

    [Fact]
    public async Task stream_idle_timeout_never_arms_without_streaming()
    {
        var api = new ScriptedStreamApi(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            return ScriptedStreamApi.Output("quiet");
        });
        var model = new Model(api, new GenerateConfig { StreamIdleTimeout = 1 }, new ModelRetryOptions(Delay: NoDelay));

        var output = await model.GenerateAsync("hello");

        Assert.Equal("quiet", output.Completion);
        Assert.Equal(1, api.Attempts);
    }

    [Fact]
    public async Task stream_idle_timeout_surfaces_when_retries_are_exhausted()
    {
        var api = new ScriptedStreamApi(async cancellationToken =>
        {
            ModelStreamObserver.ReportModelStreamStart();
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            return ScriptedStreamApi.Output("too late");
        });
        var model = new Model(api, new GenerateConfig { StreamIdleTimeout = 1 }, new ModelRetryOptions(MaxRetries: 0, Delay: NoDelay));

        var ex = await Assert.ThrowsAsync<StreamIdleTimeoutException>(() => model.GenerateAsync("hello"));

        Assert.Equal(1, ex.Timeout);
        Assert.Equal(1, api.Attempts);
    }

    [Fact]
    public async Task stream_idle_timeout_bumps_keep_a_slow_stream_alive()
    {
        var api = new ScriptedStreamApi(async cancellationToken =>
        {
            ModelStreamObserver.ReportModelStreamStart();
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.4), cancellationToken);
                ModelStreamObserver.ReportModelStreamProgress();
            }

            return ScriptedStreamApi.Output("slow but alive");
        });
        var model = new Model(api, new GenerateConfig { StreamIdleTimeout = 1 }, new ModelRetryOptions(Delay: NoDelay));

        var output = await model.GenerateAsync("hello");

        Assert.Equal("slow but alive", output.Completion);
        Assert.Equal(1, api.Attempts);
    }

    [Fact]
    public async Task stream_idle_timeout_requests_streaming_like_on_stream_does()
    {
        var api = new ScriptedStreamApi(_ => Task.FromResult(ScriptedStreamApi.Output("ok")));
        var model = new Model(api, retry: new ModelRetryOptions(Delay: NoDelay));

        await model.GenerateAsync("hello", config: new GenerateConfig { StreamIdleTimeout = 30 });
        await model.GenerateAsync("hello");
        await model.GenerateAsync("hello", onStream: _ => Task.CompletedTask);

        Assert.Equal([true, false, true], api.Requested);
        Assert.False(ModelStreamObserver.ModelStreamRequested());
    }

    // --- attempt timeout ---------------------------------------------------------------------------------

    [Fact]
    public async Task attempt_timeout_abandons_the_attempt_and_retries()
    {
        var api = new ScriptedStreamApi(
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                return ScriptedStreamApi.Output("too late");
            },
            _ => Task.FromResult(ScriptedStreamApi.Output("second")));
        var sink = new CollectingSink();
        var model = new Model(api, new GenerateConfig { AttemptTimeout = 1 }, new ModelRetryOptions(MaxRetries: 1, Delay: NoDelay)) { EventSink = sink };

        var output = await model.GenerateAsync("hello");

        Assert.Equal("second", output.Completion);
        Assert.Equal(2, api.Attempts);
        Assert.Equal("attempt_timeout '1' exceeded.", sink.Events[0].Error);
    }

    [Fact]
    public async Task caller_cancellation_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var api = new ScriptedStreamApi(async cancellationToken =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            return ScriptedStreamApi.Output("never");
        });
        var model = new Model(api, new GenerateConfig { StreamIdleTimeout = 1, AttemptTimeout = 1 }, new ModelRetryOptions(Delay: NoDelay));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.GenerateAsync("hello", cancellationToken: cts.Token));

        Assert.Equal(1, api.Attempts);
    }

    // --- client-side fallback ------------------------------------------------------------------------------

    [Fact]
    public async Task fallback_switches_after_the_configured_consecutive_failures()
    {
        var primary = new ScriptedModelApi([ScriptedTurn.Throw(Http(500)), ScriptedTurn.Throw(Http(503)), ScriptedTurn.Text("primary")], "primary");
        var fallback = new ScriptedModelApi([ScriptedTurn.Text("from fallback", new ModelUsage(1, 1, 2)), ScriptedTurn.Text("again")], "fallback");
        var api = new FallbackModelApi(primary, [fallback], failuresBeforeFallback: 2);
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 5, Delay: NoDelay));
        using var accumulators = SampleModelAccumulators.Begin();

        var output = await model.GenerateAsync("hi");
        var second = await model.GenerateAsync("hi again");

        Assert.Equal("from fallback", output.Completion);
        Assert.Equal(new ModelFallback("primary", "fallback"), output.Fallback! with { Metadata = null });
        Assert.StartsWith("http 503", (string)output.Fallback.Metadata!["reason"]!);
        Assert.Same(fallback, api.Current);
        Assert.Equal(1, primary.Remaining);
        Assert.Equal("again", second.Completion);
        Assert.Equal([new ModelFallback("primary", "fallback", 2)], accumulators.ModelFallbacks);
        Assert.Equal("primary", api.ModelName);
    }

    [Fact]
    public async Task failures_below_the_threshold_stay_on_the_primary()
    {
        var primary = new ScriptedModelApi([ScriptedTurn.Throw(Http(500)), ScriptedTurn.Throw(Http(500)), ScriptedTurn.Text("primary")], "primary");
        var fallback = new ScriptedModelApi([ScriptedTurn.Text("from fallback")], "fallback");
        var api = new FallbackModelApi(primary, [fallback], failuresBeforeFallback: 3);
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 5, Delay: NoDelay));

        var output = await model.GenerateAsync("hi");

        Assert.Equal("primary", output.Completion);
        Assert.Null(output.Fallback);
        Assert.Same(primary, api.Current);
        Assert.Equal(0, api.ConsecutiveFailures);
    }

    [Fact]
    public async Task a_terminal_error_counts_as_a_failure_and_an_exhausted_chain_rethrows()
    {
        var primary = new ScriptedModelApi([ScriptedTurn.Error(new RequestFailedException(400, "bad request"))], "primary");
        var fallback = new ScriptedModelApi([ScriptedTurn.Text("rescued"), ScriptedTurn.Throw(Http(500))], "fallback");
        var api = new FallbackModelApi(primary, [fallback]);
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 0, Delay: NoDelay));

        var output = await model.GenerateAsync("hi");
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => model.GenerateAsync("hi"));

        Assert.Equal("rescued", output.Completion);
        Assert.Equal("bad request", output.Fallback!.Metadata!["reason"]);
        Assert.Equal(500, ex.Status);
        Assert.Throws<ArgumentException>(() => new FallbackModelApi(primary, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackModelApi(primary, [fallback], 0));
    }

    // --- sample accumulators (fallback rollup, role usage) --------------------------------------------------

    private static ModelOutput FallbackOutput(string fallbackModel = "claude-opus-4-8") =>
        ModelOutput.FromContent(fallbackModel, "served") with
        {
            Fallback = new ModelFallback("claude-fable-5", fallbackModel, Metadata: new Dictionary<string, object?> { ["handoffs"] = 1 }),
        };

    [Fact]
    public void fallback_accumulation_aggregates_by_pair_without_metadata()
    {
        using var accumulators = SampleModelAccumulators.Begin();
        SampleModelAccumulators.RecordFallback(FallbackOutput());
        SampleModelAccumulators.RecordFallback(FallbackOutput());
        SampleModelAccumulators.RecordFallback(FallbackOutput("claude-sonnet-4-6"));
        SampleModelAccumulators.RecordFallback(ModelOutput.FromContent("claude-fable-5", "normal"));

        Assert.Equal(
            [new ModelFallback("claude-fable-5", "claude-opus-4-8", 2), new ModelFallback("claude-fable-5", "claude-sonnet-4-6", 1)],
            SampleModelAccumulators.SampleModelFallbacks());
        Assert.All(accumulators.ModelFallbacks, fallback => Assert.Null(fallback.Metadata));
    }

    [Fact]
    public void accumulators_are_noops_outside_a_scope_and_restore_the_previous_scope()
    {
        Assert.Null(SampleModelAccumulators.Current);
        SampleModelAccumulators.RecordFallback(FallbackOutput());
        SampleModelAccumulators.RecordRoleUsage("grader", new ModelUsage(1, 1, 2));
        Assert.Empty(SampleModelAccumulators.SampleModelFallbacks());
        Assert.Empty(SampleModelAccumulators.SampleRoleUsage());

        using (var outer = SampleModelAccumulators.Begin())
        {
            SampleModelAccumulators.RecordRoleUsage("grader", new ModelUsage(1, 2, 3));
            using (SampleModelAccumulators.Begin())
            {
                SampleModelAccumulators.RecordRoleUsage("grader", new ModelUsage(10, 20, 30));
                Assert.Equal(30, SampleModelAccumulators.SampleRoleUsage()["grader"].TotalTokens);
            }

            SampleModelAccumulators.RecordRoleUsage("grader", new ModelUsage(1, 2, 3));
            Assert.Same(outer, SampleModelAccumulators.Current);
            Assert.Equal(new ModelUsage(2, 4, 6), outer.RoleUsage["grader"]);
        }

        Assert.Null(SampleModelAccumulators.Current);
    }

    // --- model roles ---------------------------------------------------------------------------------------

    private static Model Scripted(string text = "ok", string name = ScriptedModelApi.DefaultModelName, ModelUsage? usage = null) =>
        new(new ScriptedModelApi([ScriptedTurn.Text(text, usage)], name));

    [Fact]
    public void resolve_copies_models_per_role_and_collapses_single_model_lists()
    {
        var shared = Scripted();

        var roles = ModelRoles.Resolve(new Dictionary<string, object>
        {
            ["grader"] = shared,
            ["reviewer"] = shared,
            ["pair"] = new[] { shared, shared },
            ["single"] = new List<Model> { shared },
            ["named"] = "scripted/judge",
        }, name => new Model(new ScriptedModelApi([], name)))!;

        var grader = roles.Get("grader")!;
        Assert.NotSame(shared, grader);
        Assert.Same(shared.Api, grader.Api);
        Assert.Equal("grader", grader.Role);
        Assert.Equal("reviewer", roles.Get("reviewer")!.Role);
        Assert.NotSame(grader, roles.Get("reviewer"));
        Assert.Null(shared.Role);
        Assert.Equal(2, roles.GetAll("pair")!.Count);
        Assert.Single(roles.GetAll("single")!);
        Assert.Equal("scripted/judge", roles.Get("named")!.Name);
        Assert.Equal("named", roles.Get("named")!.Role);
        Assert.Null(roles.Get("missing"));
        Assert.Equal(5, roles.Count);
        Assert.Null(ModelRoles.Resolve(null));
    }

    [Fact]
    public void resolve_rejects_invalid_and_empty_bindings_with_pythons_messages()
    {
        var invalid = Assert.Throws<PrerequisiteError>(() => ModelRoles.Resolve(new Dictionary<string, object> { ["bad"] = 42 }));
        var empty = Assert.Throws<PrerequisiteError>(() => ModelRoles.Resolve(new Dictionary<string, object> { ["empty"] = Array.Empty<string>() }));
        var mixed = Assert.Throws<PrerequisiteError>(() => ModelRoles.Resolve(new Dictionary<string, object> { ["mixed"] = new object[] { Scripted(), 1 } }));

        Assert.Equal("Model role 'bad' has an invalid value (42): expected a model name, a Model instance, or a list of these.", invalid.Message);
        Assert.Equal("Model role 'empty' was assigned an empty list (at least one model is required).", empty.Message);
        Assert.StartsWith("Model role 'mixed' has an invalid value", mixed.Message);
    }

    [Fact]
    public void merge_lets_later_role_sets_win_and_begin_installs_ambient_roles()
    {
        var taskRoles = ModelRoles.Resolve(new Dictionary<string, object> { ["grader"] = Scripted(name: "task-grader"), ["helper"] = Scripted(name: "helper") });
        var evalRoles = ModelRoles.Resolve(new Dictionary<string, object> { ["grader"] = Scripted(name: "eval-grader") });

        var merged = ModelRoles.Merge(taskRoles, evalRoles)!;

        Assert.Equal("eval-grader", merged.Get("grader")!.Name);
        Assert.Equal("helper", merged.Get("helper")!.Name);
        Assert.Null(ModelRoles.Merge(null, null));
        Assert.Same(ModelRoles.Empty, ModelRoles.Current);
        using (ModelRoles.Begin(merged))
        {
            Assert.Same(merged, ModelRoles.Current);
            using (ModelRoles.Begin(null))
            {
                Assert.Same(ModelRoles.Empty, ModelRoles.Current);
            }

            Assert.Same(merged, ModelRoles.Current);
        }

        Assert.Same(ModelRoles.Empty, ModelRoles.Current);
    }

    [Fact]
    public void get_model_resolves_bound_roles_defaults_required_and_the_active_model()
    {
        var bound = Scripted(name: "bound");
        var boundWithConfig = new Model(bound.Api, new GenerateConfig { Temperature = 0.5 });
        using var roles = ModelRoles.Begin(ModelRoles.Resolve(new Dictionary<string, object> { ["grader"] = boundWithConfig }));
        var fallback = Scripted(name: "fallback");

        var grader = ModelRoles.GetModel("grader");
        var configured = ModelRoles.GetModel("grader", config: new GenerateConfig { Temperature = 0.9, MaxTokens = 7 });
        var defaulted = ModelRoles.GetModel("reviewer", @default: fallback);
        var required = Assert.Throws<PrerequisiteError>(() => ModelRoles.GetModel("reviewer", required: true));

        Assert.Same(ModelRoles.Current.Get("grader"), grader);
        Assert.Equal("grader", grader.Role);
        Assert.NotSame(grader, configured);
        Assert.Equal(0.5, configured.Config.Temperature);            // the role's own config wins over the argument
        Assert.Equal(7, configured.Config.MaxTokens);
        Assert.Equal("grader", configured.Role);
        Assert.Same(ModelRoles.GetModel("grader", config: new GenerateConfig()), grader);
        Assert.Equal("reviewer", defaulted.Role);
        Assert.Same(fallback.Api, defaulted.Api);
        Assert.Null(fallback.Role);
        Assert.Equal("Model role 'reviewer' is required and was not specified.", required.Message);

        using (var scope = new SampleContextScope())
        {
            Assert.Same(scope.Model, ModelRoles.GetModel("reviewer"));
        }

        using var env = new EnvVarScope().Set("INSPECT_EVAL_MODEL", null).Set(FoundryModels.ModelVar, null);
        Assert.Throws<InvalidOperationException>(() => ModelRoles.GetModel("reviewer"));
    }

    [Fact]
    public async Task generating_with_a_role_model_stamps_the_event_and_records_role_usage()
    {
        var sink = new CollectingSink();
        var grader = Scripted("graded", usage: new ModelUsage(2, 3, 5)).WithRole("grader").WithEventSink(sink);
        using var accumulators = SampleModelAccumulators.Begin();

        await grader.GenerateAsync("grade this");

        var e = Assert.Single(sink.Events);
        Assert.Equal("grader", e.Role);
        Assert.Equal("grader", grader.Role);
        Assert.Equal(new ModelUsage(2, 3, 5), accumulators.RoleUsage["grader"]);
    }

    [Fact]
    public async Task eval_installs_model_roles_for_solvers_with_eval_level_roles_winning()
    {
        var main = new ScriptedModelApi(ScriptedTurn.Text("main answer"));
        var taskGrader = new ScriptedModelApi([ScriptedTurn.Text("task graded")], "task-grader");
        var evalGrader = new ScriptedModelApi([ScriptedTurn.Text("eval graded", new ModelUsage(1, 1, 2))], "eval-grader");
        var task = new EvalTask
        {
            Name = "roles",
            Dataset = new MemoryDataset([new Sample("grade me") { Target = "x" }]),
            ModelRoles = new Dictionary<string, object> { ["grader"] = new Model(taskGrader), ["helper"] = new Model(taskGrader) },
            Solver = async (state, _, _) =>
            {
                var grader = ModelRoles.GetModel("grader", required: true);
                state.Output = await grader.GenerateAsync(state.Messages);
                state.Messages.Add(state.Output.Message);
                return state;
            },
        };
        var options = new EvalOptions
        {
            Model = new Model(main),
            LogDir = _logDir,
            MaxSamples = 1,
            ModelRoles = new Dictionary<string, object> { ["grader"] = new Model(evalGrader) },
        };

        var log = await Eval.RunAsync(task, options);

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("eval graded", sample.Output.Completion);
        var modelEvent = Assert.Single(sample.Events.OfType<ModelEvent>());
        Assert.Equal("grader", modelEvent.Role);
        Assert.Equal("eval-grader", modelEvent.Model);
        Assert.Empty(taskGrader.Requests);
        Assert.Same(ModelRoles.Empty, ModelRoles.Current);
    }

    // --- token estimation ---------------------------------------------------------------------------------

    [Fact]
    public void media_token_estimates_use_the_python_constants()
    {
        Assert.Equal(1600, TokenEstimation.FallbackImageTokens);
        Assert.Equal(2000, TokenEstimation.FallbackAudioTokens);
        Assert.Equal(8000, TokenEstimation.FallbackVideoTokens);
        Assert.Equal(5000, TokenEstimation.FallbackDocumentTokens);
        Assert.Equal((16_000, 176_000, 500_000, 100_000), (TokenEstimation.AudioBytesPerSecMp3, TokenEstimation.AudioBytesPerSecWav, TokenEstimation.VideoBytesPerSec, TokenEstimation.DocumentBytesPerPage));
        Assert.Equal((50, 400, 1000), (TokenEstimation.AudioTokensPerSec, TokenEstimation.VideoTokensPerSec, TokenEstimation.DocumentTokensPerPage));

        Assert.Equal(85, TokenEstimation.CountMediaTokens(new ContentImage("https://x/img.png", "low")));
        Assert.Equal(765, TokenEstimation.CountMediaTokens(new ContentImage("https://x/img.png")));
        Assert.Equal(2000, TokenEstimation.CountMediaTokens(new ContentAudio("https://x/a.mp3", "mp3")));
        Assert.Equal(8000, TokenEstimation.CountMediaTokens(new ContentVideo("/tmp/a.mp4", "mp4")));
        Assert.Equal(5000, TokenEstimation.CountMediaTokens(new ContentDocument("/tmp/a.pdf")));
        Assert.Equal(1600, TokenEstimation.CountMediaTokens(new ContentText("not media")));

        var audio = "data:audio/mpeg;base64," + new string('A', 320_000);          // 240,000 raw bytes = 15 s of mp3 = 750 tokens
        var wav = "data:audio/wav;base64," + new string('A', 320_000);             // 240,000 raw bytes = 1.36 s of wav = 68 tokens
        var video = "data:video/mp4;base64," + new string('A', 4_000_000);         // 3,000,000 raw bytes = 6 s of video = 2400 tokens
        var document = "data:application/pdf;base64," + new string('A', 400_000);  // 300,000 raw bytes = 3 pages = 3000 tokens
        Assert.Equal((int)(audio.Length * 3 / 4 / 16_000.0 * 50), TokenEstimation.CountMediaTokens(new ContentAudio(audio, "mp3")));
        Assert.Equal((int)(wav.Length * 3 / 4 / 176_000.0 * 50), TokenEstimation.CountMediaTokens(new ContentAudio(wav, "wav")));
        Assert.Equal((int)(video.Length * 3 / 4 / 500_000.0 * 400), TokenEstimation.CountMediaTokens(new ContentVideo(video, "mp4")));
        Assert.Equal((int)(document.Length * 3 / 4 / 100_000.0 * 1000), TokenEstimation.CountMediaTokens(new ContentDocument(document)));
        Assert.Equal(50, TokenEstimation.CountMediaTokens(new ContentAudio("data:audio/mpeg;base64,AAAA", "mp3")));
        Assert.Equal(100, TokenEstimation.CountMediaTokens(new ContentVideo("data:video/mp4;base64,AAAA", "mp4")));
        Assert.Equal(100, TokenEstimation.CountMediaTokens(new ContentDocument("data:application/pdf;base64,AAAA")));
    }

    [Fact]
    public async Task count_tokens_joins_text_parts_once_and_counts_media_per_item()
    {
        var texts = new List<string>();
        var media = new List<Content>();
        var image = new ContentImage("data:image/png;base64,AA==");
        var messages = new List<ChatMessage>
        {
            new ChatMessageUser("hello"),
            new ChatMessageAssistant("", toolCalls: [new ToolCall("c1", "fn", new JsonObject { ["a"] = 1 })]),
            new ChatMessageUser(MessageContent.FromItems([new ContentText("x"), image, new ContentReasoning("secret")])),
        };

        var total = await TokenEstimation.CountTokensAsync(messages, t => { texts.Add(t); return Task.FromResult(7); }, m => { media.Add(m); return Task.FromResult(5); });
        var empty = await TokenEstimation.CountTokensAsync([], _ => Task.FromResult(0), _ => Task.FromResult(0));

        Assert.Equal(12, total);
        Assert.Equal(["hello\n\nfn\n{\"a\": 1}\nx"], texts);      // the assistant's empty string content is a (empty) text part, as in Python
        Assert.Equal([image], media);
        Assert.Equal(1, empty);
    }

    [Fact]
    public async Task text_tokens_apply_the_ten_percent_buffer_and_a_tokenizer_can_be_supplied()
    {
        Assert.Equal(1, TokenEstimation.CountTextTokens(""));
        Assert.Equal(11, TokenEstimation.CountTextTokens(new string('a', 40)));
        Assert.Equal(110, TokenEstimation.CountTextTokens("anything", _ => 100));
        Assert.Equal(3, TokenEstimation.CountTextTokens("日本語"));                     // one token per non-ASCII character; the 10% buffer floors back to 3
        var model = Scripted();
        Assert.True(await model.CountTokensAsync([new ChatMessageUser("Hello, world! This is a test message for token counting.")]) >= 10);
    }

    // --- log shape ------------------------------------------------------------------------------------------

    [Fact]
    public void new_config_fields_role_and_fallback_serialise_with_python_names_and_round_trip()
    {
        var e = new ModelEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Model = "m",
            Role = "grader",
            Input = [new ChatMessageUser("hi")],
            ToolChoice = ToolChoice.Auto,
            Config = new GenerateConfig
            {
                ResponseSchema = new ResponseSchema("ans", JsonSchemaGenerator.JsonSchemaOf<Dictionary<string, int>>()) { Strict = true },
                CachePrompt = "auto",
                Batch = new BatchConfig { Size = 10 },
                LogitBias = new Dictionary<int, double> { [42] = 10 },
                FallbackModels = ["x"],
                AttemptTimeout = 30,
                Modalities = [OutputModality.Image],
            },
            Output = ModelOutput.FromContent("m", "ok") with { Fallback = new ModelFallback("m", "f", Metadata: new Dictionary<string, object?> { ["reason"] = "r" }) },
        };

        var json = JsonSerializer.Serialize<TranscriptEvent>(e, EvalLogWriter.Options);
        var node = JsonNode.Parse(json)!;
        var back = Assert.IsType<ModelEvent>(JsonSerializer.Deserialize<TranscriptEvent>(json, EvalLogWriter.Options));

        Assert.Equal("grader", node["role"]!.GetValue<string>());
        Assert.Equal("""{"name":"ans","json_schema":{"type":"object","additionalProperties":{"type":"integer"}},"strict":true}""", node["config"]!["response_schema"]!.ToJsonString());
        Assert.Equal("auto", node["config"]!["cache_prompt"]!.GetValue<string>());
        Assert.Equal(10, node["config"]!["batch"]!["size"]!.GetValue<int>());
        Assert.Equal(10, node["config"]!["logit_bias"]!["42"]!.GetValue<double>());
        Assert.Equal("x", node["config"]!["fallback_models"]![0]!.GetValue<string>());
        Assert.Equal(30, node["config"]!["attempt_timeout"]!.GetValue<int>());
        Assert.Equal("""{"model":"m","fallback_model":"f","count":1,"metadata":{"reason":"r"}}""", node["output"]!["fallback"]!.ToJsonString());
        Assert.Equal("grader", back.Role);
        Assert.Equal("ans", back.Config.ResponseSchema!.Name);
        Assert.Equal(["object"], back.Config.ResponseSchema.JsonSchema.Type);
        Assert.Equal("f", back.Output.Fallback!.FallbackModel);
        Assert.Equal(30, back.Config.AttemptTimeout);
    }

    [Fact]
    public void generate_config_helpers_validate_and_normalise_like_python()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerateConfig { PromptLogprobs = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerateConfig { PromptLogprobs = 21 });
        Assert.Equal(5, new GenerateConfig { PromptLogprobs = 5 }.PromptLogprobs);

        Assert.Null(GenerateConfigUtil.NormalizedBatchConfig(null));
        Assert.Null(GenerateConfigUtil.NormalizedBatchConfig(false));
        Assert.Equal(100, GenerateConfigUtil.NormalizedBatchConfig(true)!.Size);
        Assert.Equal(25, GenerateConfigUtil.NormalizedBatchConfig(25)!.Size);
        var config = new BatchConfig { Size = 3 };
        Assert.Same(config, GenerateConfigUtil.NormalizedBatchConfig(config));
        Assert.Throws<ArgumentException>(() => GenerateConfigUtil.NormalizedBatchConfig("yes"));

        Assert.False(GenerateConfigUtil.HasImageOutput(null));
        Assert.NotNull(GenerateConfigUtil.ImageOutputConfig([OutputModality.Image]));
        var configured = new ImageOutput { Options = new Dictionary<string, JsonObject> { ["openai"] = new() { ["size"] = "1024x1024" } } };
        Assert.Same(configured, GenerateConfigUtil.ImageOutputConfig([OutputModality.Image, OutputModality.Configured(configured)]));

        var merged = new GenerateConfig { Verbosity = "low", Effort = "high" }.Merge(new GenerateConfig { Effort = "max", ReasoningMode = "pro" });
        Assert.Equal(("low", "max", "pro"), (merged.Verbosity, merged.Effort, merged.ReasoningMode));
    }

    [PythonFact]
    public void every_python_generate_config_field_has_a_property()
    {
        var pythonFields = PythonReference.Run("""
            from inspect_ai.model import GenerateConfig
            print("\n".join(GenerateConfig.model_fields.keys()))
            """).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var properties = typeof(GenerateConfig).GetProperties().Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)).ToHashSet(StringComparer.Ordinal);

        var missing = pythonFields.Where(field => !properties.Contains(field)).ToList();

        Assert.Empty(missing);
    }
}
