using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.Reasoning;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that plays a reasoning model driving the react
/// agent of <c>examples/reasoning.py</c> without any network access. Each turn is computed from the conversation:
/// the first two turns reason about one problem each (a <see cref="ContentReasoning"/> item ahead of the text, the
/// shape a claude-* deployment with extended thinking produces), then call <c>validate</c> with the answer; the
/// third turn submits, which ends the react loop. Usage reports the reasoning tokens the way a real model would.
/// </summary>
internal static class FakeReasoningModel
{
    public const string ModelName = "reasoning-scripted";

    /// <summary>More turns than the script needs; past its script the model always submits.</summary>
    private const int TurnBudget = 16;

    /// <summary>One entry per problem: the reasoning, the visible text, and the answer handed to <c>validate</c>.</summary>
    internal static readonly (string Reasoning, string Text, string Answer)[] Script =
    [
        (
            "The first problem is 3*x^3 - 5*x = 1, i.e. f(x) = 3x^3 - 5x - 1 = 0. Sign changes locate the roots: f(-2) = -15, f(-1) = 1, f(0) = -1, f(1) = -3, f(2) = 13, so there are three real roots, one in each of (-2, -1), (-1, 0) and (1, 2). Newton's method (f'(x) = 9x^2 - 5) from 1.5 gives 1.393, 1.3815, 1.3813; from -0.2 gives -0.2052; from -1.2 gives -1.1762.",
            "The equation 3x^3 - 5x = 1 has three real roots: x ≈ 1.3813, x ≈ -0.2052 and x ≈ -1.1762. I will validate this answer.",
            "x ≈ 1.3813, x ≈ -0.2052 or x ≈ -1.1762"),
        (
            "The second problem is x^2 - 5x + 6 = 0. Two numbers with sum 5 and product 6 are 2 and 3, so the quadratic factors as (x - 2)(x - 3) = 0 and the roots are x = 2 and x = 3. Check: 4 - 10 + 6 = 0 and 9 - 15 + 6 = 0.",
            "x^2 - 5x + 6 = 0 factors as (x - 2)(x - 3) = 0, so x = 2 or x = 3. I will validate this answer as well.",
            "x = 2 or x = 3"),
    ];

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        ModelOutput output;
        if (step < Script.Length)
        {
            var (reasoning, text, answer) = Script[step];
            output = Turn(reasoning, text, "validate", new { answer });
        }
        else
        {
            var validated = messages.OfType<ChatMessageTool>().Count(message => message.Function == "validate");
            output = Turn(
                $"Both answers have been validated ({validated} validate call{(validated == 1 ? "" : "s")} returned True); I can submit them.",
                "Both problems are solved and validated.",
                Agents.DefaultSubmitName,
                new { answer = "3x^3 - 5x = 1: x ≈ 1.3813, x ≈ -0.2052 or x ≈ -1.1762. x^2 - 5x + 6 = 0: x = 2 or x = 3." });
        }

        return WithUsage(messages, output);
    }

    /// <summary>An assistant turn of a reasoning item, a text item and one tool call.</summary>
    private static ModelOutput Turn(string reasoning, string text, string function, object arguments)
    {
        var call = new ToolCall(ShortUuid.Generate(), function, System.Text.Json.JsonSerializer.SerializeToNode(arguments)!.AsObject());
        var message = new ChatMessageAssistant(
            MessageContent.FromItems([new ContentReasoning(reasoning), new ContentText(text)]),
            toolCalls: [call],
            model: ModelName,
            source: "generate");
        return new ModelOutput { Model = ModelName, Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)] };
    }

    /// <summary>A rough token count, with the reasoning counted as reasoning tokens, so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var reasoning = output.Message.ContentList.OfType<ContentReasoning>().Sum(item => item.Reasoning.Length) / 4 + 1;
        var completion = output.Completion.Length + (output.Message.ToolCalls?.Sum(call => call.Arguments.ToJsonString().Length) ?? 0);
        var outputTokens = completion / 4 + 1 + reasoning;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) { ReasoningTokens = reasoning } };
    }
}
