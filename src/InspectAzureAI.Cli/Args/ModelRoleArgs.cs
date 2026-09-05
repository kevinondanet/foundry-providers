using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Args;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Builds the model a role mapping names (<c>model</c>, its generate config and <c>model_args</c>).</summary>
public delegate Model RoleModelFactory(string? modelName, GenerateConfig? config, IReadOnlyDictionary<string, object?>? modelArgs);

/// <summary>Port of <c>_cli/util.py</c> <c>parse_model_role_cli_args</c>.</summary>
public static class ModelRoleArgs
{
    /// <summary>
    /// Parses <c>--model-role</c> values: <c>grader=mockllm/model</c> keeps the name, <c>grader=a,b</c> (or a YAML list)
    /// binds several models, and a YAML/JSON mapping (<c>grader={model: x, temperature: 0.5}</c>) builds a distinct
    /// <see cref="Model"/> through <paramref name="factory"/> from its <c>model</c>, <c>model_args</c> and generate
    /// config fields. Text that is not key-value / YAML / JSON is an <see cref="ArgumentException"/>; <c>model_args</c>
    /// that is not a mapping is an <see cref="ArgumentException"/>; an unknown config field is a <see cref="PrerequisiteError"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? Parse(IEnumerable<string>? modelRoles, RoleModelFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var roles = modelRoles?.ToList();
        if (roles is null || roles.Count == 0)
        {
            return null;
        }

        Dictionary<string, object?> parsed;
        try
        {
            parsed = CliArgs.ParseCliArgs(roles);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("Could not parse model role arguments. Should be key-value pairs or valid YAML/JSON.", ex);
        }

        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (role, value) in parsed)
        {
            if (value is IEnumerable<object?> items)
            {
                var list = new List<object>();
                foreach (var item in items)
                {
                    var trimmed = item is string text ? text.Trim() : item;
                    if (trimmed is string { Length: 0 })
                    {
                        continue;
                    }

                    list.Add(ResolveRoleValue(role, trimmed, factory));
                }

                resolved[role] = list;
            }
            else
            {
                resolved[role] = ResolveRoleValue(role, value, factory);
            }
        }

        return resolved;
    }

    private static object ResolveRoleValue(string role, object? value, RoleModelFactory factory)
    {
        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            var parameters = new Dictionary<string, object?>(mapping, StringComparer.Ordinal);
            var modelName = parameters.Remove("model", out var name) ? name as string : null;
            IReadOnlyDictionary<string, object?>? modelArgs = null;
            if (parameters.Remove("model_args", out var args))
            {
                modelArgs = args as IReadOnlyDictionary<string, object?> ?? throw new ArgumentException("model_args must be a dict");
            }

            var config = GenerateConfigBinding.FromValues(parameters, $"model role '{role}'");
            return factory(modelName, config, modelArgs);
        }

        return value switch
        {
            string text => text,
            null => throw new ArgumentException($"Model role '{role}' has no model."),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }
}
