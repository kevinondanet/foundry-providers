using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port-level behaviour of <c>execute_tools</c> (<c>model/_call_tools.py</c>): error mapping, truncation, staging and events.</summary>
public class ToolExecutorTests
{
    private static ToolDef Tool(string name, ToolExecute execute, bool parallel = true, int? maxOutput = null, params string[] required) =>
        new(name, $"{name} tool", new ToolParams
        {
            Properties = required.ToDictionary(r => r, r => ToolParam.Of("string")),
            Required = required,
        }, execute)
        { Parallel = parallel, MaxOutput = maxOutput };

    private static ToolDef Echo(string name = "echo") => Tool(name, (args, _) => Task.FromResult<ToolResult>(args["text"]?.GetValue<string>() ?? ""), required: "text");

    private static ChatMessageAssistant Calls(params ToolCall[] calls) => new("", toolCalls: calls);

    private static ToolCall Call(string function, string id = "c1", object? args = null) =>
        new(id, function, args is null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject());

    private static async Task<ChatMessageTool> Single(ToolDef tool, ToolCall call, int? maxOutput = null)
    {
        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("hi"), Calls(call)], [tool], maxOutput);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    [Fact]
    public async Task nothing_happens_unless_the_last_message_is_an_assistant_with_tool_calls()
    {
        var noCalls = await ToolExecutor.ExecuteToolsAsync([new ChatMessageAssistant("plain")], [Echo()]);
        var notLast = await ToolExecutor.ExecuteToolsAsync([Calls(Call("echo")), new ChatMessageUser("later")], [Echo()]);
        var empty = await ToolExecutor.ExecuteToolsAsync([], [Echo()]);

        Assert.Empty(noCalls.Messages);
        Assert.Empty(notLast.Messages);
        Assert.Empty(empty.Messages);
    }

    [Fact]
    public async Task a_successful_call_yields_a_tool_message_with_id_and_function()
    {
        var message = await Single(Echo(), Call("echo", "call_7", new { text = "hello" }));

        Assert.Equal("hello", message.Text);
        Assert.Equal("call_7", message.ToolCallId);
        Assert.Equal("echo", message.Function);
        Assert.Null(message.Error);
    }

    public static TheoryData<Exception, string, string> MappedErrors => new()
    {
        { new SandboxTimeoutException("slow", "partial output"), "timeout", "Command timed out before completing." },
        { new TimeoutException("slow"), "timeout", "Command timed out before completing." },
        { new SandboxUnavailableException("daemon down"), "sandbox_unavailable", "daemon down" },
        { new UnauthorizedAccessException("Permission denied"), "permission", "Permission denied." },
        { new FileNotFoundException("missing", "/tmp/nope.txt"), "file_not_found", "File '/tmp/nope.txt' was not found." },
        { new IOException("'/tmp/adir' is a directory."), "is_a_directory", "'/tmp/adir' is a directory." },
        { new DecoderFallbackException("bad byte"), "unicode_decode", "Error decoding bytes to utf-8: bad byte" },
        { new OutputLimitExceededException("10 MiB", "kept tail"), "limit", "The tool exceeded its output limit of 10 MiB." },
        { new LimitExceededException("token", "1,000", 1200), "limit", "The tool exceeded its token limit of 1,000." },
        { new ToolParsingError("bad args"), "parsing", "bad args" },
        { new ToolError("boom"), "unknown", "boom" },
    };

    [Theory]
    [MemberData(nameof(MappedErrors))]
    public async Task tool_failures_map_to_tool_call_errors(Exception thrown, string type, string expectedMessage)
    {
        var tool = Tool("t", (_, _) => throw thrown);

        var message = await Single(tool, Call("t"));

        Assert.NotNull(message.Error);
        Assert.Equal(type, message.Error.Type);
        Assert.Equal(expectedMessage, message.Error.Message);
        var expectedContent = thrown switch
        {
            SandboxTimeoutException timeout => timeout.TruncatedOutput,
            OutputLimitExceededException limit => limit.TruncatedOutput,
            _ => "",
        };
        Assert.Equal(expectedContent, message.Text);
    }

    [Fact]
    public async Task unmapped_exceptions_propagate_after_the_stage_settles()
    {
        var tool = Tool("t", (_, _) => throw new InvalidOperationException("fatal"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Single(tool, Call("t")));

        Assert.Equal("fatal", ex.Message);
    }

    [Fact]
    public async Task a_parse_error_is_reported_without_running_the_tool()
    {
        var ran = false;
        var tool = Tool("t", (_, _) => { ran = true; return Task.FromResult<ToolResult>("x"); });
        var call = Call("t") with { ParseError = "Error parsing the following tool call arguments" };

        var message = await Single(tool, call);

        Assert.False(ran);
        Assert.Equal("parsing", message.Error!.Type);
        Assert.Equal("Error parsing the following tool call arguments", message.Error.Message);
    }

    [Fact]
    public async Task an_unknown_tool_is_reported_as_unknown()
    {
        var message = await Single(Echo(), Call("nope"));

        Assert.Equal("unknown", message.Error!.Type);
        Assert.Equal("Tool nope not found", message.Error.Message);
    }

    [Fact]
    public async Task a_missing_required_argument_is_a_parsing_error()
    {
        var message = await Single(Echo(), Call("echo"));

        Assert.Equal("parsing", message.Error!.Type);
        Assert.Equal("Required parameter text not provided to tool call.", message.Error.Message);
    }

    [Fact]
    public async Task long_text_output_is_truncated_keeping_the_tail_with_the_python_template()
    {
        var tool = Tool("big", (_, _) => Task.FromResult<ToolResult>(new string('a', 90) + "0123456789"), maxOutput: 10);
        using var scope = new SampleContextScope();

        var message = await Single(tool, Call("big"));

        Assert.Equal(
            "\nThe output of your call to big was too long to be displayed.\nHere is a truncated version:\n<START_TOOL_OUTPUT>\n0123456789\n<END_TOOL_OUTPUT>\n",
            message.Text);
        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal(new ToolTruncation(100, 10), toolEvent.Truncated);
        // Python records the truncated content, so the log's event body is what the model saw.
        Assert.Equal(message.Text, toolEvent.Result);
    }

    [Fact]
    public async Task truncation_limit_prefers_tool_then_caller_then_default()
    {
        var text = new string('b', 20 * 1024);
        var declared = Tool("declared", (_, _) => Task.FromResult<ToolResult>(text), maxOutput: 5);
        var undeclared = Tool("undeclared", (_, _) => Task.FromResult<ToolResult>(text));

        var fromTool = await Single(declared, Call("declared"), maxOutput: 50);
        var fromCaller = await Single(undeclared, Call("undeclared"), maxOutput: 50);
        var fromDefault = await Single(undeclared, Call("undeclared"));
        var disabled = await Single(undeclared, Call("undeclared"), maxOutput: 0);

        Assert.Contains("<START_TOOL_OUTPUT>\n" + new string('b', 5) + "\n<END", fromTool.Text);
        Assert.Contains("<START_TOOL_OUTPUT>\n" + new string('b', 50) + "\n<END", fromCaller.Text);
        Assert.Contains("<START_TOOL_OUTPUT>\n" + new string('b', ToolExecutor.DefaultMaxOutput) + "\n<END", fromDefault.Text);
        Assert.Equal(text, disabled.Text);
    }

    [Fact]
    public async Task content_lists_are_passed_through_untruncated()
    {
        var tool = Tool("rich", (_, _) => Task.FromResult(ToolResult.FromContents([new ContentText(new string('c', 100)), new ContentImage("data:image/png;base64,AAAA")])), maxOutput: 5);

        var message = await Single(tool, Call("rich"));

        Assert.False(message.Content.IsString);
        Assert.Equal(2, message.ContentList.Count);
        Assert.Equal(new string('c', 100), message.Text);
    }

    [Fact]
    public async Task parallel_calls_run_concurrently_and_a_serial_call_is_a_barrier()
    {
        var log = new List<string>();
        var p2Started = new TaskCompletionSource();
        ToolDef Make(string name, bool parallel, Func<Task>? body = null) => Tool(name, async (_, _) =>
        {
            lock (log) { log.Add($"{name}:start"); }
            if (name == "p2") { p2Started.TrySetResult(); }
            if (body is not null) { await body(); }
            lock (log) { log.Add($"{name}:end"); }
            return name;
        }, parallel);
        var p1 = Make("p1", true, async () =>
        {
            var finished = await Task.WhenAny(p2Started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(p2Started.Task, finished);
        });
        var p2 = Make("p2", true);
        var serial = Make("s", false);

        var result = await ToolExecutor.ExecuteToolsAsync(
            [Calls(Call("p1", "1"), Call("p2", "2"), Call("s", "3"), Call("p2", "4"))],
            [p1, p2, serial]);

        Assert.Equal(["1", "2", "3", "4"], result.Messages.Cast<ChatMessageTool>().Select(m => m.ToolCallId));
        Assert.Equal(["p1", "p2", "s", "p2"], result.Messages.Select(m => m.Text));
        Assert.True(log.IndexOf("p2:start") < log.IndexOf("p1:end"), string.Join(",", log));
        Assert.True(log.IndexOf("s:start") > log.IndexOf("p1:end") && log.IndexOf("s:start") > log.IndexOf("p2:end"), string.Join(",", log));
        Assert.True(log.LastIndexOf("p2:start") > log.IndexOf("s:end"), string.Join(",", log));
    }

    [Fact]
    public void stages_coalesce_parallel_calls_and_isolate_serial_and_unknown_ones()
    {
        var tools = new[] { Tool("p", (_, _) => Task.FromResult<ToolResult>("")), Tool("s", (_, _) => Task.FromResult<ToolResult>(""), parallel: false) };

        var stages = ToolExecutor.Stages([Call("p"), Call("p"), Call("s"), Call("unknown"), Call("p")], tools);

        Assert.Equal([[0, 1], [2], [3], [4]], stages.Select(s => s.ToArray()).ToArray());
    }

    [Fact]
    public async Task each_call_records_a_tool_event_inside_a_tool_span()
    {
        using var scope = new SampleContextScope();

        await Single(Echo(), Call("echo", "id-1", new { text = "hey" }));

        var events = scope.Transcript.Events;
        var begin = Assert.IsType<SpanBeginEvent>(events[0]);
        var toolEvent = Assert.IsType<ToolEvent>(events[1]);
        var end = Assert.IsType<SpanEndEvent>(events[2]);
        Assert.Equal("echo", begin.Name);
        Assert.Equal("tool", begin.Type);
        Assert.Equal(begin.Id, toolEvent.SpanId);
        Assert.Equal(begin.Id, end.Id);
        Assert.Equal("id-1", toolEvent.Id);
        Assert.Equal("hey", toolEvent.Arguments["text"]!.GetValue<string>());
        Assert.Equal("hey", toolEvent.Result);
        Assert.Null(toolEvent.Error);
        Assert.NotNull(toolEvent.Working);
    }

    [Fact]
    public async Task a_tool_event_is_stamped_with_completion_and_the_tool_message_id()
    {
        using var scope = new SampleContextScope();
        var before = DateTimeOffset.UtcNow;

        var message = await Single(Echo(), Call("echo", "id-2", new { text = "hey" }));

        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.NotNull(toolEvent.Completed);
        Assert.InRange(toolEvent.Timestamp, before, DateTimeOffset.UtcNow);
        Assert.InRange(toolEvent.Completed.Value, toolEvent.Timestamp, DateTimeOffset.UtcNow);
        Assert.NotNull(message.Id);
        Assert.Equal(message.Id, toolEvent.MessageId);
        Assert.Null(toolEvent.Failed);
        Assert.Null(toolEvent.Pending);
    }

    [Fact]
    public async Task an_unmapped_exception_marks_the_tool_event_failed()
    {
        using var scope = new SampleContextScope();
        var tool = Tool("t", (_, _) => throw new InvalidOperationException("fatal"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Single(tool, Call("t", "id-3")));

        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.True(toolEvent.Failed);
        Assert.NotNull(toolEvent.Completed);
        Assert.NotNull(toolEvent.MessageId);
        // As in Python, an unhandled exception carries no ToolCallError; `failed` is what distinguishes it from an empty result.
        Assert.Null(toolEvent.Error);
        Assert.Equal("", toolEvent.Result);
    }

    [Fact]
    public async Task bash_tool_runs_in_the_sample_sandbox_with_stderr_first()
    {
        using var scope = new SampleContextScope(withLocalSandbox: true);
        var bash = SandboxTools.Bash();
        var info = bash.ToInfo();

        var message = await Single(bash, Call("bash", args: new { cmd = "echo out; echo err 1>&2" }));

        Assert.Equal("bash", info.Name);
        Assert.Equal(["cmd"], info.Parameters.Required);
        Assert.Equal("err\n\nout\n", message.Text);
        Assert.Null(message.Error);
    }

    [Fact]
    public async Task bash_tool_timeout_becomes_a_timeout_tool_error()
    {
        using var scope = new SampleContextScope(withLocalSandbox: true);

        var message = await Single(SandboxTools.Bash(timeout: TimeSpan.FromMilliseconds(300)), Call("bash", args: new { cmd = "sleep 20" }));

        Assert.Equal("timeout", message.Error!.Type);
        Assert.Equal("Command timed out before completing.", message.Error.Message);
    }

    [Fact]
    public async Task tools_requiring_a_sandbox_fail_the_sample_when_none_is_configured()
    {
        using var scope = new SampleContextScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Single(SandboxTools.Bash(), Call("bash", args: new { cmd = "true" })));

        Assert.StartsWith("No sandbox environment has been provided", ex.Message);
    }
}
