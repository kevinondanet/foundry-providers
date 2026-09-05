using System.Globalization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.SweShowcase;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Turns the shared run flags into the runner's inputs, for the showcase and the model matrix alike: the
/// <see cref="EvalOptions"/> (log format, approval, cost limit and pricing, hooks), the per-run hooks, and the
/// header lines that describe the wiring. Kept out of <see cref="RunOptions"/> so the flag record stays a plain
/// parse result.
/// </summary>
internal static class RunWiring
{
    /// <summary>The eval options for one run; <paramref name="limit"/> is passed separately because the matrix defaults it differently.</summary>
    public static EvalOptions EvalOptions(RunOptions run, Model model, int? limit, IEvalReporter? reporter, IReadOnlyList<Hooks>? hooks) => new()
    {
        Model = model,
        Limit = limit,
        SampleIds = run.SampleIds.Count > 0 ? run.SampleIds : null,
        Epochs = run.Epochs,
        MaxSamples = run.MaxSamples,
        LogDir = run.LogDir,
        LogFormat = run.LogFormat,
        Cleanup = run.Cleanup,
        Reporter = reporter,
        Approval = run.Approval,
        CostLimit = run.CostLimit,
        ModelCostConfig = run.ModelCostConfig,
        Hooks = hooks is { Count: > 0 } ? hooks : null,
    };

    /// <summary>Creates the <c>--hooks</c> instances; dispose them with <see cref="DisposeHooks"/> once the run has ended.</summary>
    public static IReadOnlyList<Hooks> CreateHooks(RunOptions run, TextWriter console) => run.Hooks.Select(choice => choice.Create(console)).ToList();

    public static void DisposeHooks(IEnumerable<Hooks> hooks)
    {
        foreach (var hook in hooks)
        {
            (hook as IDisposable)?.Dispose();
        }
    }

    /// <summary>The header lines describing the optional wiring of a run (only the flags that were given).</summary>
    public static IEnumerable<string> DescribeLines(RunOptions run)
    {
        yield return $"log fmt  : {(run.LogFormat ?? LogFormats.FromEnvironment() ?? LogFormats.Default).Name()}";
        if (run.ApprovalSpec is { } approval)
        {
            yield return $"approval : {approval}";
        }

        if (run.Cache is { } cache)
        {
            yield return $"cache    : expiry {cache.Expiry ?? "never"}, per epoch";
        }

        if (run.Compaction is { } compaction)
        {
            yield return $"compact  : {compaction}";
        }

        if (run.Hooks.Count > 0)
        {
            yield return $"hooks    : {string.Join(", ", run.Hooks)}";
        }

        if (run.CostLimit is { } costLimit)
        {
            yield return $"cost lim : ${costLimit.ToString("0.############", CultureInfo.InvariantCulture)} per sample";
        }

        if (run.ModelCostConfig is { } costConfig)
        {
            yield return $"pricing  : {costConfig}";
        }
    }

    /// <summary>The total cost recorded on a usage dictionary, or null when no model was priced.</summary>
    public static double? TotalCost(IReadOnlyDictionary<string, ModelUsage> usage)
    {
        var priced = usage.Values.Where(u => u.TotalCost is not null).Select(u => u.TotalCost!.Value).ToList();
        return priced.Count == 0 ? null : priced.Sum();
    }

    public static string FormatCost(double cost) => "$" + cost.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>What the new subsystems did during a sample, read back from its transcript: cache hits, approvals, compactions.</summary>
    public static string DescribeEvents(EvalSample sample)
    {
        var cacheHits = sample.Events.OfType<ModelEvent>().Count(e => e.Cache == CacheMode.Read);
        var approvals = sample.Events.OfType<ApprovalEvent>().ToList();
        var compactions = sample.Events.OfType<CompactionEvent>().Count();
        var parts = new List<string>();
        if (cacheHits > 0)
        {
            parts.Add($"{cacheHits} cache hits");
        }

        if (approvals.Count > 0)
        {
            var decisions = approvals.GroupBy(a => a.Decision, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
            parts.Add($"approvals: {string.Join(", ", decisions)}");
        }

        if (compactions > 0)
        {
            parts.Add($"{compactions} compactions");
        }

        return string.Join(" | ", parts);
    }
}
