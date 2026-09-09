using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// How <see cref="DockerSandboxProvider"/> chooses between the bare <c>docker run</c> path and the
/// <c>docker compose</c> port for a <see cref="SandboxSpec.Config"/>. Python always goes through compose
/// (synthesising a compose file for a Dockerfile or an image); this port keeps the compose-less path for
/// images and Dockerfiles because it needs no compose plugin and gives the container host networking.
/// </summary>
public enum DockerComposeMode
{
    /// <summary>Compose for a compose file (named, in a directory, or found in the cwd for a null config); bare docker otherwise. The default.</summary>
    Auto,

    /// <summary>Everything through compose: an image or Dockerfile gets Python's auto-generated compose file (<c>network_mode: none</c>, <c>init: true</c>).</summary>
    Always,

    /// <summary>Bare docker only; a compose-shaped config is refused.</summary>
    Never,
}

/// <summary>Where the default <see cref="DockerComposeMode"/> of <see cref="DockerSandboxProvider"/> comes from.</summary>
public static class DockerComposeModes
{
    /// <summary>Environment variable naming the mode: <c>auto</c> (the default), <c>always</c> or <c>never</c>.</summary>
    public const string EnvironmentVariable = "INSPECT_DOCKER_COMPOSE";

    /// <summary>The mode <see cref="EnvironmentVariable"/> names, <see cref="DockerComposeMode.Auto"/> when unset (an unrecognised value is warned about and ignored).</summary>
    public static DockerComposeMode FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DockerComposeMode.Auto;
        }

        if (Enum.TryParse<DockerComposeMode>(value.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        ProviderLogger.Warning($"Ignoring unrecognised {EnvironmentVariable}='{value}' (expected auto, always or never).");
        return DockerComposeMode.Auto;
    }
}
