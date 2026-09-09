using System.Security.Cryptography;
using InspectAzureAI.Eval.Sandbox.Docker.Compose;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of the <c>DockerSandboxEnvironment</c> lifecycle classmethods of <c>util/_sandbox/docker/docker.py</c>.
/// Two paths, chosen per config by <see cref="ComposeMode"/>: a compose file (or a directory / the cwd holding
/// one — Deviation: the process cwd, not the task's directory as Python's <c>inspect eval</c> chdir gives; pass the
/// task directory as the config to look there) takes the compose port (<see cref="DockerComposeSandbox"/>: <c>compose build</c> once, <c>compose up --wait</c>
/// per sample, one environment per service, <c>compose down --volumes</c> on cleanup); an image or Dockerfile
/// keeps this port's compose-less path: <c>task_init</c> builds or pulls the image once, <c>sample_init</c> starts
/// one idle container (<c>sleep infinity</c>, or <c>tail -f /dev/null</c> when the image has no <c>sleep</c>) with
/// the host reachable as host.docker.internal, and cleanup removes it — or keeps it, named, for inspection.
/// <see cref="DockerComposeMode.Always"/> sends images and Dockerfiles through Python's auto-generated compose file too.
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

    private readonly DockerComposeSandbox _compose;

    public DockerSandboxProvider()
        : this(new ProcessRunner(), DockerComposeModes.FromEnvironment())
    {
    }

    /// <summary>A provider with an explicit compose policy (the parameterless constructor reads <see cref="DockerComposeModes.EnvironmentVariable"/>).</summary>
    public DockerSandboxProvider(DockerComposeMode composeMode)
        : this(new ProcessRunner(), composeMode)
    {
    }

    internal DockerSandboxProvider(DockerCli cli)
        : this(cli, new ComposeCli(new ProcessRunner(), cli.Executable), DockerComposeMode.Auto)
    {
    }

    internal DockerSandboxProvider(IProcessRunner runner, DockerComposeMode composeMode)
        : this(new DockerCli(runner), new ComposeCli(runner), composeMode)
    {
    }

    internal DockerSandboxProvider(DockerCli cli, ComposeCli compose, DockerComposeMode composeMode)
    {
        _cli = cli;
        _compose = new DockerComposeSandbox(cli, compose);
        ComposeMode = composeMode;
    }

    public string Type => "docker";

    /// <summary>Which configs go through <c>docker compose</c> (see <see cref="UsesCompose"/>).</summary>
    public DockerComposeMode ComposeMode { get; }

    public async Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default)
    {
        if (UsesCompose(config))
        {
            await _compose.TaskInitAsync(taskName, config, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DockerImages.EnsureAsync(_cli, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the sample container. Once a name has been claimed, any failure or cancellation before the cleanup
    /// delegate exists removes the container ourselves (an interrupted <c>docker run</c> may already have created it).
    /// </summary>
    public async Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (UsesCompose(config))
        {
            return await _compose.SampleInitAsync(taskName, config, metadata, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>Compose: brings down the projects samples kept (or lists them) and removes auto-generated compose files. Bare docker: images are kept (rebuilding is content-addressed), so there is nothing to do.</summary>
    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default) =>
        UsesCompose(config) ? _compose.TaskCleanupAsync(taskName, cleanup, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Whether <paramref name="config"/> takes the compose path: always under <see cref="DockerComposeMode.Always"/>;
    /// under <see cref="DockerComposeMode.Auto"/> when it is compose-shaped (see <see cref="ComposeFiles.IsComposeConfig"/>:
    /// a compose file, a directory holding one, or null with one in the cwd); never under <see cref="DockerComposeMode.Never"/>,
    /// which refuses a compose-shaped config with an <see cref="ArgumentException"/>.
    /// </summary>
    public bool UsesCompose(string? config) => ComposeMode switch
    {
        DockerComposeMode.Always => true,
        DockerComposeMode.Never => ComposeFiles.IsComposeConfig(config)
            ? throw new ArgumentException($"Sandbox config '{config}' is a docker compose file, but this provider runs without compose (DockerComposeMode.Never).", nameof(config))
            : false,
        _ => ComposeFiles.IsComposeConfig(config),
    };

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
