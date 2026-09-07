using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>
/// Python's strict <c>str.format(**kwargs)</c> for the solvers that call it directly (<c>multiple_choice</c>,
/// <c>chain_of_thought</c>, <c>self_critique</c>): <c>{{</c> / <c>}}</c> escape braces, <c>{name}</c> substitutes
/// <c>str(value)</c>, and — unlike <see cref="TemplateFormatter"/>, the <c>format_template</c> port that keeps unknown
/// placeholders — an unknown name is a <see cref="KeyNotFoundException"/> (Python <c>KeyError</c>), a lone brace a
/// <see cref="FormatException"/> (Python <c>ValueError</c>). Positional fields, attribute/index access, conversions
/// other than <c>!s</c> and format specs are not supported and raise <see cref="NotSupportedException"/>.
/// </summary>
internal static class PythonFormat
{
    public static string Format(string template, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormatException("Single '{' encountered in format string");
                }

                result.Append(Substitute(template.Substring(i + 1, close - i - 1), arguments));
                i = close + 1;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    result.Append('}');
                    i += 2;
                    continue;
                }

                throw new FormatException("Single '}' encountered in format string");
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static string Substitute(string field, IReadOnlyDictionary<string, object?> arguments)
    {
        var specIndex = field.IndexOf(':');
        var name = specIndex < 0 ? field : field[..specIndex];
        var spec = specIndex < 0 ? "" : field[(specIndex + 1)..];
        var conversionIndex = name.IndexOf('!');
        var conversion = conversionIndex < 0 ? "" : name[(conversionIndex + 1)..];
        name = conversionIndex < 0 ? name : name[..conversionIndex];

        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            throw new NotSupportedException($"Positional format field '{{{field}}}' is not supported; templates take named placeholders only.");
        }

        if (name.IndexOfAny(['.', '[']) >= 0)
        {
            throw new NotSupportedException($"Attribute or index access in format field '{{{field}}}' is not supported.");
        }

        if (conversion.Length > 0 && conversion != "s")
        {
            throw new NotSupportedException($"Conversion '!{conversion}' in format field '{{{field}}}' is not supported.");
        }

        if (spec.Length > 0)
        {
            throw new NotSupportedException($"Format spec ':{spec}' in format field '{{{field}}}' is not supported.");
        }

        if (!arguments.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"'{name}'");
        }

        return Str(value);
    }

    /// <summary>Python's <c>str(value)</c> for the value types template variables carry.</summary>
    private static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        JsonValue json when json.TryGetValue<string>(out var s) => s,
        JsonValue json when json.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonNode json => json.ToJsonString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
