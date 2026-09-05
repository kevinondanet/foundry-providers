using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of <c>util/_sandbox/docker/failure.py</c> <c>classify_exec_failure</c> for the bare <c>docker exec</c>
/// path: tells a failure of the caller's command (an ordinary <see cref="ExecResult"/>) from a provider
/// failure before the command was reached (daemon gone, container stopped, the injected <c>timeout</c>
/// wrapper missing). Recognition is deliberately narrow — the streams also carry the command's own output,
/// and a model running docker inside its sandbox emits these very messages — so only docker's exact shape
/// (a single line on a single stream with the exit code docker uses for it) is matched.
/// </summary>
internal static partial class DockerFailures
{
    private const string RuncPrefix = "oci runtime exec failed";

    /// <summary>Docker's argv wrapper injected ahead of the caller's command (<c>timeout</c>) and the binary it was asked to run.</summary>
    public sealed record InjectedWrapper(string Binary, string Target);

    public static Exception? Classify(ProcessResult result, InjectedWrapper? wrapper)
    {
        if (result.ExitCode == 0)
        {
            return null;
        }

        var stdout = result.StdoutText.Trim();
        var stderr = result.StderrText.Trim();
        // docker reports its own failure on one stream and nothing on the other; runc writes to stdout,
        // GNU timeout and the daemon to stderr, so neither stream can be relied on by itself.
        if (stdout.Length > 0 && stderr.Length > 0)
        {
            return null;
        }

        var output = stdout.Length > 0 ? stdout : stderr;
        if (output.Length == 0)
        {
            return null;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            return null;
        }

        var line = lines[0];
        var head = line.ToLowerInvariant();
        if (result.ExitCode == 1 && IsDaemonUnavailable(head))
        {
            return new SandboxUnavailableException($"The sandbox is not running and cannot execute: {line}");
        }

        if (head.StartsWith(RuncPrefix, StringComparison.Ordinal))
        {
            var notFound = RuncNotFound().Match(line);
            if (notFound.Success)
            {
                // Our own wrapper has gone missing, so the provider could not reach the caller's command; a
                // binary the caller named is their problem and stays an ordinary 127 result.
                if (result.ExitCode == 127 && wrapper is not null && notFound.Groups[1].Value == wrapper.Binary)
                {
                    return new SandboxUnavailableException($"The sandbox could not execute the command because required execution machinery is unavailable: {line}");
                }

                return null;
            }

            if (result.ExitCode == 126 && head.Contains("permission denied", StringComparison.Ordinal))
            {
                return new UnauthorizedAccessException($"Permission denied executing command: {line}");
            }

            return null;
        }

        // The wrapper launched but could not exec what we handed it. Requiring the quoted binary to be
        // exactly our target keeps a model's own `timeout ./script` out of this.
        if (wrapper is not null
            && result.ExitCode == 126
            && head.StartsWith($"{wrapper.Binary}: ", StringComparison.Ordinal)
            && head.Contains("permission denied", StringComparison.Ordinal))
        {
            var quoted = WrapperQuoted().Match(line);
            if (quoted.Success && quoted.Groups[1].Value == wrapper.Target)
            {
                return new UnauthorizedAccessException($"Permission denied executing command: {line}");
            }
        }

        return null;
    }

    private static bool IsDaemonUnavailable(string head)
    {
        if (head.StartsWith("cannot connect to the docker daemon", StringComparison.Ordinal)
            || head.StartsWith("error during connect", StringComparison.Ordinal))
        {
            return true;
        }

        if (!head.StartsWith("error response from daemon:", StringComparison.Ordinal))
        {
            return false;
        }

        return head.Contains("is not running", StringComparison.Ordinal)
            || head.Contains("no such container", StringComparison.Ordinal)
            || head.Contains("is paused", StringComparison.Ordinal)
            || head.Contains("is restarting", StringComparison.Ordinal);
    }

    [GeneratedRegex("exec: \"([^\"]*)\": executable file not found")]
    private static partial Regex RuncNotFound();

    // GNU timeout quotes with U+2018/U+2019, busybox with ASCII quotes; anchored to the colon that follows
    // the name in both wordings since a bare apostrophe also appears in prose ("can't execute 'bash': ...").
    [GeneratedRegex("[‘']([^’']*)[’']:")]
    private static partial Regex WrapperQuoted();
}
