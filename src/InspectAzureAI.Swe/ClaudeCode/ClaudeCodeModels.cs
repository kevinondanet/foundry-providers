using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of inspect_swe <c>_claude_code/model.py</c> <c>ClaudeCodeModels</c> / <c>resolve_claude_code_models</c>:
/// the cosmetic identities Claude Code presents to itself and the bridge aliases that route them to real models.
/// The served <see cref="Model"/> replaces Python's <c>bridge_model</c> sentinel string as the bridge default, and
/// roles and aliases are <see cref="Model"/> instances rather than model name strings (deviation D-C10).
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
    /// Port of <c>resolve_claude_code_models</c> (<c>model.py:36-95</c>). The presented name is
    /// <paramref name="modelConfig"/>, or the served model's name. An unset role (opus, sonnet, haiku, subagent,
    /// resolved in that order) inherits the presented name. A set role presents its own name and gets its own alias,
    /// so it reaches its own model. Caller <paramref name="modelAliases"/> are merged last and win on collisions.
    /// <paramref name="effort"/> (beyond 0.2.70) is merged into a copy of the served model's config only, because the
    /// bridge drops request-level config and a CLI flag would have no effect; roles and caller aliases keep their own.
    /// </summary>
    public static ClaudeCodeModels Resolve(
        Model servedModel,
        string? modelConfig = null,
        string? effort = null,
        IReadOnlyDictionary<string, Model>? modelAliases = null,
        Model? opusModel = null,
        Model? sonnetModel = null,
        Model? haikuModel = null,
        Model? subagentModel = null)
    {
        ArgumentNullException.ThrowIfNull(servedModel);
        var served = effort is null ? servedModel : WithEffort(servedModel, effort);
        var presented = modelConfig ?? served.Name;
        var aliases = new OrderedDictionary<string, Model>(StringComparer.Ordinal) { [presented] = served };

        string RoleName(Model? role)
        {
            if (role is null)
            {
                return presented;
            }

            aliases[role.Name] = role;
            return role.Name;
        }

        var opus = RoleName(opusModel);
        var sonnet = RoleName(sonnetModel);
        var haiku = RoleName(haikuModel);
        var subagent = RoleName(subagentModel);

        if (modelAliases is not null)
        {
            foreach (var (name, model) in modelAliases)
            {
                aliases[name] = model;
            }
        }

        return new ClaudeCodeModels(presented, opus, sonnet, haiku, subagent, aliases, served);
    }

    private static Model WithEffort(Model model, string effort) =>
        new(model.Api, model.Config.Merge(new GenerateConfig { ReasoningEffort = effort }), model.Retry) { EventSink = model.EventSink };
}
