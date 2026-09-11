using System.Collections.Concurrent;
using System.Text;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The shared sandbox for CLI agent tests: an in-memory <see cref="ISandboxEnvironment"/> whose commands go first to
/// an async <see cref="OnExec"/> handler (so a fake CLI can await bridge HTTP calls without sync-over-async) and then
/// to a default table that fails loudly on anything it does not know.
/// </summary>
/// <remarks>
/// <para>
/// Default table. Shell probes arrive from <see cref="SandboxUtil.ExecAsync"/> as <c>["bash","-c",script]</c> and are
/// matched on the script: <c>which …</c> fails (exit 1, empty stdout) unless <see cref="WhichPaths"/> names the
/// binary; <c>uname -s</c> gives <c>Linux</c>, <c>uname -m</c> gives <see cref="Machine"/> (<c>aarch64</c>), the musl
/// probe gives <see cref="Libc"/> (<c>glibc</c>) and <c>pwd</c> gives <see cref="WorkingDirectory"/>
/// (<c>/workspace</c>); the Claude Code MCP directory script succeeds, and its config write script stores stdin as the
/// file named by <c>$1</c>. Argv commands are matched on <c>Cmd[0]</c>: <c>mkdir</c>, <c>chmod</c>, <c>chown</c>,
/// <c>tar</c> and <c>rm</c> succeed; <c>test -x path</c> fails unless the path was <see cref="MarkInstalled"/>;
/// <c>[binary, "--version"]</c> fails, so an installed version reads as unknown.
/// </para>
/// <para>Anything else fails with stderr <c>CliSandbox: unexpected command &lt;argv&gt;</c>, so a surprise exec is visible.</para>
/// </remarks>
public sealed class CliSandbox : ISandboxEnvironment
{
    public const string UnexpectedCommandPrefix = "CliSandbox: unexpected command ";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Lock _gate = new();

    private readonly List<FakeExecCall> _calls = [];

    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);

    public string HostAddress { get; init; } = "127.0.0.1";

    /// <summary>What <c>uname -m</c> prints.</summary>
    public string Machine { get; init; } = "aarch64";

    /// <summary>What the musl probe prints (<c>glibc</c> or <c>musl</c>).</summary>
    public string Libc { get; init; } = "glibc";

    /// <summary>What <c>pwd</c> prints.</summary>
    public string WorkingDirectory { get; init; } = "/workspace";

    /// <summary>Files written through the sandbox, by path.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Binaries <c>which</c> finds, by name (empty by default, so <c>which</c> fails).</summary>
    public ConcurrentDictionary<string, string> WhichPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers a command first; a null result falls through to the default table.</summary>
    public Func<FakeExecCall, CancellationToken, Task<ExecResult?>>? OnExec { get; set; }

    /// <summary>Every exec so far, in order.</summary>
    public IReadOnlyList<FakeExecCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public static ExecResult Ok(string stdout = "", string stderr = "") => new(true, 0, stdout, stderr);

    public static ExecResult Fail(int returnCode = 1, string stderr = "", string stdout = "") => new(false, returnCode, stdout, stderr);

    /// <summary>The script of a <c>["bash","-c",script, …]</c> call, or null for an argv command.</summary>
    public static string? ShellScript(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Cmd.Count >= 3 && call.Cmd[0] == "bash" && call.Cmd[1] == "-c" ? call.Cmd[2] : null;
    }

    /// <summary>Makes <c>test -x <paramref name="path"/></c> succeed.</summary>
    public void MarkInstalled(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            _installed.Add(path);
        }
    }

    public bool IsInstalled(string path)
    {
        lock (_gate)
        {
            return _installed.Contains(path);
        }
    }

    /// <summary>A written file as UTF-8 text, or null when nothing was written there.</summary>
    public string? TextOf(string path) => Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

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
        var call = new FakeExecCall(cmd.ToArray(), input, cwd, env, user, timeout);
        lock (_gate)
        {
            _calls.Add(call);
        }

        if (OnExec is { } handler && await handler(call, cancellationToken).ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        return Default(call);
    }

    /// <summary>The default table (see the class remarks), usable from an <see cref="OnExec"/> handler that wraps it.</summary>
    public ExecResult Default(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var cmd = call.Cmd;
        if (ShellScript(call) is { } script)
        {
            if (script.StartsWith("which ", StringComparison.Ordinal))
            {
                return WhichPaths.TryGetValue(script["which ".Length..].Trim(), out var path) ? Ok(path + "\n") : Fail(1);
            }

            if (script == ClaudeCode.ClaudeCodeMcp.WriteConfigScript && cmd.Count == 5)
            {
                Files[cmd[4]] = Encoding.UTF8.GetBytes(call.Input ?? "");
                return Ok();
            }

            return script switch
            {
                "uname -s" => Ok("Linux\n"),
                "uname -m" => Ok(Machine + "\n"),
                SandboxUtil.MuslCheck => Ok(Libc + "\n"),
                "pwd" => Ok(WorkingDirectory + "\n"),
                ClaudeCode.ClaudeCodeMcp.PrepareDirectoryScript => Ok(),
                _ => Unexpected(cmd),
            };
        }

        if (cmd.Count == 0)
        {
            return Unexpected(cmd);
        }

        switch (cmd[0])
        {
            case "mkdir" or "chmod" or "chown" or "tar" or "rm":
                return Ok();
            case "test":
                return cmd.Count >= 3 && cmd[1] == "-x" && IsInstalled(cmd[2]) ? Ok() : Fail(1);
            case "which":
                return cmd.Count >= 2 && WhichPaths.TryGetValue(cmd[1], out var found) ? Ok(found + "\n") : Fail(1);
        }

        return cmd.Count == 2 && cmd[1] == "--version" ? Fail(1) : Unexpected(cmd);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        Files[path] = contents.ToArray();
        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        StrictUtf8.GetString(await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        Files.TryGetValue(path, out var bytes)
            ? Task.FromResult(bytes)
            : throw new FileNotFoundException($"File '{path}' was not found.", path);

    private static ExecResult Unexpected(IReadOnlyList<string> cmd) => Fail(1, UnexpectedCommandPrefix + string.Join(" ", cmd));
}
