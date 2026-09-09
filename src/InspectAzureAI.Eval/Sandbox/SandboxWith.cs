using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/context.py</c> <c>sandbox_with</c> and <c>sandbox_file_detector</c>: finds the sandbox of
/// the current sample that has a given file (or, with <c>onPath</c>, a program on its PATH). Discoveries are cached
/// per sample under Python's <c>"{name}:{file}:{on_path}"</c> key (<c>sandbox_with_environments_context_var</c>); the
/// cache lives on the sample's <see cref="SandboxEnvironments"/> instance, so it needs no field on
/// <see cref="SampleContext"/>. The single port shared by the computer, web-browser and legacy tool-support lookups.
/// </summary>
public static class SandboxWith
{
    private const string NoSandboxMessage =
        "No sandbox environment has been provided for the current sample or task. "
        + "Please specify a sandbox for the sample or a global default sandbox for the task";

    private static readonly ConditionalWeakTable<SandboxEnvironments, ConcurrentDictionary<string, ISandboxEnvironment>> Discovered = new();

    /// <summary>
    /// Port of <c>sandbox_with(file, on_path, name)</c>: the sandbox that has <paramref name="file"/> (a path, or a
    /// program name when <paramref name="onPath"/>), searching the named sandbox only when <paramref name="name"/> is
    /// given; null when none has it. No sandboxes at all is an <see cref="InvalidOperationException"/>
    /// (Python: <c>ProcessLookupError</c>).
    /// </summary>
    public static async Task<ISandboxEnvironment?> FindAsync(string file, bool onPath = false, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var sandboxes = SampleContext.Current?.Sandboxes ?? throw new InvalidOperationException(NoSandboxMessage);
        var cache = Discovered.GetValue(sandboxes, _ => new ConcurrentDictionary<string, ISandboxEnvironment>(StringComparer.Ordinal));
        var key = $"{name ?? ""}:{file}:{onPath}";
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IEnumerable<ISandboxEnvironment> candidates = name is null
            ? sandboxes.Environments.Values
            : sandboxes.Environments.TryGetValue(name, out var named) ? [named] : [];
        foreach (var environment in candidates)
        {
            var found = onPath
                ? await IsOnPathAsync(environment, file, cancellationToken).ConfigureAwait(false)
                : await IsFileReadableAsync(environment, file, cancellationToken).ConfigureAwait(false);
            if (found)
            {
                cache[key] = environment;
                return environment;
            }
        }

        return null;
    }

    /// <summary>Port of <c>sandbox_file_detector(file, on_path=True)</c>: <c>which file</c>; an unavailable sandbox is no match.</summary>
    public static async Task<bool> IsOnPathAsync(ISandboxEnvironment sandbox, string file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        try
        {
            return (await sandbox.ExecAsync(["which", file], cancellationToken: cancellationToken).ConfigureAwait(false)).Success;
        }
        catch (SandboxUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of <c>_is_file_readable</c>: <c>test -r file</c> (any failure is ignored, providers raise their own
    /// types), then the portable fallback of reading the file.
    /// </summary>
    public static async Task<bool> IsFileReadableAsync(ISandboxEnvironment sandbox, string file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        try
        {
            var result = await sandbox.ExecAsync(["test", "-r", file], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch broadly because sandbox providers may raise a variety of provider-specific exceptions.
        }

        try
        {
            await sandbox.ReadFileBytesAsync(file, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException or DecoderFallbackException)
        {
            return false;
        }
    }
}
