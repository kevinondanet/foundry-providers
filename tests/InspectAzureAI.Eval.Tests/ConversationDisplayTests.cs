using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>The <c>conversation</c> display mode (<c>model/_display.py</c>, <c>util/_conversation.py</c>) as a hook writing plain-text panels.</summary>
public sealed class ConversationDisplayTests : IDisposable
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

    [Fact]
    public void panels_are_a_titled_rule_the_content_and_a_blank_line()
    {
        var output = new StringWriter();
        var display = new ConversationDisplay(output);

        display.Panel("User", "hello\n");
        display.Panel("Marker");

        Assert.Equal("── User ──\nhello\n\n── Marker ──\n\n", output.ToString());
    }

    [Fact]
    public void assistant_messages_render_text_reasoning_and_tool_calls()
    {
        var call = new ToolCall("c1", "bash", new JsonObject { ["cmd"] = "ls -la", ["timeout"] = 30 });
        var message = new ChatMessageAssistant(new Content[] { new ContentReasoning("let me look"), new ContentText("  Listing.  ") }, toolCalls: [call]);

        var rendered = ConversationDisplay.RenderAssistant(message);

        Assert.Equal("<think>\nlet me look\n</think>\nListing.\n\n```python\nbash(cmd='ls -la', timeout=30)\n```", rendered);
        Assert.Equal("```python\nnoop()\n```", ConversationDisplay.RenderToolCalls([new ToolCall("c2", "noop", new JsonObject())]));
        Assert.Equal("plain", ConversationDisplay.RenderAssistant(new ChatMessageAssistant("plain\n")));
    }

    [Fact]
    public void tool_output_is_truncated_to_fifty_lines_with_pythons_note()
    {
        var text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}"));

        var rendered = ConversationDisplay.RenderToolOutput(text);

        Assert.StartsWith("line 1\n", rendered, StringComparison.Ordinal);
        Assert.EndsWith("line 50\n\nOutput truncated (10 additional lines)...", rendered, StringComparison.Ordinal);
        Assert.Equal("a\tb\nc", ConversationDisplay.RenderToolOutput("a\tb\u0007\nc\u200b"));
    }

    [Fact]
    public void messages_preceding_assistant_are_those_after_the_last_assistant_message()
    {
        var system = new ChatMessageSystem("sys");
        var user = new ChatMessageUser("hi");
        var assistant = new ChatMessageAssistant("hello");
        var tool = new ChatMessageTool("out", toolCallId: "c1", function: "echo");
        var follow = new ChatMessageUser("more");

        Assert.Equal([system, user], ConversationDisplay.MessagesPrecedingAssistant([system, user]));
        Assert.Equal([tool, follow], ConversationDisplay.MessagesPrecedingAssistant([system, user, assistant, tool, follow]));
        Assert.Empty(ConversationDisplay.MessagesPrecedingAssistant([user, assistant]));
    }

    [Fact]
    public void tool_messages_show_the_error_or_output_and_nothing_when_empty()
    {
        var output = new StringWriter();
        var display = new ConversationDisplay(output);

        display.Message(new ChatMessageTool("", toolCallId: "c0", function: "quiet"));
        display.Message(new ChatMessageTool("", toolCallId: "c1", function: "boom", error: new ToolCallError("unknown", "it broke")));
        display.Message(new ChatMessageUser("next"));

        Assert.Equal("── Tool Output: boom ──\nit broke\n\n── User ──\nnext\n\n", output.ToString());
    }

    [Fact]
    public async Task a_scripted_eval_prints_the_conversation_as_it_happens()
    {
        var parameters = new ToolParams { Properties = new Dictionary<string, ToolParam> { ["text"] = ToolParam.Of("string") }, Required = ["text"] };
        var echo = new ToolDef("echo", "Echoes text.", parameters, (arguments, _) => Task.FromResult<ToolResult>(arguments["text"]!.GetValue<string>()));
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("echo", new { text = "hi there" }, text: "Let me echo."),
            ScriptedTurn.Text("The echo said hi."));
        var task = new EvalTask
        {
            Name = "conversation",
            Dataset = new MemoryDataset([new Sample("Echo something.") { Target = "hi" }]),
            Solver = Solvers.Chain(Solvers.SystemMessage("You are terse."), Solvers.UseTools(echo), Solvers.Generate()),
            Scorers = [Scorers.Includes()],
        };
        var output = new StringWriter();

        var log = await Eval.RunAsync(task, new EvalOptions
        {
            Model = new Model(api),
            LogDir = _logDir,
            MaxSamples = 1,
            LogFormat = LogFormat.Json,
            Hooks = [new ConversationDisplay(output)],
        });

        Assert.Null(Assert.Single(log.Samples!).Error);
        var expected =
            "── System ──\nYou are terse.\n\n"
            + "── User ──\nEcho something.\n\n"
            + "── Assistant ──\nLet me echo.\n\n```python\necho(text='hi there')\n```\n\n"
            + "── Tool Output: echo ──\nhi there\n\n"
            + "── Assistant ──\nThe echo said hi.\n\n";
        Assert.Equal(expected, output.ToString());
    }
}
