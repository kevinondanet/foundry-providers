using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of inspect_swe <c>_claude_code/model.py</c> <c>ClaudeCodeModels</c> / <c>resolve_claude_code_models</c>:
/// the cosmetic identities Claude Code presents to itself and the bridge aliases that route them to the real
/// served model. The C# agent has no per-role model options, so every role inherits the presented name; the
/// served <see cref="Model"/> replaces Python's <c>bridge_model</c> sentinel string as the bridge default.
/// </summary>
public sealed record ClaudeCodeModels(
    string Presented,
    string Opus,
    string Sonnet,
    string Haiku,
    string Subagent,
    IReadOnlyDictionary<string, Model> Aliases,
    Model Served)
{
    /// <summary>
    /// Presented name = <paramref name="modelConfig"/> or the served model's name; <paramref name="effort"/> is
    /// merged into a copy of the served model's config (the bridge drops request-level config, so a CLI flag
    /// would have no effect); caller <paramref name="modelAliases"/> are merged last and win on collisions
    /// (and are not touched by effort).
    /// </summary>
    public static ClaudeCodeModels Resolve(Model servedModel, string? modelConfig = null, string? effort = null, IReadOnlyDictionary<string, Model>? modelAliases = null)
    {
        ArgumentNullException.ThrowIfNull(servedModel);
        var served = effort is null ? servedModel : WithEffort(servedModel, effort);
        var presented = modelConfig ?? served.Name;
        var aliases = new OrderedDictionary<string, Model>(StringComparer.Ordinal) { [presented] = served };
        if (modelAliases is not null)
        {
            foreach (var (name, model) in modelAliases)
            {
                aliases[name] = model;
            }
        }

        return new ClaudeCodeModels(presented, presented, presented, presented, presented, aliases, served);
    }

    private static Model WithEffort(Model model, string effort) =>
        new(model.Api, model.Config.Merge(new GenerateConfig { ReasoningEffort = effort }), model.Retry) { EventSink = model.EventSink };
}
