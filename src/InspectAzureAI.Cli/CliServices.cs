using System.Diagnostics;

namespace InspectAzureAI.Cli;

/// <summary>
/// The process-level services the <c>view</c> command needs (finding <c>inspect</c> on <c>PATH</c> and spawning it),
/// injectable so tests can run the CLI without touching the machine.
/// </summary>
public sealed class CliServices
{
    /// <summary>The services a real terminal session uses.</summary>
    public static CliServices Default { get; } = new();

    /// <summary>Locates an executable by name on <c>PATH</c>; null when it is not there.</summary>
    public Func<string, string?> FindOnPath { get; init; } = DefaultFindOnPath;

    /// <summary>Runs a process with inherited standard streams and returns its exit code; cancellation kills it.</summary>
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<int>> RunProcessAsync { get; init; } = DefaultRunProcessAsync;

    private static string? DefaultFindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<int> DefaultRunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        });
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
