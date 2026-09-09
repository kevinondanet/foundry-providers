using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Skills;

/// <summary>
/// The sandbox behind <c>--sandbox fake</c> for <c>examples/skills/task.py</c>: a <see cref="FakeSandboxScript"/> that
/// plays the <c>ubuntu:24.04</c> container of <c>compose.yaml</c>. The skill installer's <c>sh -c pwd</c> answers
/// <see cref="WorkingDirectory"/> (so the skills land in <c>/root/skills/&lt;name&gt;/</c> of the sample's file store,
/// with the <c>chmod +x</c> calls recorded), and the bash tool's <c>bash --login -c &lt;cmd&gt;</c> answers a
/// command that runs one of the three helper scripts with the output that script prints on a small Ubuntu 24.04
/// container on an internal Docker network. Any other command fails as the container would for an unknown one.
/// </summary>
internal static class FakeSkillsSandbox
{
    public const string WorkingDirectory = "/root";

    /// <summary>What <c>sysinfo.sh</c> prints (lscpu and free are absent from the bare image, so the /proc fallbacks answer).</summary>
    public const string SysInfoOutput =
        "=== SYSTEM INFORMATION ===\n\n"
        + "--- Operating System ---\n"
        + "Distribution: Ubuntu 24.04.2 LTS\n"
        + "Kernel: 6.10.14-linuxkit\n"
        + "Architecture: aarch64\n\n"
        + "--- CPU Information ---\n"
        + "CPU: \n"
        + "Processors: 4\n\n"
        + "--- Memory Information ---\n"
        + "Total: 7.75116 GB\n"
        + "Available: 6.94238 GB\n\n"
        + "--- System Uptime ---\n"
        + " 10:15:42 up  3:07,  0 user,  load average: 0.12, 0.08, 0.02\n";

    /// <summary>What <c>netinfo.sh</c> prints (no ip or ss in the bare image, so the /proc and hostname fallbacks answer).</summary>
    public const string NetInfoOutput =
        "=== NETWORK INFORMATION ===\n\n"
        + "--- Network Interfaces ---\n"
        + "lo\n"
        + "eth0\n\n"
        + "--- IP Addresses ---\n"
        + "172.19.0.2 \n\n"
        + "--- Routing Table ---\n"
        + "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT                                                       \n"
        + "eth0\t000013AC\t00000000\t0001\t0\t0\t0\t0000FFFF\t0\t0\t0                                                                            \n\n"
        + "--- DNS Configuration ---\n"
        + "nameserver 127.0.0.11\n\n"
        + "--- Listening Ports ---\n"
        + "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";

    /// <summary>What <c>diskinfo.sh</c> prints.</summary>
    public const string DiskInfoOutput =
        "=== DISK INFORMATION ===\n\n"
        + "--- Filesystem Usage ---\n"
        + "Filesystem      Size  Used Avail Use% Mounted on\n"
        + "overlay          59G   21G   35G  38% /\n"
        + "tmpfs            64M     0   64M   0% /dev\n"
        + "shm              64M     0   64M   0% /dev/shm\n"
        + "/dev/vda1        59G   21G   35G  38% /etc/hosts\n\n"
        + "--- Block Devices ---\n"
        + "major minor  #blocks  name\n\n"
        + " 254        0   62914560 vda\n"
        + " 254        1   62881792 vda1\n\n"
        + "--- Largest Directories in / ---\n"
        + "82M\t/\n"
        + "68M\t/usr\n"
        + "8.9M\t/var\n"
        + "4.5M\t/etc\n"
        + "72K\t/root\n\n"
        + "--- Mount Points ---\n"
        + "/dev/vda1 on /etc/hosts type ext4 (rw,relatime)\n";

    /// <summary>The script: <c>pwd</c>, the three helper scripts, and a failure for anything else.</summary>
    public static FakeSandboxScript Create() => new FakeSandboxScript()
        .OnExact(FakeSandboxScript.Ok(WorkingDirectory + "\n"), "sh", "-c", "pwd")
        .OnPrefix(FakeSandboxScript.Ok(), "chmod")
        .OnPrefix(FakeSandboxScript.Ok(), "chown")
        .OnPrefix(Bash, "bash", "--login", "-c")
        .WithDefault(FakeSandboxScript.Fail(127, "command not found\n"));

    /// <summary>The output of the helper script <paramref name="command"/> runs, else null.</summary>
    public static string? ScriptOutput(string command) =>
        command.Contains("sysinfo.sh", StringComparison.Ordinal) ? SysInfoOutput
        : command.Contains("netinfo.sh", StringComparison.Ordinal) ? NetInfoOutput
        : command.Contains("diskinfo.sh", StringComparison.Ordinal) ? DiskInfoOutput
        : null;

    private static ExecResult? Bash(FakeExecCall call)
    {
        var command = call.Cmd.Count > 3 ? call.Cmd[3] : "";
        return ScriptOutput(command) is { } output
            ? FakeSandboxScript.Ok(output)
            : FakeSandboxScript.Fail(127, $"bash: line 1: {command.Split(' ')[0]}: command not found\n");
    }
}
