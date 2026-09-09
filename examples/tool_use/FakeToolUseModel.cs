using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.ToolUse;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/tool_use.py</c>: a <see cref="ScriptedModelApi"/> whose every
/// turn is computed from the conversation, so one script serves all five tasks. It recognises the sample input,
/// makes the tool call the task is about (two <c>add</c> calls in a single turn for <c>parallel_add</c>), then
/// answers from the tool results: the sum, Yes/No from the directory listing, the file's contents (or the tool
/// error), a confirmation of the write, or the two sums side by side.
/// </summary>
internal static class FakeToolUseModel
{
    public const string ModelName = "tool-use-scripted";

    /// <summary>Every task needs two turns (a tool call, then the answer); the budget leaves room to spare.</summary>
    private const int TurnBudget = 8;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var input = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var results = messages.OfType<ChatMessageTool>().ToList();
        return input switch
        {
            ToolUseTasks.AdditionInput => results.Count == 0
                ? Call("add", new { x = 1, y = 1 }, "I'll use the add tool.")
                : Text(results[0].Text.Trim()),
            ToolUseTasks.BashInput => results.Count == 0
                ? Call("list_files", new { dir = "/usr/bin" }, "Listing /usr/bin.")
                : Text(results[0].Error is null && results[0].Text.Split('\n').Contains("python3") ? "Yes" : "No"),
            ToolUseTasks.ReadInput => results.Count == 0
                ? Call("read_file", new { file = "foo.txt" }, "Reading foo.txt.")
                : Text(results[0].Error is { } error
                    ? $"The file 'foo.txt' could not be read: {error.Message}"
                    : $"The file 'foo.txt' contains: {results[0].Text.Trim()}"),
            ToolUseTasks.WriteInput => results.Count == 0
                ? Call("write_file", new { file = "foo.txt", contents = "bar" }, "Writing foo.txt.")
                : Text(results[0].Error is { } error ? $"The file 'foo.txt' could not be written: {error.Message}" : "Wrote 'bar' to foo.txt."),
            ToolUseTasks.ParallelAddInput => results.Count == 0
                ? ParallelCalls()
                : Text(string.Join(" ", results.Select(result => result.Text.Trim()))),
            _ => Text("I only know the questions of the tool_use example."),
        };
    }

    private static ModelOutput Call(string function, object arguments, string thought) => ScriptedTurn.ToolCall(function, arguments, text: thought).Output!;

    private static ModelOutput Text(string text) => ModelOutput.FromContent(ModelName, text);

    /// <summary>One assistant message carrying both <c>add</c> calls, which <c>ToolExecutor</c> runs concurrently (the tool is parallel-safe).</summary>
    private static ModelOutput ParallelCalls()
    {
        var message = new ChatMessageAssistant(
            "Adding 1+1 and 2+2 in parallel.",
            toolCalls:
            [
                new ToolCall(ShortUuid.Generate(), "add", new JsonObject { ["x"] = 1, ["y"] = 1 }),
                new ToolCall(ShortUuid.Generate(), "add", new JsonObject { ["x"] = 2, ["y"] = 2 }),
            ],
            model: ModelName,
            source: "generate");
        return new ModelOutput { Model = ModelName, Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)] };
    }
}
