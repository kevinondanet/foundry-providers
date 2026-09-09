using System.Globalization;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>Port of <c>util/_sandbox/docker/docker.py</c> <c>parse_docker_inspect_ports</c>.</summary>
internal static class DockerPorts
{
    /// <summary>The <c>docker inspect --format</c> template that yields <c>NetworkSettings.Ports</c> as JSON.</summary>
    public const string InspectFormat = "{{json .NetworkSettings.Ports}}";

    /// <summary>
    /// Parses <c>{"5900/tcp": [{"HostIp": "0.0.0.0", "HostPort": "54023"}], "80/udp": null}</c> into port mappings;
    /// null when the container publishes nothing (Python returns <c>None</c> for an empty list too).
    /// </summary>
    public static IReadOnlyList<PortMapping>? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject ports)
        {
            return null;
        }

        var mappings = new List<PortMapping>();
        foreach (var (portProtocol, bindings) in ports)
        {
            if (bindings is not JsonArray hosts)
            {
                continue;
            }

            var slash = portProtocol.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                throw new FormatException($"Unexpected docker port key '{portProtocol}'.");
            }

            var containerPort = int.Parse(portProtocol[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var protocol = portProtocol[(slash + 1)..];
            var hostMappings = hosts
                .Select(host => host as JsonObject ?? throw new FormatException($"Unexpected docker port binding for '{portProtocol}'."))
                .Select(host => new HostMapping(
                    host["HostIp"]?.GetValue<string>() ?? "",
                    int.Parse(host["HostPort"]?.GetValue<string>() ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture)))
                .ToList();
            mappings.Add(new PortMapping(containerPort, protocol, hostMappings));
        }

        return mappings.Count > 0 ? mappings : null;
    }
}
