using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>
/// Port of <c>util/_sandbox/recon.py</c> <c>SupportedContainerOSInfo</c>: what tool injection needs to know
/// about a sandbox. <see cref="Architecture"/> is "amd64" or "arm64"; <see cref="Libc"/> is "glibc" or "musl".
/// </summary>
public sealed record SandboxOsInfo(string Os, string Distribution, string Version, string Architecture, string Libc);

/// <summary>Port of <c>util/_sandbox/recon.py</c>: detects the sandbox OS, distribution, architecture and libc with <c>sh -c</c> probes.</summary>
public static class SandboxRecon
{
    /// <summary>Per-probe exec timeout (Python: <c>timeout=120</c>).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);

    private const string SystemCommand = "\nif command -v uname >/dev/null 2>&1; then\n    uname -s\nelse\n    echo \"unknown\"\nfi\n";

    private const string ArchCommand = "\nif command -v uname >/dev/null 2>&1; then\n    uname -m\nelse\n    echo \"unknown\"\nfi\n";

    private const string MuslCheckCommand =
        "if [ -f /lib/libc.musl-x86_64.so.1 ] || "
        + "[ -f /lib/libc.musl-aarch64.so.1 ] || "
        + "ldd /bin/ls 2>&1 | grep -q musl; then "
        + "echo 'musl'; else echo 'glibc'; fi";

    private const string OsReleaseCommand = "\nif [ -f /etc/os-release ]; then\n    cat /etc/os-release\nelse\n    echo \"not_found\"\nfi\n";

    private const string KaliVersionCommand = "[ -f /etc/kali_version ] && cat /etc/kali_version";

    private const string DebianVersionCommand = "[ -f /etc/debian_version ] && cat /etc/debian_version";

    /// <summary>
    /// Port of <c>detect_sandbox_os</c>: only Linux is supported for tool injection; anything else is a
    /// <see cref="PlatformNotSupportedException"/> (Python's <c>NotImplementedError</c>).
    /// </summary>
    public static async Task<SandboxOsInfo> DetectSandboxOsAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var system = await SandboxExecAsync(sandbox, SystemCommand, cancellationToken).ConfigureAwait(false);
        system = system.Length > 0 ? system : "unknown";
        if (system == "Linux")
        {
            return await DetectLinuxAsync(sandbox, cancellationToken).ConfigureAwait(false);
        }

        throw new PlatformNotSupportedException(
            $"Tool support injection is not implemented for OS: {system}. Only Linux containers are currently supported.");
    }

    /// <summary>Port of <c>_detect_architecture</c>: maps <c>uname -m</c> to "amd64"/"arm64".</summary>
    public static async Task<string> DetectArchitectureAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var output = await SandboxExecAsync(sandbox, ArchCommand, cancellationToken).ConfigureAwait(false);
        if (output.Length == 0 || output == "unknown")
        {
            throw new InvalidOperationException("Unable to determine sandbox architecture");
        }

        return output.ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            var other => throw new PlatformNotSupportedException($"Architecture {other} is not supported."),
        };
    }

    /// <summary>Port of <c>_detect_libc</c>: "musl" when the musl loader is present or <c>ldd</c> reports musl, else "glibc".</summary>
    public static async Task<string> DetectLibcAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        var output = await SandboxExecAsync(sandbox, MuslCheckCommand, cancellationToken).ConfigureAwait(false);
        return output == "musl" ? "musl" : "glibc";
    }

    private static async Task<SandboxOsInfo> DetectLinuxAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken)
    {
        var architecture = await DetectArchitectureAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var libc = await DetectLibcAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var osRelease = await SandboxExecAsync(sandbox, OsReleaseCommand, cancellationToken).ConfigureAwait(false);
        if (osRelease.Length > 0 && osRelease != "not_found")
        {
            var info = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in osRelease.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator >= 0)
                {
                    info[line[..separator]] = line[(separator + 1)..].Trim('"');
                }
            }

            var distribution = info.GetValueOrDefault("ID", "").ToLowerInvariant() switch
            {
                "ubuntu" => "Ubuntu",
                "debian" => "Debian",
                "alpine" => "Alpine",
                _ => "Kali Linux",
            };
            return new SandboxOsInfo("Linux", distribution, info.GetValueOrDefault("VERSION", "Unknown"), architecture, libc);
        }

        var kaliVersion = await SandboxExecAsync(sandbox, KaliVersionCommand, cancellationToken).ConfigureAwait(false);
        if (kaliVersion.Length > 0)
        {
            return new SandboxOsInfo("Linux", "Kali Linux", kaliVersion, architecture, libc);
        }

        var debianVersion = await SandboxExecAsync(sandbox, DebianVersionCommand, cancellationToken).ConfigureAwait(false);
        if (debianVersion.Length > 0)
        {
            return new SandboxOsInfo("Linux", "Debian-based", debianVersion, architecture, libc);
        }

        throw new InvalidOperationException("Could not determine OS/distribution");
    }

    private static async Task<string> SandboxExecAsync(ISandboxEnvironment sandbox, string command, CancellationToken cancellationToken)
    {
        var result = await sandbox.ExecAsync(["sh", "-c", command], timeout: ProbeTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing command {command}: {result.Stderr}");
        }

        return result.Stdout.Trim();
    }
}
