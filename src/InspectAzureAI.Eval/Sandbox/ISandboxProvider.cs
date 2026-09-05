namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of the <c>SandboxEnvironment</c> lifecycle classmethods (<c>task_init</c>, <c>sample_init</c>,
/// <c>sample_cleanup</c> via <see cref="SandboxEnvironments.Cleanup"/>, <c>task_cleanup</c>) of
/// <c>util/_sandbox/environment.py</c>.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>Registry key, e.g. "local" or "docker".</summary>
    string Type { get; }

    /// <summary>Runs once per task before any sample (e.g. builds the image).</summary>
    Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default);

    /// <summary>Creates the environments for one sample; the first entry is the default sandbox.</summary>
    Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);

    /// <summary>Runs once per task after all samples.</summary>
    Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default);
}
