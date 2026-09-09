using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>One matching path for live identities and historical bare-name Foundry logs. Never rewrites disk logs.</summary>
public static class TaskIdentityMatcher
{
    public static ResolvedTask? Match(EvalSetLog log, IReadOnlyList<ResolvedTask> candidates)
    {
        var exact = candidates.Where(c => c.Identifier == log.TaskIdentifier).ToList();
        if (exact.Count > 0) return Unique(exact, log.Path);
        var legacy = new List<ResolvedTask>();
        foreach (var candidate in candidates)
        {
            var spec = log.Header.Eval;
            var changed = false;
            var model = spec.Model;
            if (!model.Contains('/') && candidate.Model.Api.IsFoundry && model == candidate.Model.Api.ModelName)
            {
                model = candidate.Model.Api.QualifiedModelName;
                changed = model != spec.Model;
            }
            else if (model != candidate.Model.Api.QualifiedModelName) continue;

            IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? roles = spec.ModelRoles;
            if (roles is not null)
            {
                var adjusted = new Dictionary<string, IReadOnlyList<ModelConfig>>(StringComparer.Ordinal);
                foreach (var (role, configs) in roles)
                {
                    var live = candidate.ModelRoles?.GetAll(role);
                    adjusted[role] = configs.Select((config, index) =>
                    {
                        if (!config.Model.Contains('/') && live is not null && index < live.Count && live[index].Api.IsFoundry && live[index].Api.ModelName == config.Model)
                        {
                            changed = true;
                            return config with { Model = live[index].Api.QualifiedModelName };
                        }
                        return config;
                    }).ToList();
                }
                roles = adjusted;
            }
            if (changed && TaskIdentifier.Compute(log.Header with { Eval = spec with { Model = model, ModelRoles = roles } }) == candidate.Identifier)
                legacy.Add(candidate);
        }
        return legacy.Count == 0 ? null : Unique(legacy, log.Path);
    }
    public static IReadOnlyList<EvalSetLog> Associate(IReadOnlyList<EvalSetLog> logs, IReadOnlyList<ResolvedTask> candidates) =>
        logs.Select(log => Match(log, candidates) is { } task ? log with { TaskIdentifier = task.Identifier } : log).ToList();
    private static ResolvedTask Unique(List<ResolvedTask> matches, string path) => matches.Count == 1 ? matches[0]
        : throw new PrerequisiteError($"Ambiguous task identity for legacy log '{Path.GetFileName(path)}'. More than one Foundry candidate matches; use a directory containing logs for a single destination.");
}
