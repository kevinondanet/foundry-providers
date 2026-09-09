using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.InlineCards;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>get_model("mockllm/model", custom_outputs=[...])</c> of the four <c>examples/inline_cards</c> tasks: a
/// <see cref="ScriptedModelApi"/> serving the scripted outputs in order, keyed on the number of assistant turns in
/// the conversation (so epochs and retries stay deterministic); past the last output the last one is repeated,
/// which in every demo is the <c>submit</c> call.
/// </summary>
public static class MockLlm
{
    /// <summary>The scripted model's name, as it appears in the banner and the log (Python: <c>mockllm/model</c>).</summary>
    public const string ModelName = "inline-cards-mockllm";

    /// <summary>More turns than a run needs (every demo has one sample and a message limit of 10).</summary>
    private const int TurnBudget = 1000;

    /// <summary>A model replaying <paramref name="outputs"/> (each a factory, so every turn gets a fresh tool call id).</summary>
    public static Model Create(params IReadOnlyList<Func<ScriptedTurn>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count == 0)
        {
            throw new ArgumentException("At least one scripted output is needed.", nameof(outputs));
        }

        ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
        {
            var step = messages.Count(message => message is ChatMessageAssistant);
            return outputs[Math.Min(step, outputs.Count - 1)]().Output!;
        }

        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));
    }

    /// <summary>
    /// What <see cref="InlineCardsExample.CreateFakeModel"/> hands the runner: every task carries its own scripted model
    /// (Python's <c>Task(model=...)</c>), which wins over the runner's, so this one is never asked for a turn.
    /// </summary>
    public static Model Placeholder() => new(new ScriptedModelApi([], ModelName));
}
