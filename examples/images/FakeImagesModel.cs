using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Images;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/images/images.py</c>: a <see cref="ScriptedModelApi"/> that
/// answers the two questions of <c>images.jsonl</c> in the bracketed form the system message asks for, without a
/// network and without looking at the pixels. Every turn is computed from the conversation (the two samples run
/// concurrently): the image part of the last user message names the file, <c>ballons.png</c> gets <c>[3]</c> and
/// <c>bike.png</c> gets <c>[bike]</c>; when the image is already inlined (a data URI) the question text decides.
/// </summary>
internal static class FakeImagesModel
{
    public const string ModelName = "images-scripted";

    /// <summary>The answer for <c>ballons.png</c> ("How many ballons are in this picture?", target "3").</summary>
    public const string BallonsAnswer = "[3]";

    /// <summary>The answer for <c>bike.png</c> ("What is this a picture of?", target ["bike", "bicycle"]).</summary>
    public const string BikeAnswer = "[bike]";

    /// <summary>The answer for anything else.</summary>
    public const string UnknownAnswer = "[unknown]";

    /// <summary>Two samples, one call each; room for epochs.</summary>
    private const int TurnBudget = 64;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>One turn: the bracketed answer for the image (or question) of the last user message.</summary>
    public static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var user = messages.OfType<ChatMessageUser>().LastOrDefault();
        var answer = user is null ? UnknownAnswer : Answer(user);
        var output = ModelOutput.FromContent(ModelName, answer);
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var outputTokens = answer.Length / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }

    /// <summary>The answer for <paramref name="user"/>: by the image file name when the message carries a file path, else by the question.</summary>
    public static string Answer(ChatMessageUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        foreach (var image in user.ContentList.OfType<ContentImage>())
        {
            if (image.Image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = Path.GetFileName(image.Image);
            if (file.Equals("ballons.png", StringComparison.OrdinalIgnoreCase))
            {
                return BallonsAnswer;
            }

            if (file.Equals("bike.png", StringComparison.OrdinalIgnoreCase))
            {
                return BikeAnswer;
            }
        }

        var question = user.Text;
        if (question.Contains("how many", StringComparison.OrdinalIgnoreCase))
        {
            return BallonsAnswer;
        }

        return question.Contains("picture of", StringComparison.OrdinalIgnoreCase) ? BikeAnswer : UnknownAnswer;
    }
}
