using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.CodeExecution;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/code_execution.py</c>: a <see cref="ScriptedModelApi"/> that
/// calls <c>code_execution</c> with <c>print(435678 + 23457)</c>, then reports the number the tool printed (or
/// the tool error).
/// </summary>
internal static class FakeCodeExecutionModel
{
    public const string ModelName = "code-execution-scripted";

    /// <summary>The code the scripted model executes.</summary>
    public const string Code = "print(435678 + 23457)";

    private const int TurnBudget = 4;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var result = messages.OfType<ChatMessageTool>().FirstOrDefault();
        if (result is null)
        {
            return ScriptedTurn.ToolCall("code_execution", new { code = Code }, text: "I'll run the addition with the code_execution tool.").Output!;
        }

        var text = result.Error is { } error
            ? $"The code could not be executed: {error.Message}"
            : $"The result of 435678 + 23457 is {result.Text.Trim()}.";
        return ModelOutput.FromContent(ModelName, text);
    }
}
