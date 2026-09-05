using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of the <c>str | list[ChatMessage]</c> input of <c>Sample</c> and <c>TaskState</c>.</summary>
public sealed record SampleInput
{
    public string? Text { get; init; }

    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    public bool IsText => Messages is null;

    public static implicit operator SampleInput(string text) => new() { Text = text };

    public static implicit operator SampleInput(ChatMessage[] messages) => new() { Messages = messages };

    public static implicit operator SampleInput(List<ChatMessage> messages) => new() { Messages = messages.ToArray() };

    /// <summary>Port of the runner's input conversion: a string becomes one user message, every message is tagged <c>source="input"</c>.</summary>
    public IReadOnlyList<ChatMessage> ToMessages()
    {
        if (Messages is { } messages)
        {
            return messages.Select(m => m.Source is null ? m with { Source = "input" } : m).ToArray();
        }

        return [new ChatMessageUser(Text ?? "") { Source = "input" }];
    }

    public override string ToString() => Text ?? string.Join("\n", (Messages ?? []).Select(m => m.Text));
}
