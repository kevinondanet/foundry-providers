using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of <c>util/_sandbox/docker/docker.py</c> <c>DockerSandboxEnvironment</c> over a single container
/// driven by the bare <c>docker</c> CLI: commands run through <c>docker exec</c> (wrapped in an in-container
/// <c>timeout -s KILL</c> when a timeout is given, because docker exec detaches on a host signal and would
/// orphan the process tree), files are written byte-exact over <c>docker exec -i</c> stdin and read back
/// through <c>cat</c>; relative paths resolve under the image's WORKDIR.
/// </summary>
public sealed partial class DockerSandboxEnvironment : ISandboxEnvironment
{
    /// <summary>Port of the <c>write_file</c> shell: create the parent directory, then stream stdin into the file named by <c>$1</c>.</summary>
    internal const string WriteFileScript = "mkdir -p \"$(dirname \"$1\")\" && cat > \"$1\"";

    /// <summary>Slack added to the host-side guard so the in-container timeout fires first under normal conditions (daemon round trip included).</summary>
    internal static readonly TimeSpan HostTimeoutSlack = TimeSpan.FromSeconds(10);

    // Python's write_file runs its shell with timeout=600 (in-container wrapper, same as here).
    private static readonly TimeSpan WriteFileTimeout = TimeSpan.FromSeconds(600);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly DockerCli _cli;

    internal DockerSandboxEnvironment(DockerCli cli, string containerName, string workingDirectory)
    {
        _cli = cli;
        ContainerName = containerName;
        WorkingDirectory = workingDirectory.Length == 0 ? "/" : workingDirectory;
    }

    /// <summary>The container's name (also usable with the docker CLI directly, e.g. <c>docker exec -it NAME bash</c>).</summary>
    public string ContainerName { get; }

    /// <summary>The image's WORKDIR ("/" when it has none): default cwd for <see cref="ExecAsync"/> and base for relative file paths.</summary>
    public string WorkingDirectory { get; }

    public string HostAddress => "host.docker.internal";

    public async Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (cmd.Count == 0)
        {
            throw new ArgumentException("cmd must contain at least the executable.", nameof(cmd));
        }

        // Not a conditional expression: its natural type would be ReadOnlyMemory<byte> (null converts through the
        // byte[] operator), so a null input would become an empty, non-null buffer and wrongly request stdin (-i).
        ReadOnlyMemory<byte>? stdin = null;
        if (input is not null)
        {
            stdin = Utf8.GetBytes(input);
        }

        var result = await ExecCoreAsync(
            cmd,
            stdin,
            cwd,
            env,
            user,
            timeout,
            SandboxLimits.MaxExecOutputSize,
            abortOnOutputLimit: false,
            cancellationToken).ConfigureAwait(false);
        return new ExecResult(result.ExitCode == 0, result.ExitCode, result.StdoutText, result.StderrText);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Utf8.GetBytes(contents), cancellationToken);

    public async Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        var file = ContainerPath(path);
        var result = await ExecCoreAsync(
            ["sh", "-c", WriteFileScript, "sh", file],
            contents,
            cwd: null,
            env: null,
            user: null,
            WriteFileTimeout,
            SandboxLimits.MaxExecOutputSize,
            abortOnOutputLimit: false,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return;
        }

        var stderr = result.StderrText;
        if (stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Permission was denied writing '{path}'. Error details: {stderr.Trim()}");
        }

        if (stderr.Contains("is a directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Failed to write file '{path}' because it is a directory already.");
        }

        throw new InvalidOperationException($"Failed to write file '{path}' (exit code {result.ExitCode}): {stderr.Trim()}");
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // Strict decoding mirrors Python's UnicodeDecodeError; GetString keeps a BOM and every newline as-is.
        return StrictUtf8.GetString(bytes);
    }

    public async Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsDirectoryAlias(path))
        {
            throw new IOException($"'{path}' is a directory.");
        }

        var limit = SandboxLimits.MaxReadFileSize;
        var result = await ExecCoreAsync(
            ["cat", ContainerPath(path)],
            input: null,
            cwd: null,
            env: null,
            user: null,
            timeout: null,
            limit,
            abortOnOutputLimit: true,
            cancellationToken).ConfigureAwait(false);
        if (result.OutputLimitExceeded || result.StdoutTotal > limit)
        {
            throw new OutputLimitExceededException(SandboxLimits.HumanReadableSize(limit), null);
        }

        if (result.ExitCode == 0)
        {
            return result.Stdout;
        }

        var stderr = result.StderrText;
        if (stderr.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"File '{path}' was not found.", path);
        }

        if (stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Permission denied reading '{path}'.");
        }

        if (stderr.Contains("is a directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{path}' is a directory.");
        }

        throw new InvalidOperationException($"Failed to read file '{path}' (exit code {result.ExitCode}): {stderr.Trim()}");
    }

    /// <summary>
    /// Copies a host file into the container with <c>docker cp</c> (creating the parent directory first): the
    /// fast path for large payloads such as an agent binary, where streaming through exec stdin is slower.
    /// </summary>
    public async Task CopyToContainerAsync(string hostPath, string containerPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException($"File '{hostPath}' was not found.", hostPath);
        }

        var file = ContainerPath(containerPath);
        var parent = PosixParent(file);
        var mkdir = await ExecAsync(["mkdir", "-p", parent], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!mkdir.Success)
        {
            throw new InvalidOperationException($"Failed to create container directory '{parent}': {mkdir.Stderr.Trim()}");
        }

        await _cli.CopyAsync(Path.GetFullPath(hostPath), $"{ContainerName}:{file}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>docker rm -f</c> on the container (the provider's sample cleanup).</summary>
    public Task RemoveAsync(CancellationToken cancellationToken = default) =>
        _cli.RemoveContainerAsync(ContainerName, cancellationToken);

    /// <summary>Port of <c>container_file</c>: absolute paths are used as-is, relative ones resolve under <see cref="WorkingDirectory"/>.</summary>
    public string ContainerPath(string path)
    {
        if (path.StartsWith('/'))
        {
            return path;
        }

        return WorkingDirectory == "/" ? "/" + path : WorkingDirectory.TrimEnd('/') + "/" + path;
    }

    private async Task<ProcessResult> ExecCoreAsync(
        IReadOnlyList<string> cmd,
        ReadOnlyMemory<byte>? input,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        string? user,
        TimeSpan? timeout,
        long outputLimit,
        bool abortOnOutputLimit,
        CancellationToken cancellationToken)
    {
        var inContainer = cmd;
        DockerFailures.InjectedWrapper? wrapper = null;
        if (timeout is { } limit)
        {
            inContainer = ["timeout", "-s", "KILL", FormatSeconds(limit), .. cmd];
            wrapper = new DockerFailures.InjectedWrapper("timeout", cmd[0]);
        }

        var started = Stopwatch.GetTimestamp();
        var result = await _cli.ExecAsync(
            ContainerName,
            inContainer,
            input,
            cwd is null ? null : ContainerPath(cwd),
            env,
            user,
            timeout is null ? null : timeout + HostTimeoutSlack,
            outputLimit,
            abortOnOutputLimit,
            cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);

        if (result.OutputLimitExceeded)
        {
            // We killed the process ourselves; its signal exit code is not a failure to classify.
            return result;
        }

        if (timeout is { } expected)
        {
            var seconds = FormatSeconds(expected);
            if (result.TimedOut)
            {
                throw new SandboxTimeoutException($"Command timed out after {seconds} seconds (docker exec did not return): {string.Join(" ", cmd)}", result.CombinedText);
            }

            // 124 is GNU timeout's own code; 137/143 also come from OOM kills and stray signals, so wall-clock
            // time disambiguates those from a real timeout.
            if (result.ExitCode is 124 or 137 or 143 && (result.ExitCode == 124 || elapsed >= expected))
            {
                throw new SandboxTimeoutException($"Command timed out after {seconds} seconds: {string.Join(" ", cmd)}", result.CombinedText);
            }
        }

        var failure = DockerFailures.Classify(result, wrapper);
        // A container dying mid-command is invisible to the classifier: docker reports nothing, just the
        // signal-death exit code. Only silent signal exits pay the inspect round trip.
        if (failure is null
            && result.ExitCode > 128
            && string.IsNullOrWhiteSpace(result.StdoutText)
            && string.IsNullOrWhiteSpace(result.StderrText)
            && !await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            failure = new SandboxUnavailableException(
                $"The sandbox is not running and cannot execute: command exited with code {result.ExitCode} and no output, and container '{ContainerName}' has exited.");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    private async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var running = await _cli.InspectAsync(ContainerName, "{{.State.Running}}", cancellationToken).ConfigureAwait(false);
            return string.Equals(running, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (SandboxUnavailableException)
        {
            return false;
        }
    }

    private static string FormatSeconds(TimeSpan timeout) => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsDirectoryAlias(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = slash < 0 ? trimmed : trimmed[(slash + 1)..];
        return name is "." or "..";
    }

    private static string PosixParent(string file)
    {
        var slash = file.LastIndexOf('/');
        return slash <= 0 ? "/" : file[..slash];
    }
}
