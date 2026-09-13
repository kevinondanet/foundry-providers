using System.Collections.Concurrent;
using System.Globalization;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.SandboxContractDemo;

// ---------------------------------------------------------------------------
// The whole point of this file: implementing the sandbox contract is small.
//
// A provider is a factory with a lifecycle (ISandboxProvider) that hands back
// environments (ISandboxEnvironment) bundled per sample (SandboxEnvironments).
// This one keeps "files" in a dictionary and "runs" a handful of argv commands
// in-process, so nothing here touches Docker or the host - yet every rule of the
// contract (argv not shell, failure vs exception, first-entry-is-default, the
// cleanup flag) is honoured exactly the way the real docker/local providers do.
// ---------------------------------------------------------------------------

/// <summary>
/// A toy <see cref="ISandboxProvider"/> registered as type <c>"memory"</c>. Its <c>config</c> string is a
/// comma-separated list of environment names (<c>null</c> = one environment called "default"), which shows
/// that <see cref="SandboxSpec.Config"/> means whatever the provider says it means.
/// </summary>
public sealed class InMemorySandboxProvider : ISandboxProvider
{
    private readonly ConcurrentQueue<string> _log = new();
    private int _live;

    public string Type => "memory";

    /// <summary>Every lifecycle call, in order, so the demo can print the sequence back.</summary>
    public IReadOnlyList<string> Log => _log.ToArray();

    /// <summary>Environments that exist right now (created minus destroyed).</summary>
    public int LiveEnvironments => Volatile.Read(ref _live);

    // Runs ONCE per distinct spec, before any sample. Docker builds/pulls its image here; we have nothing to
    // build, so we only record the call.
    public Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default)
    {
        _log.Enqueue($"TaskInit(task={taskName}, config={config ?? "null"})");
        return Task.CompletedTask;
    }

    // Runs once per (sample, epoch). One provider instance serves every concurrent sample, so nothing here may
    // depend on instance state that another sample could be mutating - hence the concurrent queue and
    // Interlocked counter.
    public Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var names = (config ?? "default").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sampleId = metadata.TryGetValue("id", out var id) ? id : "?";
        _log.Enqueue($"SampleInit(task={taskName}, sample={sampleId}) -> [{string.Join(", ", names)}]");

        var environments = names
            .Select(name => new KeyValuePair<string, ISandboxEnvironment>(name, new InMemorySandboxEnvironment(name)))
            .ToList();
        Interlocked.Add(ref _live, environments.Count);

        // The cleanup closure is the provider's only chance to tear down what it just created. Its bool is the
        // runner's cleanup flag (true = destroy, false = keep for inspection) - NOT "did the sample pass".
        return Task.FromResult(SandboxEnvironments.Create(environments, cleanup =>
        {
            _log.Enqueue($"Cleanup(sample={sampleId}, cleanup={cleanup})");
            if (cleanup)
            {
                foreach (var (_, environment) in environments)
                {
                    ((InMemorySandboxEnvironment)environment).Destroy();
                }

                Interlocked.Add(ref _live, -environments.Count);
            }

            return Task.CompletedTask;
        }));
    }

    // Runs once per distinct spec after the last sample, in a finally block with CancellationToken.None.
    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default)
    {
        _log.Enqueue($"TaskCleanup(task={taskName}, cleanup={cleanup})");
        return Task.CompletedTask;
    }
}

/// <summary>
/// A toy <see cref="ISandboxEnvironment"/>: a dictionary of files and a built-in "shell" that understands
/// <c>echo</c>, <c>cat</c>, <c>ls</c>, <c>pwd</c>, <c>env</c>, <c>exit</c> and <c>sleep</c>. Anything else is
/// "command not found" - which, per the contract, is a failed <see cref="ExecResult"/> and not an exception.
/// </summary>
public sealed class InMemorySandboxEnvironment : ISandboxEnvironment
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private volatile bool _destroyed;

    public InMemorySandboxEnvironment(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string HostAddress => "127.0.0.1";

    /// <summary>Paths currently stored, for the demo to print.</summary>
    public IReadOnlyList<string> Files => _files.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>After this every call throws <see cref="SandboxUnavailableException"/>, like a removed container.</summary>
    public void Destroy() => _destroyed = true;

    public async Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDestroyed();
        if (cmd.Count == 0)
        {
            throw new ArgumentException("cmd must contain at least the executable.", nameof(cmd));
        }

        // cmd is argv. cmd[0] is the program, the rest are its arguments *verbatim*: no variable expansion,
        // no globbing, no quoting rules. That is why "$HOME" and "*.txt" come out literally below.
        var args = cmd.Skip(1).ToList();
        switch (cmd[0])
        {
            case "echo":
                return Ok(string.Join(' ', args) + "\n");

            case "pwd":
                return Ok((cwd ?? "/") + "\n");

            case "env":
                return Ok(string.Concat((env ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}\n")));

            case "cat":
            {
                if (args.Count == 0)
                {
                    return Ok(input ?? ""); // `cat` with no file echoes stdin
                }

                var stdout = "";
                foreach (var path in args)
                {
                    if (!_files.TryGetValue(Resolve(cwd, path), out var bytes))
                    {
                        return new ExecResult(false, 1, stdout, $"cat: {path}: No such file or directory\n");
                    }

                    stdout += System.Text.Encoding.UTF8.GetString(bytes);
                }

                return Ok(stdout);
            }

            case "ls":
            {
                // "*.txt" is looked up as a file literally called "*.txt" - there is no shell to expand it.
                if (args.Count > 0 && !_files.ContainsKey(Resolve(cwd, args[0])))
                {
                    return new ExecResult(false, 2, "", $"ls: cannot access '{args[0]}': No such file or directory\n");
                }

                return Ok(string.Concat(Files.Select(f => f + "\n")));
            }

            case "exit":
            {
                // A command that runs and fails is an ORDINARY result. Callers read Success / ReturnCode.
                var code = args.Count > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
                return new ExecResult(code == 0, code, "", "");
            }

            case "sleep":
            {
                var seconds = args.Count > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 0;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeout is { } limit)
                {
                    linked.CancelAfter(limit);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), linked.Token);
                    return Ok("");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The TIMEOUT fired (not the caller's token): that is the one case that is an exception, and it
                    // carries whatever output was captured so far so a tool can still show it to the model.
                    throw new SandboxTimeoutException($"Command timed out after {timeout}.", truncatedOutput: "");
                }
            }

            default:
                // Missing executable: a failed result with the conventional 127, exactly what the local provider
                // returns when the OS cannot find the program.
                return new ExecResult(false, 127, "", $"{cmd[0]}: command not found\n");
        }

        static ExecResult Ok(string stdout) => new(true, 0, stdout, "");
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, System.Text.Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        ThrowIfDestroyed();
        _files[Resolve(null, path)] = contents.ToArray();
        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        System.Text.Encoding.UTF8.GetString(await ReadFileBytesAsync(path, cancellationToken));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDestroyed();
        return _files.TryGetValue(Resolve(null, path), out var bytes)
            ? Task.FromResult(bytes)
            : throw new FileNotFoundException($"File '{path}' was not found.", path);
    }

    // Relative paths resolve under cwd (default "/"), mirroring the local provider resolving them under its
    // per-sample temp directory.
    private static string Resolve(string? cwd, string path) =>
        path.StartsWith('/') ? path : $"{(cwd ?? "/").TrimEnd('/')}/{path}";

    private void ThrowIfDestroyed()
    {
        if (_destroyed)
        {
            throw new SandboxUnavailableException($"Sandbox '{Name}' has been destroyed.");
        }
    }
}
