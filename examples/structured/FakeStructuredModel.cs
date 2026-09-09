using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Structured;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that answers the colour questions of
/// <c>examples/structured.py</c> without any network access. Every turn is computed from the conversation (the two
/// samples run concurrently): the first user message names the colour (white or black) and the reply takes the
/// shape <see cref="Reply"/> asks for: the JSON a model honouring the <c>color</c> response schema returns, the
/// <c>RGB: r,g,b</c> text guided decoding would force, or a sentence that satisfies neither scorer so their error
/// branches can be seen. Nothing here fakes guided decoding itself: the scripted api, like the Foundry apis,
/// ignores <c>extra_body</c>.
/// </summary>
internal static class FakeStructuredModel
{
    public const string ModelName = "structured-scripted";

    /// <summary>More turns than the two samples need, so <c>--epochs</c> works too.</summary>
    private const int TurnBudget = 64;

    /// <summary>The shape of the scripted reply (<c>-T reply=json|rgb|malformed</c>).</summary>
    public enum Reply
    {
        /// <summary><c>{"red":255,"green":255,"blue":255}</c>, what a model given the <c>color</c> response schema returns.</summary>
        Json,

        /// <summary><c>RGB: 255,255,255</c>, what vLLM / SGLang guided decoding would force.</summary>
        Rgb,

        /// <summary>A sentence that neither scorer can read.</summary>
        Malformed,
    }

    public static Model Create(Reply reply = Reply.Json) =>
        new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From((messages, _) => Respond(reply, messages)), TurnBudget), ModelName));

    /// <summary>Parses a <c>-T reply=</c> value (case-insensitive); null is <see cref="Reply.Json"/>.</summary>
    public static Reply ParseReply(string? value) =>
        value is null ? Reply.Json
        : Enum.TryParse<Reply>(value, ignoreCase: true, out var reply) ? reply
        : throw new ArgumentException($"-T reply expects json, rgb or malformed, got '{value}'");

    private static ModelOutput Respond(Reply reply, IReadOnlyList<ChatMessage> messages)
    {
        var question = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var (name, red, green, blue) = question.Contains("white", StringComparison.OrdinalIgnoreCase) ? ("white", 255, 255, 255)
            : question.Contains("black", StringComparison.OrdinalIgnoreCase) ? ("black", 0, 0, 0)
            : ("grey", 128, 128, 128);
        var text = reply switch
        {
            Reply.Json => $"{{\"red\":{red},\"green\":{green},\"blue\":{blue}}}",
            Reply.Rgb => $"RGB: {red},{green},{blue}",
            _ => $"The RGB color for {name} is ({red}, {green}, {blue}).",
        };
        return WithUsage(messages, ModelOutput.FromContent(ModelName, text));
    }

    /// <summary>A rough token count so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var outputTokens = output.Completion.Length / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }
}
