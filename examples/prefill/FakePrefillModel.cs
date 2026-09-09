using System.Globalization;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Prefill;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that plays a model given the prefilled
/// conversation of <c>examples/prefill.py</c> without any network access. Every turn is computed from the
/// conversation (the three samples run concurrently): the trailing assistant message is the prefill
/// (<c>1+1=</c>), so the model evaluates that expression and answers its continuation (<c>2</c>), which is what
/// a claude-* deployment does with a native prefill. With <paramref name="prose"/> it answers in a sentence
/// instead ("The answer is 2."), the way a model that does not honour the prefill might, so the scorer's
/// "Could not extract a numerical answer" branch can be seen offline.
/// </summary>
internal static partial class FakePrefillModel
{
    public const string ModelName = "prefill-scripted";

    /// <summary>More turns than the three samples need, so <c>--epochs</c> works too.</summary>
    private const int TurnBudget = 64;

    public static Model Create(bool prose = false) =>
        new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(prose ? RespondProse : RespondContinuation), TurnBudget), ModelName));

    /// <summary>The prefill continuation: "1+1=" is answered "2".</summary>
    private static ModelOutput RespondContinuation(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var answer = Evaluate(Prefill(messages));
        return WithUsage(messages, ModelOutput.FromContent(ModelName, answer is { } value ? value.ToString(CultureInfo.InvariantCulture) : "I am not sure."));
    }

    /// <summary>A model that ignores the prefill and restates the answer in prose.</summary>
    private static ModelOutput RespondProse(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var answer = Evaluate(Prefill(messages));
        return WithUsage(messages, ModelOutput.FromContent(ModelName, answer is { } value ? $"The answer is {value.ToString(CultureInfo.InvariantCulture)}." : "I cannot work that out."));
    }

    /// <summary>The trailing assistant message the prefill solver appended (empty when the conversation has none).</summary>
    private static string Prefill(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && messages[^1] is ChatMessageAssistant assistant ? assistant.Text : "";

    /// <summary>Evaluates a prefill of the shape <c>a+b=</c>, <c>a-b=</c> or <c>a*b=</c>; null for anything else.</summary>
    internal static long? Evaluate(string prefill)
    {
        var match = Expression().Match(prefill);
        if (!match.Success)
        {
            return null;
        }

        var left = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var right = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "+" => left + right,
            "-" => left - right,
            _ => left * right,
        };
    }

    /// <summary>A rough token count so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var outputTokens = output.Completion.Length / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }

    [GeneratedRegex(@"^\s*(\d+)\s*([+\-*])\s*(\d+)\s*=\s*$")]
    private static partial Regex Expression();
}
