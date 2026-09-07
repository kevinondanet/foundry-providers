using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>A fact that runs only when INSPECT_PY names a python with inspect_ai installed.</summary>
public sealed class PythonInteropFactAttribute : FactAttribute
{
    public PythonInteropFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INSPECT_PY")))
        {
            Skip = "Set INSPECT_PY to a python with inspect_ai installed to run Python interop tests.";
        }
    }
}

/// <summary>
/// The Python-compatible log schema: every event type, the full EvalLog field set, Python's scalar formats, and
/// interop with logs written and read by inspect_ai itself.
/// </summary>
public class LogSchemaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static string FixturePath(string name)
    {
        // fixtures are not copied to the output directory, so walk up from the test assembly to the project's fixtures folder
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures", "eval-logs", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"No fixtures/eval-logs/{name} above {AppContext.BaseDirectory}");
    }

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"), name);

    private static JsonNode ParseLoose(string json) => JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(json))!;

    private static T RoundTrip<T>(T value) => Deserialize<T>(JsonSerializer.Serialize(value, EvalLogWriter.Options));

    /// <summary>Reads a fragment the way <see cref="EvalLogWriter.Deserialize"/> does: the NaN constants sanitized first.</summary>
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(PythonJsonFormat.SanitizeNonFinite(json), EvalLogWriter.Options)!;

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EvalLogWriter.Options);

    private static ToolCall BashCall() => new("call_1", "bash", new JsonObject { ["cmd"] = "ls" });

    private static ModelOutput Output() => new()
    {
        Model = "gpt",
        Choices = [new ChatCompletionChoice(new ChatMessageAssistant("The answer is 4.", model: "gpt", source: "generate") { Id = "m5" }, StopReason.Stop)],
        Usage = new ModelUsage(10, 5, 15) { InputTokensCacheRead = 2 },
        Time = 0.5,
    };

    /// <summary>One instance of each of the 23 members of Python's <c>Event</c> union.</summary>
    private static List<TranscriptEvent> AllEvents()
    {
        var call = ModelCall.Create(new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) });
        call.SetResponse(new JsonObject { ["id"] = "resp_1", ["usage"] = new JsonObject { ["total_tokens"] = 15 } }, 0.5);
        var provenance = new ProvenanceData("alice") { Timestamp = At(22), Reason = "qa" };
        var events = new List<TranscriptEvent>
        {
            new SampleInitEvent(new Sample("What is 2+2?") { Id = 1, Target = "4", Metadata = new Dictionary<string, object?> { ["difficulty"] = 1 } }, new JsonObject { ["messages"] = new JsonArray(), ["store"] = new JsonObject() }),
            new SpanBeginEvent("span-solver", "generate", "solver"),
            new StepEvent("generate", "solver", "begin"),
            StateEvent.FromChanges(new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/messages/0", ["value"] = new JsonObject { ["role"] = "user", ["content"] = "What is 2+2?" } }, new JsonObject { ["op"] = "replace", ["path"] = "/output/completion", ["value"] = "4", ["replaced"] = "" })),
            StoreEvent.FromChanges(new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/steps", ["value"] = 3 })),
            new ModelEvent
            {
                Model = "gpt",
                Role = "grader",
                Input = [new ChatMessageUser("What is 2+2?") { Id = "m2", Source = "input" }],
                Tools = [new ToolInfo("bash", "Run bash.") { Parameters = new ToolParams { Properties = new Dictionary<string, ToolParam> { ["cmd"] = new() { Type = ["string"], Description = "Command." } }, Required = ["cmd"] } }],
                ToolChoice = ToolChoice.Auto,
                Config = new GenerateConfig { MaxTokens = 100, Temperature = 0.2 },
                Output = Output(),
                Call = call,
                Retries = 0,
                Completed = At(6),
                WorkingTime = 0.5,
            },
            new ModelEvent
            {
                Model = "gpt",
                Input = [],
                ToolChoice = ToolChoice.None,
                Config = new GenerateConfig(),
                Output = new ModelOutput(),
                Error = "rate limited",
                Traceback = "Traceback...",
                TracebackAnsi = "[31mTraceback...[0m",
                Cache = CacheMode.Read,
                Retries = 1,
            },
            new ToolEvent("call_1", "bash", new JsonObject { ["cmd"] = "ls" }, "a.txt\nb.txt", new ToolCallError("timeout", "Command timed out before completing."), new ToolTruncation(20000, 16384), TimeSpan.FromSeconds(1.25))
            {
                View = new ToolCallContent("markdown", "```\nls\n```") { Title = "bash" },
                Completed = At(8),
                Failed = true,
                MessageId = "m4",
                Pending = false,
            },
            new ToolEvent("call_2", "think", new JsonObject(), null) { ResultContent = [new ContentText("thought")], Completed = At(9), Agent = "helper", AgentSpanId = "span-agent" },
            new ApprovalEvent("run ls", BashCall(), "human", "modify")
            {
                View = new ToolCallView { Call = new ToolCallContent("text", "ls") },
                Modified = new ToolCall("call_1", "bash", new JsonObject { ["cmd"] = "ls -la" }),
                Explanation = "fine",
            },
            new SandboxEvent("exec", Cmd: "ls", Options: new JsonObject { ["timeout"] = 30 }, Result: 0, Output: "a.txt\nb.txt", Completed: At(11)),
            new SandboxEvent("write_file", File: "/tmp/x.txt", Input: "hello"),
            new SubtaskEvent("helper", new JsonObject { ["x"] = 1 }) { Type = "subtask", Result = new JsonObject { ["y"] = 2 }, Completed = At(13), WorkingTime = 0.2 },
            new CompactionEvent { Type = "summary", TokensBefore = 1000, TokensAfter = 200, Source = "inspect" },
            new LoggerEvent(new LoggingMessage("info", "GET /x 200", 1757000000000.0) { Name = "httpx", Filename = "_client.py", Module = "_client", Lineno = 42 }),
            new InputEvent("yes", "[1myes[0m") { Message = "Continue?", Fields = [new InputField("confirm", "boolean") { Description = "Proceed" }], Outcome = "accepted", Content = new Dictionary<string, object?> { ["confirm"] = true } },
            new AnchorEvent("anchor-1") { Source = "mod.fn" },
            new BranchEvent("anchor-1"),
            new CheckpointEvent(1, "time", 1, At(18), 10, 1024, new SnapshotDetails("snap-h", 1024, 10) { AdditionalFiles = 1 })
            {
                Sandboxes = new Dictionary<string, SnapshotDetails> { ["default"] = new("snap-s", 2048, 20) { Files = ["/tmp/x.txt"] } },
                Extra = new JsonObject { ["future_field"] = "kept" },
            },
            new InterruptEvent("limit", "tool_call") { InterruptedToolCallId = "call_1" },
            new ScoreEvent(new Score("C") { Answer = "4", Explanation = "matched", Metadata = new Dictionary<string, object?> { ["strict"] = true } }, new Target("4"))
            {
                Scorer = "match",
                ScorerArgs = new Dictionary<string, object?> { ["location"] = "any" },
                ModelUsage = new Dictionary<string, ModelUsage> { ["gpt"] = new(10, 5, 15) },
            },
            new ScoreEvent(Score.Unscored("no_response"), new Target(["4", "four"]), Intermediate: true),
            new ScoreEditEvent("match", new ScoreEdit { Value = new ScoreValue.Str("I"), Explanation = "reviewer override", Provenance = provenance }),
            new SampleLimitEvent("message", "Message limit reached", 10),
            new ErrorEvent(new EvalError("boom", "Traceback...", "Traceback...")),
            new InfoEvent("claude_code", new JsonObject { ["type"] = "system", ["n"] = 1 }),
            new InfoEvent(null, null),
            new StepEvent("generate", "solver", "end"),
            new SpanEndEvent("span-solver"),
        };

        for (var i = 0; i < events.Count; i++)
        {
            events[i] = events[i] with { Uuid = $"ev-{i:00}", Timestamp = At(i), WorkingStart = i, SpanId = i > 1 ? "span-solver" : null };
        }

        return events;
    }

    /// <summary>A timeline over <see cref="AllEvents"/> with a nested agent span, a branch and an outline.</summary>
    private static Timeline SampleTimeline() => new("Default", "agent view", new TimelineSpan("main", "Main")
    {
        SpanType = "agent",
        Content =
        [
            new TimelineEvent("ev-00"),
            new TimelineSpan("agent-1", "Helper")
            {
                SpanType = "agent",
                Content = [new TimelineEvent("ev-05"), new TimelineEvent("ev-07")],
                Branches = [new TimelineSpan("b1", "branch") { SpanType = "branch", BranchedFrom = "anchor-1", Content = [new TimelineEvent("ev-17")] }],
                Description = "sub-agent",
                Utility = true,
                ToolInvoked = true,
                AgentResult = "done",
            },
        ],
        Outline = new Outline { Nodes = [new OutlineNode("ev-05") { Children = [new OutlineNode("ev-07")] }] },
    });

    private static EvalLog FullLog()
    {
        var sample1 = new EvalSample
        {
            Id = 1,
            Epoch = 1,
            Input = "What is 2+2?",
            Target = "4",
            Sandbox = new SandboxSpec("docker", "Dockerfile"),
            Files = ["hello.txt"],
            Setup = "echo hi",
            Messages = [new ChatMessageSystem("Be helpful.") { Id = "m1", Source = "input" }, new ChatMessageUser("What is 2+2?") { Id = "m2", Source = "input" }],
            Output = Output(),
            Scores = new Dictionary<string, Score>
            {
                ["match"] = new Score("C") { Answer = "4", History = [new ScoreEdit { Value = new ScoreValue.Str("C"), Provenance = new ProvenanceData("bob") { Timestamp = At(40) } }] },
                ["unscored"] = Score.Unscored("no_response"),
                ["listnan"] = new Score(new ScoreValue.List([1.0, double.NaN])),
                ["dictnan"] = new Score(new ScoreValue.Dict(new Dictionary<string, ScoreValue?> { ["a"] = double.NaN, ["b"] = double.PositiveInfinity, ["c"] = double.NegativeInfinity, ["d"] = null })),
            },
            Metadata = new Dictionary<string, object?> { ["difficulty"] = 1, ["ratio"] = 0.5, ["unicode"] = "héllo ✓ 日本" },
            Store = new Dictionary<string, object?> { ["steps"] = 3 },
            Events = AllEvents(),
            Timelines = [SampleTimeline()],
            ModelUsage = new Dictionary<string, ModelUsage> { ["gpt"] = new(10, 5, 15) },
            RoleUsage = new Dictionary<string, ModelUsage> { ["grader"] = new(1, 2, 3) },
            ModelFallbacks = [new ModelFallback("gpt", "gpt-mini") { Count = 2 }],
            StartedAt = T0,
            CompletedAt = At(2),
            TotalTime = 2.0,
            WorkingTime = 1.5,
            Uuid = "uuid-1",
            ErrorRetries = [new EvalRetryError("flaky", "trace", "trace") { Events = [new InfoEvent("retry", JsonValue.Create("first attempt")) { Uuid = "ev-r", Timestamp = T0 }] }],
            Attachments = new Dictionary<string, string> { ["abc123"] = "attachment text" },
            TurnCount = 3,
            TokenLimit = 1000,
            TokenLimitType = "total",
            MessageLimit = 10,
            TimeLimit = 300,
        };
        var sample2 = new EvalSample
        {
            Id = "s2",
            Epoch = 2,
            Input = new ChatMessage[] { new ChatMessageUser(new Content[] { new ContentText("look"), new ContentImage("data:image/png;base64,AAAA", "high") }) { Id = "m7", Source = "input" } },
            Target = new Target(["a", "b"]),
            Choices = ["a", "b"],
            Error = new EvalError("sandbox exploded", "trace", "trace"),
            Limit = new EvalSampleLimit("message", 10, "Message limit reached"),
            Invalidation = new ProvenanceData("carol") { Timestamp = At(50), Reason = "bad sample" },
            Uuid = "uuid-2",
        };
        return new EvalLog
        {
            Status = EvalStatus.Success,
            Eval = new EvalSpec
            {
                EvalId = "eval-1",
                RunId = "run1",
                Created = T0,
                Task = "hello-swe",
                TaskId = "task1",
                TaskVersion = "0",
                TaskFile = "tasks/hello.py",
                TaskArgs = new Dictionary<string, object?> { ["difficulty"] = "easy" },
                Solver = "generate",
                SolverArgs = new Dictionary<string, object?>(),
                Tags = ["needs_qa", "demo"],
                Dataset = new EvalDataset { Name = "hello", Location = "/tasks/hello/dataset.json", Samples = 2, SampleIds = [1, "s2"], Shuffled = false },
                Sandbox = new SandboxSpec("docker", "Dockerfile"),
                Model = "gpt",
                ModelGenerateConfig = new GenerateConfig { MaxTokens = 100 },
                ModelBaseUrl = "https://example.invalid",
                ModelArgs = new Dictionary<string, object?> { ["k"] = "v" },
                ModelRoles = new Dictionary<string, IReadOnlyList<ModelConfig>> { ["grader"] = [new ModelConfig("gpt-mini")], ["pair"] = [new ModelConfig("a"), new ModelConfig("b")] },
                Config = new EvalConfig { LimitRange = new SampleRange(0, 2), Epochs = 2, EpochsReducer = ["mean"], MaxSamples = 4, MessageLimit = 10, TokenLimit = 1000, TimeLimit = 300, FailOnError = 0.5, SandboxCleanup = true, SampleShuffleSeed = 42, Notification = "slack", AcpServer = 8080 },
                Revision = new EvalRevision("git", "https://example.invalid/repo.git", "abc123") { Dirty = false },
                Packages = new Dictionary<string, string> { ["inspect_ai"] = "0.3.262" },
                Metadata = new Dictionary<string, object?> { ["owner"] = "kev" },
                Scorers = [new EvalScorer("match") { Options = new Dictionary<string, object?> { ["location"] = "any" }, Metrics = new JsonArray(new JsonObject { ["name"] = "accuracy" }) }],
                HeadlineMetric = new HeadlineMetric { Scorer = "match", Metric = "accuracy" },
            },
            Plan = new EvalPlan { Steps = [new EvalPlanStep("generate") { Params = new Dictionary<string, object?> { ["tool_calls"] = "loop" } }], Config = new GenerateConfig { Temperature = 0.2 } },
            Results = new EvalResults
            {
                TotalSamples = 2,
                CompletedSamples = 1,
                Scores =
                [
                    new EvalScore("match", "match")
                    {
                        Reducer = "mean",
                        ScoredSamples = 1,
                        UnscoredSamples = 1,
                        Params = new Dictionary<string, object?> { ["location"] = "any" },
                        Metrics = new Dictionary<string, EvalMetric>
                        {
                            ["accuracy"] = new("accuracy", 1.0),
                            ["stderr"] = new("stderr", double.NaN) { Params = new Dictionary<string, object?> { ["cluster"] = null } },
                            ["inf"] = new("inf", double.PositiveInfinity) { Group = "g" },
                        },
                    },
                ],
                Headline = new HeadlineMetric { Scorer = "match", Metric = "accuracy" },
            },
            Stats = new EvalStats
            {
                StartedAt = T0,
                CompletedAt = At(5),
                ModelUsage = new Dictionary<string, ModelUsage> { ["gpt"] = new(10, 5, 15) },
                ConnectionLimitHistory = [new ConnectionLimitChange(1757000000.5, "gpt", 10, 20, LimitChangeReason.RateLimit)],
            },
            LogUpdates =
            [
                new LogUpdate(new ProvenanceData("alice") { Timestamp = At(30), Reason = "QA complete" })
                {
                    Edits = [new TagsEdit { TagsAdd = ["qa_passed"], TagsRemove = ["needs_qa"] }, new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["reviewer"] = "alice" }, MetadataRemove = ["owner"] }],
                },
            ],
            ConfigUpdates = [new ConfigUpdate([new ConfigValueChange("eval", "max_samples") { Value = JsonValue.Create(8), Previous = JsonValue.Create(4) }], "task", new ProvenanceData("ops") { Timestamp = At(31) })],
            Samples = [sample1, sample2],
            Reductions = [new EvalSampleReductions("match", [new EvalSampleScore(new Score("C") { Answer = "4" }) { SampleId = 1 }, new EvalSampleScore(Score.Unscored()) { SampleId = "s2" }]) { Reducer = "mean" }],
        };
    }

    [Fact]
    public void every_event_type_round_trips()
    {
        var events = AllEvents();
        Assert.Equal(23, events.Select(e => e.Event).Distinct().Count());

        foreach (var original in events)
        {
            var json = Json<TranscriptEvent>(original);
            var read = Deserialize<TranscriptEvent>(json);

            Assert.Equal(original.GetType(), read.GetType());
            Assert.Equal(json, Json<TranscriptEvent>(read));
            Assert.Equal(original.Uuid, read.Uuid);
            Assert.Equal(original.SpanId, read.SpanId);
            Assert.Equal(original.Timestamp, read.Timestamp);
            Assert.Equal(original.WorkingStart, read.WorkingStart);
        }
    }

    [Fact]
    public void events_write_base_fields_first_then_event_then_own_fields()
    {
        var json = Json<TranscriptEvent>(new StepEvent("generate", "solver", "begin") { Uuid = "u", SpanId = "s", Timestamp = At(0.5), WorkingStart = 1.5, Metadata = new Dictionary<string, object?> { ["k"] = 1 }, Pending = true });
        var keys = JsonNode.Parse(json)!.AsObject().Select(pair => pair.Key).ToArray();

        Assert.Equal(["uuid", "span_id", "timestamp", "working_start", "metadata", "pending", "event", "action", "type", "name"], keys);
        Assert.Contains("\"timestamp\": \"2026-09-04T10:30:00.500000+00:00\"", json);
        Assert.Contains("\"working_start\": 1.5", json);

        var checkpointKeys = JsonNode.Parse(Json<TranscriptEvent>(AllEvents().OfType<CheckpointEvent>().Single()))!.AsObject().Select(pair => pair.Key).ToArray();
        Assert.Equal(["checkpoint_id", "trigger", "turn", "created_at", "duration_ms", "size_bytes", "host", "sandboxes", "future_field", "uuid", "span_id", "timestamp", "working_start", "event"], checkpointKeys);
    }

    [Fact]
    public void event_specific_fields_read_back_typed()
    {
        var events = Deserialize<List<TranscriptEvent>>(Json(AllEvents()));

        var init = Assert.IsType<SampleInitEvent>(events[0]);
        Assert.Equal(1, init.Sample.Id);
        Assert.Equal("4", init.Sample.Target.Text);
        Assert.Empty(init.State!["messages"]!.AsArray());
        var state = Assert.IsType<StateEvent>(events[3]);
        Assert.Equal(2, state.Changes.GetArrayLength());
        Assert.Equal("replace", state.Changes[1].GetProperty("op").GetString());
        Assert.Equal("", state.Changes[1].GetProperty("replaced").GetString());
        Assert.Equal(3, Assert.IsType<StoreEvent>(events[4]).Changes[0].GetProperty("value").GetInt32());
        var model = Assert.IsType<ModelEvent>(events[5]);
        Assert.Equal("grader", model.Role);
        Assert.Equal("resp_1", (string?)model.Call!.Response!["id"]);
        var failed = Assert.IsType<ModelEvent>(events[6]);
        Assert.Equal(CacheMode.Read, failed.Cache);
        Assert.Equal("Traceback...", failed.Traceback);
        var tool = Assert.IsType<ToolEvent>(events[7]);
        Assert.Equal("bash", tool.View!.Title);
        Assert.Equal(TimeSpan.FromSeconds(1.25), tool.Working);
        Assert.Equal(1.25, tool.WorkingTime);
        Assert.True(tool.Failed);
        Assert.False(tool.Pending);
        var think = Assert.IsType<ToolEvent>(events[8]);
        Assert.Null(think.Result);
        Assert.Equal("thought", Assert.IsType<ContentText>(Assert.Single(think.ResultContent!)).Text);
        Assert.Equal("helper", think.Agent);
        var approval = Assert.IsType<ApprovalEvent>(events[9]);
        Assert.Equal("ls -la", (string?)approval.Modified!.Arguments["cmd"]);
        Assert.Equal("ls", approval.View!.Call!.Content);
        var exec = Assert.IsType<SandboxEvent>(events[10]);
        Assert.Equal(30, (int?)exec.Options!["timeout"]);
        Assert.Equal(0, exec.Result);
        Assert.Equal("/tmp/x.txt", Assert.IsType<SandboxEvent>(events[11]).File);
        var subtask = Assert.IsType<SubtaskEvent>(events[12]);
        Assert.Equal(2, (int?)subtask.Result!["y"]);
        Assert.Equal(200, Assert.IsType<CompactionEvent>(events[13]).TokensAfter);
        var logger = Assert.IsType<LoggerEvent>(events[14]);
        Assert.Equal("httpx", logger.Message.Name);
        Assert.Equal(1757000000000.0, logger.Message.Created);
        var input = Assert.IsType<InputEvent>(events[15]);
        Assert.Equal("boolean", Assert.Single(input.Fields!).Type);
        Assert.Equal(true, input.Content!["confirm"]);
        Assert.Equal("mod.fn", Assert.IsType<AnchorEvent>(events[16]).Source);
        Assert.Equal("anchor-1", Assert.IsType<BranchEvent>(events[17]).FromAnchor);
        var checkpoint = Assert.IsType<CheckpointEvent>(events[18]);
        Assert.Equal(At(18), checkpoint.CreatedAt);
        Assert.Equal(["/tmp/x.txt"], checkpoint.Sandboxes["default"].Files);
        Assert.Equal("kept", (string?)checkpoint.Extra!["future_field"]);
        Assert.Equal("call_1", Assert.IsType<InterruptEvent>(events[19]).InterruptedToolCallId);
        var score = Assert.IsType<ScoreEvent>(events[20]);
        Assert.Equal("match", score.Scorer);
        Assert.Equal(15, score.ModelUsage!["gpt"].TotalTokens);
        Assert.True(Assert.IsType<ScoreEvent>(events[21]).Score.IsUnscored);
        var edit = Assert.IsType<ScoreEditEvent>(events[22]);
        Assert.Equal(new ScoreValue.Str("I"), edit.Edit.Value.Value);
        Assert.False(edit.Edit.Reason.IsSet);
        Assert.Equal("alice", edit.Edit.Provenance!.Author);
        Assert.Equal(10, Assert.IsType<SampleLimitEvent>(events[23]).Limit);
        Assert.Equal("boom", Assert.IsType<ErrorEvent>(events[24]).Error.Message);
        Assert.Equal("system", (string?)Assert.IsType<InfoEvent>(events[25]).Data!["type"]);
        Assert.Null(Assert.IsType<InfoEvent>(events[26]).Data);
    }

    [Fact]
    public void cleared_score_edit_field_is_omitted_and_reads_back_unchanged()
    {
        // Python writes a cleared (None) field as absent (exclude_none), which its reader takes as UNCHANGED
        var json = Json(new ScoreEdit { Answer = Edited<string>.Set(null), Explanation = "x" });

        Assert.DoesNotContain("answer", json);
        Assert.Contains("\"value\": \"UNCHANGED\"", json);
        var read = Deserialize<ScoreEdit>(json);
        Assert.False(read.Answer.IsSet);
        Assert.False(read.Value.IsSet);
        Assert.Equal("x", read.Explanation.Value);
    }

    [Fact]
    public void info_event_without_data_still_writes_data_null_for_python()
    {
        var json = Json<TranscriptEvent>(new InfoEvent(null, null));

        Assert.Contains("\"data\": null", json);
    }

    [Fact]
    public void whole_log_round_trips_through_write_and_read()
    {
        var log = FullLog();
        var path = TempPath("2026-09-04T10-30-00_hello-swe_abc123.json");
        try
        {
            EvalLogWriter.Write(log, path);
            var read = EvalLogWriter.Read(path);

            Assert.Equal(EvalLogWriter.Serialize(log), EvalLogWriter.Serialize(read));
            Assert.Equal(2, read.Version);
            Assert.Equal("eval-1", read.Eval.EvalId);
            Assert.Equal(new SampleRange(0, 2), read.Eval.Config.LimitRange);
            Assert.Null(read.Eval.Config.Limit);
            Assert.Equal(FailOnError.Threshold(0.5), read.Eval.Config.FailOnError);
            Assert.Equal(42, read.Eval.Config.SampleShuffleSeed);
            Assert.Equal("slack", read.Eval.Config.Notification);
            Assert.Equal(8080, read.Eval.Config.AcpServer);
            Assert.Equal("gpt-mini", Assert.Single(read.Eval.ModelRoles!["grader"]).Model);
            Assert.Equal(2, read.Eval.ModelRoles["pair"].Count);
            Assert.Equal("accuracy", (string?)read.Eval.Scorers![0].Metrics![0]!["name"]);
            Assert.Equal("generate", Assert.Single(read.Plan.Steps).Solver);
            Assert.Equal(0.2, read.Plan.Config.Temperature);
            Assert.Equal("g", read.Results!.Scores[0].Metrics["inf"].Group);
            Assert.True(double.IsPositiveInfinity(read.Results.Scores[0].Metrics["inf"].Value));
            Assert.True(double.IsNaN(read.Results.Scores[0].Metrics["stderr"].Value));
            Assert.Equal(LimitChangeReason.RateLimit, Assert.Single(read.Stats.ConnectionLimitHistory).Reason);
            Assert.Equal(["demo", "qa_passed"], read.Tags);
            Assert.Equal(new Dictionary<string, object?> { ["reviewer"] = "alice" }, read.Metadata);
            Assert.Equal(8, (int?)Assert.Single(read.ConfigUpdates!).Changes[0].Value);
            Assert.Equal("s2", Assert.Single(read.Reductions!).Samples[1].SampleId);
            Assert.True(read.Reductions![0].Samples[1].Score.IsUnscored);

            var sample = read.Samples![0];
            Assert.Equal(23, sample.Events.Select(e => e.Event).Distinct().Count());
            Assert.Equal("bob", Assert.Single(sample.Scores!["match"].History).Provenance!.Author);
            var listnan = Assert.IsType<ScoreValue.List>(sample.Scores["listnan"].Value);
            Assert.True(Assert.IsType<ScoreValue.Num>(listnan.Items[1]).Value is double.NaN);
            var dictnan = Assert.IsType<ScoreValue.Dict>(sample.Scores["dictnan"].Value);
            Assert.True(double.IsNegativeInfinity(Assert.IsType<ScoreValue.Num>(dictnan.Items["c"]).Value));
            Assert.Null(dictnan.Items["d"]);
            Assert.Equal(0.5, sample.Metadata["ratio"]);
            Assert.Equal(3, sample.RoleUsage["grader"].TotalTokens);
            Assert.Equal("gpt-mini", Assert.Single(sample.ModelFallbacks!).FallbackModel);
            Assert.Equal("first attempt", (string?)Assert.IsType<InfoEvent>(Assert.Single(Assert.Single(sample.ErrorRetries!).Events!)).Data);
            Assert.Equal("attachment text", sample.Attachments["abc123"]);
            Assert.Equal(3, sample.TurnCount);
            Assert.Equal("carol", read.Samples[1].Invalidation!.Author);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void log_json_follows_python_field_order_and_defaults()
    {
        var root = ParseLoose(EvalLogWriter.Serialize(FullLog())).AsObject();

        Assert.Equal(["version", "status", "eval", "plan", "results", "stats", "invalidated", "log_updates", "config_updates", "tags", "metadata", "samples", "reductions"], root.Select(pair => pair.Key).ToArray());
        Assert.Equal(2, (int?)root["version"]);
        Assert.False((bool?)root["invalidated"]);
        var eval = root["eval"]!.AsObject();
        Assert.Equal("eval_id", eval.First().Key);
        Assert.Equal("2026-09-04T10:30:00+00:00", (string?)eval["created"]);
        Assert.Equal(0, (int?)eval["task_version"]);
        Assert.Equal("easy", (string?)eval["task_args_passed"]!["difficulty"]);
        Assert.Empty(eval["solver_args_passed"]!.AsObject());
        Assert.Empty(eval["task_attribs"]!.AsObject());
        Assert.Equal([0, 2], eval["config"]!["limit"]!.AsArray().Select(node => (int)node!).ToArray());
        Assert.Equal(0.5, (double?)eval["config"]!["fail_on_error"]);
        Assert.Equal(42, (int?)eval["config"]!["sample_shuffle"]);
        Assert.Equal("gpt-mini", (string?)eval["model_roles"]!["grader"]!["model"]);
        Assert.Equal(2, eval["model_roles"]!["pair"]!.AsArray().Count);
        Assert.Equal("plan", (string?)root["plan"]!["name"]);
        Assert.Equal("loop", (string?)root["plan"]!["steps"]![0]!["params_passed"]!["tool_calls"]);
        Assert.Equal(["name", "group", "value", "params"], root["results"]!["scores"]![0]!["metrics"]!["inf"]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(["started_at", "completed_at", "model_usage", "role_usage", "connection_limit_history"], root["stats"]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal("2026-09-04T10:30:30Z", (string?)root["log_updates"]![0]!["provenance"]!["timestamp"]);
        Assert.Equal(["edits", "provenance"], root["log_updates"]![0]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(["scorer", "reducer", "samples"], root["reductions"]![0]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(1, (int?)root["reductions"]![0]!["samples"]![0]!["sample_id"]);

        var sample = root["samples"]![1]!.AsObject();
        Assert.Equal(["id", "epoch", "input", "choices", "target", "messages", "output", "metadata", "store", "events", "model_usage", "role_usage", "uuid", "invalidation", "error", "attachments", "limit"], sample.Select(pair => pair.Key).ToArray());
        var first = root["samples"]![0]!.AsObject();
        Assert.Equal(["events", "timelines", "model_usage"], first.Select(pair => pair.Key).SkipWhile(key => key != "events").Take(3).ToArray());
        var helper = first["timelines"]![0]!["root"]!["content"]![1]!.AsObject();
        Assert.Equal(["type", "id", "name", "span_type", "content", "branches", "description", "utility", "tool_invoked", "agent_result"], helper.Select(pair => pair.Key).ToArray());
        Assert.Equal("helper", (string?)helper["name"]);
        Assert.Equal(["type", "event"], helper["content"]![0]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(["type", "id", "name", "span_type", "content", "branches", "branched_from", "utility", "tool_invoked"], helper["branches"]![0]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal("", (string?)sample["output"]!["completion"]);
        Assert.Equal(10.0, (double?)sample["limit"]!["limit"]);
        Assert.Single(root["samples"]![0]!["scores"]!["match"]!["history"]!.AsArray());
    }

    [Fact]
    public void unset_stats_times_write_as_empty_strings()
    {
        var json = Json(new EvalStats());

        Assert.Contains("\"started_at\": \"\"", json);
        Assert.Null(RoundTrip(new EvalStats()).StartedAt);
    }

    [Fact]
    public void python_written_fixture_reads_with_key_fields()
    {
        var log = EvalLogWriter.Read(FixturePath("python_eval_log.json"));

        Assert.Equal(2, log.Version);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("eval-1", log.Eval.EvalId);
        Assert.Equal("hello-swe", log.Eval.Task);
        Assert.Equal(T0, log.Eval.Created);
        Assert.Equal("0", log.Eval.TaskVersion);
        Assert.Equal([1, "s2"], log.Eval.Dataset.SampleIds);
        Assert.Equal(new SandboxSpec("docker", "Dockerfile"), log.Eval.Sandbox);
        Assert.Equal(100, log.Eval.ModelGenerateConfig.MaxTokens);
        Assert.Equal(300, log.Eval.Config.TimeLimit);
        Assert.Equal(FailOnError.Never, log.Eval.Config.FailOnError);
        Assert.Equal("abc123", log.Eval.Revision!.Commit);
        Assert.Equal(["needs_qa", "demo"], log.Eval.Tags);
        Assert.Equal(["demo", "qa_passed"], log.Tags);
        Assert.Equal(new Dictionary<string, object?> { ["reviewer"] = "alice" }, log.Metadata);
        Assert.Equal("generate", Assert.Single(log.Plan.Steps).Solver);
        Assert.Equal(1.0, log.Results!.Scores[0].Metrics["accuracy"].Value);
        Assert.True(double.IsNaN(log.Results.Scores[0].Metrics["stderr"].Value));
        Assert.True(double.IsPositiveInfinity(log.Results.Scores[0].Metrics["inf"].Value));
        Assert.Null(log.Results.Scores[0].Metrics["stderr"].Params["cluster"]);
        Assert.Equal(At(5), log.Stats.CompletedAt);
        Assert.Equal("QA complete", Assert.Single(log.LogUpdates!).Provenance.Reason);
        Assert.Equal(At(30), log.LogUpdates![0].Provenance.Timestamp);

        var sample = log.Samples![0];
        Assert.Equal(1, sample.Id);
        Assert.Equal("What is 2+2?", sample.Input.Text);
        Assert.Equal(5, sample.Messages.Count);
        Assert.Equal("The answer is 4.", sample.Output.Completion);
        Assert.Equal(new ScoreValue.Str("C"), sample.Scores!["match"].Value);
        Assert.True(sample.Scores["unscored"].IsUnscored);
        Assert.Equal("no_response", sample.Scores["unscored"].Reason);
        Assert.True(Assert.IsType<ScoreValue.Num>(Assert.IsType<ScoreValue.List>(sample.Scores["listnan"].Value).Items[1]).Value is double.NaN);
        var dictnan = Assert.IsType<ScoreValue.Dict>(sample.Scores["dictnan"].Value).Items;
        Assert.True(Assert.IsType<ScoreValue.Num>(dictnan["a"]).Value is double.NaN);
        Assert.True(double.IsPositiveInfinity(Assert.IsType<ScoreValue.Num>(dictnan["b"]).Value));
        Assert.True(double.IsNegativeInfinity(Assert.IsType<ScoreValue.Num>(dictnan["c"]).Value));
        Assert.Equal("héllo ✓ 日本", sample.Metadata["unicode"]);
        Assert.Equal("attachment text", sample.Attachments["abc123"]);
        Assert.Equal(2.0, sample.TotalTime);

        var events = sample.Events;
        Assert.Equal(29, events.Count);
        Assert.Equal(23, events.Select(e => e.Event).Distinct().Count());
        Assert.All(events, e => Assert.StartsWith("ev-", e.Uuid));
        Assert.Equal(7.0, events[7].WorkingStart);
        Assert.Equal("span-solver", events[7].SpanId);
        Assert.Null(events[0].SpanId);
        Assert.Equal(1, Assert.IsType<SampleInitEvent>(events[0]).Sample.Id);
        Assert.Equal("/messages/0", Assert.IsType<StateEvent>(events[3]).Changes[0].GetProperty("path").GetString());
        var model = Assert.IsType<ModelEvent>(events[5]);
        Assert.Equal("grader", model.Role);
        Assert.Equal(["cmd"], model.Tools[0].Parameters.Required);
        Assert.Same(ToolChoice.Auto, model.ToolChoice);
        Assert.Equal(At(6), model.Completed);
        Assert.Equal("resp_1", (string?)model.Call!.Response!["id"]);
        Assert.Same(ToolChoice.None, Assert.IsType<ModelEvent>(events[6]).ToolChoice);
        var tool = Assert.IsType<ToolEvent>(events[7]);
        Assert.Equal("a.txt\nb.txt", tool.Result);
        Assert.Equal(new ToolTruncation(20000, 16384), tool.Truncated);
        Assert.Equal(TimeSpan.FromSeconds(1.25), tool.Working);
        Assert.Equal("m4", tool.MessageId);
        Assert.Equal("thought", Assert.IsType<ContentText>(Assert.Single(Assert.IsType<ToolEvent>(events[8]).ResultContent!)).Text);
        Assert.Equal("approve", Assert.IsType<ApprovalEvent>(events[9]).Decision);
        Assert.Equal("ls", Assert.IsType<SandboxEvent>(events[10]).Cmd);
        Assert.Equal("hello", Assert.IsType<SandboxEvent>(events[11]).Input);
        Assert.Equal(0.2, Assert.IsType<SubtaskEvent>(events[12]).WorkingTime);
        Assert.Equal(1000, Assert.IsType<CompactionEvent>(events[13]).TokensBefore);
        Assert.Equal("info", Assert.IsType<LoggerEvent>(events[14]).Message.Level);
        Assert.Equal("accepted", Assert.IsType<InputEvent>(events[15]).Outcome);
        Assert.Equal("anchor-1", Assert.IsType<AnchorEvent>(events[16]).AnchorId);
        Assert.Equal("anchor-1", Assert.IsType<BranchEvent>(events[17]).FromAnchor);
        var checkpoint = Assert.IsType<CheckpointEvent>(events[18]);
        Assert.Equal("time", checkpoint.Trigger);
        Assert.Equal(At(18), checkpoint.CreatedAt);
        Assert.Equal("snap-s", checkpoint.Sandboxes["default"].SnapshotId);
        Assert.Equal(1, checkpoint.Host.AdditionalFiles);
        Assert.Equal("limit", Assert.IsType<InterruptEvent>(events[19]).Source);
        Assert.Equal("match", Assert.IsType<ScoreEvent>(events[20]).Scorer);
        Assert.Equal(new Target(["4", "four"]), Assert.IsType<ScoreEvent>(events[21]).Target);
        var edit = Assert.IsType<ScoreEditEvent>(events[22]).Edit;
        Assert.Equal(new ScoreValue.Str("I"), edit.Value.Value);
        Assert.Equal("reviewer override", edit.Explanation.Value);
        Assert.False(edit.Metadata.IsSet);
        Assert.Equal(At(22), edit.Provenance!.Timestamp);
        Assert.Equal(10, Assert.IsType<SampleLimitEvent>(events[23]).Limit);
        Assert.Equal("boom", Assert.IsType<ErrorEvent>(events[24]).Message);
        Assert.Equal("plain text", (string?)Assert.IsType<InfoEvent>(events[26]).Data);
        Assert.Equal("span-solver", Assert.IsType<SpanEndEvent>(events[28]).Id);

        var timeline = Assert.Single(sample.Timelines!);
        Assert.Equal("Default", timeline.Name);
        Assert.Equal("agent view", timeline.Description);
        Assert.Equal("main", timeline.Root.Name);
        Assert.Equal("agent", timeline.Root.SpanType);
        Assert.Equal(29, timeline.Root.Content.Count);
        Assert.Same(events[5], Assert.IsType<TimelineEvent>(timeline.Root.Content[5]).Resolve(events));
        Assert.Equal("ev-07", Assert.Single(timeline.Root.Outline!.Nodes[0].Children).Event);

        var errored = log.Samples[1];
        Assert.Equal("s2", errored.Id);
        Assert.Equal("high", Assert.IsType<ContentImage>(Assert.IsType<ChatMessageUser>(errored.Input.Messages![1]).Content.Items![1]).Detail);
        Assert.Equal("sandbox exploded", errored.Error!.Message);
        Assert.Equal(new EvalSampleLimit("message", 10, "Message limit reached"), errored.Limit);
        Assert.Equal("s2", Assert.Single(log.Reductions!).Samples[1].SampleId);
    }

    [Fact]
    public void python_written_fixture_rewrites_to_the_same_json()
    {
        var original = File.ReadAllText(FixturePath("python_eval_log.json"));
        var rewritten = EvalLogWriter.Serialize(EvalLogWriter.Deserialize(original));

        Assert.True(JsonNode.DeepEquals(ParseLoose(original), ParseLoose(rewritten)), Diff(ParseLoose(original), ParseLoose(rewritten), "$"));
    }

    private static string Diff(JsonNode? a, JsonNode? b, string path)
    {
        if (JsonNode.DeepEquals(a, b))
        {
            return "";
        }

        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
            {
                var inner = Diff(oa[key], ob[key], $"{path}.{key}");
                if (inner.Length > 0)
                {
                    return inner;
                }
            }
        }

        if (a is JsonArray aa && b is JsonArray ab)
        {
            for (var i = 0; i < Math.Max(aa.Count, ab.Count); i++)
            {
                var inner = Diff(i < aa.Count ? aa[i] : null, i < ab.Count ? ab[i] : null, $"{path}[{i}]");
                if (inner.Length > 0)
                {
                    return inner;
                }
            }
        }

        return $"{path}: {a?.ToJsonString() ?? "<missing>"} != {b?.ToJsonString() ?? "<missing>"}";
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(0.5, "0.5")]
    [InlineData(1234.5678, "1234.5678")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(9007199254740992.0, "9007199254740992.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(123456789012345680.0, "1.2345678901234568e+17")]
    [InlineData(1e300, "1e+300")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.0001234, "0.0001234")]
    [InlineData(1e-5, "0.00001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(2.5e-7, "2.5e-7")]
    [InlineData(1.23e-8, "1.23e-8")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1757000000000.0, "1757000000000.0")]
    [InlineData(0.3333333333333333, "0.3333333333333333")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void doubles_format_like_pydantic(double value, string expected)
    {
        Assert.Equal(expected, PythonJsonFormat.FormatDouble(value));
    }

    [Fact]
    public void datetimes_use_python_isoformat_and_read_any_offset()
    {
        Assert.Equal("2026-09-04T10:30:00+00:00", PythonJsonFormat.FormatIso(T0));
        Assert.Equal("2026-09-04T10:30:00.250000+00:00", PythonJsonFormat.FormatIso(T0.AddMilliseconds(250)));
        Assert.Equal("2026-09-04T10:30:00+00:00", PythonJsonFormat.FormatIso(new DateTimeOffset(2026, 9, 4, 12, 30, 0, TimeSpan.FromHours(2))));
        Assert.Equal("2026-09-04T10:30:00Z", PythonJsonFormat.FormatPydantic(T0));
        Assert.Equal("2026-09-04T10:30:00.000001Z", PythonJsonFormat.FormatPydantic(T0.AddTicks(10)));
        Assert.Equal(T0, PythonJsonFormat.ParseDateTime("2026-09-04T10:30:00Z"));
        Assert.Equal(T0, PythonJsonFormat.ParseDateTime("2026-09-04T10:30:00+00:00"));
        Assert.Equal(T0, PythonJsonFormat.ParseDateTime("2026-09-04T12:30:00+02:00"));
        Assert.Equal(T0, PythonJsonFormat.ParseDateTime("2026-09-04T10:30:00"));
        Assert.Equal(T0.AddMilliseconds(250), PythonJsonFormat.ParseDateTime("2026-09-04T10:30:00.250000+00:00"));
    }

    [Fact]
    public void sanitize_rewrites_bare_constants_only_outside_strings()
    {
        const string json = "{\"a\": NaN, \"b\": [Infinity, -Infinity], \"s\": \"NaN and Infinity \\\" -Infinity\", \"n\": null}";

        var node = ParseLoose(json)!.AsObject();

        Assert.Equal(PythonJsonFormat.NaNSentinel, (string?)node["a"]);
        Assert.Equal(PythonJsonFormat.PositiveInfinitySentinel, (string?)node["b"]![0]);
        Assert.Equal(PythonJsonFormat.NegativeInfinitySentinel, (string?)node["b"]![1]);
        Assert.Equal("NaN and Infinity \" -Infinity", (string?)node["s"]);
        Assert.Same("{\"x\": 1}", PythonJsonFormat.SanitizeNonFinite("{\"x\": 1}"));
    }

    [Fact]
    public void non_finite_values_write_as_constants_and_read_back_everywhere()
    {
        var metadata = new Dictionary<string, object?> { ["nan"] = double.NaN, ["inf"] = double.PositiveInfinity, ["text"] = "NaN", ["list"] = new List<object?> { double.NegativeInfinity } };
        var json = Json(metadata);

        Assert.Contains("\"nan\": NaN", json);
        Assert.Contains("\"inf\": Infinity", json);
        Assert.Contains("\"text\": \"NaN\"", json);
        var read = JsonSerializer.Deserialize<Dictionary<string, object?>>(PythonJsonFormat.SanitizeNonFinite(json), EvalLogWriter.Options)!;
        Assert.True(read["nan"] is double.NaN);
        Assert.Equal(double.PositiveInfinity, read["inf"]);
        Assert.Equal("NaN", read["text"]);
        Assert.Equal(double.NegativeInfinity, Assert.IsType<List<object?>>(read["list"])[0]);

        var node = JsonSerializer.Deserialize<JsonObject>(PythonJsonFormat.SanitizeNonFinite("{\"x\": NaN, \"y\": [1.5, -Infinity]}"), EvalLogWriter.Options)!;
        Assert.Equal("{\"x\": NaN, \"y\": [1.5, -Infinity]}".Replace(" ", ""), Json(node).Replace(" ", "").Replace("\n", ""));

        Assert.True(Deserialize<Score>("{\"value\": null}").IsUnscored);
        Assert.Throws<JsonException>(() => Deserialize<Score>("{\"value\": [1, null]}"));
    }

    [Fact]
    public void newer_log_versions_are_rejected_and_older_ones_normalised()
    {
        var log = EvalLogWriter.Serialize(new EvalLog { Eval = new EvalSpec { Task = "t", Model = "m", Dataset = new EvalDataset() } });

        Assert.Throws<InvalidDataException>(() => EvalLogWriter.Deserialize(log.Replace("\"version\": 2", "\"version\": 3")));
        Assert.Equal(2, EvalLogWriter.Deserialize(log.Replace("\"version\": 2", "\"version\": 1")).Version);
    }

    [Fact]
    public void version_1_logs_of_this_port_still_read()
    {
        // the pre-port writer wrote NaN as null, no plan and no uuid / working_start on events
        const string legacy = """
            {
              "version": 1,
              "status": "success",
              "eval": {"run_id": "r", "created": "2026-09-03T10:30:00+00:00", "task": "t", "task_id": "i", "task_version": "0", "dataset": {}, "model": "m", "config": {"limit": 2}},
              "results": {"total_samples": 1, "completed_samples": 1, "scores": [{"name": "s", "scorer": "s", "metrics": {"stderr": {"name": "stderr", "value": null}}}]},
              "stats": {"model_usage": {}},
              "samples": [{"id": 1, "epoch": 1, "input": "q", "target": "a", "scores": {"s": {"value": null, "reason": "no answer"}}, "events": [{"event": "step", "timestamp": "2026-09-03T10:30:00+00:00", "action": "begin", "type": "solver", "name": "g"}]}]
            }
            """;

        var log = EvalLogWriter.Deserialize(legacy);

        Assert.Equal(2, log.Version);
        Assert.Equal("0", log.Eval.TaskVersion);
        Assert.Equal(2, log.Eval.Config.Limit);
        Assert.NotEmpty(log.Eval.EvalId);
        Assert.True(double.IsNaN(log.Results!.Scores[0].Metrics["stderr"].Value));
        Assert.True(log.Samples![0].Scores!["s"].IsUnscored);
        var step = Assert.IsType<StepEvent>(Assert.Single(log.Samples[0].Events));
        Assert.Null(step.Uuid);
        Assert.Equal(0, step.WorkingStart);
    }

    [Fact]
    public void samples_are_sorted_by_epoch_then_id_on_write()
    {
        EvalSample Sample(object id, int epoch) => new() { Id = id, Epoch = epoch, Input = "q" };
        var log = new EvalLog
        {
            Eval = new EvalSpec { Task = "t", Model = "m", Dataset = new EvalDataset() },
            Samples = [Sample("b", 2), Sample(10, 1), Sample(2, 1), Sample("a", 1), Sample(1, 2)],
        };
        var path = TempPath("sorted.json");
        try
        {
            EvalLogWriter.Write(log, path);

            Assert.Equal([2, 10, "a", 1, "b"], EvalLogWriter.Read(path).Samples!.Select(sample => sample.Id).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void edit_eval_log_filters_noops_and_recomputes_tags_and_metadata()
    {
        var log = new EvalLog { Eval = new EvalSpec { Task = "t", Model = "m", Dataset = new EvalDataset(), Tags = ["needs_qa"], Metadata = new Dictionary<string, object?> { ["owner"] = "kev", ["n"] = 1 } } };
        var provenance = new ProvenanceData("alice") { Reason = "review" };

        var edited = EvalLogEditing.EditEvalLog(log, [
            new TagsEdit { TagsAdd = ["qa_passed", "needs_qa"], TagsRemove = ["missing"] },
            new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["n"] = 1, ["reviewer"] = "alice" }, MetadataRemove = ["owner", "absent"] },
        ], provenance);

        var update = Assert.Single(edited.LogUpdates!);
        Assert.Same(provenance, update.Provenance);
        var tags = Assert.IsType<TagsEdit>(update.Edits[0]);
        Assert.Equal(["qa_passed"], tags.TagsAdd);
        Assert.Empty(tags.TagsRemove);
        var metadata = Assert.IsType<MetadataEdit>(update.Edits[1]);
        Assert.Equal(["reviewer"], metadata.MetadataSet.Keys);
        Assert.Equal(["owner"], metadata.MetadataRemove);
        Assert.Equal(["needs_qa", "qa_passed"], edited.Tags);
        Assert.Equal(new Dictionary<string, object?> { ["n"] = 1, ["reviewer"] = "alice" }, edited.Metadata);
        Assert.Equal(["needs_qa"], edited.Eval.Tags);

        Assert.Same(edited, EvalLogEditing.EditEvalLog(edited, [new TagsEdit { TagsAdd = ["qa_passed"] }], provenance));
        Assert.Equal(["needs_qa", "qa_passed"], EvalLogWriter.Deserialize(EvalLogWriter.Serialize(edited)).Tags);
        Assert.Throws<ArgumentException>(() => EvalLogEditing.EditEvalLog(log, [new TagsEdit { TagsAdd = [" "] }], provenance));
        Assert.Throws<ArgumentException>(() => EvalLogEditing.EditEvalLog(log, [new TagsEdit { TagsAdd = ["x"], TagsRemove = ["x"] }], provenance));
        Assert.Throws<ArgumentException>(() => EvalLogEditing.EditEvalLog(log, [new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["k"] = 1 }, MetadataRemove = ["k"] }], provenance));
    }

    [Fact]
    public void eval_config_scalar_sample_id_and_bool_fail_on_error_read()
    {
        var config = JsonSerializer.Deserialize<EvalConfig>("{\"limit\": 3, \"sample_id\": \"s1\", \"fail_on_error\": true, \"sample_shuffle\": false}", EvalLogWriter.Options)!;

        Assert.Equal(3, config.Limit);
        Assert.Equal(["s1"], config.SampleId);
        Assert.Equal(FailOnError.Always, config.FailOnError);
        Assert.False(config.SampleShuffle);
        Assert.Equal("{\"limit\":3,\"sample_id\":[\"s1\"],\"sample_shuffle\":false,\"fail_on_error\":true}", Json(config).Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void legacy_sandbox_forms_read_and_object_configs_are_rejected()
    {
        Assert.Equal(new SandboxSpec("docker", "compose.yaml"), JsonSerializer.Deserialize<SandboxSpec>("[\"docker\", \"compose.yaml\"]", EvalLogWriter.Options));
        Assert.Equal(new SandboxSpec("local"), JsonSerializer.Deserialize<SandboxSpec>("{\"type\": \"local\", \"config\": null}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SandboxSpec>("{\"type\": \"docker\", \"config\": {\"image\": \"x\"}}", EvalLogWriter.Options));
    }

    [Fact]
    public void sandbox_event_legacy_constructor_maps_raw_input_and_result()
    {
        var e = new SandboxEvent("exec", new JsonObject { ["cmd"] = new JsonArray("ls", "-la"), ["timeout"] = 30, ["input"] = "y\n" }, new JsonObject { ["returncode"] = 1, ["stdout"] = "out", ["stderr"] = "err" });

        Assert.Equal("ls -la", e.Cmd);
        Assert.Equal(30, (int?)e.Options!["timeout"]);
        Assert.Equal("y\n", e.Input);
        Assert.Equal(1, e.Result);
        Assert.Equal("outerr", e.Output);
        Assert.Null(new SandboxEvent("read_file", new JsonObject { ["file"] = "/x" }).Options);
    }

    [Fact]
    public void transcript_stamps_uuid_span_and_working_start()
    {
        var transcript = new Transcript();
        transcript.Add(new InfoEvent("a", null));
        using (transcript.Span("solver", "solver"))
        {
            transcript.Add(new InfoEvent("b", null) { WorkingStart = 5 });
        }

        var events = transcript.Events;
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(ShortUuid.Length, e.Uuid!.Length));
        Assert.Equal(events.Count, events.Select(e => e.Uuid).Distinct().Count());
        Assert.True(events[0].WorkingStart >= 0);
        Assert.True(events[3].WorkingStart >= events[1].WorkingStart);
        Assert.Equal(5, events[2].WorkingStart);
        Assert.Equal(Assert.IsType<SpanBeginEvent>(events[1]).Id, events[2].SpanId);
    }

    [Fact]
    public void unknown_events_and_unsupported_edits_are_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptEvent>("{\"event\":\"teleport\"}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LogEdit>("{\"type\":\"rename\"}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptEvent>("{\"event\":\"score\"}", EvalLogWriter.Options));
    }

    [PythonInteropFact]
    public void python_reads_a_log_written_by_csharp()
    {
        var log = FullLog();
        var path = TempPath("csharp_log.json");
        try
        {
            EvalLogWriter.Write(log, path);
            const string script = """
                import json, sys, math
                from inspect_ai.log import read_eval_log, write_eval_log
                log = read_eval_log(sys.argv[1])
                write_eval_log(log, sys.argv[2])
                def value(v):
                    if isinstance(v, float) and math.isnan(v):
                        return "nan"
                    if isinstance(v, float) and math.isinf(v):
                        return "inf"
                    return v
                print(json.dumps({
                    "version": log.version,
                    "status": log.status,
                    "tags": log.tags,
                    "metadata": log.metadata,
                    "sample_ids": [s.id for s in log.samples],
                    "scores": {str(s.id): {k: value(v.value) if not isinstance(v.value, (list, dict)) else "complex" for k, v in (s.scores or {}).items()} for s in log.samples},
                    "event_types": [e.event for e in log.samples[0].events],
                    "metrics": {k: value(m.value) for k, m in log.results.scores[0].metrics.items()},
                    "limit": log.samples[1].limit.type,
                    "reductions": [r.samples[0].sample_id for r in log.reductions],
                    "timelines": [[t.name, t.root.name, len(t.root.content), t.root.content[1].name, type(t.root.content[0].event).__name__, t.root.content[1].branches[0].branched_from] for t in log.samples[0].timelines],
                    "summary": log.samples[0].summary().model_dump(exclude_none=True, mode="json")["scores"]["match"],
                }))
                """;
            var scriptPath = Path.Combine(Path.GetDirectoryName(path)!, "read_log.py");
            File.WriteAllText(scriptPath, script);
            var rewritten = Path.Combine(Path.GetDirectoryName(path)!, "python_rewrite.json");

            var (exitCode, stdout, stderr) = RunPython(scriptPath, path, rewritten);

            Assert.True(exitCode == 0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            Assert.Equal(2, (int?)result["version"]);
            Assert.Equal("success", (string?)result["status"]);
            Assert.Equal(["demo", "qa_passed"], result["tags"]!.AsArray().Select(node => (string)node!).ToArray());
            Assert.Equal("alice", (string?)result["metadata"]!["reviewer"]);
            Assert.Equal(1, (int?)result["sample_ids"]![0]);
            Assert.Equal("s2", (string?)result["sample_ids"]![1]);
            Assert.Equal("C", (string?)result["scores"]!["1"]!["match"]);
            Assert.Equal("nan", (string?)result["scores"]!["1"]!["unscored"]);
            Assert.Equal(log.Samples![0].Events.Select(e => e.Event).ToArray(), result["event_types"]!.AsArray().Select(node => (string)node!).ToArray());
            Assert.Equal(1.0, (double?)result["metrics"]!["accuracy"]);
            Assert.Equal("nan", (string?)result["metrics"]!["stderr"]);
            Assert.Equal("message", (string?)result["limit"]);
            Assert.Equal(1, (int?)result["reductions"]![0]);
            Assert.Equal(["Default", "main", "2", "helper", "SampleInitEvent", "anchor-1"], result["timelines"]![0]!.AsArray().Select(node => node!.ToString()).ToArray());
            Assert.Equal("{\"value\":\"C\",\"answer\":\"4\",\"history\":[]}", result["summary"]!.ToJsonString());

            // the log Python wrote back reads to the same log
            var roundTripped = EvalLogWriter.Read(rewritten);
            Assert.Equal(EvalLogWriter.Serialize(log), EvalLogWriter.Serialize(roundTripped));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void timeline_nodes_round_trip_and_default_to_events()
    {
        var timeline = SampleTimeline();
        var json = Json(timeline);

        var read = Deserialize<Timeline>(json);
        Assert.Equal(json, Json(read));
        var helper = Assert.IsType<TimelineSpan>(read.Root.Content[1]);
        Assert.Equal("helper", helper.Name);
        Assert.True(helper.Utility);
        Assert.Equal("anchor-1", Assert.Single(helper.Branches).BranchedFrom);
        Assert.Equal("ev-07", Assert.IsType<TimelineEvent>(helper.Content[1]).Event);
        Assert.Null(new TimelineEvent("missing").Resolve(AllEvents()));

        Assert.Equal("upper", Deserialize<TimelineSpan>("{\"type\": \"span\", \"id\": \"x\", \"name\": \"UPPER\"}").Name);
        Assert.Equal("ev-1", Assert.IsType<TimelineEvent>(Deserialize<TimelineNode>("{\"event\": \"ev-1\"}")).Event);
        Assert.Throws<JsonException>(() => Deserialize<TimelineNode>("{\"type\": \"lane\"}"));
        Assert.Throws<JsonException>(() => Deserialize<TimelineNode>("{\"type\": \"event\", \"event\": {\"event\": \"step\"}}"));
    }

    [Fact]
    public void sample_summary_thins_like_python()
    {
        // vectors from textwrap.shorten(text[:8192], width=1024, placeholder="...") via the inspect_ai venv
        Assert.Equal("short", LogThinning.ThinText("short"));
        Assert.Equal("...", LogThinning.ThinText(new string('x', 3000)));
        Assert.Equal("a b c d", LogThinning.ThinText("a b  c\n\td"));
        Assert.Equal("lead and trail", LogThinning.ThinText(" lead and trail "));
        var words = LogThinning.ThinText(string.Concat(Enumerable.Repeat("word ", 300)));
        Assert.Equal(1022, words.Length);
        Assert.EndsWith("word...", words);
        Assert.Equal(new string('w', 1020) + "...", LogThinning.ThinText(new string('w', 1020) + " tail"));
        Assert.Equal(new string('w', 1021) + "...", LogThinning.ThinText(new string('w', 1021) + " tail"));
        Assert.Equal("...", LogThinning.ThinText(new string('w', 1022) + " tail"));
        Assert.Equal(new string('w', 1024), LogThinning.ThinText(new string('w', 1024)));
        Assert.Equal("...", LogThinning.ThinText(new string('w', 1025)));
        Assert.Equal(new string('w', 1022) + " t", LogThinning.ThinText(new string('w', 1022) + " t"));
        Assert.Equal(new string('x', 5120) + "...\n(content truncated)", LogThinning.TruncateText(new string('x', 6000)));

        var metadata = LogThinning.ThinMetadata(new Dictionary<string, object?>
        {
            ["i"] = 1,
            ["f"] = 1.5,
            ["b"] = true,
            ["s"] = new string('x', 2000),
            ["small"] = new Dictionary<string, object?> { ["a"] = 1 },
            ["big"] = new Dictionary<string, object?> { ["k"] = new string('y', 2000) },
            ["list"] = Enumerable.Range(0, 600).Cast<object?>().ToList(),
            ["none"] = null,
            ["edge"] = new string('x', 1024),
        });
        Assert.Equal(1, metadata["i"]);
        Assert.Equal(1.5, metadata["f"]);
        Assert.Equal(true, metadata["b"]);
        Assert.Equal("...", metadata["s"]);
        Assert.Equal(1, Assert.IsType<Dictionary<string, object?>>(metadata["small"])["a"]);
        Assert.Equal(LogThinning.KeyRemoved, metadata["big"]);
        Assert.Equal(LogThinning.KeyRemoved, metadata["list"]);
        Assert.Null(metadata["none"]);
        Assert.True(metadata.ContainsKey("none"));
        Assert.Equal(new string('x', 1024), metadata["edge"]);

        var log = FullLog();
        var summary = log.Samples![0].Summary();
        Assert.Equal(1, summary.Id);
        Assert.Equal("What is 2+2?", summary.Input.Text);
        Assert.Equal(new Target("4"), summary.Target);
        Assert.Equal("4", summary.Scores!["match"].Answer);
        Assert.Empty(summary.Scores["match"].History);
        Assert.Null(summary.Scores["unscored"].Reason);
        Assert.Null(summary.Error);
        Assert.Equal(1, summary.Retries);
        Assert.True(summary.Completed);
        Assert.Equal(2, summary.MessageCount);
        Assert.Equal(3, summary.TurnCount);
        var keys = ParseLoose(Json(summary)).AsObject().Select(pair => pair.Key).ToArray();
        Assert.Equal(["id", "epoch", "input", "target", "metadata", "scores", "model_usage", "role_usage", "model_fallbacks", "started_at", "completed_at", "total_time", "working_time", "uuid", "retries", "completed", "message_count", "turn_count", "token_limit", "token_limit_type", "message_limit", "time_limit"], keys);

        var errored = log.Samples[1].Summary();
        Assert.Equal("sandbox exploded", errored.Error);
        Assert.Equal("message", errored.Limit);
        Assert.Equal("Message limit reached", errored.LimitReason);
        Assert.Null(errored.Retries);
        Assert.Equal(0, errored.MessageCount);
        var items = errored.Input.Messages![0].Content.Items!;
        Assert.Equal("look", Assert.IsType<ContentText>(items[0]).Text);
        Assert.Equal("(Image)", Assert.IsType<ContentText>(items[1]).Text);
        Assert.Equal(EvalLogWriter.Serialize(log), EvalLogWriter.Serialize(FullLog()));
    }

    [Theory]
    [InlineData("hello", "cbd8a7b341bd9b025b1e906a48ae1d19")]
    [InlineData("héllo ✓ 日本", "dc2a8bf12f24e0834b14de8c0680d0f6")]
    [InlineData("data:image/png;base64,AAAA", "eb386248aacb60628864ded1f85dd216")]
    [InlineData("", "00000000000000000000000000000000")]
    public void murmur_hash_matches_python_mm3_hash(string text, string expected)
    {
        Assert.Equal(expected, MurmurHash3.Hash(text));
        Assert.Equal("74e484d9fb2c68a3f256fe7de4c92ebc", MurmurHash3.Hash(new string('x', 101)));
    }

    [Fact]
    public void condense_sample_moves_long_text_and_images_to_attachments_and_resolve_restores_them()
    {
        var longText = new string('x', 150);
        var dataUri = "data:image/png;base64," + new string('A', 200);
        var call = ModelCall.Create(new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["content"] = longText }) });
        call.SetResponse(new JsonObject { ["text"] = longText }, 0.1);
        var sample = new EvalSample
        {
            Id = 1,
            Epoch = 1,
            Input = new ChatMessage[] { new ChatMessageUser(new Content[] { new ContentText(longText), new ContentImage(dataUri) }) { Id = "m1" } },
            Messages = [new ChatMessageUser(longText) { Id = "m2" }, new ChatMessageUser(new Content[] { new ContentImage(dataUri) }) { Id = "m3" }],
            Events =
            [
                new SampleInitEvent(new Sample(longText)) { Uuid = "e0", Timestamp = T0 },
                new InfoEvent("x", JsonValue.Create(longText)) { Uuid = "e1", Timestamp = T0 },
                new ModelEvent { Uuid = "e2", Timestamp = T0, Model = "m", Input = [new ChatMessageUser(longText) { Id = "m4" }], ToolChoice = ToolChoice.Auto, Config = new GenerateConfig(), Output = ModelOutput.FromContent("m", longText), Call = call },
                new ToolEvent("c", "f", new JsonObject { ["a"] = longText, ["short"] = "ok" }, longText) { Uuid = "e3", Timestamp = T0 },
                StateEvent.FromChanges(new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/x", ["value"] = longText })) with { Uuid = "e4", Timestamp = T0 },
            ],
            Metadata = new Dictionary<string, object?> { ["note"] = longText },
        };
        var hash = MurmurHash3.Hash(longText);
        var imageHash = MurmurHash3.Hash(dataUri);

        var condensed = LogAttachments.CondenseSample(sample);

        Assert.Equal(new[] { hash, imageHash }.Order(), condensed.Attachments.Keys.Order());
        Assert.Equal(longText, condensed.Messages[0].Content.Text);
        Assert.Equal("attachment://" + imageHash, Assert.IsType<ContentImage>(condensed.Messages[1].Content.Items![0]).Image);
        Assert.Equal(longText, Assert.IsType<ContentText>(condensed.Input.Messages![0].Content.Items![0]).Text);
        Assert.Equal("attachment://" + imageHash, Assert.IsType<ContentImage>(condensed.Input.Messages![0].Content.Items![1]).Image);
        Assert.Equal("attachment://" + hash, Assert.IsType<SampleInitEvent>(condensed.Events[0]).Sample.Input.Text);
        Assert.Equal("attachment://" + hash, (string?)Assert.IsType<InfoEvent>(condensed.Events[1]).Data);
        var model = Assert.IsType<ModelEvent>(condensed.Events[2]);
        Assert.Equal("attachment://" + hash, model.Input[0].Content.Text);
        Assert.Equal("attachment://" + hash, model.Output.Completion);
        Assert.Equal("attachment://" + hash, (string?)model.Call!.Request["messages"]![0]!["content"]);
        Assert.Equal("attachment://" + hash, (string?)model.Call.Response!["text"]);
        var tool = Assert.IsType<ToolEvent>(condensed.Events[3]);
        Assert.Equal("attachment://" + hash, (string?)tool.Arguments["a"]);
        Assert.Equal("ok", (string?)tool.Arguments["short"]);
        Assert.Equal(longText, tool.Result);
        Assert.Equal("attachment://" + hash, Assert.IsType<StateEvent>(condensed.Events[4]).Changes[0].GetProperty("value").GetString());
        Assert.Equal(longText, condensed.Metadata["note"]);
        Assert.Equal(EvalLogWriter.Serialize(new EvalLog { Eval = FullLog().Eval, Samples = [condensed] }), EvalLogWriter.Serialize(new EvalLog { Eval = FullLog().Eval, Samples = [LogAttachments.CondenseSample(condensed)] }));

        var full = LogAttachments.ResolveSampleAttachments(condensed, ResolveAttachments.Full);
        Assert.Equal(Json(sample), Json(full));

        var core = LogAttachments.ResolveSampleAttachments(condensed);
        Assert.Equal([hash], core.Attachments.Keys);
        Assert.Equal("attachment://" + hash, (string?)Assert.IsType<ModelEvent>(core.Events[2]).Call!.Request["messages"]![0]!["content"]);
        Assert.Equal(longText, Assert.IsType<ModelEvent>(core.Events[2]).Input[0].Content.Text);
        Assert.Equal(longText, Assert.IsType<InfoEvent>(core.Events[1]).Data!.GetValue<string>());

        var stripped = LogAttachments.CondenseSample(sample, logImages: false);
        Assert.Equal([hash], stripped.Attachments.Keys);
        Assert.Equal(LogAttachments.Base64DataRemoved, Assert.IsType<ContentImage>(stripped.Messages[1].Content.Items![0]).Image);

        Assert.True(LogAttachments.IsDataUri("data:;base64,AAAA"));
        Assert.False(LogAttachments.IsDataUri("data:text/plain,hello"));
    }

    [Theory]
    [InlineData(ResolveAttachments.Core)]
    [InlineData(ResolveAttachments.Full)]
    public void document_and_server_tool_attachments_resolve_and_round_trip(ResolveAttachments mode)
    {
        var attachments = new Dictionary<string, string>();
        string Reference(string value)
        {
            var hash = MurmurHash3.Hash(value);
            attachments[hash] = value;
            return "attachment://" + hash;
        }

        var document = new ContentDocument("data:application/pdf;base64," + new string('A', 200), "report.pdf", "application/pdf") { Citations = true };
        var tool = new ContentToolUse("web_search", "srv1", "web_search", Json(new { query = new string('q', 150) }), Json(new[] { new { title = new string('r', 150) } }))
        { Context = "search", Error = new string('e', 150) };
        var referencedTool = tool with { Arguments = Reference(tool.Arguments), Result = Reference(tool.Result), Error = Reference(tool.Error) };
        var messages = new ChatMessage[] { new ChatMessageAssistant(new Content[] { referencedTool }) };
        var sample = new EvalSample
        {
            Id = 1, Epoch = 1,
            Input = new ChatMessage[] { new ChatMessageUser(new Content[] { document with { Document = Reference(document.Document) } }) },
            Messages = messages,
            Events = [new ModelEvent { Model = "m", Input = messages, ToolChoice = ToolChoice.Auto, Config = new GenerateConfig(), Output = new ModelOutput { Choices = [new ChatCompletionChoice((ChatMessageAssistant)messages[0], StopReason.Stop)] } }],
            Attachments = attachments,
        };

        var resolved = LogAttachments.ResolveSampleAttachments(sample, mode);

        Assert.Equal(document, Assert.IsType<ContentDocument>(resolved.Input.Messages![0].ContentList[0]));
        Assert.Equal(tool, Assert.IsType<ContentToolUse>(resolved.Messages[0].ContentList[0]));
        var modelEvent = Assert.IsType<ModelEvent>(resolved.Events[0]);
        Assert.Equal(tool, Assert.IsType<ContentToolUse>(modelEvent.Input[0].ContentList[0]));
        Assert.Equal(tool, Assert.IsType<ContentToolUse>(modelEvent.Output.Message.ContentList[0]));
        Assert.Empty(resolved.Attachments);
        Assert.Empty(LogAttachments.AttachmentRefs(Json(resolved)));

        var condensed = LogAttachments.CondenseSample(resolved);
        Assert.Equal(4, condensed.Attachments.Count);
        Assert.Equal(Json(resolved), Json(LogAttachments.ResolveSampleAttachments(condensed, mode)));
        var stripped = LogAttachments.CondenseSample(resolved, logImages: false);
        Assert.Equal(LogAttachments.Base64DataRemoved, Assert.IsType<ContentDocument>(stripped.Input.Messages![0].ContentList[0]).Document);
    }

    [Fact]
    public void python_image_log_attachments_resolve_and_hash_like_python()
    {
        var log = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_images.json")));
        var sample = log.Samples![0];
        Assert.Equal(2, sample.Attachments.Count);
        var image = Assert.IsType<ContentImage>(Assert.IsType<ChatMessageUser>(sample.Input.Messages![0]).Content.Items![1]);
        Assert.Equal("attachment://734b3dc396aa9924b5f2a48de42561d2", image.Image);
        var callRefs = LogAttachments.AttachmentRefs(Json(sample.Events.OfType<ModelEvent>().Single().Call));

        var resolved = LogAttachments.ResolveSampleAttachments(sample, ResolveAttachments.Full);
        var resolvedImage = Assert.IsType<ContentImage>(Assert.IsType<ChatMessageUser>(resolved.Input.Messages![0]).Content.Items![1]).Image;
        Assert.StartsWith("data:image/", resolvedImage);
        Assert.Equal("734b3dc396aa9924b5f2a48de42561d2", MurmurHash3.Hash(resolvedImage));
        Assert.Empty(resolved.Attachments);
        Assert.Empty(LogAttachments.AttachmentRefs(Json(resolved)));

        var core = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_images.json")), ResolveAttachments.Core).Samples![0];
        Assert.StartsWith("data:image/", Assert.IsType<ContentImage>(Assert.IsType<ChatMessageUser>(core.Input.Messages![0]).Content.Items![1]).Image);
        Assert.Equal(callRefs.Order(), core.Attachments.Keys.Order());
        Assert.Equal(callRefs.Order(), LogAttachments.AttachmentRefs(Json(core.Events.OfType<ModelEvent>().Single().Call)).Order());
    }

    [Fact]
    public void legacy_python_logs_read_with_migrations()
    {
        var nan = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_with_nan.txt")));
        Assert.Equal(2, nan.Version);
        var score = Assert.Single(nan.Results!.Scores);
        Assert.Equal("model_graded_fact", score.Name);
        Assert.Equal("model_graded_fact", score.Scorer);
        Assert.True(double.IsNaN(score.Metrics["accuracy"].Value));
        Assert.Equal(0.0, score.Metrics["bootstrap_std"].Value);
        Assert.Equal(new DateTimeOffset(2024, 5, 5, 7, 59, 35, TimeSpan.Zero), nan.Eval.Created);
        Assert.Equal(20, nan.Eval.Config.Limit);
        Assert.Null(nan.Samples);
        Assert.NotEmpty(nan.Eval.EvalId);

        var header = EvalLogWriter.ReadHeader(FixturePath(Path.Combine("python", "log_valid.txt")));
        Assert.Equal(42, header.Eval.Metadata!["meaning_of_life"]);
        Assert.Null(header.Samples);

        Assert.Throws<InvalidDataException>(() => EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_version_3.txt"))));
        Assert.ThrowsAny<JsonException>(() => EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_invalid.txt"))));

        var length = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_length_stop_reason.txt")));
        Assert.Equal(StopReason.MaxTokens, length.Samples![0].Output.StopReason);
        Assert.Contains(length.Samples[0].Events.OfType<ModelEvent>(), e => e.Output.Choices.Count > 0 && e.Output.StopReason == StopReason.MaxTokens);

        var formats = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_formats.json")));
        var sample = formats.Samples![0];
        Assert.Equal(" Yes", sample.Target.Text);
        Assert.Equal(11, sample.Events.Count);
        Assert.IsType<SampleInitEvent>(sample.Events[0]);
        Assert.Equal(2, sample.Attachments.Count);
        Assert.Equal(new ScoreValue.Str("I"), sample.Scores!["match"].Value);
        var reductions = Assert.Single(formats.Reductions!);
        Assert.Equal("match", reductions.Scorer);
        Assert.Equal(1, reductions.Samples[0].SampleId);
        Assert.Equal("No", reductions.Samples[0].Score.Answer);
        Assert.Equal("match", Assert.Single(formats.Results!.Scores).Name);

        var errors = EvalLogWriter.Read(FixturePath(Path.Combine("python", "log_tool_call_error.json")));
        Assert.Equal(
            ["parsing", "timeout", "parsing", "unicode_decode", "permission", "file_not_found", "is_a_directory", "limit", "approval", "unknown", "output_limit"],
            errors.Samples![0].Messages.OfType<ChatMessageTool>().Select(message => message.Error!.Type).ToArray());

        // a single legacy `score` is filed under the first scorer's name; old and new forms together are rejected
        var legacy = EvalLogWriter.Serialize(new EvalLog { Eval = FullLog().Eval, Results = new EvalResults { Scores = [new EvalScore("grader", "grader")] } })
            .Replace("\"results\":", "\"samples\": [{\"id\": 1, \"epoch\": 1, \"input\": \"q\", \"target\": \"a\", \"score\": {\"value\": \"C\"}}], \"results\":");
        Assert.Equal(new ScoreValue.Str("C"), EvalLogWriter.Deserialize(legacy).Samples![0].Scores!["grader"].Value);
        Assert.Throws<JsonException>(() => EvalLogWriter.Deserialize(legacy.Replace("\"score\":", "\"scores\": {}, \"score\":")));
        var maxMessages = JsonSerializer.Deserialize<EvalConfig>("{\"max_messages\": 7}", EvalLogWriter.Options)!;
        Assert.Equal(7, maxMessages.MessageLimit);
    }

    [Fact]
    public void model_call_pool_refs_round_trip()
    {
        var call = ModelCall.Create(new JsonObject { ["model"] = "m" });
        call.SetResponse(new JsonObject { ["id"] = "r" }, 0.2);
        call.CallRefs = [new CallRef(0, 2), new CallRef(5, 6)];
        call.CallKey = "k1";

        var json = Json(call);
        Assert.Equal("{\"request\":{\"model\":\"m\"},\"response\":{\"id\":\"r\"},\"time\":0.2,\"call_refs\":[[0,2],[5,6]],\"call_key\":\"k1\"}", json.Replace(" ", "").Replace("\n", ""));
        var read = Deserialize<ModelCall>(json);
        Assert.Equal(call.CallRefs, read.CallRefs);
        Assert.Equal("k1", read.CallKey);
        Assert.Null(Deserialize<ModelCall>("{\"request\": {}}").CallRefs);
    }

    [Fact]
    public void null_metadata_values_survive_edits_and_round_trips()
    {
        var log = new EvalLog { Eval = new EvalSpec { Task = "t", Model = "m", Dataset = new EvalDataset() }, Samples = [new EvalSample { Id = 1, Epoch = 1, Input = "q", Metadata = new Dictionary<string, object?> { ["n"] = null } }] };
        var provenance = new ProvenanceData("alice");

        var edited = EvalLogEditing.EditEvalLog(log, [new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["missing"] = null } }], provenance);
        Assert.True(edited.Metadata.ContainsKey("missing"));
        Assert.Null(edited.Metadata["missing"]);
        Assert.Same(edited, EvalLogEditing.EditEvalLog(edited, [new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["missing"] = null } }], provenance));

        var read = EvalLogWriter.Deserialize(EvalLogWriter.Serialize(edited));
        Assert.True(read.Metadata.ContainsKey("missing"));
        Assert.Null(read.Metadata["missing"]);
        Assert.Null(Assert.IsType<MetadataEdit>(Assert.Single(read.LogUpdates!).Edits[0]).MetadataSet["missing"]);
        Assert.True(read.Samples![0].Metadata.ContainsKey("n"));
        Assert.Contains("\"n\": null", EvalLogWriter.Serialize(edited));
    }

    [Fact]
    public void read_header_drops_samples_and_reductions()
    {
        var path = TempPath("header.json");
        try
        {
            EvalLogWriter.Write(FullLog(), path);

            var header = EvalLogWriter.ReadHeader(path);
            Assert.Null(header.Samples);
            Assert.Null(header.Reductions);
            Assert.Equal("eval-1", header.Eval.EvalId);
            Assert.Equal(2, header.Results!.TotalSamples);
            Assert.Equal(["demo", "qa_passed"], header.Tags);
            Assert.Equal(path, header.Location);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPython(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("INSPECT_PY")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
