using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>agent/_bridge/types.py</c> <c>_MessageFingerprint</c>: the (role, hash-of-text) identity used for
/// thread prefix comparisons. Ids and metadata are deliberately excluded because messages round-trip through
/// the scaffold's own conversation store between calls, so only role and text are stable.
/// </summary>
internal readonly record struct MessageFingerprint(string Role, string TextHash)
{
    /// <summary>Port of <c>_message_fingerprint</c>.</summary>
    public static MessageFingerprint Of(ChatMessage message) => new(message.Role, Mm3Hash.Hash(message.Text));

    /// <summary>
    /// Port of <c>_condensed_fingerprint</c>: the same message after transcript condensation replaced its text
    /// with <c>attachment://&lt;mm3-hash-of-text&gt;</c> — computable from the fingerprint alone because the
    /// attachment id is the hash the fingerprint already stores.
    /// </summary>
    public MessageFingerprint Condensed() => new(Role, Mm3Hash.Hash(ThreadTracking.AttachmentProtocol + TextHash));
}

/// <summary>
/// Port of <c>_Descent</c>: graded descent-from-initial-input verdict, ordered by strength of evidence that a
/// thread is the main conversation. <c>Quoted</c> (nothing but the initial input in literal double quotes)
/// outranks <c>Exact</c> because quote-wrapping is a scaffold's conversation-store transform (only the
/// persisted main thread produces it) whereas a verbatim resend is also what side calls produce by copying
/// the raw input; generic containment ranks below both.
/// </summary>
internal enum Descent
{
    No = 0,
    Contained = 1,
    Exact = 2,
    Quoted = 3,
}

/// <summary>Port of the module-level thread-tracking helpers of <c>agent/_bridge/types.py</c>.</summary>
internal static class ThreadTracking
{
    /// <summary>Port of <c>log/_condense.py</c> <c>ATTACHMENT_PROTOCOL</c>.</summary>
    public const string AttachmentProtocol = "attachment://";

    /// <summary>
    /// Port of <c>_ANCHOR_CONTAINMENT_MIN_CHARS</c>: below this a side call could contain the initial text by
    /// coincidence (a bash path-detection call quoting a short prompt), so short prompts anchor only by exact,
    /// condensed or quote-wrapped match.
    /// </summary>
    public const int AnchorContainmentMinChars = 20;

    /// <summary>Port of <c>_extends</c>: whether <paramref name="fps"/> is a proper extension (continuation) of <paramref name="prefix"/>.</summary>
    public static bool Extends(IReadOnlyList<MessageFingerprint> prefix, IReadOnlyList<MessageFingerprint> fps)
    {
        if (fps.Count <= prefix.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (fps[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Port of <c>_position_descent</c>: how one aligned message anchors on its initial counterpart. Verbatim
    /// and condensed forms are <c>Exact</c>; a same-role message that is exactly the initial text (or its
    /// condensed reference) in double quotes is <c>Quoted</c>; containing the initial text (subject to
    /// <see cref="AnchorContainmentMinChars"/>) or its exact attachment reference is <c>Contained</c>.
    /// </summary>
    public static Descent PositionDescent(ChatMessage message, MessageFingerprint fp, MessageFingerprint initial, MessageFingerprint condensed, string initialText)
    {
        if (fp == initial || fp == condensed)
        {
            return Descent.Exact;
        }

        if (fp.Role != initial.Role)
        {
            return Descent.No;
        }

        var text = message.Text;
        var stripped = text.Trim();
        var reference = AttachmentProtocol + initial.TextHash;
        if (stripped.Length >= 2 && stripped[0] == '"' && stripped[^1] == '"')
        {
            var interior = stripped[1..^1].Trim();
            if (interior == reference || (initialText.Length > 0 && interior == initialText))
            {
                return Descent.Quoted;
            }
        }

        if ((initialText.Length >= AnchorContainmentMinChars && text.Contains(initialText, StringComparison.Ordinal))
            || text.Contains(reference, StringComparison.Ordinal))
        {
            return Descent.Contained;
        }

        return Descent.No;
    }
}
