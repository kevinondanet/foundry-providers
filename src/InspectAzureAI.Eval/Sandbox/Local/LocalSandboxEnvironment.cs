using System.ComponentModel;
using System.Text;
using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Local;

/// <summary>
/// Port of <c>util/_sandbox/local.py</c> <c>LocalSandboxEnvironment</c>: commands run on the host with a
/// per-sample temp directory as the default cwd; relative file paths resolve under it; <c>user</c> is
/// ignored; a timeout kills the whole process tree. Processes run through the same <see cref="ProcessRunner"/>
/// as the docker CLI, so pipe draining, output caps and kill handling live in one place.
/// </summary>
public sealed partial class LocalSandboxEnvironment : ISandboxEnvironment, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IProcessRunner _runner;

    public LocalSandboxEnvironment(string? directory = null)
        : this(directory, null)
    {
    }

    internal LocalSandboxEnvironment(string? directory, IProcessRunner? runner)
    {
        WorkingDirectory = directory ?? Path.Combine(Path.GetTempPath(), "inspect-swe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkingDirectory);
        _runner = runner ?? new ProcessRunner();
    }

    /// <summary>The sample's temp directory (default cwd and base for relative paths).</summary>
    public string WorkingDirectory { get; }

    public string HostAddress => "127.0.0.1";

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

        if (user is not null)
        {
            ProviderLogger.WarnOnce("The 'user' parameter is ignored in LocalSandboxEnvironment. Commands will run as the current user.");
        }

        var workingDirectory = ResolvePath(cwd ?? WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory '{cwd}' does not exist.");
        }

        // Not a conditional expression: null must stay null (an empty buffer would redirect stdin).
        ReadOnlyMemory<byte>? stdin = null;
        if (input is not null)
        {
            stdin = Utf8.GetBytes(input);
        }

        var request = new ProcessRequest(cmd[0], cmd.Skip(1).ToArray())
        {
            Input = stdin,
            WorkingDirectory = workingDirectory,
            Environment = env,
            Timeout = timeout,
            OutputLimit = SandboxLimits.MaxExecOutputSize,
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex) when (ex.InnerException is Win32Exception missing)
        {
            // A missing executable is the command's failure, not the sandbox's (the docker provider reports
            // it the same way through the container's exit status).
            return new ExecResult(false, 127, "", $"{cmd[0]}: {missing.Message}");
        }

        if (result.TimedOut)
        {
            var seconds = timeout!.Value.TotalSeconds;
            throw new SandboxTimeoutException($"Command timed out after {seconds} seconds: {string.Join(" ", cmd)}", result.CombinedText);
        }

        return new ExecResult(result.ExitCode == 0, result.ExitCode, result.StdoutText, result.StderrText);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Utf8.GetBytes(contents), cancellationToken);

    public async Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllBytesAsync(full, contents, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // Strict decoding mirrors Python's UnicodeDecodeError; GetString keeps a BOM and every newline as-is.
        return StrictUtf8.GetString(bytes);
    }

    public async Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(path);
        if (Directory.Exists(full))
        {
            throw new IOException($"'{path}' is a directory.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"File '{path}' was not found.", path);
        }

        var maxSize = SandboxLimits.MaxReadFileSize;
        if (new FileInfo(full).Length > maxSize)
        {
            throw new OutputLimitExceededException(SandboxLimits.HumanReadableSize(maxSize), null);
        }

        return await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the temp directory (errors ignored, like Python's <c>ignore_cleanup_errors</c>).</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkingDirectory))
            {
                Directory.Delete(WorkingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path);
}
