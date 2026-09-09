using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Runner;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Factories for the scripted models behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> wrapped as a
/// <see cref="Model"/> whose every turn is computed from the conversation (<see cref="ScriptedTurn.From(Func{IReadOnlyList{ChatMessage}, IReadOnlyList{ToolInfo}, ModelOutput})"/>),
/// so concurrent samples stay deterministic. <see cref="ScriptedTurn.Text"/> hard-codes the output model name
/// "scripted"; these factories carry the example's own model name and a rough token count instead.
/// </summary>
public static class FakeModels
{
    /// <summary>Turns a scripted model serves before it runs out (more than any example's samples times its turns).</summary>
    public const int DefaultTurnBudget = 10_000;

    /// <summary>A model named <paramref name="name"/> whose reply to each request is <paramref name="answer"/> of the conversation, with <see cref="Output"/>'s rough usage.</summary>
    public static Model Answering(string name, Func<IReadOnlyList<ChatMessage>, string> answer, int turns = DefaultTurnBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(answer);
        return Scripted(name, (messages, _) => Output(name, answer(messages), messages), turns);
    }

    /// <summary>A model named <paramref name="name"/> whose every turn is <paramref name="respond"/> over the conversation and the tools offered.</summary>
    public static Model Scripted(string name, Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolInfo>, ModelOutput> respond, int turns = DefaultTurnBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(respond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turns);
        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(respond), turns), name));
    }

    /// <summary>A text completion from <paramref name="name"/> with the rough usage the examples report: about one token per four characters of the input and of <paramref name="text"/>.</summary>
    public static ModelOutput Output(string name, string text, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(messages);
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var outputTokens = text.Length / 4 + 1;
        return ModelOutput.FromContent(name, text) with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }
}
