namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/environment.py</c> <c>SandboxEnvironments</c>. The FIRST entry is the default
/// sandbox, so <see cref="Environments"/> must be insertion ordered (use <see cref="Create"/> or an
/// <see cref="OrderedDictionary{TKey, TValue}"/>). <see cref="Cleanup"/> receives the runner's cleanup
/// flag: true removes the environments, false keeps them for inspection.
/// </summary>
public sealed record SandboxEnvironments(IReadOnlyDictionary<string, ISandboxEnvironment> Environments, Func<bool, Task>? Cleanup = null)
{
    /// <summary>The default sandbox (first entry); throws when there are none.</summary>
    public ISandboxEnvironment Default =>
        Environments.Count > 0
            ? Environments.First().Value
            : throw new InvalidOperationException("SandboxEnvironments contains no environments.");

    /// <summary>Builds an insertion-ordered set from (name, environment) pairs.</summary>
    public static SandboxEnvironments Create(IEnumerable<KeyValuePair<string, ISandboxEnvironment>> environments, Func<bool, Task>? cleanup = null)
    {
        var ordered = new OrderedDictionary<string, ISandboxEnvironment>();
        foreach (var (name, environment) in environments)
        {
            ordered.Add(name, environment);
        }

        return new SandboxEnvironments(ordered, cleanup);
    }

    /// <summary>A single environment registered under "default".</summary>
    public static SandboxEnvironments Single(ISandboxEnvironment environment, Func<bool, Task>? cleanup = null) =>
        Create([new KeyValuePair<string, ISandboxEnvironment>("default", environment)], cleanup);
}
