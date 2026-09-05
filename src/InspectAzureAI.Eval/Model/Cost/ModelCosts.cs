using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>Port of the cost functions of <c>model/_model.py</c>: <c>compute_model_cost</c> and <c>sample_total_cost</c>.</summary>
public static class ModelCosts
{
    /// <summary>
    /// Port of <c>CACHE_WRITE_1H_MULTIPLIER</c>: Anthropic bills 1-hour cache writes at 2x the base input price
    /// against 1.25x for the default 5-minute writes, so a 1-hour write costs 2 / 1.25 times what
    /// <see cref="ModelCost.InputCacheWrite"/> (the 5-minute rate) records.
    /// </summary>
    public const double CacheWrite1hMultiplier = 2.0 / 1.25;

    /// <summary>
    /// Port of <c>compute_model_cost</c>: the cost in dollars of one call from its token counts and the model's
    /// per-million prices. <paramref name="cacheTtl"/> is the prompt-cache TTL used for the call (e.g. <c>"1h"</c>)
    /// for providers that bill longer-lived cache writes at a higher rate; null bills writes at <see cref="ModelCost.InputCacheWrite"/>.
    /// The arithmetic is performed in Python's order so results match to the last bit.
    /// </summary>
    public static double ComputeModelCost(ModelCost costData, ModelUsage usage, string? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(costData);
        ArgumentNullException.ThrowIfNull(usage);

        var cost = usage.InputTokens * costData.Input / 1_000_000;
        cost += usage.OutputTokens * costData.Output / 1_000_000;

        if (usage.InputTokensCacheWrite is { } cacheWrite)
        {
            var inputCacheWrite = costData.InputCacheWrite;
            if (cacheTtl == "1h")
            {
                inputCacheWrite *= CacheWrite1hMultiplier;
            }

            cost += cacheWrite * inputCacheWrite / 1_000_000;
        }

        if (usage.InputTokensCacheRead is { } cacheRead)
        {
            cost += cacheRead * costData.InputCacheRead / 1_000_000;
        }

        return cost;
    }

    /// <summary>
    /// The cost of <paramref name="usage"/> for the model named <paramref name="model"/> (a Foundry deployment name
    /// or an Inspect <c>provider/model</c> string), or null when the model is unknown or has no cost data — never
    /// zero, so an unpriced model is distinguishable from a free one.
    /// </summary>
    public static double? ComputeCostForModel(string model, ModelUsage usage, string? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        return ModelInfoLookup.GetModelInfo(model)?.Cost is { } cost ? ComputeModelCost(cost, usage, cacheTtl) : null;
    }

    /// <summary>
    /// The output with <c>usage.total_cost</c> set, as <c>record_and_check_model_usage</c> sets it on the usage
    /// object shared by the returned output and the <c>ModelEvent</c>; unchanged when there is no usage or no cost data.
    /// </summary>
    public static ModelOutput PriceOutput(string model, ModelOutput output, string? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Usage is not { } usage || ComputeCostForModel(model, usage, cacheTtl) is not { } cost)
        {
            return output;
        }

        return output with { Usage = usage with { TotalCost = cost } };
    }

    /// <summary>Port of <c>sample_total_cost</c>: the total cost across all models of a sample's usage.</summary>
    public static double SampleTotalCost(IReadOnlyDictionary<string, ModelUsage> usageByModel)
    {
        ArgumentNullException.ThrowIfNull(usageByModel);
        return usageByModel.Values.Sum(usage => usage.TotalCost ?? 0.0);
    }
}
