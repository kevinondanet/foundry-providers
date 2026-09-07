using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tool.py</c> <c>ToolResult</c>: either plain text (which is what gets truncated) or a
/// list of content items passed to the model untouched.
/// </summary>
public sealed record ToolResult
{
    public string? Text { get; init; }

    public IReadOnlyList<Content>? Contents { get; init; }

    public static readonly ToolResult Empty = new() { Text = "" };

    public static implicit operator ToolResult(string text) => new() { Text = text };

    public static ToolResult FromContents(IEnumerable<Content> contents) => new() { Contents = contents.ToArray() };

    /// <summary>The text handed to the model (content lists contribute their text items).</summary>
    public string AsText() => Text ?? string.Join("\n", (Contents ?? []).OfType<ContentText>().Select(c => c.Text));
}
