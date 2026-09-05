using System.Security.Cryptography;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of the <c>DockerSandboxEnvironment</c> lifecycle classmethods of <c>util/_sandbox/docker/docker.py</c>
/// without compose: <c>task_init</c> builds or pulls the image once, <c>sample_init</c> starts one idle
/// container (<c>sleep infinity</c>, or <c>tail -f /dev/null</c> when the image has no <c>sleep</c>) with the
/// host reachable as host.docker.internal, and cleanup removes it — or keeps it, named, for inspection.
/// </summary>
public sealed class DockerSandboxProvider : ISandboxProvider
{
    /// <summary>Image used when the sandbox config is null.</summary>
    public const string DefaultImage = DockerImages.DefaultImage;

    /// <summary>Container names are this prefix plus 12 random hex digits.</summary>
    public const string ContainerPrefix = "inspect-swe-";

    private static readonly string[] SleepCommand = ["sleep", "infinity"];

    private static readonly string[] TailCommand = ["tail", "-f", "/dev/null"];

    private readonly DockerCli _cli;

    public DockerSandboxProvider()
        : this(new DockerCli())
    {
    }

    internal DockerSandboxProvider(DockerCli cli)
    {
        _cli = cli;
    }

    public string Type => "docker";

    public async Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default) =>
        await DockerImages.EnsureAsync(_cli, config, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Starts the sample container. Once a name has been claimed, any failure or cancellation before the cleanup
    /// delegate exists removes the container ourselves (an interrupted <c>docker run</c> may already have created it).
    /// </summary>
    public async Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var image = await DockerImages.EnsureAsync(_cli, config, cancellationToken).ConfigureAwait(false);
        var name = ContainerPrefix + RandomNumberGenerator.GetHexString(12, lowercase: true);
        string workingDirectory;
        try
        {
            await StartContainerAsync(image, name, cancellationToken).ConfigureAwait(false);
            workingDirectory = await _cli.InspectAsync(name, "{{.Config.WorkingDir}}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveQuietlyAsync(name).ConfigureAwait(false);
            throw;
        }

        var environment = new DockerSandboxEnvironment(_cli, name, workingDirectory);
        return SandboxEnvironments.Single(environment, async cleanup =>
        {
            if (cleanup)
            {
                await RemoveQuietlyAsync(name).ConfigureAwait(false);
            }
            else
            {
                ProviderLogger.Info($"Sandbox container {name} was kept for inspection (docker exec -it {name} bash; docker rm -f {name} to remove).");
            }
        });
    }

    /// <summary>Images are kept (rebuilding is content-addressed), so there is nothing to do.</summary>
    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private async Task StartContainerAsync(string image, string name, CancellationToken cancellationToken)
    {
        try
        {
            await _cli.RunDetachedAsync(image, name, SleepCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex) when (IsMissingBinary(ex, SleepCommand[0]))
        {
            // A failed `docker run` leaves the container in the Created state under our name.
            await RemoveQuietlyAsync(name).ConfigureAwait(false);
            await _cli.RunDetachedAsync(image, name, TailCommand, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsMissingBinary(SandboxUnavailableException ex, string binary) =>
        ex.Message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains($"\"{binary}\"", StringComparison.Ordinal);

    /// <summary>Removal is never cancelled: it runs on the failure path of a cancelled init and must not orphan the container.</summary>
    private async Task RemoveQuietlyAsync(string name)
    {
        try
        {
            await _cli.RemoveContainerAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            ProviderLogger.Warning($"Failed to remove sandbox container {name}: {ex.Message}");
        }
    }
}
