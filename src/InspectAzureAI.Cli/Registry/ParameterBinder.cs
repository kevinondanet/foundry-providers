using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Cli.Models;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Registry;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Binds the parsed <c>-T</c> / <c>-S</c> / <c>-M</c> arguments (the values <see cref="CliArgs.ParseCliArgs"/> produces)
/// to a factory method's parameters by name, the way Python passes <c>**task_args</c> to a <c>@task</c> function:
/// names match ignoring case, underscores and hyphens (<c>max_attempts</c> binds <c>maxAttempts</c>), an unknown
/// argument or a missing required one is a <see cref="PrerequisiteError"/>, and each value is converted to the
/// parameter's type (numbers, booleans, strings, enums, lists, dictionaries, <see cref="TimeSpan"/> seconds,
/// <see cref="GenerateConfig"/> mappings and model names for <c>Model</c> parameters).
/// </summary>
public static class ParameterBinder
{
    /// <summary>Binds <paramref name="args"/> to <paramref name="method"/>'s parameters, in declaration order.</summary>
    public static object?[] Bind(MethodBase method, IReadOnlyDictionary<string, object?> args, string subject)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        var parameters = method.GetParameters();
        var byKey = new Dictionary<string, ParameterInfo>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            byKey[Normalize(parameter.Name ?? "")] = parameter;
        }

        var unknown = args.Keys.Where(key => !byKey.ContainsKey(Normalize(key))).ToList();
        if (unknown.Count > 0)
        {
            var valid = parameters.Length == 0 ? "(none)" : string.Join(", ", parameters.Select(p => SnakeCase(p.Name ?? "")));
            throw new PrerequisiteError($"Unknown argument(s) for {subject}: {string.Join(", ", unknown)}. Valid arguments: {valid}.");
        }

        var values = new object?[parameters.Length];
        var given = args.ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var key = Normalize(parameter.Name ?? "");
            if (given.TryGetValue(key, out var value))
            {
                values[i] = Convert(value, parameter.ParameterType, parameter.Name ?? key, subject);
            }
            else if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue is null && parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : parameter.DefaultValue;
            }
            else
            {
                throw new PrerequisiteError($"Missing required argument '{SnakeCase(parameter.Name ?? key)}' for {subject}.");
            }
        }

        return values;
    }

    /// <summary>Converts one parsed value to <paramref name="target"/>; a value that cannot be converted is a <see cref="PrerequisiteError"/>.</summary>
    public static object? Convert(object? value, Type target, string name, string subject)
    {
        ArgumentNullException.ThrowIfNull(target);
        // task args read back from a log (eval-retry) arrive as JSON nodes
        value = value switch
        {
            System.Text.Json.Nodes.JsonNode node => CliArgs.JsonToValue(node),
            System.Text.Json.JsonElement element => CliArgs.JsonToValue(System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())),
            _ => value,
        };
        var underlying = Nullable.GetUnderlyingType(target);
        if (value is null)
        {
            if (underlying is not null || !target.IsValueType)
            {
                return null;
            }

            throw Invalid(value, target, name, subject);
        }

        var type = underlying ?? target;
        try
        {
            if (type == typeof(object))
            {
                return value;
            }

            if (type == typeof(string))
            {
                return value switch
                {
                    string s => s,
                    bool b => b ? "true" : "false",
                    IEnumerable and not string => throw Invalid(value, target, name, subject),
                    _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
                };
            }

            if (type == typeof(bool))
            {
                return value switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var b) => b,
                    long l => l != 0,
                    _ => throw Invalid(value, target, name, subject),
                };
            }

            if (type.IsEnum)
            {
                return value switch
                {
                    string s => ParseEnum(type, s) ?? throw Invalid(value, target, name, subject),
                    long l => Enum.ToObject(type, l),
                    _ => throw Invalid(value, target, name, subject),
                };
            }

            if (type == typeof(TimeSpan))
            {
                return TimeSpan.FromSeconds(System.Convert.ToDouble(value is string ts ? double.Parse(ts, CultureInfo.InvariantCulture) : value, CultureInfo.InvariantCulture));
            }

            if (type == typeof(Model))
            {
                return value switch
                {
                    Model model => model,
                    string modelName => ModelProviders.Resolve(modelName),
                    _ => throw Invalid(value, target, name, subject),
                };
            }

            if (type == typeof(GenerateConfig))
            {
                return value is IReadOnlyDictionary<string, object?> config
                    ? GenerateConfigBinding.FromValues(config, $"{name} of {subject}")
                    : throw Invalid(value, target, name, subject);
            }

            if (type.IsPrimitive || type == typeof(decimal) || type == typeof(BigInteger))
            {
                return ConvertNumber(value, type) ?? throw Invalid(value, target, name, subject);
            }

            if (typeof(IDictionary).IsAssignableFrom(type) || IsGenericDictionary(type))
            {
                return value is IReadOnlyDictionary<string, object?> dict && type.IsAssignableFrom(typeof(Dictionary<string, object?>))
                    ? new Dictionary<string, object?>(dict, StringComparer.Ordinal)
                    : throw Invalid(value, target, name, subject);
            }

            if (ElementTypeOf(type) is { } elementType)
            {
                var items = value is IEnumerable enumerable and not string ? enumerable.Cast<object?>().ToList() : [value];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                foreach (var item in items)
                {
                    list.Add(Convert(item, elementType, name, subject));
                }

                if (type.IsArray)
                {
                    var array = Array.CreateInstance(elementType, list.Count);
                    list.CopyTo(array, 0);
                    return array;
                }

                return type.IsAssignableFrom(list.GetType()) ? list : throw Invalid(value, target, name, subject);
            }

            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw Invalid(value, target, name, subject);
        }
    }

    /// <summary>The comparison key for a parameter or argument name: lower case without underscores or hyphens.</summary>
    internal static string Normalize(string name) => name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>The Python spelling of a C# parameter name (<c>maxAttempts</c> → <c>max_attempts</c>).</summary>
    internal static string SnakeCase(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static PrerequisiteError Invalid(object? value, Type target, string name, string subject) =>
        new($"Invalid value for argument '{SnakeCase(name)}' of {subject}: expected {Describe(target)}, got {Show(value)}.");

    private static string Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string) ? "a string"
            : underlying == typeof(bool) ? "a boolean"
            : underlying.IsEnum ? $"one of {string.Join(", ", Enum.GetNames(underlying).Select(SnakeCase))}"
            : underlying == typeof(Model) ? "a model name"
            : ElementTypeOf(underlying) is { } element ? $"a list of {Describe(element)}"
            : underlying.IsPrimitive || underlying == typeof(decimal) ? "a number"
            : underlying.Name;
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static object? ParseEnum(Type type, string text)
    {
        var wanted = Normalize(text);
        foreach (var candidate in Enum.GetNames(type))
        {
            if (Normalize(candidate) == wanted)
            {
                return Enum.Parse(type, candidate);
            }
        }

        return null;
    }

    private static object? ConvertNumber(object value, Type type)
    {
        object number = value switch
        {
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            string => throw new FormatException(),
            bool => throw new FormatException(),
            IEnumerable => throw new FormatException(),
            _ => value,
        };

        if (type == typeof(BigInteger))
        {
            return number switch
            {
                BigInteger big => big,
                long l => new BigInteger(l),
                double d => new BigInteger(d),
                _ => throw new FormatException(),
            };
        }

        if (number is BigInteger)
        {
            throw new OverflowException();
        }

        if (number is double d2 && type != typeof(double) && type != typeof(float) && type != typeof(decimal) && d2 != Math.Floor(d2))
        {
            throw new FormatException();
        }

        return System.Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
    }

    private static bool IsGenericDictionary(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || type.GetGenericTypeDefinition() == typeof(IDictionary<,>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
