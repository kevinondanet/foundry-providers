using System.Text;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of the <c>subprocess()</c> call surface of <c>util/_subprocess.py</c> as used by the docker and local
/// providers: one host process, argv (never a shell string), optional raw stdin bytes, a host-side timeout
/// and a keep-the-tail output cap. Injectable so <see cref="DockerCli"/> can be exercised without Docker.
/// </summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>A host process to run: <see cref="FileName"/> plus argv entries passed verbatim through <c>ProcessStartInfo.ArgumentList</c>.</summary>
internal sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Bytes written to stdin (then closed); null leaves stdin unredirected.</summary>
    public ReadOnlyMemory<byte>? Input { get; init; }

    /// <summary>Working directory of the process (the local sandbox's sample directory); null inherits ours.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Variables added to the process environment. Secrets travel this way rather than on the command line
    /// (the docker CLI forwards a bare <c>-e NAME</c> from its own environment), so they never show in <c>ps</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Host-side guard: the process tree is killed when it expires (<see cref="ProcessResult.TimedOut"/>).</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Per-stream cap in bytes; only the tail is kept.</summary>
    public long OutputLimit { get; init; } = SandboxLimits.DefaultMaxExecOutputSize;

    /// <summary>When true the process is killed as soon as a stream exceeds <see cref="OutputLimit"/> (file reads) instead of trimming silently (exec output).</summary>
    public bool AbortOnOutputLimit { get; init; }
}

/// <summary>Outcome of a <see cref="ProcessRequest"/>: exit code, the retained tail of each stream and how many bytes each stream produced in total.</summary>
internal sealed record ProcessResult(
    int ExitCode,
    byte[] Stdout,
    byte[] Stderr,
    long StdoutTotal,
    long StderrTotal,
    bool TimedOut = false,
    bool OutputLimitExceeded = false)
{
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public bool Success => ExitCode == 0 && !TimedOut && !OutputLimitExceeded;

    public string StdoutText => Decode(Stdout, StdoutTotal);

    public string StderrText => Decode(Stderr, StderrTotal);

    /// <summary>Port of the output a <c>TimeoutError</c> carries: stdout, then stderr, joined by a newline when both are present.</summary>
    public string CombinedText => SandboxOutput.Combine(StdoutText, StderrText);

    public static ProcessResult Ok(string stdout = "", string stderr = "") =>
        FromText(0, stdout, stderr);

    public static ProcessResult Failed(int exitCode, string stderr = "", string stdout = "") =>
        FromText(exitCode, stdout, stderr);

    public static ProcessResult FromText(int exitCode, string stdout, string stderr)
    {
        var outBytes = LenientUtf8.GetBytes(stdout);
        var errBytes = LenientUtf8.GetBytes(stderr);
        return new ProcessResult(exitCode, outBytes, errBytes, outBytes.Length, errBytes.Length);
    }

    private static string Decode(byte[] bytes, long total)
    {
        var start = 0;
        if (total > bytes.Length)
        {
            // The tail cut can land inside a multi-byte sequence; skip its continuation bytes rather than
            // surfacing a replacement character the command never produced.
            while (start < bytes.Length && start < 3 && (bytes[start] & 0xC0) == 0x80)
            {
                start++;
            }
        }

        return LenientUtf8.GetString(bytes, start, bytes.Length - start);
    }
}
