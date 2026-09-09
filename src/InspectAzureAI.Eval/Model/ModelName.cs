using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port of <c>model/_model.py</c> <c>ModelName</c>: a model's api (provider) and the specific model served by
/// that api, for structural matching of a model against string specifications so tasks can condition their
/// behaviour on models or model families. A specification can be fully specified (<c>openai/gpt-4</c>), a model
/// name only (<c>gpt-4</c>) or a substring of the model name (<c>gpt</c>) — see <see cref="Matches"/>.
/// Deviation: Python reads the api from the provider registry (<c>registry_info(model.api).name</c>); there is no
/// registry here, so <see cref="ProviderName"/> maps this repo's api types to their Python provider names
/// (<c>AzureAIModelApi</c> → <c>azureai</c>, <c>AnthropicFoundryModelApi</c> → <c>anthropic</c>, a
/// <c>FallbackModelApi</c> → its primary) and derives any other api's name from its type
/// (<c>ScriptedModelApi</c> → <c>scripted</c>). Two <see cref="ModelName"/>s compare by api and name, where
/// Python's <c>__eq__</c> is false for anything but a string.
/// </summary>
public sealed class ModelName : IEquatable<ModelName>
{
    /// <summary>Port of <c>ModelName(str)</c>: parses <c>api/name</c>; a name without an api is an <see cref="ArgumentException"/> (Python's <c>ValueError</c>).</summary>
    public ModelName(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (api, name) = Parse(model);
        Api = api ?? throw new ArgumentException("API not specified for model name");
        Name = name;
    }

    /// <summary>Port of <c>ModelName(Model)</c>: the api's <see cref="ProviderName"/> and the model's <see cref="Model.Name"/>.</summary>
    public ModelName(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Api = ProviderName(model.Api);
        Name = model.Name;
    }

    /// <summary>A model name from an explicit api and name (no parsing).</summary>
    public ModelName(string api, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(api);
        ArgumentNullException.ThrowIfNull(name);
        Api = api;
        Name = name;
    }

    /// <summary>Port of <c>ModelName.api</c>: the provider, e.g. <c>openai</c>.</summary>
    public string Api { get; }

    /// <summary>Port of <c>ModelName.name</c>: the model served by the api, e.g. <c>gpt-4</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Port of <c>ModelName.__eq__</c> against a string: a specification carrying an api matches when that api is a
    /// substring of <see cref="Api"/> and its name part a substring of <see cref="Name"/>; otherwise the name part
    /// alone must be a substring of <see cref="Name"/> (so, as in Python, <c>other/gpt-4</c> still matches a
    /// <c>gpt-4</c> served by another api). Comparisons are ordinal and case-sensitive, like Python's <c>in</c>.
    /// </summary>
    public bool Matches(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var (api, name) = Parse(pattern);
        if (api is { Length: > 0 } && Api.Contains(api, StringComparison.Ordinal) && Name.Contains(name, StringComparison.Ordinal))
        {
            return true;
        }

        return Name.Contains(name, StringComparison.Ordinal);
    }

    /// <summary>Port of <c>ModelName.__str__</c>: <c>api/name</c>.</summary>
    public override string ToString() => $"{Api}/{Name}";

    public bool Equals(ModelName? other) =>
        other is not null && string.Equals(Api, other.Api, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <summary>
    /// A string is matched as a specification (<see cref="Matches"/>, Python's <c>__eq__</c>); a <see cref="ModelName"/>
    /// compares by api and name. Deviation: Python's <c>ModelName</c> is unhashable, so its pattern equality never meets
    /// a hash table; here <see cref="GetHashCode"/> hashes the api and name, so a <see cref="ModelName"/> equal to a
    /// string pattern does not share its hash — do not key a collection by <c>object</c> mixing the two.
    /// </summary>
    public override bool Equals(object? obj) => obj switch
    {
        string pattern => Matches(pattern),
        ModelName other => Equals(other),
        _ => false,
    };

    public override int GetHashCode() => HashCode.Combine(Api, Name);

    /// <summary>Port of <c>ModelName == "spec"</c>: <see cref="Matches"/>; a null model equals only a null pattern.</summary>
    public static bool operator ==(ModelName? model, string? pattern) =>
        model is null ? pattern is null : pattern is not null && model.Matches(pattern);

    public static bool operator !=(ModelName? model, string? pattern) => !(model == pattern);

    public static bool operator ==(string? pattern, ModelName? model) => model == pattern;

    public static bool operator !=(string? pattern, ModelName? model) => !(model == pattern);

    /// <summary>
    /// The provider (api) name of <paramref name="api"/> — the port's stand-in for Python's
    /// <c>registry_info(model.api).name</c> with the package prefix stripped: this repo's Foundry apis by their
    /// Python provider names, a <see cref="FallbackModelApi"/> by its primary, and any other api by its type name
    /// lower-cased without a <c>ModelApi</c> / <c>Api</c> suffix.
    /// </summary>
    public static string ProviderName(IModelApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.ProviderName;
    }

    /// <summary>Port of <c>ModelName._parse_model</c>: <c>api/name</c> splits on the first slash; no slash means no api.</summary>
    private static (string? Api, string Name) Parse(string model) =>
        model.Split('/', 2) is [var api, var name] ? (api, name) : (null, model);
}
