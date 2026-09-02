namespace InspectAzureAI.Provider.Core;

/// <summary>
/// The <c>str | list[Content]</c> union used for <c>ChatMessageBase.content</c>
/// (<c>src/inspect_ai/model/_chat_message.py</c>). Exactly one of <see cref="Text"/> or
/// <see cref="Items"/> is set.
/// </summary>
public sealed class MessageContent : IEquatable<MessageContent>
{
    private MessageContent(string? text, IReadOnlyList<Content>? items)
    {
        Text = text;
        Items = items;
    }

    /// <summary>String form of the content, or null when <see cref="Items"/> is set.</summary>
    public string? Text { get; }

    /// <summary>List form of the content, or null when <see cref="Text"/> is set.</summary>
    public IReadOnlyList<Content>? Items { get; }

    /// <summary>True when the content is the plain string form.</summary>
    public bool IsString => Items is null;

    public static MessageContent FromString(string text) => new(text, null);

    public static MessageContent FromItems(IEnumerable<Content> items) => new(null, items.ToList());

    public static implicit operator MessageContent(string text) => FromString(text);

    public static implicit operator MessageContent(List<Content> items) => FromItems(items);

    public static implicit operator MessageContent(Content[] items) => FromItems(items);

    public bool Equals(MessageContent? other)
    {
        if (other is null)
        {
            return false;
        }

        if (IsString != other.IsString)
        {
            return false;
        }

        return IsString ? Text == other.Text : Items!.SequenceEqual(other.Items!);
    }

    public override bool Equals(object? obj) => Equals(obj as MessageContent);

    public override int GetHashCode() => IsString ? Text!.GetHashCode() : Items!.Count;

    public override string ToString() => IsString ? Text! : $"[{string.Join(", ", Items!)}]";
}
