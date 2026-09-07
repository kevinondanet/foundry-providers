using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Swe.Util;

/// <summary>Port of inspect_swe <c>_util/messages.py</c> <c>UserTurn</c>: the user messages that form the next prompt.</summary>
public sealed record UserTurn(IReadOnlyList<ChatMessageUser> Messages, bool HasAssistantResponse);

/// <summary>Port of inspect_swe <c>_util/messages.py</c> (<c>user_turn</c>, <c>build_user_prompt</c>, <c>collect_user_images</c>).</summary>
public static class AgentPrompt
{
    /// <summary>
    /// Port of <c>user_turn</c>: the user messages after the last assistant response (all of them when there is
    /// none yet). A conversation ending with an assistant message is an <see cref="ArgumentException"/>.
    /// </summary>
    public static UserTurn GetUserTurn(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count > 0 && messages[^1] is ChatMessageAssistant)
        {
            throw new ArgumentException("Messages input ends with an assistant messages.");
        }

        var lastAssistant = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is ChatMessageAssistant)
            {
                lastAssistant = i;
                break;
            }
        }

        var users = messages.Skip(lastAssistant + 1).OfType<ChatMessageUser>().ToArray();
        return new UserTurn(users, lastAssistant >= 0);
    }

    /// <summary>
    /// Port of <c>build_user_prompt</c>: the text of the current user turn joined by blank lines. Non-text
    /// content the agent cannot carry is dropped with a warning unless its type is in <paramref name="handledContent"/>.
    /// </summary>
    public static (string Prompt, bool HasAssistantResponse) BuildUserPrompt(IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string>? handledContent = null)
    {
        var turn = GetUserTurn(messages);
        var dropped = turn.Messages
            .Where(m => !m.Content.IsString)
            .SelectMany(m => m.ContentList)
            .Select(c => c.Type)
            .Where(type => type != "text" && (handledContent is null || !handledContent.Contains(type)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (dropped.Length > 0)
        {
            ProviderLogger.Warning($"Input contains {string.Join(", ", dropped)} content, which this agent does not support; it was dropped from the prompt.");
        }

        var prompt = string.Join("\n\n", turn.Messages.Select(m => m.Text));
        return (prompt, turn.HasAssistantResponse);
    }

    /// <summary>Port of <c>collect_user_images</c>: the images in the current user turn.</summary>
    public static IReadOnlyList<ContentImage> CollectUserImages(IReadOnlyList<ChatMessage> messages) =>
        GetUserTurn(messages).Messages
            .Where(m => !m.Content.IsString)
            .SelectMany(m => m.ContentList)
            .OfType<ContentImage>()
            .ToArray();
}
