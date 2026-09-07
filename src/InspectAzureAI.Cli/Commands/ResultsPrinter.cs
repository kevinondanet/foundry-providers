using System.Globalization;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Cli.Commands;

/// <summary>
/// Plain-text port of the results panel Python prints after an eval or a scoring pass (<c>_display/core/results.py</c>
/// <c>task_scores</c> and <c>sample_coverage_messages</c>): one line per scorer with its metrics, the sample coverage,
/// the status and the log location.
/// </summary>
internal static class ResultsPrinter
{
    public static void Print(TextWriter writer, EvalLog log, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        writer.WriteLine();
        writer.WriteLine($"Results for {log.Eval.Task} ({log.Eval.Model})");
        if (log.Results is { } results)
        {
            foreach (var score in results.Scores)
            {
                var name = score.Reducer is { } reducer ? $"{score.Name}/{reducer}" : score.Name;
                var metrics = string.Join("  ", score.Metrics.Values.Select(metric => $"{metric.Name}: {Format(metric.Value)}"));
                writer.WriteLine($"  {name}  {metrics}");
            }

            if (results.Scores.Count == 0)
            {
                writer.WriteLine("  (no scores)");
            }

            if (results.CompletedSamples != results.TotalSamples)
            {
                writer.WriteLine($"  {results.CompletedSamples}/{results.TotalSamples} samples completed");
            }
        }

        var status = log.Status.ToString().ToLowerInvariant();
        writer.WriteLine(log.Error is { } error ? $"  status: {status} ({FirstLine(error.Message)})" : $"  status: {status}");
        var path = location ?? log.Location;
        if (path is not null)
        {
            writer.WriteLine($"  Log: {path}");
        }

        writer.WriteLine();
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "nan" : double.IsInfinity(value) ? (value > 0 ? "inf" : "-inf") : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }
}
