using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of the line framing of inspect_swe <c>_claude_code/_events/stream.py</c>.</summary>
public class ClaudeCodeStreamTests
{
    [Fact]
    public void complete_lines_are_parsed_as_they_arrive_across_chunks()
    {
        var stream = new ClaudeCodeStream();

        var first = stream.PushStdout("{\"a\":1}\n{\"b\"");
        var second = stream.PushStdout(":2}\n\n   \n");
        var done = stream.Complete(0);

        var a = Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(Assert.Single(first));
        Assert.Equal(1, a.Raw["a"]!.GetValue<int>());
        Assert.Equal("{\"a\":1}", a.Line);
        var b = Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(Assert.Single(second));
        Assert.Equal("{\"b\":2}", b.Line);
        Assert.Equal(0, Assert.IsType<ClaudeCodeStreamEvent.Exit>(Assert.Single(done)).Code);
    }

    [Fact]
    public void trailing_partial_line_is_flushed_before_the_exit_event()
    {
        var stream = new ClaudeCodeStream();

        Assert.Empty(stream.PushStdout("{\"type\":\"result\"}"));
        var done = stream.Complete(3);

        Assert.Equal(2, done.Count);
        Assert.Equal("result", Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(done[0]).Raw["type"]!.GetValue<string>());
        Assert.Equal(3, Assert.IsType<ClaudeCodeStreamEvent.Exit>(done[1]).Code);
        Assert.Single(stream.Complete(0));
    }

    [Fact]
    public void lines_are_stripped_and_crlf_is_tolerated()
    {
        var stream = new ClaudeCodeStream();

        var events = stream.PushStdout("  {\"x\":true}  \r\n\r\n");

        var e = Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(Assert.Single(events));
        Assert.Equal("{\"x\":true}", e.Line);
    }

    [Fact]
    public void unparseable_lines_surface_as_parse_errors()
    {
        var stream = new ClaudeCodeStream();

        var events = stream.PushStdout("not json\n{\"ok\":1}\nnull\n");

        Assert.Equal(3, events.Count);
        Assert.Equal("not json", Assert.IsType<ClaudeCodeStreamEvent.ParseError>(events[0]).Line);
        Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(events[1]);
        Assert.Equal("null", Assert.IsType<ClaudeCodeStreamEvent.ParseError>(events[2]).Line);
    }

    [Fact]
    public void parse_frames_a_finished_process_with_stderr_and_exit()
    {
        var events = ClaudeCodeStream.Parse(new ExecResult(false, 1, "{\"a\":1}\n{\"b\":2}", "boom"));

        // The unterminated last line is the trailing partial, flushed on completion after stderr (as in Python).
        Assert.Equal(4, events.Count);
        Assert.Equal("{\"a\":1}", Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(events[0]).Line);
        Assert.Equal("boom", Assert.IsType<ClaudeCodeStreamEvent.Stderr>(events[1]).Data);
        Assert.Equal("{\"b\":2}", Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(events[2]).Line);
        Assert.Equal(1, Assert.IsType<ClaudeCodeStreamEvent.Exit>(events[3]).Code);

        var quiet = ClaudeCodeStream.Parse(new ExecResult(true, 0, "", ""));
        Assert.Equal(0, Assert.IsType<ClaudeCodeStreamEvent.Exit>(Assert.Single(quiet)).Code);
    }
}

/// <summary>Port of inspect_swe <c>tests/test_claude_code_exit.py</c>: the refusal exit rule, the uncaught-error retry rule and the stop-reason tracker.</summary>
public class ClaudeCodeExitTests
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
    [InlineData(1, "", StopReason.ContentFilter, true)]
    [InlineData(1, "", StopReason.Stop, false)]
    [InlineData(1, "", StopReason.ToolCalls, false)]
    [InlineData(1, "", null, false)]
    [InlineData(1, "boom", StopReason.ContentFilter, false)]
    [InlineData(1, "  \n", StopReason.ContentFilter, true)]
    [InlineData(2, "", StopReason.ContentFilter, false)]
    public void is_claude_code_refusal_exit(int exitCode, string stderr, StopReason? stopReason, bool expected)
    {
        Assert.Equal(expected, ClaudeCodeExit.IsRefusalExit(exitCode, stderr, stopReason));
    }

    [Fact]
    public void classify_maps_success_refusal_retry_and_failure()
    {
        Assert.Equal(ClaudeCodeExitKind.Success, ClaudeCodeExit.Classify(0, "boom", null, 3, 0));
        Assert.Equal(ClaudeCodeExitKind.Refusal, ClaudeCodeExit.Classify(1, "", StopReason.ContentFilter, 3, 0));
        Assert.Equal(ClaudeCodeExitKind.RetryUncaughtError, ClaudeCodeExit.Classify(1, "", StopReason.Stop, 3, 2));
        Assert.Equal(ClaudeCodeExitKind.Failure, ClaudeCodeExit.Classify(1, "", StopReason.Stop, 3, 3));
        Assert.Equal(ClaudeCodeExitKind.Failure, ClaudeCodeExit.Classify(1, "", null, null, 0));
        Assert.Equal(ClaudeCodeExitKind.Failure, ClaudeCodeExit.Classify(1, "stack trace", null, 3, 0));
        Assert.Equal(ClaudeCodeExitKind.Failure, ClaudeCodeExit.Classify(2, "", StopReason.ContentFilter, 3, 0));
        Assert.Equal("Error executing claude code agent 2: boom", ClaudeCodeExit.ErrorMessage(2, "boom"));
    }

    [Fact]
    public void tracker_records_the_latest_completed_stop_reason_and_resets()
    {
        var tracker = new ClaudeCodeStopReasonTracker();
        Assert.Null(tracker.LastStopReason);

        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", "hi")));
        Assert.Equal(StopReason.Stop, tracker.LastStopReason);

        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", "", StopReason.ContentFilter)));
        Assert.Equal(StopReason.ContentFilter, tracker.LastStopReason);

        tracker.OnModelEvent(Event(ModelOutput.FromContent("m", ""), error: "429"));
        tracker.OnModelEvent(Event(new ModelOutput()));
        Assert.Equal(StopReason.ContentFilter, tracker.LastStopReason);

        tracker.Reset();
        Assert.Null(tracker.LastStopReason);
    }

    [Fact]
    public void twenty_thousand_lines_pushed_at_once_are_framed_in_one_linear_pass()
    {
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 200) + "\"}]}}\n";
        var stdout = string.Concat(Enumerable.Repeat(line, 20_000)) + "{\"type\":\"result\"";
        var stream = new ClaudeCodeStream();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var events = stream.PushStdout(stdout);
        var tail = stream.Complete(0);

        Assert.Equal(20_000, events.Count);
        Assert.All(events, e => Assert.IsType<ClaudeCodeStreamEvent.Jsonl>(e));
        Assert.IsType<ClaudeCodeStreamEvent.ParseError>(tail[0]);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"framing took {started.Elapsed}");
    }
}
