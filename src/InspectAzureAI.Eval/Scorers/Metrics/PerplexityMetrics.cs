using System.Globalization;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>scorer/_metrics/perplexity.py</c>: corpus perplexity from the <c>num_tokens</c> and
/// <c>sum_log_probs</c> entries a perplexity scorer records in each score's metadata.
/// </summary>
public static partial class Metrics
{
    /// <summary>
    /// Port of <c>perplexity_per_token()</c>: <c>exp(−Σ sum_log_probs / Σ num_tokens)</c>, weighting samples by token
    /// count; NaN with no tokens, +∞ on overflow. A sample without the metadata keys counts as (0, 0) with a warning.
    /// </summary>
    public static MetricDef PerplexityPerToken() =>
        new("perplexity_per_token", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var totalLogProbs = 0.0;
            var totalTokens = 0L;
            foreach (var sample in scores)
            {
                var (tokens, sumLogProbs) = PerplexityMetadata(sample);
                totalLogProbs += sumLogProbs;
                totalTokens += tokens;
            }

            return totalTokens == 0 ? ScoreValue.NaN : Math.Exp(-totalLogProbs / totalTokens);
        });

    /// <summary>
    /// Port of <c>perplexity_per_seq()</c>: <c>exp(mean_i(−sum_log_probs_i / num_tokens_i))</c>, the geometric mean of
    /// the per-sample perplexities; samples without tokens are skipped, NaN when none remain, +∞ on overflow.
    /// </summary>
    public static MetricDef PerplexityPerSeq() =>
        new("perplexity_per_seq", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var nll = new List<double>();
            foreach (var sample in scores)
            {
                var (tokens, sumLogProbs) = PerplexityMetadata(sample);
                if (tokens > 0)
                {
                    nll.Add(-sumLogProbs / tokens);
                }
            }

            return nll.Count == 0 ? ScoreValue.NaN : Math.Exp(nll.Sum() / nll.Count);
        });

    /// <summary>Port of <c>_get_perplexity_metadata</c>: (0, 0.0) with a warning when either key is missing or null.</summary>
    private static (long Tokens, double SumLogProbs) PerplexityMetadata(SampleScore sample)
    {
        var metadata = sample.Score.Metadata;
        object? tokens = metadata is not null && metadata.TryGetValue("num_tokens", out var t) ? t : null;
        object? sumLogProbs = metadata is not null && metadata.TryGetValue("sum_log_probs", out var s) ? s : null;
        if (tokens is null || sumLogProbs is null)
        {
            ProviderLogger.Warning(
                $"Perplexity metric: sample {IdText(sample.SampleId)} missing metadata keys (num_tokens={PythonText.Str(tokens)}, "
                + $"sum_log_probs={PythonText.Str(sumLogProbs)}). Ensure the scorer is perplexity() or target_perplexity().");
            return (0, 0.0);
        }

        return ((long)Convert.ToDouble(tokens, CultureInfo.InvariantCulture), Convert.ToDouble(sumLogProbs, CultureInfo.InvariantCulture));
    }
}
