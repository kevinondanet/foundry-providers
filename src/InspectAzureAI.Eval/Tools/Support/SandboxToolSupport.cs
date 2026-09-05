using System.Runtime.CompilerServices;
using System.Text;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>
/// Port of <c>SandboxInjectionError</c>: wraps any failure during injection so it is never mistaken for a
/// tool error that should be handed to the model (injection happens as a side effect of a tool call).
/// </summary>
public sealed class SandboxInjectionException(string message, Exception? cause = null) : Exception(message, cause);

/// <summary>A sandbox with the tools injected and the user the injected CLI runs as (Python's <c>sandbox._tools_user</c>).</summary>
public sealed record InjectedSandbox(ISandboxEnvironment Sandbox, string? ToolsUser)
{
    /// <summary>A JSON-RPC transport over this sandbox's injected CLI.</summary>
    public SandboxJsonRpcTransport Transport => new(Sandbox, SandboxToolSupport.SandboxCli);

    /// <summary>The <see cref="JsonRpcCallOptions"/> for a call with the given timeout, run as the tools user.</summary>
    public JsonRpcCallOptions CallOptions(TimeSpan? timeout) => new(timeout, ToolsUser);
}

/// <summary>
/// Port of <c>tool/_sandbox_tools_utils/sandbox.py</c> (<c>sandbox_with_injected_tools</c> and the injection
/// steps) plus the injection protocol of <c>util/_sandbox/context.py</c> (<c>sandbox_with_injection</c>,
/// <c>sandbox_file_detector</c>): finds a sandbox for the current sample, injects the published
/// <c>inspect-sandbox-tools</c> onedir bundle into <see cref="SandboxToolsDir"/> when the launcher is not
/// there yet (as root and 0700 when the sandbox allows it), starts the in-container server, verifies the
/// launcher answers the <c>version</c> RPC, and hands back the sandbox with the user its CLI runs as.
/// Injection state is kept per sandbox instance, as Python keeps <c>_inject_lock</c> and <c>_tools_user</c>
/// on the environment object.
/// </summary>
public sealed class SandboxToolSupport
{
    /// <summary>Port of <c>util/_sandbox/_cli.py</c> <c>SANDBOX_TOOLS_BASE_NAME</c>.</summary>
    public const string SandboxToolsBaseName = "inspect-sandbox-tools";

    /// <summary>
    /// Port of <c>SANDBOX_TOOLS_DIR</c>: /var/tmp is present on every major distribution, writable by all
    /// users (rootless sandboxes), unlikely to be cleared mid-eval, and the dot-prefixed random name keeps
    /// an agent from stumbling on it.
    /// </summary>
    public const string SandboxToolsDir = "/var/tmp/.da7be258e003d428";

    /// <summary>Port of <c>SANDBOX_CLI</c>: the launcher inside the extracted tree.</summary>
    public const string SandboxCli = SandboxToolsDir + "/" + SandboxToolsBaseName;

    /// <summary>Timeout for the post-injection <c>version</c> RPC (the frozen launcher's cold start can be slow on constrained hosts).</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(60);

    private const string NoSandboxMessage =
        "No sandbox environment has been provided for the current sample or task. "
        + "Please specify a sandbox for the sample or a global default sandbox for the task";

    private readonly ISandboxToolsBinarySource _binaries;

    private readonly ConditionalWeakTable<ISandboxEnvironment, SandboxState> _states = new();

    /// <param name="binaries">Where the artifact comes from; defaults to a <see cref="SandboxToolsBinary"/> over the published bucket.</param>
    public SandboxToolSupport(ISandboxToolsBinarySource? binaries = null)
    {
        _binaries = binaries ?? new SandboxToolsBinary();
    }

    /// <summary>The process-wide instance the built-in tools use.</summary>
    public static SandboxToolSupport Default { get; } = new();

    /// <summary>
    /// Port of <c>sandbox_with_injected_tools</c>: an explicit <paramref name="sandbox"/> is injected into
    /// directly; a <paramref name="sandboxName"/> selects that sandbox of the current sample; otherwise the
    /// sample's sandbox needing the fewest injections is chosen (the first that already has the tools wins).
    /// </summary>
    public async Task<InjectedSandbox> SandboxWithInjectedToolsAsync(string? sandboxName = null, ISandboxEnvironment? sandbox = null, CancellationToken cancellationToken = default)
    {
        var target = sandbox
            ?? (sandboxName is { Length: > 0 }
                ? SampleContext.Require().Sandbox(sandboxName)
                : await InjectionTargetAsync(cancellationToken).ConfigureAwait(false));

        var state = StateFor(target);
        await state.InjectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await HasToolsAsync(target, cancellationToken).ConfigureAwait(false))
            {
                await InjectAsync(target, cancellationToken).ConfigureAwait(false);
                if (!await HasToolsAsync(target, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Injection failed - detector still returns False after injection");
                }

                state.Version = await VerifyVersionAsync(target, state.ToolsUser, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            state.InjectLock.Release();
        }

        return new InjectedSandbox(target, state.ToolsUser);
    }

    /// <summary>
    /// Port of <c>sandbox_file_detector(SANDBOX_CLI)</c>: <c>test -r</c> on the launcher, falling back to
    /// reading it when the probe fails (a provider without a working <c>test</c>).
    /// </summary>
    public static async Task<bool> HasToolsAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        try
        {
            var result = await sandbox.ExecAsync(["test", "-r", SandboxCli], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Providers signal "cannot exec" with provider-specific types, so the probe catches broadly and
            // the read below is the portable answer.
        }

        try
        {
            await sandbox.ReadFileBytesAsync(SandboxCli, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The user the injected CLI runs as for this sandbox ("root" when injected as root, else null), or null when never injected.</summary>
    public string? ToolsUser(ISandboxEnvironment sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return _states.TryGetValue(sandbox, out var state) ? state.ToolsUser : null;
    }

    /// <summary>The version string the injected launcher reported after injection, or null when this instance did not inject into the sandbox.</summary>
    public string? InjectedVersion(ISandboxEnvironment sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return _states.TryGetValue(sandbox, out var state) ? state.Version : null;
    }

    /// <summary>The injected package's <c>version</c> RPC (the in-process tool the launcher answers itself).</summary>
    public static Task<string> VersionAsync(ISandboxEnvironment sandbox, string? user = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return JsonRpc.ExecScalarRequestAsync<string>(
            "version",
            null,
            new SandboxJsonRpcTransport(sandbox, SandboxCli),
            SandboxToolsErrorMapper.Instance,
            new JsonRpcCallOptions(timeout ?? VersionTimeout, user),
            cancellationToken);
    }

    /// <summary>
    /// Port of <c>_inject_container_tools_code</c>: detect arch and libc, fetch the matching artifact, create
    /// the tools directory (as root when possible), extract the tree, restrict it to 0700 when root, and
    /// start the server. Any failure is wrapped in a <see cref="SandboxInjectionException"/>.
    /// </summary>
    public async Task InjectAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var state = StateFor(sandbox);
        try
        {
            var info = await SandboxRecon.DetectSandboxOsAsync(sandbox, cancellationToken).ConfigureAwait(false);
            var musl = info.Libc == "musl";
            var artifact = await _binaries.OpenAsync(info.Architecture, musl, cancellationToken).ConfigureAwait(false);

            // Create the install dir as root if possible, and restrict it to 0700; fall back to the default
            // user for rootless sandboxes (where user-switching will be disabled, auto-detected by the server).
            if (await CreateToolsDirAsRootAsync(sandbox, cancellationToken).ConfigureAwait(false))
            {
                state.ToolsUser = "root";
            }
            else
            {
                var mkdir = await sandbox.ExecAsync(["mkdir", "-p", SandboxToolsDir], cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!mkdir.Success)
                {
                    throw new InvalidOperationException($"Failed to create sandbox tools dir: {mkdir.Stderr}");
                }
            }

            await ExtractToolsTreeAsync(sandbox, artifact, state.ToolsUser, cancellationToken).ConfigureAwait(false);

            // A root-owned 0700 tree prevents access by other, non-root users, but not by a process running
            // in the sandbox as root. The launcher is invoked as root, so this does not impede tool calls.
            if (state.ToolsUser == "root")
            {
                var chmod = await sandbox.ExecAsync(["chmod", "700", SandboxToolsDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!chmod.Success)
                {
                    throw new InvalidOperationException($"Failed to chmod sandbox tools dir: {chmod.Stderr}");
                }
            }

            // Start the server as root so it can setuid to any user; without root, user-switching is disabled.
            var start = await sandbox.ExecAsync([SandboxCli, "start-server"], user: state.ToolsUser, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!start.Success)
            {
                throw new InvalidOperationException($"Failed to start sandbox tools server: {start.Stderr}");
            }

            ProviderLogger.Info($"Injected {artifact.Name} into the sandbox at {SandboxToolsDir}" + (state.ToolsUser is null ? " (rootless)" : " as root"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxInjectionException($"Failed to inject sandbox tools into sandbox: {ex.Message}", ex);
        }
    }

    private static async Task<string> VerifyVersionAsync(ISandboxEnvironment sandbox, string? user, CancellationToken cancellationToken)
    {
        string version;
        try
        {
            version = await VersionAsync(sandbox, user, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxInjectionException($"Injected sandbox tools did not answer the version request: {ex.Message}", ex);
        }

        if (version.Trim().Length == 0)
        {
            throw new SandboxInjectionException("Injected sandbox tools reported an empty version");
        }

        ProviderLogger.Info($"Sandbox tools v{SandboxToolsBinary.Version} injected; the launcher reports package version {version}");
        return version;
    }

    private static async Task<bool> CreateToolsDirAsRootAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sandbox.ExecAsync(["mkdir", "-p", SandboxToolsDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broad catch is deliberate: providers signal "cannot exec as root" by raising provider-specific
            // exception types. Trade-off: any probe failure selects the rootless install.
            ProviderLogger.Info($"root sandbox tools dir probe failed; falling back to default user: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Port of <c>_extract_tools_tree</c>: stage the gzipped tar through write_file (binary-safe) and
    /// <c>tar xzf</c> it; when the sandbox's <c>tar</c> lacks gzip support, inject the uncompressed tar and <c>tar xf</c> it.
    /// </summary>
    private async Task ExtractToolsTreeAsync(ISandboxEnvironment sandbox, SandboxToolsArtifact artifact, string? user, CancellationToken cancellationToken)
    {
        var gzTemp = $"{SandboxToolsDir}.pkg.tgz";
        await sandbox.WriteFileAsync(gzTemp, artifact.GzipBytes, cancellationToken).ConfigureAwait(false);
        var result = await sandbox.ExecAsync(["tar", "xzf", gzTemp, "-C", SandboxToolsDir], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        await sandbox.ExecAsync(["rm", "-f", gzTemp], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return;
        }

        ProviderLogger.Info($"tar xzf failed ({result.Stderr.Trim()}); retrying with uncompressed tar");
        var tarTemp = $"{SandboxToolsDir}.pkg.tar";
        var tarBytes = await _binaries.UncompressedTarAsync(artifact, cancellationToken).ConfigureAwait(false);
        await sandbox.WriteFileAsync(tarTemp, tarBytes, cancellationToken).ConfigureAwait(false);
        result = await sandbox.ExecAsync(["tar", "xf", tarTemp, "-C", SandboxToolsDir], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        await sandbox.ExecAsync(["rm", "-f", tarTemp], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to extract sandbox tools: {result.Stderr}");
        }
    }

    /// <summary>Port of <c>_get_injection_target</c> for a single injectable: the first sandbox that already has the tools, else the first sandbox.</summary>
    private static async Task<ISandboxEnvironment> InjectionTargetAsync(CancellationToken cancellationToken)
    {
        var environments = SampleContext.Current?.Sandboxes?.Environments;
        if (environments is null || environments.Count == 0)
        {
            throw new InvalidOperationException(NoSandboxMessage);
        }

        ISandboxEnvironment? best = null;
        foreach (var environment in environments.Values)
        {
            if (await HasToolsAsync(environment, cancellationToken).ConfigureAwait(false))
            {
                return environment;
            }

            best ??= environment;
        }

        return best!;
    }

    private SandboxState StateFor(ISandboxEnvironment sandbox) => _states.GetValue(sandbox, _ => new SandboxState());

    private sealed class SandboxState
    {
        public SemaphoreSlim InjectLock { get; } = new(1, 1);

        public string? ToolsUser { get; set; }

        public string? Version { get; set; }
    }
}
