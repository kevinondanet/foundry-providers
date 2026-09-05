namespace InspectAzureAI.Eval.Hooks;

/// <summary>
/// The eval identity Python's <c>ActiveSample</c> lends to the model-level hooks (<c>eval_set_id</c>, <c>run_id</c>,
/// <c>eval_id</c>, <c>task</c>, <c>sample_uuid</c>) plus the run's resolved hook list, carried by an AsyncLocal the
/// runner installs around each sample attempt so <c>Model</c> can stamp them without parameters. Outside a sample
/// the model hooks see no ids and only the process-wide registry.
/// </summary>
internal sealed record HookContext(string? EvalSetId, string RunId, string EvalId, string TaskName, string SampleId, IReadOnlyList<Hooks> Hooks)
{
    private static readonly AsyncLocal<HookContext?> Ambient = new();

    public static HookContext? Current => Ambient.Value;

    /// <summary>Installs <paramref name="context"/> for the current async flow; disposing restores the previous one.</summary>
    public static IDisposable Begin(HookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(HookContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
