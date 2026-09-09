using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CopilotCli;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The Copilot CLI counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeModels"/> (inspect_swe
/// <c>_claude_code/model.py</c>): the one cosmetic name the CLI sends as <c>model</c> and the bridge aliases that
/// route it (and the configured alias name) to the sample's served model. Effort is applied host-side because the
/// bridge drops request-level generation config.
/// </summary>
public sealed record CopilotCliModels(string Presented, IReadOnlyDictionary<string, Model> Aliases, Model Served)
{
    /// <summary>
    /// Presented name = <paramref name="modelConfig"/> or <paramref name="model"/>; both names are registered as
    /// aliases of the served model (with <paramref name="effort"/> merged into a copy of its config when given).
    /// </summary>
    public static CopilotCliModels Resolve(Model servedModel, string model = CopilotCliOptions.DefaultModel, string? modelConfig = null, string? effort = null)
    {
        ArgumentNullException.ThrowIfNull(servedModel);
        ArgumentNullException.ThrowIfNull(model);
        var served = effort is null ? servedModel : WithEffort(servedModel, effort);
        var presented = modelConfig ?? model;
        var aliases = new OrderedDictionary<string, Model>(StringComparer.Ordinal) { [presented] = served, [model] = served };
        return new CopilotCliModels(presented, aliases, served);
    }

    /// <summary>A copy through <see cref="Model.WithConfig"/> so the role, event sink and adaptive-connection setting survive.</summary>
    private static Model WithEffort(Model model, string effort) =>
        model.WithConfig(model.Config.Merge(new GenerateConfig { ReasoningEffort = effort }));
}
