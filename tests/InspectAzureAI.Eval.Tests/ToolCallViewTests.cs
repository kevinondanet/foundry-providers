using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Port of <c>ToolCall.view</c>: written inside <c>tool_calls[*]</c> of logs, read back from Python logs, and never sent to a model provider.</summary>
public class ToolCallViewTests
{
    private const string Marker = "VIEW-MARKER-7f3a";

    private static readonly ToolCallContent View = new("markdown", $"```bash\nls -la {Marker}\n```") { Title = "bash" };

    private static ToolCall Call(ToolCallContent? view = null) => new("call_1", "bash", new JsonObject { ["cmd"] = "ls -la" }) { View = view };

    private static ChatMessageAssistant Assistant(ToolCallContent? view) => new("Listing files.", toolCalls: [Call(view)], model: "gpt", source: "generate");

    private static IReadOnlyList<ChatMessage> Conversation(ToolCallContent? view) =>
    [
        new ChatMessageUser("What is in this directory?"),
        Assistant(view),
        new ChatMessageTool("a.txt", toolCallId: "call_1", function: "bash"),
    ];

    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), "inspect-tool-call-view-tests", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void a_view_is_written_inside_tool_calls_and_read_back()
    {
        var json = JsonSerializer.SerializeToNode<ChatMessage>(Assistant(View), EvalLogWriter.Options)!;

        var written = json["tool_calls"]![0]!["view"]!;
        Assert.Equal("bash", written["title"]!.GetValue<string>());
        Assert.Equal("markdown", written["format"]!.GetValue<string>());
        Assert.Equal(View.Content, written["content"]!.GetValue<string>());

        var read = Assert.IsType<ChatMessageAssistant>(JsonSerializer.Deserialize<ChatMessage>(json.ToJsonString(), EvalLogWriter.Options));
        Assert.Equal(View, Assert.Single(read.ToolCalls!).View);
    }

    [Fact]
    public void a_null_view_is_omitted()
    {
        var json = JsonSerializer.SerializeToNode<ChatMessage>(Assistant(null), EvalLogWriter.Options)!;

        var call = json["tool_calls"]![0]!.AsObject();
        Assert.False(call.ContainsKey("view"));
        Assert.Equal(["id", "function", "arguments", "type"], call.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void a_python_written_tool_call_view_reads()
    {
        const string python = """
            {"id": "m1", "content": "Listing files.", "source": "generate", "role": "assistant",
             "tool_calls": [{"id": "call_1", "function": "bash", "arguments": {"cmd": "ls"}, "parse_error": null,
                             "view": {"title": null, "format": "markdown", "content": "```bash\nls\n```\n"}, "type": "function"},
                            {"id": "call_2", "function": "think", "arguments": {}, "parse_error": null, "view": null, "type": "function"}],
             "model": "gpt-5"}
            """;

        var message = Assert.IsType<ChatMessageAssistant>(JsonSerializer.Deserialize<ChatMessage>(python, EvalLogWriter.Options));

        var view = message.ToolCalls![0].View!;
        Assert.Null(view.Title);
        Assert.Equal("markdown", view.Format);
        Assert.Equal("```bash\nls\n```\n", view.Content);
        Assert.Null(message.ToolCalls[1].View);
    }

    [Theory]
    [InlineData("log.json")]
    [InlineData("log.eval")]
    public void views_round_trip_through_json_and_eval_logs(string fileName)
    {
        var output = new ModelOutput { Model = "gpt", Choices = [new ChatCompletionChoice(Assistant(View), StopReason.ToolCalls)] };
        var log = new EvalLog
        {
            Eval = new EvalSpec { Task = "views", Model = "gpt", Dataset = new EvalDataset() },
            Samples =
            [
                new EvalSample
                {
                    Id = 1,
                    Epoch = 1,
                    Input = "What is in this directory?",
                    Target = "a.txt",
                    Messages = [.. Conversation(View), Assistant(null)],
                    Output = output,
                },
            ],
        };
        var path = TempPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            EvalLogWriter.Write(log, path);
            var sample = Assert.Single(EvalLogWriter.Read(path).Samples!);

            Assert.Equal(View, Assert.IsType<ChatMessageAssistant>(sample.Messages[1]).ToolCalls![0].View);
            Assert.Null(Assert.IsType<ChatMessageAssistant>(sample.Messages[3]).ToolCalls![0].View);
            Assert.Equal(View, sample.Output.Message.ToolCalls![0].View);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void the_anthropic_wire_does_not_carry_views()
    {
        var withView = AnthropicFoundryModelApi.Messages(Conversation(View)).ToJsonString();
        var without = AnthropicFoundryModelApi.Messages(Conversation(null)).ToJsonString();

        Assert.DoesNotContain(Marker, withView, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view\"", withView, StringComparison.Ordinal);
        Assert.Equal(without, withView);
    }

    [Fact]
    public void the_responses_wire_does_not_carry_views()
    {
        var withView = ResponsesInput.InputItems(Conversation(View)).ToJsonString();

        Assert.DoesNotContain(Marker, withView, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view\"", withView, StringComparison.Ordinal);
        Assert.Equal(ResponsesInput.InputItems(Conversation(null)).ToJsonString(), withView);
    }

    [Fact]
    public void the_chat_completions_wire_does_not_carry_views()
    {
        using var api = new OpenAIModelApi("gpt-4o", baseUrl: "http://127.0.0.1:9/v1", apiKey: "test-key");

        var withView = api.BuildRequest(Conversation(View), [], ToolChoice.Auto, new GenerateConfig(), streaming: false).ToJsonString();

        Assert.DoesNotContain(Marker, withView, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view\"", withView, StringComparison.Ordinal);
    }

    [Fact]
    public void the_prompt_cache_key_is_unchanged_when_there_is_no_view()
    {
        var entry = new CacheEntry(null, new GenerateConfig(), Conversation(null), "gpt", new CachePolicy(), ToolChoice.Auto, []);

        var messages = CacheKey.Components(entry)[1]!.AsArray();

        // the pre-view shape of an assistant message's tool call, byte for byte
        Assert.Equal(
            """{"id": "call_1", "function": "bash", "arguments": {"cmd": "ls -la"}, "type": "function"}""",
            PythonJson.Dumps(messages[1]!["tool_calls"]![0]));
        Assert.Equal(CacheKey.Compute(new CacheEntry(null, new GenerateConfig(), Conversation(null), "gpt", new CachePolicy(), ToolChoice.Auto, [])), entry.Key);
        Assert.NotEqual(new CacheEntry(null, new GenerateConfig(), Conversation(View), "gpt", new CachePolicy(), ToolChoice.Auto, []).Key, entry.Key);
    }
}
