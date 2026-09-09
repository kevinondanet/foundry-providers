using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>A sandbox running the legacy <c>inspect-tool-support</c> service and the version it reports.</summary>
public sealed record LegacyToolSupportSandbox(ISandboxEnvironment Sandbox, Version Version)
{
    /// <summary>A JSON-RPC transport over this sandbox's <c>inspect-tool-support</c> CLI.</summary>
    public SandboxJsonRpcTransport Transport => new(Sandbox, LegacyToolSupport.LegacySandboxCli);
}

/// <summary>
/// Port of <c>tool/_sandbox_tools_utils/_legacy_helpers.py</c>: the compatibility layer for the legacy
/// <c>inspect_tool_support</c> service, which still hosts the web browser while the injected
/// <c>inspect-sandbox-tools</c> executable handles bash_session, text_editor and MCP. The legacy service is
/// deployed through the <c>aisiuk/inspect-tool-support</c> image (or a <c>pip install inspect-tool-support</c>
/// into the user's own image), never injected, so it is found by name on the sandbox's <c>PATH</c>.
/// </summary>
public static class LegacyToolSupport
{
    /// <summary>Port of <c>LEGACY_SANDBOX_CLI</c>.</summary>
    public const string LegacySandboxCli = "inspect-tool-support";

    /// <summary>Port of <c>_INSPECT_TOOL_SUPPORT_IMAGE_DOCKERHUB</c>.</summary>
    public const string InspectToolSupportImageDockerHub = "aisiuk/inspect-tool-support";

    /// <summary>Port of <c>_FIRST_PUBLISHED_VERSION</c>: assumed when the container has no <c>version</c> method.</summary>
    public static readonly Version FirstPublishedVersion = new(0, 1, 6);

    /// <summary>The <c>version</c> RPC budget (Python: <c>timeout=5</c>).</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Port of <c>legacy_tool_support_sandbox(tool_name, sandbox_name=)</c>: the sample sandbox (or the named
    /// one) with <c>inspect-tool-support</c> on its <c>PATH</c> and the version it reports; none is a
    /// <see cref="PrerequisiteError"/> carrying Python's compose-file guidance.
    /// </summary>
    public static async Task<LegacyToolSupportSandbox> LegacyToolSupportSandboxAsync(string toolName, string? sandboxName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var sandbox = await SandboxWith.FindAsync(LegacySandboxCli, onPath: true, name: sandboxName, cancellationToken).ConfigureAwait(false);
        if (sandbox is not null)
        {
            var currentVersion = await GetSandboxToolSupportVersionAsync(sandbox, cancellationToken).ConfigureAwait(false);
            return new LegacyToolSupportSandbox(sandbox, currentVersion);
        }

        throw new PrerequisiteError(NotFoundMessage(toolName, sandboxName));
    }

    /// <summary>The <see cref="PrerequisiteError"/> text of <c>legacy_tool_support_sandbox</c> (Python's dedented f-string, stripped).</summary>
    public static string NotFoundMessage(string toolName, string? sandboxName = null)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var where = sandboxName is null ? "any of the sandboxes" : $"the sandbox '{sandboxName}'";
        return
            $"The {toolName} service was not found in {where} for this sample. Please add the {toolName} to your configuration.\n"
            + "\n"
            + $"For example, the following Docker compose file uses the {InspectToolSupportImageDockerHub} reference image as its default sandbox:\n"
            + "\n"
            + "services:\n"
            + "  default:\n"
            + $"    image: \"{InspectToolSupportImageDockerHub}\"\n"
            + "    init: true\n"
            + "\n"
            + "Alternatively, you can include the service into your own Dockerfile:\n"
            + "\n"
            + "ENV PATH=\"$PATH:/opt/inspect_tool_support/bin\"\n"
            + "RUN python -m venv /opt/inspect_tool_support && \\\n"
            + "    /opt/inspect_tool_support/bin/pip install inspect-tool-support && \\\n"
            + "    /opt/inspect_tool_support/bin/inspect-tool-support post-install";
    }

    /// <summary>
    /// Port of <c>_get_sandbox_tool_support_version</c>: the <c>version</c> RPC of the legacy CLI; a container
    /// without a version method (-32601) is assumed to be <see cref="FirstPublishedVersion"/>.
    /// </summary>
    public static async Task<Version> GetSandboxToolSupportVersionAsync(ISandboxEnvironment sandbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        try
        {
            var version = await JsonRpc.ExecScalarRequestAsync<string>(
                "version",
                new JsonObject(),
                new SandboxJsonRpcTransport(sandbox, LegacySandboxCli),
                SandboxToolsErrorMapper.Instance,
                new JsonRpcCallOptions(VersionTimeout),
                cancellationToken).ConfigureAwait(false);
            return ParseSemver(version);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("-32601", StringComparison.Ordinal))
        {
            // The container doesn't even have a version method. The first version
            // published was 0.1.6, so we'll have to assume it was that old.
            return FirstPublishedVersion;
        }
    }

    /// <summary>
    /// Port of <c>semver.Version.parse</c> for the comparisons the legacy helpers need. Deviation: the
    /// pre-release and build metadata of a SemVer string are dropped and the numeric core is returned as a
    /// <see cref="Version"/> (major.minor.patch); an invalid string is an <see cref="InvalidOperationException"/>
    /// (Python's <c>ValueError</c>).
    /// </summary>
    public static Version ParseSemver(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            core = core[..cut];
        }

        var parts = core.Split('.');
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return new Version(major, minor, patch);
        }

        throw new InvalidOperationException($"{text} is not valid SemVer string");
    }
}
