using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.TextEditor;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/text_editor.py</c>: a <see cref="ScriptedModelApi"/> whose turns
/// are computed from the conversation. Each of the four <c>generate()</c> turns sees a new user message and answers
/// it with the <c>text_editor</c> call it asks for (<c>create</c>, <c>view</c>, <c>str_replace</c>, <c>insert</c>
/// with <c>insert_text</c>), then, once the tool result is in, a one-line confirmation, so every generate loop
/// ends after one call.
/// </summary>
internal static class FakeTextEditorModel
{
    public const string ModelName = "text-editor-scripted";

    /// <summary>What the scripted model writes with <c>create</c>: the <c>greet</c> function the sample asks for.</summary>
    public const string GreetingSource = "def greet(name):\n    return f'Hello, {name}!'\n";

    /// <summary>Four generate turns of two calls each, with room to spare.</summary>
    private const int TurnBudget = 12;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        if (messages.Count > 0 && messages[^1] is ChatMessageTool result)
        {
            var command = LastCommand(messages);
            return Text(result.Error is { } error
                ? $"The text_editor {command} command failed: {error.Message}"
                : command switch
                {
                    "create" => "I created /tmp/greeting.py with a `greet` function that returns 'Hello, {name}!'.",
                    "view" => "The file contains the `greet` function as expected.",
                    "str_replace" => "I replaced 'Hello' with 'Goodbye' in /tmp/greeting.py.",
                    "insert" => "I inserted '# Author: Inspector' after line 1 of /tmp/greeting.py.",
                    _ => "Done.",
                });
        }

        var request = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        return request switch
        {
            TextEditorTask.Input => Call(new { command = "create", path = TextEditorTask.FilePath, file_text = GreetingSource }, "Creating the file with the text_editor tool."),
            TextEditorTask.ViewMessage => Call(new { command = "view", path = TextEditorTask.FilePath }, "Viewing the file."),
            TextEditorTask.ReplaceMessage => Call(new { command = "str_replace", path = TextEditorTask.FilePath, old_str = "Hello", new_str = "Goodbye" }, "Replacing Hello with Goodbye."),
            TextEditorTask.InsertMessage => Call(new { command = "insert", path = TextEditorTask.FilePath, insert_line = 1, insert_text = "# Author: Inspector" }, "Inserting the author line after line 1."),
            _ => Text("I'm not sure what to do next."),
        };
    }

    private static string LastCommand(IReadOnlyList<ChatMessage> messages) =>
        messages.OfType<ChatMessageAssistant>().LastOrDefault(message => message.ToolCalls is { Count: > 0 })?.ToolCalls![0].Arguments["command"]?.GetValue<string>() ?? "";

    private static ModelOutput Call(object arguments, string thought) => ScriptedTurn.ToolCall("text_editor", arguments, text: thought).Output!;

    private static ModelOutput Text(string text) => ModelOutput.FromContent(ModelName, text);
}
