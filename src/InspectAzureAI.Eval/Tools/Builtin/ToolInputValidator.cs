using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of <c>validate_tool_input</c> (<c>model/_call_tools.py</c>): checks a tool call's arguments against
/// the tool's parameter schema before the tool runs and raises the same <see cref="ToolParsingError"/> message
/// Python's jsonschema Draft 7 validator produces ("Found N validation errors parsing tool input arguments:"
/// followed by one line per error). Python runs this for every tool; the .NET <see cref="ToolExecutor"/> only
/// checks required parameters, so the built-in tools call it themselves. Covers the schema subset the
/// built-in tools declare: type, enum, items, properties, required, additionalProperties and anyOf.
/// </summary>
internal static class ToolInputValidator
{
    /// <summary>Validates <paramref name="arguments"/> against <paramref name="parameters"/>, throwing <see cref="ToolParsingError"/> on the first pass of errors.</summary>
    public static void Validate(JsonObject arguments, ToolParams parameters)
    {
        var errors = Errors(arguments, parameters);
        if (errors.Count > 0)
        {
            throw new ToolParsingError(
                $"Found {errors.Count} validation errors parsing tool input arguments:\n"
                + string.Join("\n", errors.Select(e => "- " + e)));
        }
    }

    /// <summary>The error messages (jsonschema wording) for <paramref name="arguments"/>, empty when valid.</summary>
    public static List<string> Errors(JsonObject arguments, ToolParams parameters)
    {
        // Draft 7 reports each keyword independently, in the schema's dump order: for a ToolParams object that
        // is type, properties, required, additionalProperties.
        var errors = new List<string>();
        ValidateProperties(arguments, parameters.Properties, errors);
        ValidateRequired(arguments, parameters.Required, errors);
        ValidateAdditionalProperties(arguments, parameters.Properties, parameters.AdditionalProperties, errors);
        return errors;
    }

    private static bool IsValid(JsonNode? instance, ToolParam schema)
    {
        var errors = new List<string>();
        ValidateSchema(instance, schema, errors);
        return errors.Count == 0;
    }

    // ToolParam dump order: type, enum, items, properties, additionalProperties, anyOf, required; the structural
    // keywords skip instances of another type, as jsonschema does.
    private static void ValidateSchema(JsonNode? instance, ToolParam schema, List<string> errors)
    {
        if (schema.Type is { Count: > 0 } types && !types.Any(t => IsType(instance, t)))
        {
            errors.Add($"{Repr(instance)} is not of type {string.Join(", ", types.Select(t => $"'{t}'"))}");
        }

        if (schema.Enum is { } allowed && !allowed.Any(e => JsonNode.DeepEquals(e, instance)))
        {
            errors.Add($"{Repr(instance)} is not one of [{string.Join(", ", allowed.Select(Repr))}]");
        }

        if (schema.Items is { } items && instance is JsonArray array)
        {
            foreach (var item in array)
            {
                ValidateSchema(item, items, errors);
            }
        }

        if (instance is JsonObject obj)
        {
            if (schema.Properties is { } properties)
            {
                ValidateProperties(obj, properties, errors);
            }

            ValidateAdditionalProperties(obj, schema.Properties, schema.AdditionalProperties, errors);
        }

        if (schema.AnyOf is { } anyOf && !anyOf.Any(s => IsValid(instance, s)))
        {
            errors.Add($"{Repr(instance)} is not valid under any of the given schemas");
        }

        if (schema.Required is { } required && instance is JsonObject required_target)
        {
            ValidateRequired(required_target, required, errors);
        }
    }

    private static void ValidateProperties(JsonObject instance, IReadOnlyDictionary<string, ToolParam> properties, List<string> errors)
    {
        foreach (var (name, schema) in properties)
        {
            if (instance.TryGetPropertyValue(name, out var value))
            {
                ValidateSchema(value, schema, errors);
            }
        }
    }

    private static void ValidateRequired(JsonObject instance, IReadOnlyList<string> required, List<string> errors)
    {
        foreach (var name in required)
        {
            if (!instance.ContainsKey(name))
            {
                errors.Add($"'{name}' is a required property");
            }
        }
    }

    private static void ValidateAdditionalProperties(JsonObject instance, IReadOnlyDictionary<string, ToolParam>? properties, object? additionalProperties, List<string> errors)
    {
        var extras = instance.Where(p => properties is null || !properties.ContainsKey(p.Key)).ToList();
        if (extras.Count == 0)
        {
            return;
        }

        switch (additionalProperties)
        {
            case false:
                var names = string.Join(", ", extras.Select(e => $"'{e.Key}'"));
                errors.Add($"Additional properties are not allowed ({names} {(extras.Count == 1 ? "was" : "were")} unexpected)");
                break;
            case ToolParam schema:
                foreach (var extra in extras)
                {
                    ValidateSchema(extra.Value, schema, errors);
                }

                break;
        }
    }

    /// <summary>Draft 7 type check: integers admit integral floats (2.0) but never booleans.</summary>
    internal static bool IsType(JsonNode? instance, string type)
    {
        var kind = instance?.GetValueKind() ?? JsonValueKind.Null;
        return type switch
        {
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "null" => kind == JsonValueKind.Null,
            "array" => instance is JsonArray,
            "object" => instance is JsonObject,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && IsIntegral((JsonValue)instance!),
            _ => false,
        };
    }

    internal static bool IsIntegral(JsonValue value) =>
        TryGetInt64(value, out _) || (TryGetDouble(value, out var d) && double.IsInteger(d));

    /// <summary>
    /// The integral value of a JSON number, whether it was parsed from text or created in memory
    /// (<c>JsonValue.Create(5)</c> only answers <c>TryGetValue&lt;int&gt;</c>, so the node is normalised through
    /// a <see cref="JsonElement"/>). False for non-numbers and for numbers with a fractional part.
    /// </summary>
    internal static bool TryGetInt64(JsonValue value, out long result)
    {
        if (value.TryGetValue<long>(out result) || value.TryGetValue<int>(out var i) && (result = i) == i)
        {
            return true;
        }

        var element = ToElement(value);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out result))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) && double.IsInteger(d) && d is >= long.MinValue and <= long.MaxValue)
        {
            result = (long)d;
            return true;
        }

        result = 0;
        return false;
    }

    /// <summary>The value of a JSON number as a double (parsed or in-memory); false for non-numbers.</summary>
    internal static bool TryGetDouble(JsonValue value, out double result)
    {
        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        var element = ToElement(value);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static JsonElement ToElement(JsonValue value) =>
        value.TryGetValue<JsonElement>(out var element) ? element : JsonSerializer.SerializeToElement(value);

    /// <summary>Python <c>repr</c> of a JSON value, as jsonschema quotes instances in its messages.</summary>
    internal static string Repr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(p => $"{ReprString(p.Key)}: {Repr(p.Value)}")) + "}";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(Repr)) + "]";
        }

        var value = (JsonValue)node;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return ReprString(value.GetValue<string>());
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Null:
                return "None";
            default:
                if (TryGetInt64(value, out var integer) && !HasFraction(value))
                {
                    return integer.ToString(CultureInfo.InvariantCulture);
                }

                return TryGetDouble(value, out var number) ? ReprFloat(number) : value.ToJsonString();
        }
    }

    /// <summary>Whether the number was written with a fraction or exponent (Python reprs <c>2.0</c> as a float even though it is integral).</summary>
    private static bool HasFraction(JsonValue value)
    {
        var text = ToElement(value).GetRawText();
        return text.Contains('.') || text.Contains('e') || text.Contains('E');
    }

    private static string ReprFloat(double d)
    {
        if (double.IsNaN(d))
        {
            return "nan";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "inf" : "-inf";
        }

        var text = d.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E'))
        {
            return text.Replace("E", "e", StringComparison.Ordinal);
        }

        return text.Contains('.') ? text : text + ".0";
    }

    private static string ReprString(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < ' ' || c == '\u007f')
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append(quote).ToString();
    }
}
