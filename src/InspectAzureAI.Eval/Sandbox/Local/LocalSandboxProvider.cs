namespace InspectAzureAI.Eval.Sandbox.Local;

/// <summary>
/// Port of the <c>LocalSandboxEnvironment</c> lifecycle classmethods (<c>util/_sandbox/local.py</c>):
/// every sample gets a fresh temp directory that is removed on cleanup.
/// </summary>
public sealed class LocalSandboxProvider : ISandboxProvider
{
    public string Type => "local";

    public Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var environment = new LocalSandboxEnvironment();
        return Task.FromResult(SandboxEnvironments.Single(environment, cleanup =>
        {
            // cleanup=false keeps the directory so a failed sample can be inspected (mirrors the docker
            // provider keeping its container); Python's local sandbox always deletes it.
            if (cleanup)
            {
                environment.Dispose();
            }

            return Task.CompletedTask;
        }));
    }

    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
