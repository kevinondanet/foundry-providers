namespace InspectAzureAI.Eval.Sandbox;

/// <summary>Port of <c>util/_sandbox/environment.py</c> <c>HostMapping</c>: one host-side binding of a published container port.</summary>
public sealed record HostMapping(string HostIp, int HostPort);

/// <summary>Port of <c>PortMapping</c>: a container port, its protocol ("tcp"/"udp") and the host bindings docker assigned.</summary>
public sealed record PortMapping(int ContainerPort, string Protocol, IReadOnlyList<HostMapping> Mappings);

/// <summary>
/// Port of <c>SandboxConnection</c>: what a person needs to attach to a running sandbox — the shell
/// command (e.g. <c>docker exec -it NAME bash -l</c>), the VS Code attach command, published ports and
/// the container name where one applies.
/// </summary>
public sealed record SandboxConnection(
    string Type,
    string Command,
    IReadOnlyList<string>? VscodeCommand = null,
    IReadOnlyList<PortMapping>? Ports = null,
    string? Container = null);

/// <summary>
/// The <c>connection()</c> method of <c>SandboxEnvironment</c>, on the environments that support it (docker).
/// Deviation: an optional interface rather than a member of <see cref="ISandboxEnvironment"/>, so providers
/// without a connection story (local, fakes) need not raise <c>NotImplementedError</c>.
/// </summary>
public interface ISandboxConnectionProvider
{
    /// <summary>Connection details for the running sandbox; <see cref="SandboxUnavailableException"/> when it is not running.</summary>
    Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default);
}
