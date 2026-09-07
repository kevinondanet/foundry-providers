using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Swe.Util;

/// <summary>Port of inspect_swe <c>_util/sandbox.py</c>: platform detection, agent cwd resolution and the checked exec helper.</summary>
public static class SandboxUtil
{
    /// <summary>Where agent binaries are installed inside the sandbox (<c>SANDBOX_INSTALL_DIR</c>).</summary>
    public const string SandboxInstallDir = "/var/tmp/.5c95f967ca830048";

    private const string MuslCheck =
        "if [ -f /lib/libc.musl-x86_64.so.1 ] || "
        + "[ -f /lib/libc.musl-aarch64.so.1 ] || "
        + "ldd /bin/ls 2>&1 | grep -q musl; then "
        + "echo 'musl'; else echo 'glibc'; fi";

    /// <summary>Port of <c>bash_command</c>.</summary>
    public static IReadOnlyList<string> BashCommand(string cmd) => ["bash", "-c", cmd];

    /// <summary>Port of <c>detect_sandbox_platform</c>: "linux-{x64|arm64}[-musl]"; other systems throw with Python's messages.</summary>
    public static async Task<string> DetectPlatformAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        var osName = await ExecAsync(sandbox, "uname -s", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (osName != "Linux")
        {
            throw new PlatformNotSupportedException($"Unsupported OS: {osName}");
        }

        var arch = await ExecAsync(sandbox, "uname -m", cancellationToken: cancellationToken).ConfigureAwait(false);
        var archType = arch switch
        {
            "x86_64" or "amd64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {arch}"),
        };

        var libc = await ExecAsync(sandbox, MuslCheck, cancellationToken: cancellationToken).ConfigureAwait(false);
        return libc == "musl" ? $"linux-{archType}-musl" : $"linux-{archType}";
    }

    /// <summary>
    /// Port of <c>resolve_agent_cwd</c>: an explicit cwd is honored (relative ones canonicalized inside the
    /// sandbox); otherwise the sandbox default, except that "/" (an image without WORKDIR) falls back to the
    /// user's home directory.
    /// </summary>
    public static async Task<string> ResolveAgentCwdAsync(ISandboxEnvironment sandbox, string? user, string? cwd, CancellationToken cancellationToken = default)
    {
        if (cwd is not null)
        {
            if (cwd.StartsWith('/'))
            {
                return cwd;
            }

            return await ExecAsync(sandbox, "pwd", user, cwd, cancellationToken).ConfigureAwait(false);
        }

        var workingDir = await ExecAsync(sandbox, "pwd", user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (workingDir != "/")
        {
            return workingDir;
        }

        var homeDir = await ExecAsync(sandbox, "cd ~ 2>/dev/null && pwd || echo \"/\"", user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (homeDir != "/")
        {
            ProviderLogger.Info($"Sandbox default working directory is '/' (image likely has no WORKDIR); running agent in home directory '{homeDir}' instead.");
        }

        return homeDir;
    }

    /// <summary>Port of <c>sandbox_exec</c>: runs <c>bash -c cmd</c>, failing with Python's message on a non-zero exit, and returns trimmed stdout.</summary>
    public static async Task<string> ExecAsync(ISandboxEnvironment sandbox, string cmd, string? user = null, string? cwd = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var result = await sandbox.ExecAsync(BashCommand(cmd), user: user, cwd: cwd, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing sandbox command {cmd}: {result.Stderr}");
        }

        return result.Stdout.Trim();
    }
}
