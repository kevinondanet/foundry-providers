using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// The rest of <c>scorer/_reducer/reducer.py</c> (<c>majority_score</c>, <c>pass_k</c>, <c>collect_score</c>) and the
/// name-based lookup and validation of <c>scorer/_reducer/registry.py</c> (<c>create_reducers</c>, <c>validate_reducer</c>).
/// </summary>
public static partial class Reducers
{
    private static readonly Regex KSuffix = new(@"^(.*?)_(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ValidatedName = new(@"^(pass_at|pass_k|at_least)_(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] RegisteredNames = ["mode", "majority", "mean", "median", "at_least", "pass_at", "pass_k", "max", "collect"];

    /// <summary>
    /// Port of <c>majority_score</c>: the value carried by more than half of the panel, per key and per index for
    /// dictionaries and lists. Unscored (NaN) scores count towards the panel size but cast no vote, so nothing
    /// short of a strict majority wins; with no majority the reduced value is NaN. The reduced score's metadata
    /// records the votes under <c>panel</c> (<c>votes</c>, <c>size</c>, <c>failures</c>).
    /// </summary>
    public static ScoreReducer Majority() =>
        Named("majority", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var panelSize = scores.Count;
            ScoreValue? StrictMajority(IReadOnlyList<CountEntry> counts)
            {
                var winner = counts.MaxBy(e => e.Count)!;
                return winner.Count * 2 > panelSize ? winner.Value : ScoreValue.NaN;
            }

            var representative = FirstScored(scores);
            var reduced = representative is null
                ? NanScore(scores)
                : representative.Value switch
                {
                    ScoreValue.Dict => ReduceDict(scores, values => ApplyCounter(values, StrictMajority)),
                    ScoreValue.List => ReduceList(scores, values => ApplyCounter(values, StrictMajority)),
                    _ => ReduceScalars(scores, values => ApplyCounter(values, StrictMajority)),
                };
            return WithPanelMetadata(reduced, scores);
        });

    /// <summary>
    /// Port of <c>pass_k(k, value)</c>: the probability that all <paramref name="k"/> attempts succeed, the
    /// draw-without-replacement estimator <c>C(correct, k) / C(total, k)</c>; NaN when fewer than k scored epochs remain.
    /// </summary>
    public static ScoreReducer PassK(int k, double value = 1.0, Func<ScoreValue, double>? valueToFloat = null) =>
        Named($"pass_k_{k}", Statistic(valueToFloat ?? ValueToFloat.Default, values =>
        {
            var total = values.Count;
            if (total < k)
            {
                return double.NaN;
            }

            var correct = values.Count(v => v >= value);
            return (double)Binomial(correct, k) / (double)Binomial(total, k);
        }));

    /// <summary>
    /// Port of <c>collect_score</c>: every scalar value as a list, unscored (NaN) values dropped; NaN when none
    /// remain. A list or dictionary value throws, since it cannot be collected as one scalar.
    /// </summary>
    public static ScoreReducer Collect() =>
        Named("collect", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var values = new List<ScoreValue>();
            foreach (var score in scores)
            {
                if (score.Value is ScoreValue.List or ScoreValue.Dict)
                {
                    throw new ArgumentException(
                        $"collect reducer requires scalar score values, but got {score.Value.GetType().Name}. "
                        + "It preserves each scorer's scalar value as a list and cannot collect dict/list values.",
                        nameof(scores));
                }

                if (!score.Value.IsNaN)
                {
                    values.Add(score.Value);
                }
            }

            return values.Count == 0 ? NanScore(scores) : ReducedScore(new ScoreValue.List(values), scores);
        });

    /// <summary>
    /// Port of <c>create_reducers</c> for one name: a registered reducer by name (<c>mean</c>, <c>median</c>, <c>mode</c>,
    /// <c>majority</c>, <c>max</c>, <c>collect</c>) or the <c>&lt;name&gt;_&lt;k&gt;</c> shorthand of the parameterised ones
    /// (<c>at_least_2</c>, <c>pass_at_3</c>, <c>pass_k_3</c>). An unknown name throws.
    /// </summary>
    public static ScoreReducer Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var reducerName = name;
        int? k = null;
        if (!RegisteredNames.Contains(name, StringComparer.Ordinal))
        {
            var match = KSuffix.Match(name);
            if (match.Success)
            {
                reducerName = match.Groups[1].Value;
                k = int.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        return reducerName switch
        {
            "mean" => Mean(),
            "median" => Median(),
            "mode" => Mode(),
            "majority" => Majority(),
            "max" => Max(),
            "collect" => Collect(),
            "at_least" => AtLeast(RequireK(name, k)),
            "pass_at" => PassAt(RequireK(name, k)),
            "pass_k" => PassK(RequireK(name, k)),
            _ => throw new ArgumentException($"Unknown score reducer '{name}'.", nameof(name)),
        };
    }

    /// <summary>
    /// Port of <c>validate_reducer</c>: a built-in <c>pass_at_k</c>, <c>pass_k_k</c> or <c>at_least_k</c> reducer whose k
    /// exceeds the epoch count cannot be satisfied and throws <see cref="PrerequisiteError"/>.
    /// </summary>
    public static void Validate(int epochs, ScoreReducer reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        var name = NameOf(reducer);
        if (name is null)
        {
            return;
        }

        var match = ValidatedName.Match(name);
        if (match.Success && int.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture) is var k && k > epochs)
        {
            throw new PrerequisiteError($"Reducer '{name}' requires {k} epochs however evaluation has only {epochs} epochs.");
        }
    }

    private static int RequireK(string name, int? k) =>
        k ?? throw new ArgumentException($"Score reducer '{name}' requires a k suffix (e.g. '{name}_2').", nameof(name));

    private static BigInteger Binomial(int n, int k)
    {
        if (k < 0 || k > n)
        {
            return BigInteger.Zero;
        }

        k = Math.Min(k, n - k);
        var result = BigInteger.One;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }

        return result;
    }

    /// <summary>Port of <c>_with_panel_metadata</c>: the votes cast, the panel size and the unscored members, copied so the record cannot alias a score.</summary>
    private static Score WithPanelMetadata(Score reduced, IReadOnlyList<Score> scores)
    {
        var votes = new List<object?>();
        var failures = new List<object?>();
        for (var index = 0; index < scores.Count; index++)
        {
            var score = scores[index];
            var unscored = score.Value.IsNaN;
            votes.Add(unscored ? null : ToPlain(score.Value));
            if (unscored)
            {
                failures.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = index,
                    ["reason"] = UnscoredReason(score),
                    ["explanation"] = score.Explanation,
                });
            }
        }

        var panel = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["votes"] = votes,
            ["size"] = scores.Count,
            ["failures"] = failures,
        };
        var metadata = reduced.Metadata is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(reduced.Metadata, StringComparer.Ordinal);
        metadata["panel"] = panel;
        return reduced with { Metadata = metadata };
    }

    /// <summary>Port of <c>_unscored_reason</c>: <see cref="Score.Reason"/>, else the legacy <c>metadata["unscored_reason"]</c>.</summary>
    private static string? UnscoredReason(Score score)
    {
        if (score.Reason is not null)
        {
            return score.Reason;
        }

        return score.Metadata is not null && score.Metadata.TryGetValue("unscored_reason", out var legacy) && legacy is string text ? text : null;
    }

    /// <summary>A score value as the plain objects metadata holds (string, double, bool, list, dictionary).</summary>
    internal static object? ToPlain(ScoreValue? value) => value switch
    {
        null => null,
        ScoreValue.Str s => s.Value,
        ScoreValue.Num n => n.Value,
        ScoreValue.Bool b => b.Value,
        ScoreValue.List l => l.Items.Select(ToPlain).ToList(),
        ScoreValue.Dict d => d.Items.ToDictionary(pair => pair.Key, pair => ToPlain(pair.Value), StringComparer.Ordinal),
        _ => throw new InvalidOperationException($"Unknown score value {value.GetType().Name}."),
    };
}
