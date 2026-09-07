using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// The per-sample model accumulators of <c>model/_model.py</c> that <c>init_sample_model_usage</c> installs:
/// the model-fallback rollup (<c>init_sample_model_fallbacks</c> / <c>record_sample_model_fallback</c> /
/// <c>sample_model_fallbacks</c>) and usage by role (<c>init_sample_role_usage</c> / <c>sample_role_usage</c>).
/// Ambient (an <see cref="AsyncLocal{T}"/> installed by <see cref="Begin"/>); recording outside a scope is a
/// no-op and the readers return empty, exactly as Python's context variables do without their init.
/// </summary>
public sealed class SampleModelAccumulators : IDisposable
{
    private static readonly AsyncLocal<SampleModelAccumulators?> Ambient = new();

    private readonly object _sync = new();
    private readonly SampleModelAccumulators? _previous;
    private readonly Dictionary<(string Model, string FallbackModel), int> _fallbacks = new();
    private readonly List<(string Model, string FallbackModel)> _fallbackOrder = [];
    private readonly Dictionary<string, ModelUsage> _roleUsage = new(StringComparer.Ordinal);

    private SampleModelAccumulators(SampleModelAccumulators? previous)
    {
        _previous = previous;
    }

    /// <summary>The accumulators of the current sample, or null outside a <see cref="Begin"/> scope.</summary>
    public static SampleModelAccumulators? Current => Ambient.Value;

    /// <summary>Installs fresh accumulators for the current async flow (port of <c>init_sample_model_usage</c>); disposing restores the previous ones.</summary>
    public static SampleModelAccumulators Begin()
    {
        var accumulators = new SampleModelAccumulators(Ambient.Value);
        Ambient.Value = accumulators;
        return accumulators;
    }

    /// <summary>
    /// Port of <c>record_sample_model_fallback</c>: adds the output's <see cref="ModelOutput.Fallback"/> (if any) to
    /// the rollup, keyed by (model, fallback model). No-op without an active scope.
    /// </summary>
    public static void RecordFallback(ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Fallback is { } fallback)
        {
            Current?.AddFallback(fallback);
        }
    }

    /// <summary>Port of the role branch of <c>record_and_check_model_usage</c>: accumulates usage under <paramref name="role"/>.</summary>
    public static void RecordRoleUsage(string role, ModelUsage usage)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(usage);
        Current?.AddRoleUsage(role, usage);
    }

    /// <summary>Port of <c>sample_model_fallbacks()</c>: the rollup for the active sample (empty outside a scope).</summary>
    public static IReadOnlyList<ModelFallback> SampleModelFallbacks() => Current?.ModelFallbacks ?? [];

    /// <summary>Port of <c>sample_role_usage()</c>: usage by role for the active sample (empty outside a scope).</summary>
    public static IReadOnlyDictionary<string, ModelUsage> SampleRoleUsage() => Current?.RoleUsage ?? new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    /// <summary>
    /// Fallbacks recorded so far, one entry per (model, fallback model) pair in first-seen order with the
    /// summed <see cref="ModelFallback.Count"/>; per-call <see cref="ModelFallback.Metadata"/> is never carried into the rollup.
    /// </summary>
    public IReadOnlyList<ModelFallback> ModelFallbacks
    {
        get
        {
            lock (_sync)
            {
                return _fallbackOrder.Select(key => new ModelFallback(key.Model, key.FallbackModel, _fallbacks[key])).ToArray();
            }
        }
    }

    /// <summary>Usage summed per role name.</summary>
    public IReadOnlyDictionary<string, ModelUsage> RoleUsage
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, ModelUsage>(_roleUsage, StringComparer.Ordinal);
            }
        }
    }

    private void AddFallback(ModelFallback fallback)
    {
        var key = (fallback.Model, fallback.FallbackModel);
        lock (_sync)
        {
            if (!_fallbacks.ContainsKey(key))
            {
                _fallbackOrder.Add(key);
                _fallbacks[key] = 0;
            }

            _fallbacks[key] += fallback.Count;
        }
    }

    private void AddRoleUsage(string role, ModelUsage usage)
    {
        lock (_sync)
        {
            _roleUsage[role] = _roleUsage.TryGetValue(role, out var existing) ? existing + usage : usage;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Ambient.Value, this))
        {
            Ambient.Value = _previous;
        }
    }
}
