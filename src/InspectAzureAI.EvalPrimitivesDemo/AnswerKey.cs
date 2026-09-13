using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.EvalPrimitivesDemo;

/// <summary>
/// The scripted model's "brain": a fixed reply for each quiz question, chosen by looking at the last user
/// message. Because the reply is a pure function of the conversation, every run - and every concurrently
/// running sample - gets exactly the same answers, which is what makes the whole eval deterministic.
/// The replies are deliberately varied (a sentence, a decimal, a wrong answer, a multiple-choice line) so
/// that different scorers disagree about them.
/// </summary>
public static class AnswerKey
{
    private static readonly (string Needle, string Reply)[] Replies =
    [
        ("capital of France", "The capital of France is Paris."),   // answer buried in a sentence
        ("7 * 6", "42.0"),                                           // right, but as a decimal
        ("12 - 5", "8"),                                             // deliberately wrong
        ("Red Planet", "ANSWER: B\nMars is the red planet."),         // multiple-choice convention, then chatter
        ("primary colour", "Blue"),                                  // one of several accepted targets
        ("Estimate pi", "3.1416"),                                   // more precision than the target
    ];

    /// <summary>What the model says next, given the conversation so far.</summary>
    public static string Reply(IReadOnlyList<ChatMessage> messages)
    {
        var question = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        foreach (var (needle, reply) in Replies)
        {
            if (question.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return reply;
            }
        }

        return "I don't know.";
    }
}
