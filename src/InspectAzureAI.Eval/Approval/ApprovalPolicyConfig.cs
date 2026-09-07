using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of <c>approval/_policy.py</c> <c>ApproverPolicyConfig</c>: one entry of a policy file — the approver's
/// registry <see cref="Name"/>, its <see cref="Tools"/> (a string or an array of strings, kept in the file's shape)
/// and its <see cref="Params"/>. Keys other than <c>name</c>, <c>tools</c> and <c>params</c> are collected into
/// <see cref="Params"/> (an unknown key wins over the same key under <c>params</c>), as in Python.
/// </summary>
public sealed record ApproverPolicyConfig
{
    public ApproverPolicyConfig(string name, JsonNode tools, JsonObject? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tools);
        Name = name;
        Tools = tools.DeepClone();
        ToolSpecs = ParseTools(Tools);
        Params = parameters?.DeepClone().AsObject() ?? new JsonObject();
    }

    public string Name { get; }

    /// <summary>A JSON string or an array of strings, as written in the file.</summary>
    public JsonNode Tools { get; }

    /// <summary>The specs of <see cref="Tools"/> as a list.</summary>
    public IReadOnlyList<string> ToolSpecs { get; }

    public JsonObject Params { get; }

    /// <summary>Whether <see cref="Tools"/> is a single string.</summary>
    public bool ToolsAsString => Tools is JsonValue;

    /// <summary>Port of the pydantic validation plus <c>collect_unknown_fields</c>.</summary>
    public static ApproverPolicyConfig FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var name = json["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var nameText)
            ? nameText
            : throw new ArgumentException("Approval policy entry needs a string 'name'.", nameof(json));
        var tools = json["tools"] ?? throw new ArgumentException($"Approval policy entry '{name}' needs 'tools' (a string or a list of strings).", nameof(json));
        var parameters = json["params"] switch
        {
            null => new JsonObject(),
            JsonObject obj => obj.DeepClone().AsObject(),
            _ => throw new ArgumentException($"Approval policy entry '{name}': 'params' must be an object.", nameof(json)),
        };
        foreach (var pair in json)
        {
            if (pair.Key is not ("name" or "tools" or "params"))
            {
                parameters[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return new ApproverPolicyConfig(name, tools, parameters);
    }

    /// <summary>Port of <c>model_dump()</c>: <c>{"name", "tools", "params"}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["tools"] = Tools.DeepClone(),
        ["params"] = Params.DeepClone(),
    };

    private static IReadOnlyList<string> ParseTools(JsonNode tools)
    {
        switch (tools)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return [text];
            case JsonArray array:
                var specs = new List<string>(array.Count);
                foreach (var item in array)
                {
                    if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                    {
                        specs.Add(itemText);
                    }
                    else
                    {
                        throw new ArgumentException($"Approval policy 'tools' entries must be strings, not {Describe(item)}.", nameof(tools));
                    }
                }

                return specs;
            default:
                throw new ArgumentException($"Approval policy 'tools' must be a string or a list of strings, not {Describe(tools)}.", nameof(tools));
        }
    }

    private static string Describe(JsonNode? node) => node is null ? "null" : node.ToJsonString();
}

/// <summary>Port of <c>approval/_policy.py</c> <c>ApprovalPolicyConfig</c>: the <c>approvers</c> list of a policy file.</summary>
public sealed record ApprovalPolicyConfig(IReadOnlyList<ApproverPolicyConfig> Approvers)
{
    /// <summary>Parses <c>{"approvers": [...]}</c>; any other shape is an <see cref="ArgumentException"/>.</summary>
    public static ApprovalPolicyConfig FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json["approvers"] is not JsonArray approvers)
        {
            throw new ArgumentException("Approval policy config needs an 'approvers' list.", nameof(json));
        }

        var entries = new List<ApproverPolicyConfig>(approvers.Count);
        foreach (var entry in approvers)
        {
            entries.Add(entry is JsonObject obj
                ? ApproverPolicyConfig.FromJson(obj)
                : throw new ArgumentException($"Approval policy 'approvers' entries must be objects, not {(entry is null ? "null" : entry.ToJsonString())}.", nameof(json)));
        }

        return new ApprovalPolicyConfig(entries);
    }

    /// <summary>Port of <c>read_config_object</c> for JSON text: the text must be a JSON object.</summary>
    public static ApprovalPolicyConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid approval policy: {ex.Message}", nameof(json), ex);
        }

        return node is JsonObject obj ? FromJson(obj) : throw new ArgumentException($"Invalid approval policy: {json}", nameof(json));
    }

    /// <summary>Port of <c>model_dump()</c>: what <c>EvalConfig.approval</c> carries.</summary>
    public JsonObject ToJson() => new()
    {
        ["approvers"] = new JsonArray(Approvers.Select(a => (JsonNode?)a.ToJson()).ToArray()),
    };
}
