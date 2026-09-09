using System.Collections;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of the <c>dict[str, list[Metric]]</c> form of <c>@scorer(metrics=...)</c> (<c>scorer/_scorer.py</c>): metrics keyed by
/// the keys of a dictionary-valued score. Each key produces its own <see cref="Log.EvalScore"/> named after the key, with
/// the scorer recorded in <see cref="Log.EvalScore.Scorer"/> (<c>_eval/task/results.py</c> <c>scorers_from_metric_dict</c>).
/// A key may be a glob pattern (<c>fnmatch</c>: <c>*</c>, <c>?</c>, <c>[seq]</c>, <c>[!seq]</c>) that is expanded against the
/// keys of the first dictionary-valued sample score (<c>resolve_glob_metric_keys</c>); <c>{"*": [...]}</c> applies the same
/// metrics to every key. Keys keep their insertion order, as a Python dict does. Attach it to a scorer with
/// <see cref="ScorerDef.MetricsByKey"/> or <see cref="Scorers.Custom(string, Scorer, MetricDict, MetricDef[])"/>.
/// </summary>
public sealed class MetricDict : IReadOnlyDictionary<string, IReadOnlyList<MetricDef>>
{
    private readonly OrderedDictionary<string, IReadOnlyList<MetricDef>> _entries = new(StringComparer.Ordinal);

    public MetricDict()
    {
    }

    public MetricDict(IEnumerable<KeyValuePair<string, IReadOnlyList<MetricDef>>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var (key, metrics) in entries)
        {
            Add(key, metrics);
        }
    }

    /// <summary>Python <c>{"*": [...]}</c>: the same metrics for every key of the score value.</summary>
    public static MetricDict ForAllKeys(params IReadOnlyList<MetricDef> metrics) => new() { ["*"] = metrics };

    /// <summary>The metrics of <paramref name="key"/>; setting adds the key (at the end) or replaces its metrics in place.</summary>
    public IReadOnlyList<MetricDef> this[string key]
    {
        get => _entries[key];
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            _entries[key] = [.. value];
        }
    }

    /// <summary>Adds (or replaces) the metrics of <paramref name="key"/>; collection-initializer friendly: <c>new MetricDict { { "a", Metrics.Mean() } }</c>.</summary>
    public void Add(string key, params IReadOnlyList<MetricDef> metrics) => this[key] = metrics;

    public IEnumerable<string> Keys => _entries.Keys;

    public IEnumerable<IReadOnlyList<MetricDef>> Values => _entries.Values;

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IReadOnlyList<MetricDef> value) => _entries.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, IReadOnlyList<MetricDef>>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The dictionary-metrics form of the <c>@scorer(metrics=...)</c> decorator.</summary>
public static partial class Scorers
{
    /// <summary>
    /// <c>@scorer(metrics=...)</c> with per-key metrics: <paramref name="metricsByKey"/> alone is Python's
    /// <c>metrics={"key": [...], ...}</c> (only per-key scores are produced); together with <paramref name="metrics"/> it is the
    /// list form <c>metrics=[accuracy(), {"key": [...]}]</c>, which also produces the scorer's own score. See <see cref="MetricDict"/>.
    /// </summary>
    public static ScorerDef Custom(string name, Scorer scorer, MetricDict metricsByKey, params MetricDef[] metrics)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(metricsByKey);
        ArgumentNullException.ThrowIfNull(metrics);
        return new(name, scorer, metrics) { MetricsByKey = metricsByKey };
    }
}
