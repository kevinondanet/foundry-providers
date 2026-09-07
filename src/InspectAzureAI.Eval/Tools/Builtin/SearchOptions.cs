using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>The value kinds of a provider option field, the subset of pydantic types <c>TavilyOptions</c> and <c>ExaOptions</c> use.</summary>
internal enum SearchOptionKind
{
    Int,
    Bool,
    String,
    StringList,
    StringLiteral,
    IntLiteral,
    BoolOrStringLiteral,
}

/// <summary>One field of a provider options model: its name, kind and (for literals) the admitted values.</summary>
internal sealed record SearchOptionField(string Name, SearchOptionKind Kind, IReadOnlyList<string>? Literals = null, IReadOnlyList<int>? IntLiterals = null);

/// <summary>
/// Port of <c>Options.model_validate(in_options).model_dump(exclude_none=True)</c> for the provider option
/// models: checks each known field's type (with pydantic's lax coercions for ints and bools), drops unknown
/// keys and nulls, and returns the options in model field order. A bad value is an
/// <see cref="ArgumentException"/>, the stand-in for pydantic's <c>ValidationError</c>.
/// </summary>
internal static class SearchOptionsValidator
{
    private static readonly HashSet<string> TrueStrings = new(["1", "on", "t", "true", "y", "yes"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FalseStrings = new(["0", "off", "f", "false", "n", "no"], StringComparer.OrdinalIgnoreCase);

    public static JsonObject Validate(string modelName, JsonObject input, IReadOnlyList<SearchOptionField> fields)
    {
        var errors = new List<string>();
        var output = new JsonObject();
        foreach (var field in fields)
        {
            if (!input.TryGetPropertyValue(field.Name, out var value) || value is null)
            {
                continue;
            }

            var converted = Convert(field, value, errors);
            if (converted is not null)
            {
                output[field.Name] = converted;
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"{errors.Count} validation error{(errors.Count == 1 ? "" : "s")} for {modelName}\n" + string.Join("\n", errors),
                nameof(input));
        }

        return output;
    }

    private static JsonNode? Convert(SearchOptionField field, JsonNode value, List<string> errors)
    {
        switch (field.Kind)
        {
            case SearchOptionKind.Int:
                if (TryInt(value, out var i))
                {
                    return JsonValue.Create(i);
                }

                break;
            case SearchOptionKind.Bool:
                if (TryBool(value, out var b))
                {
                    return JsonValue.Create(b);
                }

                break;
            case SearchOptionKind.String:
                if (IsString(value))
                {
                    return value.DeepClone();
                }

                break;
            case SearchOptionKind.StringList:
                if (value is JsonArray array && array.All(item => item is not null && IsString(item)))
                {
                    return value.DeepClone();
                }

                break;
            case SearchOptionKind.StringLiteral:
                if (IsString(value) && field.Literals!.Contains(value.GetValue<string>(), StringComparer.Ordinal))
                {
                    return value.DeepClone();
                }

                break;
            case SearchOptionKind.IntLiteral:
                if (TryInt(value, out var literal) && field.IntLiterals!.Contains(literal))
                {
                    return JsonValue.Create(literal);
                }

                break;
            case SearchOptionKind.BoolOrStringLiteral:
                if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.DeepClone();
                }

                if (IsString(value) && field.Literals!.Contains(value.GetValue<string>(), StringComparer.Ordinal))
                {
                    return value.DeepClone();
                }

                if (TryBool(value, out var coerced))
                {
                    return JsonValue.Create(coerced);
                }

                break;
        }

        errors.Add($"{field.Name}\n  Input should be {Expected(field)} [input_value={ToolInputValidator.Repr(value)}]");
        return null;
    }

    private static string Expected(SearchOptionField field) => field.Kind switch
    {
        SearchOptionKind.Int => "a valid integer",
        SearchOptionKind.Bool => "a valid boolean",
        SearchOptionKind.String => "a valid string",
        SearchOptionKind.StringList => "a valid list of strings",
        SearchOptionKind.StringLiteral => string.Join(", ", field.Literals!.Select(l => $"'{l}'")),
        SearchOptionKind.IntLiteral => string.Join(", ", field.IntLiterals!.Select(l => l.ToString(CultureInfo.InvariantCulture))),
        SearchOptionKind.BoolOrStringLiteral => "a valid boolean or " + string.Join(", ", field.Literals!.Select(l => $"'{l}'")),
        _ => "valid",
    };

    private static bool IsString(JsonNode value) => value.GetValueKind() == JsonValueKind.String;

    private static bool TryInt(JsonNode value, out int result)
    {
        result = 0;
        if (value is not JsonValue scalar)
        {
            return false;
        }

        switch (scalar.GetValueKind())
        {
            case JsonValueKind.Number:
                if (ToolInputValidator.TryGetInt64(scalar, out var integer) && integer is >= int.MinValue and <= int.MaxValue)
                {
                    result = (int)integer;
                    return true;
                }

                return false;
            case JsonValueKind.String:
                return int.TryParse(scalar.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
            default:
                return false;
        }
    }

    private static bool TryBool(JsonNode value, out bool result)
    {
        result = false;
        if (value is not JsonValue scalar)
        {
            return false;
        }

        switch (scalar.GetValueKind())
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                var text = scalar.GetValue<string>();
                if (TrueStrings.Contains(text))
                {
                    result = true;
                    return true;
                }

                return FalseStrings.Contains(text);
            case JsonValueKind.Number:
                if (ToolInputValidator.TryGetInt64(scalar, out var n) && n is 0 or 1)
                {
                    result = n == 1;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}
