namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of the instance side of <c>util/_sandbox/docker/docker.py</c> <c>DockerSandboxEnvironment</c>: one
/// service of a running compose project. Deviation: commands, file reads and writes go through
/// <see cref="DockerSandboxEnvironment"/> against the service's container (resolved once from <c>compose ps</c>
/// after <c>up</c>) — <c>docker exec CONTAINER</c> rather than <c>docker compose exec SERVICE</c>. The two are
/// equivalent for a started project, the bare form skips compose's per-call re-read of the compose file,
/// and it keeps the secret-safe <c>-e NAME</c> environment forwarding and the exec failure classification of
/// the bare path. Copies (<c>compose cp</c>) and <see cref="ConnectionAsync"/> (<c>compose ps</c>, the
/// <c>docker exec -it</c> command and the published ports) follow Python exactly.
/// </summary>
public sealed class DockerComposeSandboxEnvironment : ISandboxEnvironment, ISandboxConnectionProvider
{
    private readonly DockerCli _docker;

    private readonly ComposeCli _compose;

    private readonly DockerSandboxEnvironment _container;

    internal DockerComposeSandboxEnvironment(DockerCli docker, ComposeCli compose, ComposeProject project, string service, string containerName, string workingDirectory)
    {
        _docker = docker;
        _compose = compose;
        Project = project;
        Service = service;
        _container = new DockerSandboxEnvironment(docker, containerName, workingDirectory);
    }

    /// <summary>The compose project (name, compose file, forwarded environment) this service belongs to.</summary>
    public ComposeProject Project { get; }

    /// <summary>The service's name in the compose file.</summary>
    public string Service { get; }

    /// <summary>The container compose created for the service (<c>PROJECT-SERVICE-1</c>), usable with the docker CLI directly.</summary>
    public string ContainerName => _container.ContainerName;

    /// <summary>The container's working directory ("/" when the image has none): default cwd for <see cref="ExecAsync"/> and base for relative file paths.</summary>
    public string WorkingDirectory => _container.WorkingDirectory;

    public string HostAddress => _container.HostAddress;

    public Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        _container.ExecAsync(cmd, input, cwd, env, user, timeout, cancellationToken);

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        _container.WriteFileAsync(path, contents, cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default) =>
        _container.WriteFileAsync(path, contents, cancellationToken);

    public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        _container.ReadFileAsync(path, cancellationToken);

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default) =>
        _container.ReadFileBytesAsync(path, cancellationToken);

    /// <summary>Port of <c>container_file</c>: absolute paths are used as-is, relative ones resolve under <see cref="WorkingDirectory"/>.</summary>
    public string ContainerPath(string path) => _container.ContainerPath(path);

    /// <summary>
    /// Copies a host file into the service with <c>compose cp -L -- HOST SERVICE:PATH</c> (creating the parent
    /// directory first). The fast path for large payloads such as an agent binary.
    /// </summary>
    public async Task CopyToContainerAsync(string hostPath, string containerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostPath);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException($"File '{hostPath}' was not found.", hostPath);
        }

        var file = ContainerPath(containerPath);
        var parent = PosixParent(file);
        var mkdir = await ExecAsync(["mkdir", "-p", parent], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!mkdir.Success)
        {
            throw new InvalidOperationException($"Failed to create container directory '{parent}': {mkdir.Stderr.Trim()}");
        }

        await _compose.CopyAsync(Project, Path.GetFullPath(hostPath), $"{Service}:{file}", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the <c>compose_cp</c> half of <c>read_file</c>: copies <paramref name="containerPath"/> out of the
    /// service to <paramref name="hostPath"/> with <c>compose cp -L -- SERVICE:PATH HOST</c>, mapping compose's
    /// errors to <see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/> and
    /// <see cref="IOException"/> (a directory) as Python maps them to <c>FileNotFoundError</c>,
    /// <c>PermissionError</c> and <c>IsADirectoryError</c>. Never retried on timeout (a failed copy may
    /// have replaced the destination).
    /// </summary>
    public async Task CopyFromContainerAsync(string containerPath, string hostPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        ArgumentException.ThrowIfNullOrEmpty(hostPath);
        var file = ContainerPath(containerPath);
        try
        {
            await _compose.CopyAsync(
                Project,
                $"{Service}:{file}",
                Path.GetFullPath(hostPath),
                outputLimit: SandboxLimits.MaxReadFileSize,
                timeoutRetry: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message.ToLowerInvariant();
            if (message.Contains("could not find the file", StringComparison.Ordinal) || message.Contains("no such file or directory", StringComparison.Ordinal))
            {
                throw new FileNotFoundException("No such file or directory.", containerPath, ex);
            }

            if (message.Contains("permission denied", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Permission denied: '{containerPath}'.", ex);
            }

            if (message.Contains("cannot copy directory", StringComparison.Ordinal) || message.Contains("is a directory", StringComparison.Ordinal))
            {
                throw new IOException($"'{containerPath}' is a directory.", ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Port of <c>connection()</c>: the service's container from <c>compose ps</c> (a service that is not running is a
    /// <see cref="SandboxUnavailableException"/>), the <c>docker exec -it [--user USER] CONTAINER bash -l</c>
    /// command, the VS Code attach command (omitted when a user is given, as VS Code cannot attach as one), and
    /// the published ports read from <c>docker inspect</c> (null when that times out, as Python lets it be silent).
    /// </summary>
    public async Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default)
    {
        var containers = await _compose.PsAsync(Project, cancellationToken: cancellationToken).ConfigureAwait(false);
        var container = containers.FirstOrDefault(entry => entry.Service == Service)?.Name;
        if (string.IsNullOrEmpty(container))
        {
            throw new SandboxUnavailableException($"Service '{Service}' is not currently running.");
        }

        IReadOnlyList<string>? vscodeCommand = user is null ? ["remote-containers.attachToRunningContainer", container] : null;
        List<string> command = ["docker", "exec", "-it"];
        if (!string.IsNullOrEmpty(user))
        {
            command.Add("--user");
            command.Add(user);
        }

        command.AddRange([container, "bash", "-l"]);
        var ports = await GetPortsInfoAsync(container, cancellationToken).ConfigureAwait(false);
        return new SandboxConnection("docker", ShellWords.Join(command), vscodeCommand, ports, container);
    }

    /// <summary>Port of <c>get_ports_info</c> over <see cref="DockerPorts"/>: the published ports, or null when <c>docker inspect</c> does not answer in time.</summary>
    private async Task<IReadOnlyList<PortMapping>?> GetPortsInfoAsync(string container, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await _docker.InspectAsync(container, DockerPorts.InspectFormat, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex) when (ex.Message.Contains("did not complete", StringComparison.Ordinal))
        {
            // It is a policy decision (Python's) to let docker inspect timeouts be silent.
            return null;
        }

        return DockerPorts.Parse(json);
    }

    private static string PosixParent(string file)
    {
        var slash = file.LastIndexOf('/');
        return slash <= 0 ? "/" : file[..slash];
    }
}
