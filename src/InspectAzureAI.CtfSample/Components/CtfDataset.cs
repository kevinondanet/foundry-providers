using InspectAzureAI.Eval.Dataset;

namespace InspectAzureAI.CtfSample.Components;

/// <summary>
/// COMPONENT: Dataset.
///
/// A dataset is a list of <see cref="Sample"/>s: an <c>input</c> the solver receives, a <c>target</c> the scorer
/// compares against, optional <c>metadata</c>, and optional sandbox provisioning (<c>files</c> to copy in and a
/// <c>setup</c> script to run). Here every sample is one CTF challenge, and its <c>setup</c> script plants the flag
/// inside the container before the solver starts.
///
/// The JSON file uses its own column names (<c>challenge</c>, <c>flag</c>), so a <see cref="FieldSpec"/> maps them
/// onto the sample fields, the way Inspect's <c>json_dataset(..., FieldSpec(input=..., target=...))</c> does.
/// </summary>
internal static class CtfDataset
{
    public const string Name = "ctf";

    /// <summary>Which JSON columns become which sample fields; the listed extra columns land in <see cref="Sample.Metadata"/>.</summary>
    private static readonly FieldSpec Fields = new(
        Input: "challenge",
        Target: "flag",
        Id: "id",
        Setup: "setup",
        Metadata: ["category", "difficulty", "points"]);

    /// <summary>Loads <c>ctf/dataset.json</c>, optionally keeping only one challenge category (a dataset filter).</summary>
    public static IDataset Load(string? category = null)
    {
        var dataset = Datasets.Json(CtfData.DatasetPath, fields: Fields, name: Name);
        if (category is null)
        {
            return dataset;
        }

        return dataset.Filter(
            sample => string.Equals(Category(sample), category, StringComparison.OrdinalIgnoreCase),
            name: $"{Name}[{category}]");
    }

    public static string Category(Sample sample) =>
        sample.Metadata?.TryGetValue("category", out var value) == true ? value?.ToString() ?? "" : "";

    public static IReadOnlyList<string> Categories(IDataset dataset) =>
        dataset.Select(Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
}
