namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of the <c>SandboxEnvironment.connection()</c> call site: resolves the connection of any
/// <see cref="ISandboxEnvironment"/> through the optional <see cref="ISandboxConnectionProvider"/> seam.
/// Deviation: Python raises <c>NotImplementedError</c> from the base class; here an environment that does not
/// implement the provider interface raises <see cref="NotSupportedException"/> instead.
/// </summary>
public static class SandboxConnections
{
    /// <summary>
    /// Port of <c>sandbox.connection(user=...)</c>: the connection details of a running sandbox;
    /// <see cref="NotSupportedException"/> when the environment has no connection story.
    /// </summary>
    public static Task<SandboxConnection> ConnectionAsync(this ISandboxEnvironment sandbox, string? user = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return sandbox is ISandboxConnectionProvider provider
            ? provider.ConnectionAsync(user, cancellationToken)
            : throw new NotSupportedException($"Sandbox environment '{sandbox.GetType().Name}' does not support connections.");
    }
}
