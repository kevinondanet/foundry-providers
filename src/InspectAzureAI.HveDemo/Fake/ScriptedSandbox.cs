using System.Text;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.HveDemo.Fake;

/// <summary>
/// A copy of <c>examples/Runner/ScriptedSandbox.cs</c> <c>ScriptedSandboxProvider</c>: the sandbox provider behind
/// <c>--sandbox fake</c>, registered with <see cref="SandboxRegistry"/> under <see cref="TypeName"/>. Every sample gets a
/// <see cref="ScriptedSandboxEnvironment"/> answering from the demo's <see cref="FakeSandboxScript"/>. Registering a
/// new instance (one per run) replaces the previous script.
/// </summary>
public sealed class ScriptedSandboxProvider(FakeSandboxScript script) : ISandboxProvider
{
    public const string TypeName = "fake";

    public FakeSandboxScript Script { get; } = script ?? throw new ArgumentNullException(nameof(script));

    public string Type => TypeName;

    /// <summary>Registers a provider for <paramref name="script"/> and returns the spec that selects it.</summary>
    public static SandboxSpec Register(FakeSandboxScript script)
    {
        SandboxRegistry.Register(new ScriptedSandboxProvider(script));
        return new SandboxSpec(TypeName);
    }

    public Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var environment = new ScriptedSandboxEnvironment(Script);
        Script.Record(environment);
        return Task.FromResult(SandboxEnvironments.Single(environment, _ =>
        {
            environment.Dispose();
            return Task.CompletedTask;
        }));
    }

    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// One sample's fake sandbox, the demo's adaptation of the examples' <c>ScriptedSandboxEnvironment</c>: exec calls
/// are recorded on the script and answered by its rules, or run for real on this host when a rule says so. What is
/// new is the <see cref="MirrorDirectory"/>: a host temp directory that mirrors the sandbox file system, so that the
/// sample's workspace files, the setup script, the checks and the fake CLI's tool calls all operate on one real
/// directory tree with <c>python3</c>, <c>bash</c> and <c>git</c> from this host. Sandbox paths map onto it with
/// <see cref="HostPath"/> (<c>/workspace</c> is the mirror root; any other absolute path lands under <c>.root/</c>).
/// <see cref="Files"/> keeps the seeded and written bytes for assertions, as the original did.
/// </summary>
public sealed class ScriptedSandboxEnvironment : ISandboxEnvironment, IDisposable
{
    /// <summary>The sandbox working directory the fake presents (the Dockerfile's <c>WORKDIR</c>).</summary>
    public const string WorkingDirectory = HveData.SandboxWorkingDirectory;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly AsyncLocal<ScriptedSandboxEnvironment?> CurrentEnvironment = new();

    private readonly object _sync = new();
    private readonly LocalSandboxEnvironment _local;

    public ScriptedSandboxEnvironment(FakeSandboxScript script)
    {
        Script = script ?? throw new ArgumentNullException(nameof(script));
        MirrorDirectory = Path.Combine(Path.GetTempPath(), "inspect-hve-fake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(MirrorDirectory);
        _local = new LocalSandboxEnvironment(MirrorDirectory);
        Files = new Dictionary<string, byte[]>(script.Files, StringComparer.Ordinal);
        foreach (var (path, bytes) in script.Files)
        {
            WriteMirror(path, bytes);
        }
    }

    /// <summary>The environment answering the exec call on the current async flow (set for the duration of a script handler), else null.</summary>
    public static ScriptedSandboxEnvironment? Current => CurrentEnvironment.Value;

    public FakeSandboxScript Script { get; }

    public string HostAddress => Script.HostAddress;

    /// <summary>The host directory that plays <c>/workspace</c>; deleted when the sample's sandbox is cleaned up.</summary>
    public string MirrorDirectory { get; }

    /// <summary>This sample's files by sandbox path (a copy of the script's initial files plus what was written through this environment).</summary>
    public Dictionary<string, byte[]> Files { get; }

    /// <summary>The exec calls this sample made, in order.</summary>
    public List<FakeExecCall> Calls { get; } = [];

    /// <summary>The host path of a sandbox path: <c>/workspace/...</c> and relative paths under the mirror, other absolute paths under <c>.root/</c>.</summary>
    public string HostPath(string sandboxPath)
    {
        ArgumentNullException.ThrowIfNull(sandboxPath);
        if (!sandboxPath.StartsWith('/'))
        {
            return Path.GetFullPath(Path.Combine(MirrorDirectory, sandboxPath));
        }

        if (sandboxPath == WorkingDirectory)
        {
            return MirrorDirectory;
        }

        if (sandboxPath.StartsWith(WorkingDirectory + "/", StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(MirrorDirectory, sandboxPath[(WorkingDirectory.Length + 1)..]));
        }

        return Path.GetFullPath(Path.Combine(MirrorDirectory, ".root", sandboxPath.TrimStart('/')));
    }

    /// <summary>
    /// An argv element rewritten for a local run: absolute sandbox paths the runner or the agent hand over
    /// (<c>/workspace/...</c>, the setup script under <c>/tmp</c>, the plugin under <c>/opt</c>, any path written through
    /// this environment) become their host paths; everything else is left alone.
    /// </summary>
    public string MapArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (!argument.StartsWith('/'))
        {
            return argument;
        }

        bool known;
        lock (_sync)
        {
            known = Files.ContainsKey(argument);
        }

        return known
            || argument == WorkingDirectory
            || argument.StartsWith(WorkingDirectory + "/", StringComparison.Ordinal)
            || argument.StartsWith("/tmp/", StringComparison.Ordinal)
            || argument.StartsWith("/opt/", StringComparison.Ordinal)
            ? HostPath(argument)
            : argument;
    }

    public async Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cancellationToken.ThrowIfCancellationRequested();
        var call = new FakeExecCall(cmd, input, cwd, env, user, timeout);
        lock (_sync)
        {
            Calls.Add(call);
        }

        Script.Record(call);
        CurrentEnvironment.Value = this;
        var (result, local) = await Script.ResolveAsync(call, cancellationToken).ConfigureAwait(false);
        if (!local)
        {
            return result!;
        }

        var mapped = cmd.Select(MapArgument).ToList();
        var hostCwd = HostPath(cwd ?? WorkingDirectory);
        Directory.CreateDirectory(hostCwd);
        return await _local.ExecAsync(mapped, input, hostCwd, env, user: null, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = contents.ToArray();
        lock (_sync)
        {
            Files[path] = bytes;
        }

        WriteMirror(path, bytes);
        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        StrictUtf8.GetString(await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = HostPath(path);
        if (File.Exists(host))
        {
            return Task.FromResult(File.ReadAllBytes(host));
        }

        lock (_sync)
        {
            return Files.TryGetValue(path, out var bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"File '{path}' was not found.", path);
        }
    }

    /// <summary>Whether this environment recorded <paramref name="call"/> (the same instance).</summary>
    public bool Recorded(FakeExecCall call)
    {
        lock (_sync)
        {
            return Calls.Any(recorded => ReferenceEquals(recorded, call));
        }
    }

    /// <summary>The text of a file in the mirror (written by a tool, a check or the seed), for assertions; null when absent.</summary>
    public string? FileText(string path)
    {
        var host = HostPath(path);
        if (File.Exists(host))
        {
            return File.ReadAllText(host);
        }

        lock (_sync)
        {
            return Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _local.Dispose();
        }
    }

    private void WriteMirror(string path, byte[] bytes)
    {
        var host = HostPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, bytes);
    }
}
