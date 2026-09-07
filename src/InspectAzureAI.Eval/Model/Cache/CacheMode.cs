namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// Port of the <c>cache: Literal["read", "write"] | None</c> field of <c>event/_model.py</c> <c>ModelEvent</c>:
/// <see cref="Read"/> when the output was served from the prompt cache, <see cref="Write"/> when the attempt ran under a
/// cache policy (its output is stored on success).
/// </summary>
public enum CacheMode
{
    Read,
    Write,
}

/// <summary>Wire-name helpers for <see cref="CacheMode"/>.</summary>
public static class CacheModeExtensions
{
    /// <summary>The Python literal value (<c>read</c> or <c>write</c>).</summary>
    public static string ToWire(this CacheMode mode) => mode switch
    {
        CacheMode.Read => "read",
        CacheMode.Write => "write",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cache mode."),
    };

    /// <summary>Parses a Python literal value; anything but <c>read</c> or <c>write</c> is an <see cref="ArgumentException"/>.</summary>
    public static CacheMode FromWire(string value) => value switch
    {
        "read" => CacheMode.Read,
        "write" => CacheMode.Write,
        _ => throw new ArgumentException($"Unknown cache mode '{value}'.", nameof(value)),
    };
}
