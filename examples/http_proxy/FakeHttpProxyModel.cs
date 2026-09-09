using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.HttpProxy;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> whose every turn is computed from the conversation.
/// It is reached through the sandbox agent bridge twice as "Claude Code" (the plan for the task prompt, then the
/// report once the script's output is in the conversation) and once as "FutureModel" (the haiku, for the request
/// the stand-in script sends to the bridge's OpenAI route).
/// </summary>
internal static class FakeHttpProxyModel
{
    public const string ModelName = "http-proxy-scripted";

    /// <summary>The plan the stand-in CLI gets for the task prompt.</summary>
    public const string Plan = "I'll write futuremodel_haiku.py: it POSTs to https://api.futuremodel.ai/v1/chat/completions with model 'futuremodel-1' and the key from FUTUREMODEL_API_KEY, then I'll run it.";

    /// <summary>The haiku "FutureModel" generates.</summary>
    public const string Haiku = "Silent cursor blinks,\nlogic blooms in nested loops,\nbugs dissolve at dawn.";

    /// <summary>The prefix of the report the stand-in CLI gets once the script has run.</summary>
    public const string ReportPrefix = "The script ran successfully against the FutureModel API. The haiku it generated:\n\n";

    /// <summary>Three turns are needed; the budget leaves room to spare.</summary>
    private const int TurnBudget = 8;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var lastUser = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        const string scriptOutput = "Script output:\n";
        if (lastUser == FakeClaudeCli.HaikuPrompt)
        {
            return Text(Haiku);
        }

        if (lastUser.StartsWith(scriptOutput, StringComparison.Ordinal))
        {
            return Text(ReportPrefix + lastUser[scriptOutput.Length..]);
        }

        return Text(lastUser == HttpProxyExample.Input ? Plan : "I only know the prompts of the http_proxy example.");
    }

    private static ModelOutput Text(string text) => ModelOutput.FromContent(ModelName, text);
}
