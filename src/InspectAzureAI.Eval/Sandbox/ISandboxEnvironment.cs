namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/environment.py</c> <c>SandboxEnvironment</c>: the per-sample execution
/// environment agents and tools run commands in and read/write files through.
/// </summary>
public interface ISandboxEnvironment
{
    /// <summary>Hostname a process inside the sandbox uses to reach the host machine ("127.0.0.1" for the local sandbox, "host.docker.internal" for Docker).</summary>
    string HostAddress { get; }

    /// <summary>
    /// Runs <paramref name="cmd"/> (argv, never a shell string). A missing executable is a failed
    /// <see cref="ExecResult"/>; a provider that cannot run commands at all throws
    /// <see cref="SandboxUnavailableException"/>; an expired <paramref name="timeout"/> throws
    /// <see cref="SandboxTimeoutException"/> carrying the output captured so far.
    /// </summary>
    Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes UTF-8 text, creating parent directories.</summary>
    Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>Writes raw bytes, creating parent directories.</summary>
    Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a UTF-8 text file preserving newlines exactly. Throws <see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/>, an <see cref="IOException"/> for a directory and
    /// <see cref="OutputLimitExceededException"/> beyond the read-file limit.
    /// </summary>
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads a file byte-exact (same errors as <see cref="ReadFileAsync"/>).</summary>
    Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default);
}
