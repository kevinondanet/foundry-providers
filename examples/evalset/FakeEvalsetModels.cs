using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Evalset;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The two scripted models behind <c>--fake</c>, standing in for <c>openai/gpt-4o-mini</c> and
/// <c>anthropic/claude-3-5-haiku-latest</c>: each answers Yes or No to a popularity prompt, a short security
/// answer to a security_guide question, and <c>GRADE: C</c> or <c>GRADE: I</c> to the model_graded_fact grading
/// prompt (all chosen deterministically from the prompt text, so the two models agree on some samples and not
/// others). The first model's first call throws, so a run shows the eval set retrying that task and reusing the
/// samples it had completed.
/// </summary>
public static class FakeEvalsetModels
{
    /// <summary>The first model's name (stands in for <c>openai/gpt-4o-mini</c>).</summary>
    public const string FirstModelName = "scripted/gpt-4o-mini";

    /// <summary>The second model's name (stands in for <c>anthropic/claude-3-5-haiku-latest</c>).</summary>
    public const string SecondModelName = "scripted/claude-3-5-haiku-latest";

    /// <summary>The message of the first model's scripted outage.</summary>
    public const string OutageMessage = "scripted provider outage: the first call to scripted/gpt-4o-mini fails so the eval set retries the task";

    /// <summary>Turns queued per model: 116 calls per pass (16 security_guide generates and grades, 100 popularity generates), with room for retries and epochs.</summary>
    public const int Turns = 10_000;

    /// <summary>The marker of the model_graded_fact grading prompt.</summary>
    private const string GradingMarker = "[BEGIN DATA]";

    private static readonly string[] SecurityAnswers =
    [
        "Use parameterized queries and validate all input.",
        "Apply output encoding and a strict content security policy.",
        "Enforce least privilege and rotate credentials regularly.",
        "Validate and canonicalise paths before opening files.",
        "Use constant-time comparison and a vetted crypto library.",
    ];

    /// <summary>The first model: an outage on its first call, then scripted answers.</summary>
    public static Model First(bool outage = true)
    {
        var turns = Enumerable.Repeat(ScriptedTurn.From((messages, tools) => Answer(FirstModelName, messages, bias: 0)), Turns);
        if (outage)
        {
            turns = turns.Prepend(ScriptedTurn.Throw(new InvalidOperationException(OutageMessage)));
        }

        return new Model(new ScriptedModelApi(turns, FirstModelName));
    }

    /// <summary>The second model: scripted answers only.</summary>
    public static Model Second() =>
        new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From((messages, tools) => Answer(SecondModelName, messages, bias: 1)), Turns), SecondModelName));

    /// <summary>The scripted answer of <paramref name="model"/> to <paramref name="messages"/>; <paramref name="bias"/> shifts the coin so the two models differ.</summary>
    public static ModelOutput Answer(string model, IReadOnlyList<ChatMessage> messages, int bias)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);
        var prompt = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        var system = messages.FirstOrDefault(message => message is ChatMessageSystem)?.Text ?? "";
        var coin = (CharacterSum(prompt) + bias) % 3;
        string text;
        if (prompt.Contains(GradingMarker, StringComparison.Ordinal))
        {
            text = coin == 0 ? "The submission misses the expert's point.\n\nGRADE: I" : "The submission covers the expert answer.\n\nGRADE: C";
        }
        else if (system.Contains("computer security expert", StringComparison.Ordinal))
        {
            text = SecurityAnswers[(CharacterSum(prompt) + bias) % SecurityAnswers.Length];
        }
        else
        {
            text = coin == 0 ? "No" : "Yes";
        }

        return ModelOutput.FromContent(model, text);
    }

    private static int CharacterSum(string text)
    {
        var sum = 0;
        foreach (var c in text)
        {
            sum += c;
        }

        return sum;
    }
}
