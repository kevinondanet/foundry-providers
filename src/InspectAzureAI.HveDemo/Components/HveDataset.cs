using System.Globalization;
using InspectAzureAI.Eval.Dataset;

namespace InspectAzureAI.HveDemo.Components;

/// <summary>
/// COMPONENT: Dataset.
///
/// A dataset is a list of <see cref="Sample"/>s: an <c>input</c> the solver receives, a <c>target</c> the scorers
/// compare against, <c>metadata</c>, and sandbox provisioning (<c>files</c> copied in before the solver starts and a
/// <c>setup</c> script). Here every sample is one HVE Core exercise: <c>metadata.kind</c> says whether it is an
/// <c>implement</c>, <c>review</c> or <c>skill</c> task, <c>metadata.agent</c> names the HVE custom agent to run
/// (<c>--agent hve-core:...</c>) or is null for the default agent, <c>metadata.artefact</c> is the file the agent must
/// produce, <c>metadata.check</c> a deterministic command that passes only when the artefact is right, and
/// <c>metadata.hve_components</c> lists the plugin pieces (agents, skills, instructions, prompts) the sample is
/// designed to exercise.
///
/// The JSON records carry <c>id</c>, <c>input</c>, <c>target</c> and a nested <c>metadata</c> object; the
/// <see cref="FieldSpec"/> names those columns (Inspect's <c>json_dataset(..., FieldSpec(...))</c>). What the JSON
/// does not carry are the per-sample <see cref="Sample.Files"/>: they are assembled here from three directories on
/// the host, the shared <c>.github</c> overlay, the sample's own workspace and the vendored plugin (copied to
/// <see cref="HveData.PluginSandboxPath"/>), so that one dataset works unchanged in Docker, locally and in the fake sandbox.
/// </summary>
public static class HveDataset
{
    public const string Name = "hve";

    /// <summary>The three sample kinds, in the order the suite reports them.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["implement", "review", "skill"];

    /// <summary>Which JSON columns become which sample fields; with no explicit metadata list the record's own <c>metadata</c> object is used.</summary>
    public static readonly FieldSpec Fields = new(Input: "input", Target: "target", Id: "id");

    /// <summary>
    /// Loads <c>hve/dataset.json</c>, optionally keeping only one <c>kind</c> (a dataset filter). The plugin is copied
    /// to <paramref name="pluginSandboxPath"/> in every sample's sandbox; null leaves it out (a caller pointing
    /// <c>--plugin-dir</c> at a plugin the sandbox already has).
    /// </summary>
    public static IDataset Load(string? kind = null, string? pluginSandboxPath = HveData.PluginSandboxPath) =>
        Load(HveData.DatasetPath, HveData.WorkspaceRoot, HveData.PluginDirectory, kind, pluginSandboxPath);

    /// <summary>The loader behind <see cref="Load(string?, string?)"/> with explicit locations, for tests.</summary>
    public static IDataset Load(string datasetPath, string workspaceRoot, string pluginDirectory, string? kind = null, string? pluginSandboxPath = HveData.PluginSandboxPath)
    {
        var loaded = Datasets.Json(datasetPath, fields: Fields, name: Name);
        IDataset dataset = new MemoryDataset(loaded.Select(sample => Provision(sample, workspaceRoot, pluginDirectory, pluginSandboxPath)), loaded.Name, loaded.Location);
        if (kind is null)
        {
            return dataset;
        }

        if (!Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown sample kind '{kind}' (expected one of {string.Join(", ", Kinds)}).", nameof(kind));
        }

        return dataset.Filter(sample => string.Equals(Kind(sample), kind, StringComparison.OrdinalIgnoreCase), name: $"{Name}[{kind}]");
    }

    /// <summary>
    /// Attaches the sandbox provisioning: the shared overlay, then the sample's workspace (later entries win, as
    /// Python's dict update would), then the plugin under <see cref="HveData.PluginSandboxPath"/>; <c>metadata.setup</c>
    /// becomes the sample's setup script, run in the workspace after the files are copied.
    /// </summary>
    private static Sample Provision(Sample sample, string workspaceRoot, string pluginDirectory, string? pluginSandboxPath)
    {
        var id = Convert.ToString(sample.Id, CultureInfo.InvariantCulture) ?? throw new InvalidDataException("Every HVE sample needs an id.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        AddTree(files, Path.Combine(workspaceRoot, "_shared"), "");
        var own = Path.Combine(workspaceRoot, id);
        if (!Directory.Exists(own))
        {
            throw new InvalidDataException($"Sample '{id}' has no workspace directory at {own}.");
        }

        AddTree(files, own, "");
        if (pluginSandboxPath is not null)
        {
            files[pluginSandboxPath] = pluginDirectory;
        }

        return sample with
        {
            Files = files,
            Setup = Setup(sample),
        };
    }

    private static void AddTree(Dictionary<string, string> files, string directory, string prefix)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains("__pycache__", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            files[prefix + relative] = file;
        }
    }

    public static string Kind(Sample sample) => Text(sample.Metadata, "kind");

    public static string Kind(IReadOnlyDictionary<string, object?>? metadata) => Text(metadata, "kind");

    /// <summary>The HVE custom agent (<c>hve-core:...</c>) the sample runs with, or null for the CLI's default agent.</summary>
    public static string? Agent(IReadOnlyDictionary<string, object?>? metadata) => Text(metadata, "agent") is { Length: > 0 } agent ? agent : null;

    public static string Artefact(IReadOnlyDictionary<string, object?>? metadata) => Text(metadata, "artefact");

    public static string Check(IReadOnlyDictionary<string, object?>? metadata) => Text(metadata, "check");

    public static string Rubric(IReadOnlyDictionary<string, object?>? metadata) => Text(metadata, "rubric");

    public static string? Setup(Sample sample) => Text(sample.Metadata, "setup") is { Length: > 0 } setup ? setup : null;

    /// <summary>The <c>hve_components</c> list: <c>agent/x</c>, <c>skill/x</c>, <c>instructions/x</c> or <c>prompt/x</c> entries.</summary>
    public static IReadOnlyList<string> Components(IReadOnlyDictionary<string, object?>? metadata) =>
        metadata?.TryGetValue("hve_components", out var value) == true && value is System.Collections.IEnumerable items && value is not string
            ? items.Cast<object?>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? "").Where(item => item.Length > 0).ToList()
            : [];

    public static IReadOnlyList<string> KindsOf(IDataset dataset) =>
        dataset.Select(Kind).Where(kind => kind.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(kind => Kinds.ToList().IndexOf(kind)).ToList();

    private static string Text(IReadOnlyDictionary<string, object?>? metadata, string key) =>
        metadata?.TryGetValue(key, out var value) == true ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
}
