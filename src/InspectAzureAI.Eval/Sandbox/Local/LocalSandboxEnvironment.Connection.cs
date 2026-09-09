namespace InspectAzureAI.Eval.Sandbox.Local;

/// <summary>
/// A connection for the local sandbox: a login shell in the sample's working directory.
/// Deviation: Python's <c>LocalSandboxEnvironment</c> does not implement <c>connection()</c> (the base class raises
/// <c>NotImplementedError</c>); this port offers <c>cd DIR &amp;&amp; bash -l</c> so a human can work in the sample
/// directory. <c>user</c> is ignored, as it is for <see cref="LocalSandboxEnvironment.ExecAsync"/>.
/// </summary>
public sealed partial class LocalSandboxEnvironment : ISandboxConnectionProvider
{
    public Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = $"cd {ShellWords.Quote(WorkingDirectory)} && bash -l";
        return Task.FromResult(new SandboxConnection("local", command));
    }
}
