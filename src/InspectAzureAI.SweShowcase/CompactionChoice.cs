using System.Globalization;
using InspectAzureAI.Eval.Model.Compaction;

namespace InspectAzureAI.SweShowcase;

/// <summary>
/// The <c>--compaction</c> flag: one of Inspect's compaction strategies (<c>edit</c>, <c>summary</c>, <c>trim</c>,
/// <c>auto</c>) with an optional threshold after a colon — an absolute token count (<c>edit:20000</c>) or a fraction
/// of the model's context window (<c>summary:0.8</c>; the strategies default to 0.9). The strategy is built once
/// per agent loop through <see cref="Hook"/>, as <c>react(compaction=...)</c> does.
/// </summary>
internal sealed record CompactionChoice(string Strategy, CompactionThreshold Threshold, string Spec)
{
    public static readonly IReadOnlyList<string> Strategies = ["edit", "summary", "trim", "auto"];

    public static CompactionChoice Parse(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var parts = spec.Trim().Split(':', 2);
        var strategy = parts[0].Trim().ToLowerInvariant();
        if (!Strategies.Contains(strategy, StringComparer.Ordinal))
        {
            throw new UsageError($"--compaction expects {string.Join("|", Strategies)}[:threshold], got '{spec}'");
        }

        var threshold = CompactionThreshold.Default;
        if (parts.Length == 2)
        {
            var value = parts[1].Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens > 0)
            {
                threshold = CompactionThreshold.FromTokens(tokens);
            }
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction > 0 && fraction <= 1)
            {
                threshold = CompactionThreshold.FromFraction(fraction);
            }
            else
            {
                throw new UsageError($"--compaction threshold expects a positive token count or a fraction in (0, 1], got '{value}'");
            }
        }

        return new CompactionChoice(strategy, threshold, spec.Trim());
    }

    /// <summary>A fresh strategy instance (strategies carry per-loop state such as memory warnings).</summary>
    public ICompactionStrategy CreateStrategy() => Strategy switch
    {
        "edit" => new CompactionEdit(Threshold),
        "summary" => new CompactionSummary(Threshold),
        "trim" => new CompactionTrim(Threshold),
        "auto" => new CompactionAuto(Threshold),
        _ => throw new InvalidOperationException($"Unknown compaction strategy '{Strategy}'."),
    };

    /// <summary>The seam the agent loops take (see <see cref="Compaction.Hook"/>).</summary>
    public CompactionHook Hook() => Compaction.Hook(CreateStrategy());

    public override string ToString() => Spec;
}
