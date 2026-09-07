using System.Text;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Testing;

/// <summary>A recorded <see cref="FakeSandboxEnvironment.ExecAsync"/> call.</summary>
public sealed record FakeExecCall(IReadOnlyList<string> Cmd, string? Input, string? Cwd, IReadOnlyDictionary<string, string>? Env, string? User, TimeSpan? Timeout);

/// <summary>
/// An in-memory <see cref="ISandboxEnvironment"/> for tests: files live in <see cref="Files"/>, commands are
/// answered by the scripted <see cref="OnExec"/> table (default: success with empty output) and recorded in <see cref="Calls"/>.
/// </summary>
public sealed class FakeSandboxEnvironment(Func<IReadOnlyList<string>, ExecResult>? onExec = null) : ISandboxEnvironment
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string HostAddress { get; init; } = "127.0.0.1";

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<FakeExecCall> Calls { get; } = [];

    /// <summary>Answers a command; a returned null falls back to <see cref="Ok"/>.</summary>
    public Func<IReadOnlyList<string>, ExecResult?>? OnExec { get; set; } = onExec is null ? null : cmd => onExec(cmd);

    /// <summary>Answers a command given the whole call (stdin included); consulted before <see cref="OnExec"/>, a returned null falls through to it.</summary>
    public Func<FakeExecCall, ExecResult?>? OnExecCall { get; set; }

    public static ExecResult Ok(string stdout = "", string stderr = "") => new(true, 0, stdout, stderr);

    public static ExecResult Fail(int returnCode, string stderr = "", string stdout = "") => new(false, returnCode, stdout, stderr);

    public Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new FakeExecCall(cmd, input, cwd, env, user, timeout);
        Calls.Add(call);
        return Task.FromResult(OnExecCall?.Invoke(call) ?? OnExec?.Invoke(cmd) ?? Ok());
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        Files[path] = contents.ToArray();
        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        StrictUtf8.GetString(await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        Files.TryGetValue(path, out var bytes)
            ? Task.FromResult(bytes)
            : throw new FileNotFoundException($"File '{path}' was not found.", path);
}
