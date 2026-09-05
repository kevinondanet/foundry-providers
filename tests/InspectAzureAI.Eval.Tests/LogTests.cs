using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The JSON eval log: snake_case field names, polymorphic messages/content/events/scores and write → read round trips.</summary>
public class LogTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);

    private static ToolInfo BashTool() => new("bash", "Use this function to execute bash commands.")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["cmd"] = new() { Type = ["string"], Description = "The bash command to execute." } },
            Required = ["cmd"],
        },
    };

    private static EvalLog SampleLog()
    {
        var toolCall = new ToolCall("call_1", "bash", new JsonObject { ["cmd"] = "ls" });
        var assistant = new ChatMessageAssistant(
            new Content[] { new ContentReasoning("let me look", "sig", false), new ContentText("Listing files.") },
            toolCalls: [toolCall],
            model: "gpt",
            source: "generate");
        var final = new ChatMessageAssistant("The answer is 4.", model: "gpt", source: "generate");
        var output = new ModelOutput
        {
            Model = "gpt",
            Choices = [new ChatCompletionChoice(final, StopReason.Stop)],
            Usage = new ModelUsage(10, 5, 15) { InputTokensCacheRead = 2 },
            Time = 0.5,
        };
        var call = ModelCall.Create(new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) });
        call.SetResponse(new JsonObject { ["id"] = "resp_1", ["usage"] = new JsonObject { ["total_tokens"] = 15 } }, 0.5);

        var sample1 = new EvalSample
        {
            Id = 1,
            Epoch = 1,
            Input = "What is 2+2?",
            Target = "4",
            Sandbox = new SandboxSpec("docker", "Dockerfile"),
            Files = ["hello.txt"],
            Setup = "echo hi",
            Messages =
            [
                new ChatMessageSystem("Be helpful.") { Source = "input" },
                new ChatMessageUser("What is 2+2?") { Source = "input" },
                assistant,
                new ChatMessageTool("bash: not found", toolCallId: "call_1", function: "bash", error: new ToolCallError("unknown", "bash: not found")),
                final,
            ],
            Output = output,
            Scores = new Dictionary<string, Score>
            {
                ["match"] = new Score("C") { Answer = "4", Explanation = "matched", Metadata = new Dictionary<string, object?> { ["strict"] = true } },
                ["num"] = new Score(0.5),
                ["flag"] = new Score(true),
                ["list"] = new Score(new ScoreValue.List([1, "a", false])),
                ["dict"] = new Score(new ScoreValue.Dict(new Dictionary<string, ScoreValue?> { ["a"] = 1, ["b"] = null, ["c"] = "x" })),
                ["unscored"] = Score.Unscored("no answer"),
            },
            Metadata = new Dictionary<string, object?> { ["check"] = "python3 -c 'print(4)'", ["difficulty"] = 1, ["tags"] = new List<object?> { "a", 2 } },
            Store = new Dictionary<string, object?> { ["mini_swe_agent_exit_status"] = "Submitted", ["steps"] = 3, ["nested"] = new Dictionary<string, object?> { ["k"] = null } },
            Events =
            [
                new SpanBeginEvent("span1", "solver", "solver") { Timestamp = Created },
                new StepEvent("generate", "solver", "begin") { Timestamp = Created, SpanId = "span1" },
                new ModelEvent
                {
                    Timestamp = Created,
                    SpanId = "span1",
                    Model = "gpt",
                    Input = [new ChatMessageUser("What is 2+2?") { Source = "input" }],
                    Tools = [BashTool()],
                    ToolChoice = ToolChoice.Auto,
                    Config = new GenerateConfig { MaxTokens = 100, Temperature = 0.2 },
                    Output = output,
                    Call = call,
                    Retries = 0,
                    Completed = Created.AddSeconds(1),
                    WorkingTime = 0.5,
                },
                new ModelEvent
                {
                    Timestamp = Created,
                    Model = "gpt",
                    Input = [],
                    ToolChoice = new ToolFunction("bash"),
                    Config = new GenerateConfig(),
                    Output = new ModelOutput(),
                    Error = "rate limited",
                    Retries = 1,
                },
                new ToolEvent("call_1", "bash", new JsonObject { ["cmd"] = "ls" }, "a.txt\nb.txt", new ToolCallError("timeout", "Command timed out before completing."), new ToolTruncation(20000, 16384), TimeSpan.FromSeconds(1.25)) { Timestamp = Created, SpanId = "span1" },
                new SandboxEvent("exec", new JsonObject { ["cmd"] = new JsonArray("ls") }, new JsonObject { ["returncode"] = 0 }) { Timestamp = Created },
                new ScoreEvent(new Score("C") { Answer = "4" }, new Target("4"), Intermediate: true) { Timestamp = Created },
                new InfoEvent("claude_code", new JsonObject { ["type"] = "system", ["n"] = 1 }) { Timestamp = Created },
                new InfoEvent("note", null) { Timestamp = Created },
                new ErrorEvent("boom", "Traceback...") { Timestamp = Created },
                new StepEvent("generate", "solver", "end") { Timestamp = Created, SpanId = "span1" },
                new SpanEndEvent("span1") { Timestamp = Created },
            ],
            ModelUsage = new Dictionary<string, ModelUsage> { ["gpt"] = new ModelUsage(10, 5, 15) { InputTokensCacheRead = 2 } },
            StartedAt = Created,
            CompletedAt = Created.AddSeconds(2),
            TotalTime = 2.0,
            WorkingTime = 1.5,
            Uuid = "uuid-1",
        };

        var sample2 = new EvalSample
        {
            Id = "s2",
            Epoch = 2,
            Input = new ChatMessage[] { new ChatMessageSystem("sys") { Source = "input" }, new ChatMessageUser(new Content[] { new ContentText("look"), new ContentImage("data:image/png;base64,AAAA", "high") }) { Source = "input" } },
            Target = new Target(["a", "b"]),
            Choices = ["a", "b"],
            Messages = [new ChatMessageUser("look")],
            Error = new EvalError("sandbox exploded", "trace"),
            Limit = new EvalSampleLimit("message", 10, "Message limit reached"),
        };

        return new EvalLog
        {
            Status = EvalStatus.Success,
            Eval = new EvalSpec
            {
                RunId = "run1",
                Created = Created,
                Task = "hello-swe",
                TaskId = "task1",
                TaskVersion = "0",
                Dataset = new EvalDataset { Name = "hello", Location = "/tasks/hello/dataset.json", Samples = 2, SampleIds = [1, "s2"], Shuffled = false },
                Sandbox = new SandboxSpec("docker", "Dockerfile"),
                Model = "gpt",
                Config = new EvalConfig { Limit = 2, Epochs = 2, MaxSamples = 4, MessageLimit = 10, TokenLimit = 1000, TimeLimit = 300, FailOnError = false, SandboxCleanup = true },
            },
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
                        Metrics = new Dictionary<string, EvalMetric> { ["accuracy"] = new("accuracy", 1.0), ["stderr"] = new("stderr", double.NaN) },
                    },
                ],
            },
            Stats = new EvalStats { StartedAt = Created, CompletedAt = Created.AddSeconds(5), ModelUsage = new Dictionary<string, ModelUsage> { ["gpt"] = new ModelUsage(10, 5, 15) } },
            Samples = [sample1, sample2],
        };
    }

    private static string TempLogPath() =>
        Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"), "logs", "2026-09-03T10-30-00_hello-swe_abc123.json");

    [Fact]
    public void log_round_trips_through_write_and_read()
    {
        var log = SampleLog();
        var path = TempLogPath();
        try
        {
            EvalLogWriter.Write(log, path);
            var read = EvalLogWriter.Read(path);

            Assert.Equal(EvalLogWriter.Serialize(log), EvalLogWriter.Serialize(read));
            Assert.Equal(EvalStatus.Success, read.Status);
            Assert.Equal(1, read.Version);
            Assert.Equal("hello-swe", read.Eval.Task);
            Assert.Equal(Created, read.Eval.Created);
            Assert.Equal(new SandboxSpec("docker", "Dockerfile"), read.Eval.Sandbox);
            Assert.Equal([1, "s2"], read.Eval.Dataset.SampleIds);
            Assert.Equal(300, read.Eval.Config.TimeLimit);
            Assert.Equal(new ModelUsage(10, 5, 15), read.Stats.ModelUsage["gpt"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void sample_messages_output_scores_and_store_read_back_typed()
    {
        var read = EvalLogWriter.Deserialize(EvalLogWriter.Serialize(SampleLog()));

        var sample = read.Samples![0];
        Assert.Equal(1, sample.Id);
        Assert.Equal("What is 2+2?", sample.Input.Text);
        Assert.Equal(new Target("4"), sample.Target);
        Assert.Equal(["hello.txt"], sample.Files);
        Assert.Equal(5, sample.Messages.Count);
        Assert.IsType<ChatMessageSystem>(sample.Messages[0]);
        Assert.Equal("input", sample.Messages[1].Source);

        var assistant = Assert.IsType<ChatMessageAssistant>(sample.Messages[2]);
        Assert.Equal("gpt", assistant.Model);
        var reasoning = Assert.IsType<ContentReasoning>(assistant.Content.Items![0]);
        Assert.Equal("let me look", reasoning.Reasoning);
        Assert.Equal("sig", reasoning.Signature);
        Assert.Equal("Listing files.", assistant.Text);
        var call = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("ls", (string?)call.Arguments["cmd"]);
        Assert.Equal("function", call.Type);

        var tool = Assert.IsType<ChatMessageTool>(sample.Messages[3]);
        Assert.Equal(new ToolCallError("unknown", "bash: not found"), tool.Error);
        Assert.Equal("call_1", tool.ToolCallId);

        Assert.Equal("The answer is 4.", sample.Output.Completion);
        Assert.Equal(StopReason.Stop, sample.Output.StopReason);
        Assert.Equal(new ModelUsage(10, 5, 15) { InputTokensCacheRead = 2 }, sample.Output.Usage);

        var scores = sample.Scores!;
        Assert.Equal(new ScoreValue.Str("C"), scores["match"].Value);
        Assert.Equal("4", scores["match"].Answer);
        Assert.Equal(true, scores["match"].Metadata!["strict"]);
        Assert.Equal(new ScoreValue.Num(0.5), scores["num"].Value);
        Assert.Equal(new ScoreValue.Bool(true), scores["flag"].Value);
        Assert.Equal(new ScoreValue.List([1, "a", false]), scores["list"].Value);
        Assert.Equal(new ScoreValue.Dict(new Dictionary<string, ScoreValue?> { ["a"] = 1, ["b"] = null, ["c"] = "x" }), scores["dict"].Value);
        Assert.True(scores["unscored"].IsUnscored);
        Assert.Equal("no answer", scores["unscored"].Reason);

        Assert.Equal(1, sample.Metadata["difficulty"]);
        Assert.Equal(new List<object?> { "a", 2 }, sample.Metadata["tags"]);
        Assert.Equal("Submitted", sample.Store["mini_swe_agent_exit_status"]);
        Assert.Equal(3, sample.Store["steps"]);
        Assert.Null(Assert.IsType<Dictionary<string, object?>>(sample.Store["nested"])["k"]);
        Assert.Equal(2, sample.ModelUsage["gpt"].InputTokensCacheRead);
        Assert.Equal(2.0, sample.TotalTime);
        Assert.Equal("uuid-1", sample.Uuid);
        Assert.Null(sample.Error);

        var errored = read.Samples[1];
        Assert.Equal("s2", errored.Id);
        Assert.Equal(2, errored.Epoch);
        Assert.False(errored.Input.IsText);
        var image = Assert.IsType<ContentImage>(Assert.IsType<ChatMessageUser>(errored.Input.Messages![1]).Content.Items![1]);
        Assert.Equal("high", image.Detail);
        Assert.Equal(new Target(["a", "b"]), errored.Target);
        Assert.Equal("sandbox exploded", errored.Error!.Message);
        Assert.Equal(new EvalSampleLimit("message", 10, "Message limit reached"), errored.Limit);
        Assert.True(errored.Output.Empty);
        Assert.Null(errored.Scores);
    }

    [Fact]
    public void transcript_events_round_trip_with_python_discriminators()
    {
        var read = EvalLogWriter.Deserialize(EvalLogWriter.Serialize(SampleLog()));

        var events = read.Samples![0].Events;
        Assert.Equal(
            ["span_begin", "step", "model", "model", "tool", "sandbox", "score", "info", "info", "error", "step", "span_end"],
            events.Select(e => e.Event).ToArray());
        Assert.All(events, e => Assert.Equal(Created, e.Timestamp));
        Assert.Equal("span1", events[1].SpanId);
        Assert.Null(events[0].SpanId);

        var model = Assert.IsType<ModelEvent>(events[2]);
        Assert.Equal("gpt", model.Model);
        Assert.Equal("bash", Assert.Single(model.Tools).Name);
        Assert.Equal(["cmd"], model.Tools[0].Parameters.Required);
        Assert.Equal(["string"], model.Tools[0].Parameters.Properties["cmd"].Type);
        Assert.Same(ToolChoice.Auto, model.ToolChoice);
        Assert.Equal(100, model.Config.MaxTokens);
        Assert.Equal("The answer is 4.", model.Output.Completion);
        Assert.Equal("resp_1", (string?)model.Call!.Response!["id"]);
        Assert.Equal(0.5, model.Call.Time);
        Assert.Equal(Created.AddSeconds(1), model.Completed);

        var failed = Assert.IsType<ModelEvent>(events[3]);
        Assert.Equal(new ToolFunction("bash"), failed.ToolChoice);
        Assert.Equal("rate limited", failed.Error);
        Assert.True(failed.Output.Empty);

        var tool = Assert.IsType<ToolEvent>(events[4]);
        Assert.Equal("ls", (string?)tool.Arguments["cmd"]);
        Assert.Equal("timeout", tool.Error!.Type);
        Assert.Equal(new ToolTruncation(20000, 16384), tool.Truncated);
        Assert.Equal(TimeSpan.FromSeconds(1.25), tool.Working);

        var sandbox = Assert.IsType<SandboxEvent>(events[5]);
        Assert.Equal(0, (int?)sandbox.Result!["returncode"]);
        var score = Assert.IsType<ScoreEvent>(events[6]);
        Assert.True(score.Intermediate);
        Assert.Equal(new Target("4"), score.Target);
        Assert.Equal("system", (string?)Assert.IsType<InfoEvent>(events[7]).Data!["type"]);
        Assert.Null(Assert.IsType<InfoEvent>(events[8]).Data);
        Assert.Equal("Traceback...", Assert.IsType<ErrorEvent>(events[9]).Traceback);
        Assert.Equal("begin", Assert.IsType<StepEvent>(events[1]).Action);
        Assert.Equal("span1", Assert.IsType<SpanEndEvent>(events[11]).Id);
    }

    [Fact]
    public void json_uses_snake_case_python_shapes_and_null_for_nan()
    {
        var json = EvalLogWriter.Serialize(SampleLog());
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("success", (string?)root["status"]);
        Assert.Equal(2, (int?)root["results"]!["total_samples"]);
        Assert.Equal("hello-swe", (string?)root["eval"]!["task"]);
        Assert.Equal("0", (string?)root["eval"]!["task_version"]);
        Assert.Equal(10, (int?)root["eval"]!["config"]!["message_limit"]);
        Assert.Equal(15, (int?)root["stats"]!["model_usage"]!["gpt"]!["total_tokens"]);

        var metrics = root["results"]!["scores"]![0]!["metrics"]!.AsObject();
        Assert.Equal(1.0, (double?)metrics["accuracy"]!["value"]);
        Assert.True(metrics.ContainsKey("stderr"));
        Assert.Null(metrics["stderr"]!["value"]);

        var sample = root["samples"]![0]!.AsObject();
        Assert.Equal("user", (string?)sample["messages"]![1]!["role"]);
        Assert.Equal("reasoning", (string?)sample["messages"]![2]!["content"]![0]!["type"]);
        Assert.Equal("text", (string?)sample["messages"]![2]!["content"]![1]!["type"]);
        Assert.Equal("call_1", (string?)sample["messages"]![2]!["tool_calls"]![0]!["id"]);
        Assert.Equal("stop", (string?)sample["output"]!["stop_reason"] ?? (string?)sample["output"]!["choices"]![0]!["stop_reason"]);
        Assert.Null(sample["scores"]!["unscored"]!["value"]);
        Assert.True(sample["scores"]!.AsObject().ContainsKey("unscored"));
        Assert.Equal("tool", (string?)sample["events"]![4]!["event"]);
        Assert.Equal("span1", (string?)sample["events"]![1]!["span_id"]);
        Assert.Equal(1.25, (double?)sample["events"]![4]!["working_time"]);
        Assert.Equal("boom", (string?)sample["events"]![9]!["error"]!["message"]);
        Assert.DoesNotContain("\"Completion\"", json);
        Assert.DoesNotContain("\"is_unscored\"", json);

        var read = EvalLogWriter.Deserialize(json);
        Assert.True(double.IsNaN(read.Results!.Scores[0].Metrics["stderr"].Value));
    }

    [Fact]
    public void empty_output_and_explicit_completion_serialize()
    {
        var empty = JsonSerializer.Serialize(new ModelOutput(), EvalLogWriter.Options);
        var explicitCompletion = ModelOutput.FromContent("m", "raw text") with { Completion = "submitted" };

        var read = JsonSerializer.Deserialize<ModelOutput>(JsonSerializer.Serialize(explicitCompletion, EvalLogWriter.Options), EvalLogWriter.Options)!;

        Assert.Contains("\"choices\": []", empty);
        Assert.Equal("submitted", read.Completion);
        Assert.Equal("raw text", read.Message.Text);
    }

    [Fact]
    public void unknown_event_role_or_content_type_is_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptEvent>("{\"event\":\"teleport\"}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatMessage>("{\"role\":\"bot\",\"content\":\"x\"}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Content>("{\"type\":\"hologram\"}", EvalLogWriter.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatMessageAssistant>("{\"role\":\"user\",\"content\":\"x\"}", EvalLogWriter.Options));
    }

    [Fact]
    public void write_creates_missing_directories()
    {
        var path = TempLogPath();
        try
        {
            EvalLogWriter.Write(new EvalLog { Eval = new EvalSpec { Task = "t", Model = "m", Dataset = new EvalDataset() } }, path);

            Assert.True(File.Exists(path));
            var read = EvalLogWriter.Read(path);
            Assert.Equal(EvalStatus.Started, read.Status);
            Assert.Equal("t", read.Eval.Task);
            Assert.Null(read.Samples);
            Assert.Null(read.Results);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
