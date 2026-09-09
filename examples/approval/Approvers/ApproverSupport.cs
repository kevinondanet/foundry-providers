using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Approval;

/// <summary>
/// What the two approver ports of <c>examples/approval/approval.py</c> share: the tool-call argument they inspect
/// (the console app prints the same one) and the parameter (de)serialization behind their registry factories,
/// after the <c>RequireString</c> helper the library's own <c>auto</c> and <c>human</c> approvers use.
/// </summary>
internal static class ApproverSupport
{
    /// <summary>
    /// Port of <c>str(next(iter(call.arguments.values())))</c>: the first argument's value whatever its name (for
    /// compatibility with a broader range of command-executing tools), rendered as Python's <c>str()</c> renders the
    /// JSON value: a string as is, <c>null</c> as <c>None</c>, a boolean as <c>True</c>/<c>False</c>, a number as
    /// its JSON text. Deviation: an array or object renders as JSON text rather than a Python <c>repr</c>, and a
    /// call with no arguments reads as an empty string, which both approvers then reject as empty; in Python
    /// <c>next</c> raises <c>StopIteration</c> and the sample errors. The case is reachable: the approver runs
    /// before the executor validates the tool's required parameters.
    /// </summary>
    public static string FirstArgumentText(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        foreach (var pair in call.Arguments)
        {
            return pair.Value switch
            {
                null => "None",
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
                var node => node.ToJsonString(),
            };
        }

        return string.Empty;
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    public static IReadOnlyList<string> RequireStringList(JsonNode? value, string approver, string parameter) =>
        value is JsonArray array
            ? array.Select(item => RequireString(item, approver, parameter)).ToArray()
            : throw new ArgumentException($"Parameter '{parameter}' of approver '{approver}' must be a list of strings.", parameter);

    /// <summary>A <c>dict[str, list[str]]</c> parameter: an object mapping each key to a list of strings.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RequireStringLists(JsonNode? value, string approver, string parameter)
    {
        if (value is not JsonObject lists)
        {
            throw new ArgumentException($"Parameter '{parameter}' of approver '{approver}' must be an object mapping each key to a list of strings.", parameter);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in lists)
        {
            result[pair.Key] = RequireStringList(pair.Value, approver, $"{parameter}.{pair.Key}");
        }

        return result;
    }

    public static bool RequireBool(JsonNode? value, string approver, string parameter) =>
        value is JsonValue json && json.TryGetValue<bool>(out var flag)
            ? flag
            : throw new ArgumentException($"Parameter '{parameter}' of approver '{approver}' must be a boolean.", parameter);

    public static ArgumentException Required(string approver, string parameter) =>
        new($"Parameter '{parameter}' of approver '{approver}' is required.", parameter);

    /// <summary>
    /// Port of what <c>registry_params</c> records for a policy entry: every parameter the entry gives, explicit
    /// defaults included. The direct C# factories record an optional argument only when it is non-default (a
    /// <c>bool</c> parameter cannot tell an explicit <c>false</c> from the default), so this copies into
    /// <paramref name="parameters"/> the values of <paramref name="given"/> they left out: an explicit <c>false</c>,
    /// or a JSON <c>null</c> (Python's <c>None</c>, which the approvers treat as "not given").
    /// </summary>
    public static void RecordGiven(JsonObject parameters, JsonObject given)
    {
        foreach (var pair in given)
        {
            if (!parameters.ContainsKey(pair.Key))
            {
                parameters[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    private static string RequireString(JsonNode? value, string approver, string parameter) =>
        value is JsonValue json && json.TryGetValue<string>(out var text)
            ? text
            : throw new ArgumentException($"Parameter '{parameter}' of approver '{approver}' must be a list of strings.", parameter);
}
