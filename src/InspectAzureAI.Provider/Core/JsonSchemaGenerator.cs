using System.Collections;
using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Reflection port of <c>json_schema(t)</c> / <c>cls_json_schema(cls)</c> in <c>src/inspect_ai/util/_json.py</c>:
/// derives a <see cref="JsonSchema"/> for a CLR type the way Python derives one for a type hint.
/// <list type="bullet">
/// <item>integral types → <c>integer</c>; floating/decimal → <c>number</c>; <c>string</c>/<c>char</c> → <c>string</c>; <c>bool</c> → <c>boolean</c>;</item>
/// <item><see cref="DateTime"/>/<see cref="DateTimeOffset"/> → <c>string</c>/<c>date-time</c>, <see cref="DateOnly"/> → <c>date</c>, <see cref="TimeOnly"/> → <c>time</c>;</item>
/// <item><see cref="Nullable{T}"/> and nullable reference annotations → <c>anyOf [T, null]</c> (Python <c>Optional[T]</c>);</item>
/// <item>arrays, generic enumerables and tuples → <c>array</c> with <c>items</c>; dictionaries → <c>object</c> with <c>additionalProperties</c>;</item>
/// <item>enums → <c>string</c> with <c>enum</c> (member names, or <see cref="JsonStringEnumMemberNameAttribute"/> / <see cref="EnumMemberAttribute"/> values);</item>
/// <item>records, classes and structs → <c>object</c> with <c>properties</c> and <c>required</c> (the pydantic
/// <c>cls_json_schema</c> path): public readable properties, required when declared <c>required</c> or bound to a
/// constructor parameter without a default, <c>default</c> from the constructor parameter, <c>description</c> from
/// <see cref="DescriptionAttribute"/> on the property or its constructor parameter, names from <see cref="JsonPropertyNameAttribute"/>.
/// <c>additionalProperties: false</c> is set on every object <c>json_schema()</c> itself produces (a type passed in, or
/// reached through a collection, union or nullable), but not on objects nested inside another object's properties,
/// which come from pydantic's own schema in Python and carry no <c>additionalProperties</c>;</item>
/// <item><c>object</c>, <see cref="JsonNode"/>, System types with no natural mapping and recursive references → the empty schema (Python <c>Any</c>).</item>
/// </list>
/// Port-only extensions: <see cref="Guid"/> → <c>string</c>/<c>uuid</c>, <see cref="Uri"/> → <c>string</c>/<c>uri</c>,
/// <see cref="TimeSpan"/> → <c>string</c>/<c>duration</c>, <c>byte[]</c> → <c>string</c>/<c>byte</c> (how System.Text.Json serialises them).
/// </summary>
public static class JsonSchemaGenerator
{
    private static readonly JsonSerializerOptions DefaultValueOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly HashSet<Type> IntegerTypes =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(nint), typeof(nuint), typeof(BigInteger),
    ];

    private static readonly HashSet<Type> NumberTypes = [typeof(float), typeof(double), typeof(decimal), typeof(Half)];

    /// <summary>Port of <c>json_schema(t)</c> for <typeparamref name="T"/>.</summary>
    public static JsonSchema JsonSchemaOf<T>() => JsonSchemaOf(typeof(T));

    /// <summary>Port of <c>json_schema(t)</c>.</summary>
    public static JsonSchema JsonSchemaOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Schema(type, null, [], insideModel: false);
    }

    /// <summary>The schema of a parameter's type, honouring its nullable reference annotation.</summary>
    public static JsonSchema JsonSchemaOf(ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return Schema(parameter.ParameterType, new NullabilityInfoContext().Create(parameter), [], insideModel: false);
    }

    /// <summary>The schema of a property's type, honouring its nullable reference annotation.</summary>
    public static JsonSchema JsonSchemaOf(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Schema(property.PropertyType, new NullabilityInfoContext().Create(property), [], insideModel: false);
    }

    /// <summary>Port of <c>cls_json_schema(cls)</c>: the object schema of a record, class or struct.</summary>
    public static JsonSchema ClsJsonSchema(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ObjectSchema(type, [], insideModel: false);
    }

    /// <summary>
    /// Port of <c>python_type_to_json_type</c>: maps a Python type name (<c>str</c>, <c>int</c>, <c>float</c>,
    /// <c>bool</c>, <c>list</c>, <c>dict</c>, <c>None</c>) to a JSON type; null is treated as <c>string</c> and any
    /// other name is an <see cref="ArgumentException"/>.
    /// </summary>
    public static string PythonTypeToJsonType(string? pythonType) => pythonType switch
    {
        "str" => "string",
        "int" => "integer",
        "float" => "number",
        "bool" => "boolean",
        "list" => "array",
        "dict" => "object",
        "None" => "null",
        null => "string",
        _ => throw new ArgumentException($"Unsupported type: {pythonType} for Python to JSON conversion.", nameof(pythonType)),
    };

    private static JsonSchema Schema(Type type, NullabilityInfo? nullability, HashSet<Type> visiting, bool insideModel)
    {
        if (nullability is { ReadState: NullabilityState.Nullable } && !type.IsValueType)
        {
            return new JsonSchema { AnyOf = [Schema(type, null, visiting, insideModel), JsonSchema.Of("null")] };
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return new JsonSchema { AnyOf = [Schema(underlying, null, visiting, insideModel), JsonSchema.Of("null")] };
        }

        if (IntegerTypes.Contains(type))
        {
            return JsonSchema.Of("integer");
        }

        if (NumberTypes.Contains(type))
        {
            return JsonSchema.Of("number");
        }

        if (type == typeof(string) || type == typeof(char))
        {
            return JsonSchema.Of("string");
        }

        if (type == typeof(bool))
        {
            return JsonSchema.Of("boolean");
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new JsonSchema { Type = ["string"], Format = "date-time" };
        }

        if (type == typeof(DateOnly))
        {
            return new JsonSchema { Type = ["string"], Format = "date" };
        }

        if (type == typeof(TimeOnly))
        {
            return new JsonSchema { Type = ["string"], Format = "time" };
        }

        if (type == typeof(TimeSpan))
        {
            return new JsonSchema { Type = ["string"], Format = "duration" };
        }

        if (type == typeof(Guid))
        {
            return new JsonSchema { Type = ["string"], Format = "uuid" };
        }

        if (type == typeof(Uri))
        {
            return new JsonSchema { Type = ["string"], Format = "uri" };
        }

        if (type == typeof(byte[]))
        {
            return new JsonSchema { Type = ["string"], Format = "byte" };
        }

        if (type.IsEnum)
        {
            return EnumSchema(type);
        }

        if (type == typeof(JsonObject))
        {
            return new JsonSchema { Type = ["object"], AdditionalProperties = new JsonSchema() };
        }

        if (type == typeof(JsonArray))
        {
            return new JsonSchema { Type = ["array"], Items = new JsonSchema() };
        }

        if (type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonElement) || type == typeof(JsonDocument))
        {
            return new JsonSchema();
        }

        if (DictionaryValueType(type) is var (isDictionary, valueType, valueIndex) && isDictionary)
        {
            return new JsonSchema
            {
                Type = ["object"],
                AdditionalProperties = valueType is null ? new JsonSchema() : Schema(valueType, GenericArgumentNullability(nullability, valueIndex), visiting, insideModel),
            };
        }

        if (type.IsArray)
        {
            return new JsonSchema { Type = ["array"], Items = Schema(type.GetElementType()!, nullability?.ElementType, visiting, insideModel) };
        }

        if (typeof(ITuple).IsAssignableFrom(type))
        {
            var first = type.IsGenericType ? type.GetGenericArguments()[0] : null;
            return new JsonSchema { Type = ["array"], Items = first is null ? new JsonSchema() : Schema(first, GenericArgumentNullability(nullability, 0), visiting, insideModel) };
        }

        if (EnumerableElementType(type) is var (isEnumerable, elementType, elementIndex) && isEnumerable)
        {
            return new JsonSchema
            {
                Type = ["array"],
                Items = elementType is null ? new JsonSchema() : Schema(elementType, GenericArgumentNullability(nullability, elementIndex), visiting, insideModel),
            };
        }

        if (typeof(Delegate).IsAssignableFrom(type) || type.IsPointer || type.IsByRef || IsSystemType(type))
        {
            return new JsonSchema();
        }

        return ObjectSchema(type, visiting, insideModel);
    }

    private static JsonSchema EnumSchema(Type type)
    {
        var values = new List<JsonNode?>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                       ?? field.GetCustomAttribute<EnumMemberAttribute>()?.Value
                       ?? field.Name;
            values.Add(JsonValue.Create(name));
        }

        return values.Count > 0 ? new JsonSchema { Type = ["string"], Enum = values } : new JsonSchema { Enum = values };
    }

    private static JsonSchema ObjectSchema(Type type, HashSet<Type> visiting, bool insideModel)
    {
        if (!visiting.Add(type))
        {
            return new JsonSchema();
        }

        try
        {
            var properties = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
            var required = new List<string>();
            var nullabilityContext = new NullabilityInfoContext();
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            var parameters = constructor?.GetParameters().ToDictionary(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase)
                             ?? new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true)
                {
                    continue;
                }

                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }

                parameters.TryGetValue(property.Name, out var parameter);
                var schema = Schema(property.PropertyType, nullabilityContext.Create(property), visiting, insideModel: true);
                var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description
                                  ?? parameter?.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (description is not null)
                {
                    schema = schema with { Description = description };
                }

                if (parameter is { HasDefaultValue: true, DefaultValue: { } defaultValue })
                {
                    schema = schema with { Default = ToJsonNode(defaultValue) };
                }

                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
                properties[name] = schema;
                var isRequired = property.GetCustomAttribute<RequiredMemberAttribute>() is not null
                                 || parameter is { HasDefaultValue: false };
                if (isRequired)
                {
                    required.Add(name);
                }
            }

            return new JsonSchema
            {
                Type = ["object"],
                Properties = properties,
                Required = required.Count > 0 ? required : null,
                AdditionalProperties = insideModel ? null : false,
            };
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    /// <summary>A CLR value as a JSON node (enums by name), for <c>default</c> entries.</summary>
    public static JsonNode? ToJsonNode(object? value) =>
        value is null ? null : value as JsonNode ?? JsonSerializer.SerializeToNode(value, value.GetType(), DefaultValueOptions);

    private static NullabilityInfo? GenericArgumentNullability(NullabilityInfo? nullability, int index) =>
        nullability is { GenericTypeArguments.Length: > 0 } && index < nullability.GenericTypeArguments.Length ? nullability.GenericTypeArguments[index] : null;

    private static (bool IsDictionary, Type? ValueType, int ValueIndex) DictionaryValueType(Type type)
    {
        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType)
            {
                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    var valueIndex = type.IsGenericType && type.GetGenericArguments().Length == 2 ? 1 : 0;
                    return (true, candidate.GetGenericArguments()[1], valueIndex);
                }
            }
        }

        return typeof(IDictionary).IsAssignableFrom(type) ? (true, null, 0) : (false, null, 0);
    }

    private static (bool IsEnumerable, Type? ElementType, int ElementIndex) EnumerableElementType(Type type)
    {
        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return (true, candidate.GetGenericArguments()[0], 0);
            }
        }

        return typeof(IEnumerable).IsAssignableFrom(type) ? (true, null, 0) : (false, null, 0);
    }

    private static IEnumerable<Type> Interfaces(Type type)
    {
        if (type.IsInterface)
        {
            yield return type;
        }

        foreach (var candidate in type.GetInterfaces())
        {
            yield return candidate;
        }
    }

    /// <summary>System types with no natural JSON mapping (streams, tasks, reflection objects, ...) are the empty schema.</summary>
    private static bool IsSystemType(Type type) =>
        type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)) && !type.IsEnum;
}
