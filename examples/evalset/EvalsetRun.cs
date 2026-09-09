using System.Globalization;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.EvalSet;

namespace InspectAzureAI.Examples.Evalset;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/evalset.py</c> <c>run</c>: "Run 2 tasks on 2 models, retrying as required if errors occur."
/// Runs <c>security_guide</c> and <c>popularity</c> (<see cref="EvalsetTasks"/>) against every model of
/// <paramref name="models"/> as one eval set in <paramref name="logDir"/>, with the given <c>max_tasks</c> and
/// <c>retry_attempts</c> (default 10) and the eval-set defaults for the rest (retry_wait 30 seconds doubling to at
/// most an hour, retry_connections, retry_cleanup). Re-running with the same log directory resumes: completed
/// logs are reused and only failed tasks run again. Deviation: the models are <see cref="Model"/> instances
/// (Foundry deployments, or the scripted stand-ins) rather than the names <c>openai/gpt-4o-mini</c> and
/// <c>anthropic/claude-3-5-haiku-latest</c>.
/// </summary>
public static class EvalsetRun
{
    /// <summary>Python's <c>--retry-attempts</c> default.</summary>
    public const int DefaultRetryAttempts = 10;

    /// <summary>Runs the eval set; the result's <c>Success</c> is true only when every task completed.</summary>
    public static Task<EvalSetResult> RunAsync(
        string logDir,
        IReadOnlyList<Model> models,
        int? maxTasks = null,
        int retryAttempts = DefaultRetryAttempts,
        IEvalReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one model is required.", nameof(models));
        }

        // run eval_set
        return EvalSet.RunAsync(
            tasks: [EvalsetTasks.SecurityGuide(), EvalsetTasks.Popularity()],
            options: new EvalSetOptions
            {
                Eval = new EvalOptions
                {
                    Model = models[0],
                    LogDir = logDir,
                    LogFormat = LogFormat.Eval,
                    Reporter = reporter,
                },
                Models = models,
                MaxTasks = maxTasks,
                RetryAttempts = retryAttempts,
            },
            cancellationToken);
    }

    /// <summary>
    /// One line per log of <paramref name="result"/>: task, model, status, completed/total samples, the first scorer's
    /// accuracy, how many samples were retried after an error, and whether the log was reused from the directory
    /// (a header without samples) — then Python's closing message.
    /// </summary>
    public static string Summarize(EvalSetResult result, string logDir)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(logDir);
        var lines = new List<string>
        {
            $"{"task",-16} {"model",-36} {"status",-10} {"samples",-9} {"accuracy",-9} note",
        };
        foreach (var log in result.Logs)
        {
            var completed = log.Results?.CompletedSamples ?? 0;
            var total = log.Results?.TotalSamples ?? 0;
            var accuracy = log.Results?.Scores.FirstOrDefault()?.Metrics.TryGetValue("accuracy", out var metric) == true
                ? metric.Value.ToString("0.000", CultureInfo.InvariantCulture)
                : "n/a";
            var retried = log.Samples?.Count(sample => sample.ErrorRetries is { Count: > 0 }) ?? 0;
            var note = log.Samples is null
                ? "already complete, reused from the log directory"
                : retried > 0 ? $"{retried} sample{(retried == 1 ? "" : "s")} retried after an error" : "";
            lines.Add($"{log.Eval.Task,-16} {log.Eval.Model,-36} {log.Status.ToString().ToLowerInvariant(),-10} {$"{completed}/{total}",-9} {accuracy,-9} {note}");
        }

        lines.Add(result.Success
            ? $"Completed all tasks in '{logDir}' successfully"
            : $"Did not successfully complete all tasks in '{logDir}'.");
        return string.Join(Environment.NewLine, lines);
    }
}
