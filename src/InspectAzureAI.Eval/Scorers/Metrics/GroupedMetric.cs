using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>grouped(all=...)</c>: how the aggregate entry of a grouped metric is computed.</summary>
public enum GroupedAll
{
    /// <summary><c>"samples"</c>: apply the metric to every sample regardless of group.</summary>
    Samples,

    /// <summary><c>"groups"</c>: the mean of the per-group metric values (through <c>value_to_float</c>).</summary>
    Groups,

    /// <summary><c>False</c>: no aggregate entry.</summary>
    None,
}

/// <summary>Port of <c>scorer/_metrics/grouped.py</c>.</summary>
public static partial class Metrics
{
    /// <summary>
    /// Port of <c>grouped(metric, group_key, all, all_label, value_to_float, name_template)</c>: applies
    /// <paramref name="metric"/> to each group of samples sharing the <paramref name="groupKey"/> metadata value and
    /// returns a dictionary keyed by <paramref name="nameTemplate"/> (with <c>{group_name}</c> substituted, the group
    /// name being Python's <c>str()</c> of the metadata value), plus an <paramref name="allLabel"/> aggregate unless
    /// <paramref name="all"/> is <see cref="GroupedAll.None"/>. A sample without the key throws, as does a group whose
    /// name collides with <paramref name="allLabel"/>.
    /// </summary>
    public static MetricDef Grouped(
        MetricDef metric,
        string groupKey,
        GroupedAll all = GroupedAll.Samples,
        string allLabel = "all",
        Func<ScoreValue, double>? valueToFloat = null,
        string nameTemplate = "{group_name}")
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentException.ThrowIfNullOrEmpty(groupKey);
        ArgumentNullException.ThrowIfNull(allLabel);
        ArgumentNullException.ThrowIfNull(nameTemplate);
        var toFloat = valueToFloat ?? ValueToFloat.Default;
        return new("grouped", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var groups = new OrderedDictionary<string, List<SampleScore>>(StringComparer.Ordinal);
            foreach (var sampleScore in scores)
            {
                if (sampleScore.SampleMetadata is null || !sampleScore.SampleMetadata.TryGetValue(groupKey, out var groupValue))
                {
                    throw new ArgumentException(
                        $"Sample {IdText(sampleScore.SampleId)} has no {groupKey} metadata. To compute a grouped metric each sample metadata must have a value for '{groupKey}'",
                        nameof(scores));
                }

                var groupName = PythonText.Str(groupValue);
                if (!groups.TryGetValue(groupName, out var group))
                {
                    group = [];
                    groups[groupName] = group;
                }

                group.Add(sampleScore);
            }

            var result = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal);
            foreach (var (groupName, group) in groups)
            {
                result[nameTemplate.Replace("{group_name}", groupName, StringComparison.Ordinal)] = metric.Compute(group);
            }

            if (all == GroupedAll.None)
            {
                return new ScoreValue.Dict(result);
            }

            if (result.ContainsKey(allLabel))
            {
                throw new ArgumentException(
                    $"The group name '{allLabel}' collides with the `all_label` used for the aggregate score. Pass a different `all_label` "
                    + "to grouped() (or set all=False) to avoid overwriting this group's metric.",
                    nameof(scores));
            }

            ScoreValue? aggregate = all == GroupedAll.Samples
                ? metric.Compute(scores)
                : result.Count == 0 ? 0.0 : result.Values.Sum(value => value is null ? WarnNone() : toFloat(value)) / result.Count;
            result[allLabel] = aggregate;
            return new ScoreValue.Dict(result);
        });
    }

    private static double WarnNone()
    {
        ProviderLogger.Warning("Unable to convert value to float: None");
        return 0.0;
    }
}
