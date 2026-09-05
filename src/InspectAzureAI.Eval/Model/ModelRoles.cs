using System.Collections;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Reference to a named model role including its resolution policy (port of <c>ModelRole</c> in
/// <c>model/_model_role.py</c>): <see cref="Name"/> is looked up with <see cref="ModelRoles.GetModel"/>, and
/// <see cref="Required"/> says whether a model must be bound to it.
/// </summary>
public sealed record ModelRole(string Name, bool Required = false);

/// <summary>
/// The resolved model roles of an eval (port of the <c>dict[str, Model | list[Model]]</c> that
/// <c>resolve_model_roles</c> produces and <c>init_model_roles</c> / <c>model_roles()</c> carry): a role maps to
/// one or more <see cref="Model"/> instances, each a distinct copy stamped with the role so per-role usage is
/// attributed correctly. A single-model list collapses to the single model, as in Python; <see cref="Get"/>
/// returns the first model of a role and <see cref="GetAll"/> every model. The roles are ambient
/// (<see cref="Begin"/> / <see cref="Current"/>), and <see cref="GetModel"/> is the port of
/// <c>get_model(role=..., default=..., required=...)</c>.
/// </summary>
public sealed class ModelRoles : IReadOnlyDictionary<string, IReadOnlyList<Model>>
{
    private static readonly AsyncLocal<ModelRoles?> Ambient = new();

    private readonly Dictionary<string, IReadOnlyList<Model>> _roles;

    private ModelRoles(Dictionary<string, IReadOnlyList<Model>> roles)
    {
        _roles = roles;
    }

    /// <summary>No roles (Python's default <c>{}</c>).</summary>
    public static ModelRoles Empty { get; } = new(new Dictionary<string, IReadOnlyList<Model>>(StringComparer.Ordinal));

    /// <summary>The roles of the current eval (port of <c>model_roles()</c>); <see cref="Empty"/> when none were installed.</summary>
    public static ModelRoles Current => Ambient.Value ?? Empty;

    /// <summary>Port of <c>init_model_roles</c>: installs <paramref name="roles"/> (null → <see cref="Empty"/>) for the current async flow; disposing restores the previous ones.</summary>
    public static IDisposable Begin(ModelRoles? roles)
    {
        var previous = Ambient.Value;
        Ambient.Value = roles ?? Empty;
        return new Restore(previous);
    }

    /// <summary>
    /// Port of <c>resolve_model_roles</c>. Each value is a model name, a <see cref="Model"/>, or a non-empty
    /// sequence of these (an <c>IEnumerable</c> of <c>string</c> / <see cref="Model"/>); names are created with
    /// <paramref name="modelFactory"/> (default: <c>FoundryModels.Create(name)</c>),
    /// and every entry becomes a distinct <see cref="Model"/> copy bound to its role (<see cref="Model.WithRole"/>).
    /// Returns null for null input. Any other value or an empty list is a <see cref="PrerequisiteError"/>, with Python's messages.
    /// </summary>
    public static ModelRoles? Resolve(IReadOnlyDictionary<string, object>? roles, Func<string, Model>? modelFactory = null)
    {
        if (roles is null)
        {
            return null;
        }

        modelFactory ??= name => FoundryModels.Create(name);
        var resolved = new Dictionary<string, IReadOnlyList<Model>>(StringComparer.Ordinal);
        foreach (var (role, value) in roles)
        {
            switch (value)
            {
                case string or Model:
                    resolved[role] = [ResolveRoleModel(role, value, modelFactory)];
                    break;
                case IEnumerable sequence and not string:
                {
                    var items = sequence.Cast<object?>().ToList();
                    if (items.Any(item => item is not (string or Model)))
                    {
                        throw InvalidRole(role, value);
                    }

                    if (items.Count == 0)
                    {
                        throw new PrerequisiteError($"Model role '{role}' was assigned an empty list (at least one model is required).");
                    }

                    resolved[role] = items.Select(item => ResolveRoleModel(role, item!, modelFactory)).ToArray();
                    break;
                }

                default:
                    throw InvalidRole(role, value);
            }
        }

        return new ModelRoles(resolved);
    }

    /// <summary>Port of <c>_merge_model_roles</c> (<c>_eval/loader.py</c>): later sets win per role; null when nothing is left.</summary>
    public static ModelRoles? Merge(params ModelRoles?[] roleSets)
    {
        var merged = new Dictionary<string, IReadOnlyList<Model>>(StringComparer.Ordinal);
        foreach (var roles in roleSets)
        {
            if (roles is null)
            {
                continue;
            }

            foreach (var (role, models) in roles._roles)
            {
                merged[role] = models;
            }
        }

        return merged.Count > 0 ? new ModelRoles(merged) : null;
    }

    /// <summary>
    /// Port of <c>get_model(role=..., default=..., required=..., config=...)</c>. A bound role returns its first
    /// model (with <paramref name="config"/> layered under the model's own config and the role stamped, when a
    /// config is given); an unbound required role without a default is a <see cref="PrerequisiteError"/>; an
    /// unbound role returns <paramref name="default"/> stamped with the role, else the active sample's model, else
    /// a model named by <c>INSPECT_EVAL_MODEL</c> (first entry) or <c>INSPECT_AZUREAI_MODEL</c>; with none of
    /// those an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Model GetModel(string role, Model? @default = null, bool required = false, GenerateConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        var bound = Current.Get(role);
        if (bound is not null)
        {
            if (config is not null && !GenerateConfigIsEmpty(config))
            {
                return bound.WithConfig(config.Merge(bound.Config)).WithRole(role);
            }

            return bound;
        }

        if (required && @default is null)
        {
            throw new PrerequisiteError($"Model role '{role}' is required and was not specified.");
        }

        if (@default is not null)
        {
            return @default.WithRole(role);
        }

        if (SampleContext.Current?.ActiveModel is { } active)
        {
            return active;
        }

        var name = Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL")?.Split(',')[0]
                   ?? Environment.GetEnvironmentVariable(FoundryModels.ModelVar);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("No model specified (and no model environment variable defined)");
        }

        return FoundryModels.Create(name, config);
    }

    /// <summary>The first model bound to <paramref name="role"/>, or null.</summary>
    public Model? Get(string role) => _roles.TryGetValue(role, out var models) ? models[0] : null;

    /// <summary>Every model bound to <paramref name="role"/> (one entry for a single-model role), or null.</summary>
    public IReadOnlyList<Model>? GetAll(string role) => _roles.TryGetValue(role, out var models) ? models : null;

    public IReadOnlyList<Model> this[string key] => _roles[key];

    public IEnumerable<string> Keys => _roles.Keys;

    public IEnumerable<IReadOnlyList<Model>> Values => _roles.Values;

    public int Count => _roles.Count;

    public bool ContainsKey(string key) => _roles.ContainsKey(key);

    public bool TryGetValue(string key, out IReadOnlyList<Model> value) => _roles.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, IReadOnlyList<Model>>> GetEnumerator() => _roles.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static Model ResolveRoleModel(string role, object value, Func<string, Model> modelFactory)
    {
        // a fresh instance per role (Python: memoize=False for names, copy() for Model instances) so roles
        // sharing one model do not collapse onto one object and misattribute per-role usage
        var model = value is string name ? modelFactory(name) : (Model)value;
        return model.WithRole(role);
    }

    private static PrerequisiteError InvalidRole(string role, object? value) =>
        new($"Model role '{role}' has an invalid value ({value ?? "None"}): expected a model name, a Model instance, or a list of these.");

    /// <summary>Port of <c>not config.model_dump(exclude_none=True)</c>: every field is null.</summary>
    private static bool GenerateConfigIsEmpty(GenerateConfig config) =>
        typeof(GenerateConfig).GetProperties().All(property => property.GetValue(config) is null);

    private sealed class Restore(ModelRoles? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
