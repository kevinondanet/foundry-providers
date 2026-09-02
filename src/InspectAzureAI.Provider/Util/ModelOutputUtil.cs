using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Port of <c>collect_stop_details</c> (<c>src/inspect_ai/model/_model_output.py</c>).</summary>
public static class ModelOutputUtil
{
    /// <summary>
    /// Runs the extractor defensively: any exception is logged once and yields null; empty details are
    /// dropped; <c>category</c> and <c>explanation</c> are synthesised from the categories when unset.
    /// </summary>
    public static StopDetails? CollectStopDetails(string provider, Func<StopDetails?> fn)
    {
        StopDetails? details;
        try
        {
            details = fn();
        }
        catch (Exception ex)
        {
            ProviderLogger.WarnOnce($"Unexpected data shape collecting stop_details from {provider}: {ex.Message}");
            return null;
        }

        if (details is null || (details.Categories.Count == 0 && string.IsNullOrEmpty(details.Explanation)))
        {
            return null;
        }

        if (details.Category is null && details.Categories.Count > 0)
        {
            details = details with { Category = details.Categories[0].Category };
        }

        if (details.Explanation is null && details.Categories.Count > 0)
        {
            details = details with { Explanation = SummarizeStopCategories(details.Categories) };
        }

        return details;
    }

    private static string SummarizeStopCategories(IReadOnlyList<StopCategory> categories) =>
        "Content filtered: " + string.Join(", ", categories.Select(c => c.Level is null ? c.Category : $"{c.Category} ({c.Level})"));
}
