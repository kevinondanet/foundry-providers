namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of <c>DockerSandboxEnvironment.connection()</c> (<c>util/_sandbox/docker/docker.py</c>): the
/// <c>docker exec -it [--user USER] NAME bash -l</c> login command, the VS Code attach command (only without an
/// explicit user, as VS Code cannot attach as one), the published ports and the container name.
/// Deviation: the container is named directly (no compose project lookup); a stopped container is a
/// <see cref="SandboxUnavailableException"/> where Python raises <c>ConnectionError</c>.
/// </summary>
public sealed partial class DockerSandboxEnvironment : ISandboxConnectionProvider
{
    /// <summary>Port of the <c>vscode_command</c> the connection carries: the Dev Containers attach command.</summary>
    public const string VscodeAttachCommand = "remote-containers.attachToRunningContainer";

    public async Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default)
    {
        if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SandboxUnavailableException($"Container '{ContainerName}' is not currently running.");
        }

        List<string> argv = ["docker", "exec", "-it"];
        if (user is not null)
        {
            argv.AddRange(["--user", user]);
        }

        argv.AddRange([ContainerName, "bash", "-l"]);

        return new SandboxConnection(
            Type: "docker",
            Command: ShellWords.Join(argv),
            VscodeCommand: user is null ? [VscodeAttachCommand, ContainerName] : null,
            Ports: await PortsAsync(cancellationToken).ConfigureAwait(false),
            Container: ContainerName);
    }

    /// <summary>Port of <c>get_ports_info</c>: the published ports, or null when docker does not answer in time.</summary>
    private async Task<IReadOnlyList<PortMapping>?> PortsAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await _cli.InspectAsync(ContainerName, DockerPorts.InspectFormat, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // It's currently a policy decision to let docker timeouts be silent.
            return null;
        }

        return DockerPorts.Parse(json);
    }
}
