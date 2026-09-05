using System.Globalization;

namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/limits.py</c>: exec output is kept-the-tail at
/// <see cref="MaxExecOutputSize"/> and file reads are refused above <see cref="MaxReadFileSize"/>;
/// both are overridable through environment variables, as in Python.
/// </summary>
public static class SandboxLimits
{
    public const string MaxExecOutputSizeVar = "INSPECT_SANDBOX_MAX_EXEC_OUTPUT_SIZE";

    public const string MaxReadFileSizeVar = "INSPECT_SANDBOX_MAX_READ_FILE_SIZE";

    public const long DefaultMaxExecOutputSize = 10L * 1024 * 1024;

    public const long DefaultMaxReadFileSize = 100L * 1024 * 1024;

    /// <summary>Exec output cap in bytes (read per call so a test can override the variable).</summary>
    public static long MaxExecOutputSize => Resolve(MaxExecOutputSizeVar, DefaultMaxExecOutputSize);

    /// <summary>Read-file cap in bytes.</summary>
    public static long MaxReadFileSize => Resolve(MaxReadFileSizeVar, DefaultMaxReadFileSize);

    /// <summary>Port of <c>_human_readable_size</c>: "100 MiB", "8 KiB", "12 bytes".</summary>
    public static string HumanReadableSize(long bytes)
    {
        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;
        if (bytes >= gib && bytes % gib == 0)
        {
            return $"{bytes / gib} GiB";
        }

        if (bytes >= mib && bytes % mib == 0)
        {
            return $"{bytes / mib} MiB";
        }

        if (bytes >= kib && bytes % kib == 0)
        {
            return $"{bytes / kib} KiB";
        }

        return $"{bytes} bytes";
    }

    private static long Resolve(string variable, long fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"{variable} must be a non-negative integer number of bytes, got '{value}'.");
        }

        return parsed;
    }
}
