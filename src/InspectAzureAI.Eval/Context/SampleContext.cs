using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Context;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The ambient per-sample services Python exposes as module functions — <c>store()</c>, <c>sandbox()</c>,
/// <c>transcript()</c>, <c>get_model()</c>, <c>score()</c> and the sample limits — carried by an AsyncLocal
/// so they flow through awaits into solvers, tools and agents.
/// </summary>
public sealed class SampleContext
{
    private static readonly AsyncLocal<SampleContext?> Ambient = new();

    public static SampleContext? Current => Ambient.Value;

    /// <summary>Installs <paramref name="context"/> for the current async flow; disposing restores the previous one.</summary>
    public static IDisposable Begin(SampleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    /// <summary>Port of <c>get_model()</c> with no arguments: the model under evaluation.</summary>
    public required Model ActiveModel { get; init; }

    public Store Store { get; init; } = new();

    public Transcript Transcript { get; init; } = new();

    public Limits Limits { get; init; } = new();

    public SandboxEnvironments? Sandboxes { get; init; }

    /// <summary>
    /// Port of <c>sample_state()</c>: the sample's own <see cref="TaskState"/>, which <c>score(AgentState)</c>
    /// copies before swapping in an agent's messages and output (null outside the runner).
    /// </summary>
    public TaskState? SampleState { get; init; }

    /// <summary>Port of <c>score(state)</c>: runs the task scorers against a state (null when scoring is unavailable).</summary>
    public Func<TaskState, Task<IReadOnlyList<Score>>>? Scorer { get; init; }

    /// <summary>
    /// Port of <c>sandbox(name)</c>: null, "default" or a single registered environment resolve to the first
    /// entry; an unknown name is an <see cref="ArgumentException"/>; no sandboxes at all is an
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public ISandboxEnvironment Sandbox(string? name = null)
    {
        var environments = Sandboxes?.Environments;
        if (environments is null || environments.Count == 0)
        {
            throw new InvalidOperationException(
                "No sandbox environment has been provided for the current sample or task. "
                + "Please specify a sandbox for the sample or a global default sandbox for the task");
        }

        if (name is null || name == "default" || environments.Count == 1)
        {
            return environments.First().Value;
        }

        return environments.TryGetValue(name, out var environment)
            ? environment
            : throw new ArgumentException($"SandboxEnvironment '{name}' is not a recognized environment name.", nameof(name));
    }

    public static SampleContext Require() =>
        Current ?? throw new InvalidOperationException("No sample context is active; SampleContext.Begin must enclose this call.");

    private sealed class Scope(SampleContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
