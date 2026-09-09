using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The line framing of the Copilot CLI's JSONL stdout (a copy of the Claude Code stream).</summary>
public class CopilotCliStreamTests
{
    [Fact]
    public void complete_lines_are_parsed_as_they_arrive_across_chunks()
    {
        var stream = new CopilotCliStream();

        var first = stream.PushStdout("{\"a\":1}\n{\"b\"");
        var second = stream.PushStdout(":2}\n\n   \n");
        var done = stream.Complete(0);

        var a = Assert.IsType<CopilotCliStreamEvent.Jsonl>(Assert.Single(first));
        Assert.Equal(1, a.Raw["a"]!.GetValue<int>());
        Assert.Equal("{\"a\":1}", a.Line);
        Assert.Equal("{\"b\":2}", Assert.IsType<CopilotCliStreamEvent.Jsonl>(Assert.Single(second)).Line);
        Assert.Equal(0, Assert.IsType<CopilotCliStreamEvent.Exit>(Assert.Single(done)).Code);
    }

    [Fact]
    public void trailing_partial_line_and_parse_errors_are_surfaced()
    {
        var stream = new CopilotCliStream();

        var events = stream.PushStdout("not json\r\n  {\"ok\":1}  \nnull\n");
        Assert.Empty(stream.PushStdout("{\"type\":\"result\"}"));
        var done = stream.Complete(3);

        Assert.Equal(3, events.Count);
        Assert.Equal("not json", Assert.IsType<CopilotCliStreamEvent.ParseError>(events[0]).Line);
        Assert.Equal("{\"ok\":1}", Assert.IsType<CopilotCliStreamEvent.Jsonl>(events[1]).Line);
        Assert.Equal("null", Assert.IsType<CopilotCliStreamEvent.ParseError>(events[2]).Line);
        Assert.Equal(2, done.Count);
        Assert.Equal("result", Assert.IsType<CopilotCliStreamEvent.Jsonl>(done[0]).Raw["type"]!.GetValue<string>());
        Assert.Equal(3, Assert.IsType<CopilotCliStreamEvent.Exit>(done[1]).Code);
    }

    [Fact]
    public void parse_frames_a_finished_process_with_stderr_and_exit()
    {
        var events = CopilotCliStream.Parse(new ExecResult(false, 1, "{\"a\":1}\n{\"b\":2}", "boom"));

        Assert.Equal(4, events.Count);
        Assert.Equal("{\"a\":1}", Assert.IsType<CopilotCliStreamEvent.Jsonl>(events[0]).Line);
        Assert.Equal("boom", Assert.IsType<CopilotCliStreamEvent.Stderr>(events[1]).Data);
        Assert.Equal("{\"b\":2}", Assert.IsType<CopilotCliStreamEvent.Jsonl>(events[2]).Line);
        Assert.Equal(1, Assert.IsType<CopilotCliStreamEvent.Exit>(events[3]).Code);
        Assert.Equal(0, Assert.IsType<CopilotCliStreamEvent.Exit>(Assert.Single(CopilotCliStream.Parse(new ExecResult(true, 0, "", "")))).Code);
    }
}

/// <summary>Folding both JSONL shapes the 1.0.83 probe and Harbor's parser document into transcript events.</summary>
public class CopilotCliEventsTests
{
    private static readonly string[] SessionShape =
    [
        """{"type":"session.skills_loaded","data":{"skills":[]},"ephemeral":true,"id":"1","timestamp":"t","parentId":null}""",
        """{"type":"user.message","data":{"content":"run echo hi"},"id":"2","timestamp":"t","parentId":"1"}""",
        """{"type":"assistant.message","data":{"messageId":"m1","model":"inspect","content":"","toolRequests":[{"toolCallId":"call_1","name":"bash","arguments":{"command":"echo hi","description":"probe"},"type":"function"}]},"id":"3","timestamp":"t","parentId":"2"}""",
        """{"type":"tool.execution_start","data":{"toolCallId":"call_1","toolName":"bash","arguments":{"command":"echo hi","description":"probe"},"turnId":"0","model":"inspect"},"id":"4","timestamp":"t","parentId":"3"}""",
        """{"type":"tool.execution_partial_result","data":{"toolCallId":"call_1","partialOutput":"hi\n"},"ephemeral":true,"id":"5","timestamp":"t","parentId":"4"}""",
        """{"type":"tool.execution_complete","data":{"toolCallId":"call_1","model":"inspect","success":true,"result":{"content":"hi\n<shellId: 0 completed with exit code 0>","detailedContent":"hi\n"}},"id":"6","timestamp":"t","parentId":"4"}""",
        """{"type":"assistant.message","data":{"messageId":"m2","model":"inspect","content":"PROBE DONE","toolRequests":[]},"id":"7","timestamp":"t","parentId":"6"}""",
        """{"type":"result","timestamp":"t","sessionId":"e2f41e59-2d56-49b0-ab78-462b6b3b88d9","exitCode":0,"usage":{"premiumRequests":0,"totalApiDurationMs":25,"sessionDurationMs":1798,"codeChanges":{"linesAdded":0,"linesRemoved":0,"filesModified":[]}}}""",
    ];

    private static readonly string[] FlatShape =
    [
        """{"type":"message","role":"assistant","content":"Let me look.","model":"inspect"}""",
        """{"type":"tool_use","name":"bash","input":{"command":"ls"},"id":"toolu_1","model":"inspect"}""",
        """{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"a.py"},{"type":"text","text":"b.py"}]}""",
        """{"type":"tool_use","name":"view","input":{"path":"a.py"},"id":"toolu_2","model":"inspect"}""",
        """{"type":"tool_result","tool_use_id":"toolu_2","content":"no such file","is_error":true}""",
        """{"type":"usage","input_tokens":10,"output_tokens":5}""",
        """{"type":"result","timestamp":"t","sessionId":"flat-session","exitCode":0}""",
    ];

    private static CopilotCliEvents Fold(Transcript transcript, IEnumerable<string> lines, Func<string, ToolCall?>? bridged = null, bool recordStreamingLines = false)
    {
        var events = new CopilotCliEvents(transcript, bridged, recordStreamingLines);
        foreach (var line in lines)
        {
            events.Fold(JsonNode.Parse(line)!);
        }

        return events;
    }

    [Fact]
    public void session_shape_records_every_line_and_attaches_the_bridged_call_to_the_tool_event()
    {
        var transcript = new Transcript();
        var bridged = new ToolCall("call_1", "bash", new JsonObject { ["command"] = "echo hi", ["description"] = "probe" });

        var events = Fold(transcript, SessionShape, id => id == "call_1" ? bridged : null);

        // every line but the streaming-only partial result; the ephemeral session.skills_loaded announcement is kept
        var infos = transcript.Events.OfType<InfoEvent>().ToArray();
        Assert.Equal(SessionShape.Length - 1, infos.Length);
        Assert.All(infos, e => Assert.Equal("copilot_cli", e.Source));
        Assert.Equal("session.skills_loaded", infos[0].Data!["type"]!.GetValue<string>());
        Assert.DoesNotContain(infos, e => e.Data!["type"]!.GetValue<string>() == "tool.execution_partial_result");
        Assert.Equal("result", infos[^1].Data!["type"]!.GetValue<string>());

        var tool = Assert.Single(transcript.Events.OfType<ToolEvent>());
        Assert.Same(tool, Assert.Single(events.ToolEvents));
        Assert.Equal("call_1", tool.Id);
        Assert.Equal("bash", tool.Function);
        Assert.Same(bridged.Arguments, tool.Arguments);
        Assert.Equal("hi\n<shellId: 0 completed with exit code 0>", tool.Result);
        Assert.Null(tool.Failed);
        Assert.Null(tool.Error);
        Assert.NotNull(tool.Completed);
        Assert.Equal("markdown", tool.View!.Format);
        Assert.Contains("bash(", tool.View.Content);
        Assert.Contains("echo hi", tool.View.Content);
        // the tool event sits between the execution_complete info line and the next assistant message
        Assert.Equal(5, transcript.Events.ToList().IndexOf(tool));

        Assert.Equal("e2f41e59-2d56-49b0-ab78-462b6b3b88d9", events.SessionId);
        Assert.Equal(0, events.ResultExitCode);
        Assert.Equal(0, events.Result!["usage"]!["premiumRequests"]!.GetValue<int>());
        Assert.Null(events.SessionError);
    }

    [Fact]
    public void a_call_the_bridge_never_saw_falls_back_to_the_cli_record_and_none_at_all_is_only_an_info_line()
    {
        var transcript = new Transcript();

        Fold(transcript, SessionShape);
        var tool = Assert.Single(transcript.Events.OfType<ToolEvent>());
        Assert.Equal("bash", tool.Function);
        Assert.Equal("echo hi", tool.Arguments["command"]!.GetValue<string>());

        var orphan = new Transcript();
        Fold(orphan, ["""{"type":"tool.execution_complete","data":{"toolCallId":"call_x","success":true,"result":{"content":"?"}}}"""]);
        Assert.Empty(orphan.Events.OfType<ToolEvent>());
        Assert.Single(orphan.Events.OfType<InfoEvent>());
    }

    [Fact]
    public void a_completion_carrying_an_error_without_success_false_is_a_failed_tool_event()
    {
        var transcript = new Transcript();

        Fold(transcript,
        [
            """{"type":"tool.execution_start","data":{"toolCallId":"c","toolName":"bash","arguments":{"command":"rm -rf /"}}}""",
            """{"type":"tool.execution_complete","data":{"toolCallId":"c","error":"denied"}}""",
        ]);

        var tool = Assert.Single(transcript.Events.OfType<ToolEvent>());
        Assert.True(tool.Failed);
        Assert.Equal("denied", tool.Error!.Message);
        Assert.Null(tool.Result);
    }

    [Fact]
    public void streaming_lines_are_skipped_unless_asked_for_and_session_announcements_never_are()
    {
        string[] lines =
        [
            """{"type":"session.skills_loaded","data":{"skills":[{"name":"x"}]},"ephemeral":true}""",
            """{"type":"session.background_tasks_changed","data":{},"ephemeral":true}""",
            """{"type":"assistant.message_delta","data":{"deltaContent":"h"},"ephemeral":true}""",
            """{"type":"model.call_start","data":{},"ephemeral":true}""",
            """{"type":"assistant.message","data":{"content":"hi"}}""",
        ];
        Assert.Equal([false, true, true, true, false], lines.Select(l => CopilotCliEvents.IsStreamingLine((JsonObject)JsonNode.Parse(l)!)));

        var quiet = new Transcript();
        Fold(quiet, lines);
        Assert.Equal(["session.skills_loaded", "assistant.message"], quiet.Events.OfType<InfoEvent>().Select(e => e.Data!["type"]!.GetValue<string>()));

        var verbose = new Transcript();
        Fold(verbose, lines, recordStreamingLines: true);
        Assert.Equal(lines.Length, verbose.Events.OfType<InfoEvent>().Count());
    }

    [Fact]
    public void flat_shape_joins_text_blocks_marks_errors_and_keeps_the_result_session()
    {
        var transcript = new Transcript();

        var events = Fold(transcript, FlatShape);

        Assert.Equal(FlatShape.Length, transcript.Events.OfType<InfoEvent>().Count());
        var tools = transcript.Events.OfType<ToolEvent>().ToArray();
        Assert.Equal(2, tools.Length);
        Assert.Equal("toolu_1", tools[0].Id);
        Assert.Equal("bash", tools[0].Function);
        Assert.Equal("ls", tools[0].Arguments["command"]!.GetValue<string>());
        Assert.Equal("a.py\nb.py", tools[0].Result);
        Assert.Null(tools[0].Failed);
        Assert.Equal("view", tools[1].Function);
        Assert.Equal("no such file", tools[1].Result);
        Assert.True(tools[1].Failed);
        Assert.Equal("flat-session", events.SessionId);
        Assert.Equal(0, events.ResultExitCode);
    }

    [Fact]
    public void session_error_and_a_failed_result_are_kept_and_begin_run_clears_them_but_not_the_session()
    {
        var transcript = new Transcript();
        var events = Fold(
            transcript,
            [
                """{"type":"session.start","data":{"sessionId":"from-start"},"id":"1","timestamp":"t","parentId":null}""",
                """{"type":"session.error","data":{"errorType":"query","message":"Failed to get response from the AI model; retried 5 times","statusCode":500},"id":"2","timestamp":"t","parentId":"1"}""",
                """{"type":"tool.execution_complete","data":{"toolCallId":"c","success":false,"error":"timed out","result":{"content":""}},"id":"3","timestamp":"t","parentId":"1"}""",
                """{"type":"result","timestamp":"t","sessionId":"516ba48f","exitCode":1,"usage":{}}""",
            ],
            _ => new ToolCall("c", "bash", new JsonObject { ["command"] = "sleep 99" }));

        Assert.Equal("Failed to get response from the AI model; retried 5 times", events.SessionError);
        Assert.Equal(1, events.ResultExitCode);
        Assert.Equal("516ba48f", events.SessionId);
        var failed = Assert.Single(transcript.Events.OfType<ToolEvent>());
        Assert.True(failed.Failed);
        Assert.Equal("timed out", failed.Error!.Message);

        events.BeginRun();

        Assert.Null(events.SessionError);
        Assert.Null(events.Result);
        Assert.Null(events.ResultExitCode);
        Assert.Equal("516ba48f", events.SessionId);
    }

    [Fact]
    public void unknown_shapes_are_tolerated()
    {
        var transcript = new Transcript();

        var events = Fold(transcript, ["[1,2,3]", "\"text\"", "{\"type\":42}", "{\"no\":\"type\"}", "{\"type\":\"assistant.message\",\"data\":\"oops\"}", "{\"type\":\"result\"}"]);

        Assert.Equal(6, transcript.Events.OfType<InfoEvent>().Count());
        Assert.Empty(transcript.Events.OfType<ToolEvent>());
        Assert.Null(events.SessionId);
        Assert.Null(events.ResultExitCode);
        Assert.NotNull(events.Result);
    }
}

/// <summary>The exit rules of the Copilot CLI agent and the bridge tracker (stop reason and returned tool calls).</summary>
public class CopilotCliExitTests
{
    private static ModelEvent Event(ModelOutput output, string? error = null) => new()
    {
        Model = "m",
        Input = [],
        ToolChoice = ToolChoice.None,
        Config = new GenerateConfig(),
        Output = output,
        Error = error,
    };

    [Theory]
    [InlineData(0, null, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 1, 2)]
    public void effective_exit_code_prefers_a_non_zero_process_exit_then_the_result_line(int process, int? result, int expected)
    {
        Assert.Equal(expected, CopilotCliExit.EffectiveExitCode(process, result));
    }

    [Theory]
    [InlineData(1, "", StopReason.ContentFilter, true)]
    [InlineData(1, "", StopReason.Stop, false)]
    [InlineData(1, "", null, false)]
    [InlineData(1, "boom", StopReason.ContentFilter, false)]
    [InlineData(1, "  \n", StopReason.ContentFilter, true)]
    [InlineData(2, "", StopReason.ContentFilter, false)]
    public void is_refusal_exit(int exitCode, string stderr, StopReason? stopReason, bool expected)
    {
        Assert.Equal(expected, CopilotCliExit.IsRefusalExit(exitCode, stderr, stopReason));
    }

    [Fact]
    public void classify_maps_success_refusal_retry_and_failure()
    {
        Assert.Equal(CopilotCliExitKind.Success, CopilotCliExit.Classify(0, "boom", null, 3, 0));
        Assert.Equal(CopilotCliExitKind.Refusal, CopilotCliExit.Classify(1, "", StopReason.ContentFilter, 3, 0));
        Assert.Equal(CopilotCliExitKind.RetryUncaughtError, CopilotCliExit.Classify(1, "", StopReason.Stop, 3, 2));
        Assert.Equal(CopilotCliExitKind.Failure, CopilotCliExit.Classify(1, "", StopReason.Stop, 3, 3));
        Assert.Equal(CopilotCliExitKind.Failure, CopilotCliExit.Classify(1, "", null, null, 0));
        Assert.Equal(CopilotCliExitKind.Failure, CopilotCliExit.Classify(1, "stack trace", null, 3, 0));
        Assert.Equal(CopilotCliExitKind.Failure, CopilotCliExit.Classify(2, "", StopReason.ContentFilter, 3, 0));

        // Observed live: the self-contained binary's startup notice on stderr is not an error (Docker, 8 concurrent samples).
        const string notice = "Package extraction took 5026ms\n";
        Assert.Equal("", CopilotCliExit.SignificantStderr(notice));
        Assert.Equal("stack trace", CopilotCliExit.SignificantStderr(notice + "stack trace\n"));
        Assert.True(CopilotCliExit.IsRefusalExit(1, notice, StopReason.ContentFilter));
        Assert.Equal(CopilotCliExitKind.Refusal, CopilotCliExit.Classify(1, notice, StopReason.ContentFilter, 3, 0));
        Assert.Equal(CopilotCliExitKind.RetryUncaughtError, CopilotCliExit.Classify(1, notice, StopReason.Stop, 3, 0));
        Assert.Equal(CopilotCliExitKind.Failure, CopilotCliExit.Classify(1, notice + "boom", StopReason.Stop, 3, 0));
        Assert.Equal("Error executing copilot cli agent 1: model gave up", CopilotCliExit.ErrorMessage(1, notice, "model gave up"));
        Assert.Equal("Error executing copilot cli agent 2: boom", CopilotCliExit.ErrorMessage(2, "boom", "ignored"));
        Assert.Equal("Error executing copilot cli agent 1: model gave up", CopilotCliExit.ErrorMessage(1, " ", "model gave up"));
        Assert.Equal("Error executing copilot cli agent 1: ", CopilotCliExit.ErrorMessage(1, ""));
    }

    [Fact]
    public void tracker_records_the_latest_completed_stop_reason_and_the_returned_tool_calls()
    {
        var tracker = new CopilotCliBridgeTracker();
        Assert.Null(tracker.LastStopReason);
        Assert.Null(tracker.ToolCall("call_1"));

        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", "hi")));
        Assert.Equal(StopReason.Stop, tracker.LastStopReason);

        var call = new ToolCall("call_1", "bash", new JsonObject { ["command"] = "ls" });
        tracker.OnModelEvent(Event(new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", [call]), StopReason.ToolCalls)] }));
        Assert.Equal(StopReason.ToolCalls, tracker.LastStopReason);
        Assert.Equal(call.Function, tracker.ToolCall("call_1")!.Function);

        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", "", StopReason.ContentFilter)));
        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", ""), error: "429"));
        tracker.OnModelEvent(Event(new ModelOutput()));
        Assert.Equal(StopReason.ContentFilter, tracker.LastStopReason);

        tracker.Reset();
        Assert.Null(tracker.LastStopReason);
        Assert.NotNull(tracker.ToolCall("call_1"));
    }
}
