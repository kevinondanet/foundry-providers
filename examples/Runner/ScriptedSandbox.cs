using System.Text;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// The sandbox provider behind <c>--sandbox fake</c>, registered with <see cref="SandboxRegistry"/> under
/// <see cref="TypeName"/>: every sample gets a <see cref="ScriptedSandboxEnvironment"/> answering from the example's
/// <see cref="FakeSandboxScript"/>. Registering a new instance (one per run) replaces the previous script.
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
/// One sample's fake sandbox: exec calls are recorded on the script and answered by its rules (or run for real
/// through a <see cref="LocalSandboxEnvironment"/> when a rule says so); files live in <see cref="Files"/>, seeded
/// from the script's initial files.
/// </summary>
public sealed class ScriptedSandboxEnvironment(FakeSandboxScript script) : ISandboxEnvironment, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly AsyncLocal<ScriptedSandboxEnvironment?> CurrentEnvironment = new();

    private readonly object _sync = new();
    private LocalSandboxEnvironment? _local;

    /// <summary>The environment answering the exec call on the current async flow (set for the duration of a script handler), else null.</summary>
    public static ScriptedSandboxEnvironment? Current => CurrentEnvironment.Value;

    public FakeSandboxScript Script { get; } = script ?? throw new ArgumentNullException(nameof(script));

    public string HostAddress => Script.HostAddress;

    /// <summary>This sample's files (a copy of the script's initial files plus what the tools wrote).</summary>
    public Dictionary<string, byte[]> Files { get; } = new(script.Files, StringComparer.Ordinal);

    /// <summary>The exec calls this sample made, in order.</summary>
    public List<FakeExecCall> Calls { get; } = [];

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

        return await Local().ExecAsync(cmd, input, cwd, env, user, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            Files[path] = contents.ToArray();
        }

        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        StrictUtf8.GetString(await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>The text of a file the tools wrote (or the script seeded), for assertions; null when absent.</summary>
    public string? FileText(string path)
    {
        lock (_sync)
        {
            return Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _local?.Dispose();
            _local = null;
        }
    }

    private LocalSandboxEnvironment Local()
    {
        lock (_sync)
        {
            return _local ??= new LocalSandboxEnvironment();
        }
    }
}
