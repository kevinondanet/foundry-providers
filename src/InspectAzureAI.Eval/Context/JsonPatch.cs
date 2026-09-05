using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>jsonpatch.JsonPatchException</c>: the base of the JSON Patch errors.</summary>
public class JsonPatchException : Exception
{
    public JsonPatchException(string message)
        : base(message)
    {
    }

    public JsonPatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Port of <c>jsonpatch.InvalidJsonPatch</c>: the patch document itself is malformed.</summary>
public sealed class InvalidJsonPatchException(string message) : JsonPatchException(message);

/// <summary>Port of <c>jsonpatch.JsonPatchConflict</c>: an operation cannot be applied to the document.</summary>
public sealed class JsonPatchConflictException(string message) : JsonPatchException(message);

/// <summary>Port of <c>jsonpatch.JsonPatchTestFailed</c>: a <c>test</c> operation did not match the document.</summary>
public sealed class JsonPatchTestFailedException(string message) : JsonPatchException(message);

/// <summary>Port of <c>jsonpointer.JsonPointerException</c>: a pointer cannot be resolved against a document.</summary>
public sealed class JsonPointerException(string message) : Exception(message);

/// <summary>
/// Port of <c>jsonpointer.JsonPointer</c> (RFC 6901) over <see cref="JsonNode"/> documents: parsing with
/// <c>~0</c> / <c>~1</c> unescaping, <see cref="Resolve"/>, <see cref="ToLast"/> and <see cref="Contains"/>,
/// with the same error cases (a location not starting with <c>/</c>, a missing member, an index out of bounds,
/// a non-numeric array index, indexing into a scalar).
/// </summary>
public sealed class JsonPointer : IEquatable<JsonPointer>
{
    private readonly List<string> _parts;

    public JsonPointer(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        var parts = pointer.Split('/');
        if (parts[0] != "")
        {
            throw new JsonPointerException("Location must start with /");
        }

        _parts = parts.Skip(1).Select(Unescape).ToList();
    }

    public JsonPointer(IEnumerable<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = parts.ToList();
    }

    /// <summary>The unescaped reference tokens (empty for the root pointer <c>""</c>).</summary>
    public IReadOnlyList<string> Parts => _parts;

    /// <summary>The escaped pointer string (Python's <c>JsonPointer.path</c>).</summary>
    public string Path => Join(_parts);

    public static string Escape(string part) => part.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static string Unescape(string part) => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>Joins unescaped tokens into a pointer string.</summary>
    public static string Join(IEnumerable<string> parts) => string.Concat(parts.Select(part => "/" + Escape(part)));

    /// <summary>
    /// Port of <c>to_last</c>: the container of the last token and the token itself (validated as an array index
    /// when the container is an array); <c>(document, null)</c> for the root pointer.
    /// </summary>
    public (JsonNode? Container, string? Part) ToLast(JsonNode? document)
    {
        if (_parts.Count == 0)
        {
            return (document, null);
        }

        for (var i = 0; i < _parts.Count - 1; i++)
        {
            document = Walk(document, _parts[i]);
        }

        return (document, GetPart(document, _parts[^1]));
    }

    /// <summary>Port of <c>resolve</c>: the node the pointer refers to, or a <see cref="JsonPointerException"/>.</summary>
    public JsonNode? Resolve(JsonNode? document)
    {
        foreach (var part in _parts)
        {
            document = Walk(document, part);
        }

        return document;
    }

    /// <summary>Port of <c>contains</c>: whether this pointer is <paramref name="other"/> or lies beneath it.</summary>
    public bool Contains(JsonPointer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other._parts.Count <= _parts.Count && _parts.Take(other._parts.Count).SequenceEqual(other._parts, StringComparer.Ordinal);
    }

    public bool Equals(JsonPointer? other) => other is not null && _parts.SequenceEqual(other._parts, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as JsonPointer);

    public override int GetHashCode() => string.GetHashCode(Path, StringComparison.Ordinal);

    public override string ToString() => Path;

    /// <summary>Port of <c>get_part</c>: validates <paramref name="part"/> against the container kind without indexing.</summary>
    internal static string GetPart(JsonNode? document, string part) => document switch
    {
        JsonObject => part,
        JsonArray => part == "-" || IsIndex(part) ? part : throw new JsonPointerException($"'{part}' is not a valid sequence index"),
        _ => throw new JsonPointerException($"Document '{Describe(document)}' does not support indexing, must be mapping/sequence"),
    };

    /// <summary>Port of <c>walk</c>: one step into <paramref name="document"/>.</summary>
    internal static JsonNode? Walk(JsonNode? document, string part)
    {
        switch (document)
        {
            case JsonObject obj:
                return obj.TryGetPropertyValue(part, out var member) ? member : throw new JsonPointerException($"member '{part}' not found in {obj.ToJsonString()}");
            case JsonArray array:
                if (!TryIndex(GetPart(array, part), out var index) || index >= array.Count)
                {
                    throw new JsonPointerException($"index '{part}' is out of bounds");
                }

                return array[index];
            default:
                throw new JsonPointerException($"Document '{Describe(document)}' does not support indexing, must be mapping/sequence");
        }
    }

    internal static bool IsIndex(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);

    internal static bool TryIndex(string part, out int index) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out index) && IsIndex(part);

    private static string Describe(JsonNode? document) => document is null ? "null" : JsonValues.Kind(document).ToString().ToLowerInvariant();
}

/// <summary>
/// Port of <c>jsonpatch.apply_patch</c> and the six operations (<c>add</c>, <c>remove</c>, <c>replace</c>,
/// <c>move</c>, <c>copy</c>, <c>test</c>) over <see cref="JsonNode"/> documents, with the library's error
/// behaviour: a conflict for a missing member or an index outside the list, an invalid patch for a <c>replace</c>
/// of <c>-</c> or a <c>move</c> / <c>copy</c> without <c>from</c>, a test failure for a mismatched <c>test</c>.
/// </summary>
public static class JsonPatch
{
    /// <summary>
    /// Applies <paramref name="patch"/> in order and returns the resulting document (a new root when an operation
    /// replaces the root). The input is deep-cloned first unless <paramref name="inPlace"/> is set.
    /// </summary>
    public static JsonNode? Apply(JsonNode? document, IEnumerable<JsonChange> patch, bool inPlace = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var result = inPlace ? document : document?.DeepClone();
        foreach (var change in patch)
        {
            ArgumentNullException.ThrowIfNull(change, nameof(patch));
            result = ApplyOperation(result, change);
        }

        return result;
    }

    internal static JsonNode? ApplyOperation(JsonNode? document, JsonChange change)
    {
        var pointer = new JsonPointer(change.Path);
        return change.Op switch
        {
            JsonChangeOp.Remove => Remove(document, pointer),
            JsonChangeOp.Add => Add(document, pointer, change.Value?.DeepClone()),
            JsonChangeOp.Replace => Replace(document, pointer, change.Value?.DeepClone()),
            JsonChangeOp.Move => Move(document, pointer, FromPointer(change)),
            JsonChangeOp.Copy => Add(document, pointer, Get(document, FromPointer(change))?.DeepClone()),
            JsonChangeOp.Test => Test(document, pointer, change.Value),
            _ => throw new InvalidJsonPatchException($"Unknown operation {change.Op}"),
        };
    }

    private static JsonPointer FromPointer(JsonChange change) =>
        change.From is { } from ? new JsonPointer(from) : throw new InvalidJsonPatchException("The operation does not contain a 'from' member");

    private static JsonNode? Remove(JsonNode? document, JsonPointer pointer)
    {
        var (container, part) = pointer.ToLast(document);
        switch (container)
        {
            case JsonObject obj when part is not null && obj.Remove(part):
                return document;
            case JsonArray array when part is not null && JsonPointer.TryIndex(part, out var index) && index < array.Count:
                array.RemoveAt(index);
                return document;
            default:
                throw new JsonPatchConflictException($"can't remove a non-existent object '{part}'");
        }
    }

    /// <summary>A root pointer replaces the whole document (Python's dict-root behaviour, extended to every root).</summary>
    private static JsonNode? Add(JsonNode? document, JsonPointer pointer, JsonNode? value)
    {
        var (container, part) = pointer.ToLast(document);
        if (part is null)
        {
            return value;
        }

        switch (container)
        {
            case JsonArray array:
                if (part == "-")
                {
                    array.Add(value);
                }
                else
                {
                    var index = int.Parse(part, CultureInfo.InvariantCulture);
                    if (index > array.Count)
                    {
                        throw new JsonPatchConflictException("can't insert outside of list");
                    }

                    array.Insert(index, value);
                }

                return document;
            case JsonObject obj:
                obj[part] = value;
                return document;
            default:
                throw new JsonPatchConflictException($"unable to fully resolve json pointer {pointer.Path}, part {part}");
        }
    }

    private static JsonNode? Replace(JsonNode? document, JsonPointer pointer, JsonNode? value)
    {
        var (container, part) = pointer.ToLast(document);
        if (part is null)
        {
            return value;
        }

        if (part == "-")
        {
            throw new InvalidJsonPatchException("'path' with '-' can't be applied to 'replace' operation");
        }

        switch (container)
        {
            case JsonArray array:
                var index = int.Parse(part, CultureInfo.InvariantCulture);
                if (index >= array.Count)
                {
                    throw new JsonPatchConflictException("can't replace outside of list");
                }

                array[index] = value;
                return document;
            case JsonObject obj:
                if (!obj.ContainsKey(part))
                {
                    throw new JsonPatchConflictException($"can't replace a non-existent object '{part}'");
                }

                obj[part] = value;
                return document;
            default:
                throw new JsonPatchConflictException($"unable to fully resolve json pointer {pointer.Path}, part {part}");
        }
    }

    private static JsonNode? Move(JsonNode? document, JsonPointer pointer, JsonPointer from)
    {
        var (container, part) = from.ToLast(document);
        var value = part is null ? document : Get(container, part);
        if (pointer.Equals(from))
        {
            return document;
        }

        if (container is JsonObject && pointer.Contains(from))
        {
            throw new JsonPatchConflictException("Cannot move values into their own children");
        }

        document = Remove(document, from);
        return Add(document, pointer, value);
    }

    private static JsonNode? Test(JsonNode? document, JsonPointer pointer, JsonNode? expected)
    {
        JsonNode? actual;
        try
        {
            var (container, part) = pointer.ToLast(document);
            actual = part is null ? container : JsonPointer.Walk(container, part);
        }
        catch (JsonPointerException ex)
        {
            throw new JsonPatchTestFailedException(ex.Message);
        }

        if (!JsonValues.PythonEquals(actual, expected))
        {
            throw new JsonPatchTestFailedException(
                $"{Text(actual)} ({JsonValues.Kind(actual)}) is not equal to tested value {Text(expected)} ({JsonValues.Kind(expected)})");
        }

        return document;
    }

    private static JsonNode? Get(JsonNode? document, JsonPointer pointer)
    {
        var (container, part) = pointer.ToLast(document);
        return part is null ? document : Get(container, part);
    }

    /// <summary>Port of the <c>subobj[part]</c> lookups of <c>move</c> / <c>copy</c>: a missing key or index is a conflict.</summary>
    private static JsonNode? Get(JsonNode? container, string part)
    {
        switch (container)
        {
            case JsonObject obj when obj.TryGetPropertyValue(part, out var member):
                return member;
            case JsonArray array when JsonPointer.TryIndex(part, out var index) && index < array.Count:
                return array[index];
            default:
                throw new JsonPatchConflictException($"'{part}'");
        }
    }

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";
}

/// <summary>The JSON kinds Python distinguishes when diffing: <c>int</c> and <c>float</c> are separate, as are <c>bool</c> and numbers.</summary>
public enum JsonKind
{
    Null,
    Bool,
    Int,
    Float,
    String,
    Array,
    Object,
}

/// <summary>
/// The Python value semantics the diff relies on: <see cref="Kind"/> (the JSON type, with <c>int</c> / <c>float</c>
/// told apart the way <c>json.loads</c> does), <see cref="PythonEquals"/> (Python <c>==</c>, where
/// <c>1 == 1.0 == True</c> and dictionaries compare regardless of key order), <see cref="DumpsEquals"/> (equality
/// of <c>json.dumps</c> output, where <c>1</c>, <c>1.0</c> and <c>true</c> all differ and key order matters) and
/// <see cref="TypedEquals"/> (the <c>(value, type(value))</c> key jsonpatch uses to detect moves).
/// </summary>
public static class JsonValues
{
    public static JsonKind Kind(JsonNode? node) => node switch
    {
        null => JsonKind.Null,
        JsonObject => JsonKind.Object,
        JsonArray => JsonKind.Array,
        JsonValue value => ScalarKind(value),
        _ => throw new InvalidOperationException($"Unsupported JSON node {node.GetType().Name}."),
    };

    public static bool PythonEquals(JsonNode? a, JsonNode? b)
    {
        var ka = Kind(a);
        var kb = Kind(b);
        if (ka == JsonKind.Null || kb == JsonKind.Null)
        {
            return ka == kb;
        }

        if (IsNumeric(ka) && IsNumeric(kb))
        {
            var na = Number((JsonValue)a!);
            var nb = Number((JsonValue)b!);
            return na.IsInteger && nb.IsInteger ? na.Integer == nb.Integer : na.AsDouble == nb.AsDouble;
        }

        if (ka != kb)
        {
            return false;
        }

        switch (ka)
        {
            case JsonKind.String:
                return string.Equals(StringOf((JsonValue)a!), StringOf((JsonValue)b!), StringComparison.Ordinal);
            case JsonKind.Array:
                var arrayA = (JsonArray)a!;
                var arrayB = (JsonArray)b!;
                return arrayA.Count == arrayB.Count && arrayA.Zip(arrayB).All(pair => PythonEquals(pair.First, pair.Second));
            default:
                var objA = (JsonObject)a!;
                var objB = (JsonObject)b!;
                return objA.Count == objB.Count && objA.All(pair => objB.TryGetPropertyValue(pair.Key, out var other) && PythonEquals(pair.Value, other));
        }
    }

    public static bool DumpsEquals(JsonNode? a, JsonNode? b)
    {
        var kind = Kind(a);
        if (kind != Kind(b))
        {
            return false;
        }

        switch (kind)
        {
            case JsonKind.Null:
                return true;
            case JsonKind.Bool:
                return ((JsonValue)a!).GetValue<bool>() == ((JsonValue)b!).GetValue<bool>();
            case JsonKind.Int:
                return Number((JsonValue)a!).Integer == Number((JsonValue)b!).Integer;
            case JsonKind.Float:
                var fa = Number((JsonValue)a!).Float;
                var fb = Number((JsonValue)b!).Float;
                return (double.IsNaN(fa) && double.IsNaN(fb)) || BitConverter.DoubleToInt64Bits(fa) == BitConverter.DoubleToInt64Bits(fb);
            case JsonKind.String:
                return string.Equals(StringOf((JsonValue)a!), StringOf((JsonValue)b!), StringComparison.Ordinal);
            case JsonKind.Array:
                var arrayA = (JsonArray)a!;
                var arrayB = (JsonArray)b!;
                return arrayA.Count == arrayB.Count && arrayA.Zip(arrayB).All(pair => DumpsEquals(pair.First, pair.Second));
            default:
                var objA = (JsonObject)a!;
                var objB = (JsonObject)b!;
                return objA.Count == objB.Count
                    && objA.Zip(objB).All(pair => string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) && DumpsEquals(pair.First.Value, pair.Second.Value));
        }
    }

    public static bool TypedEquals(JsonNode? a, JsonNode? b) => Kind(a) == Kind(b) && PythonEquals(a, b);

    private static bool IsNumeric(JsonKind kind) => kind is JsonKind.Bool or JsonKind.Int or JsonKind.Float;

    private static JsonKind ScalarKind(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return ElementKind(element);
        }

        return value.GetValue<object>() switch
        {
            bool => JsonKind.Bool,
            string or char => JsonKind.String,
            int or long or short or byte or sbyte or ushort or uint or ulong or BigInteger => JsonKind.Int,
            double or float or decimal => JsonKind.Float,
            var other => ElementKind(JsonSerializer.SerializeToElement(other)),
        };
    }

    private static JsonKind ElementKind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => JsonKind.Bool,
        JsonValueKind.String => JsonKind.String,
        JsonValueKind.Number => IsFloatText(element.GetRawText()) ? JsonKind.Float : JsonKind.Int,
        JsonValueKind.Object => JsonKind.Object,
        JsonValueKind.Array => JsonKind.Array,
        _ => JsonKind.Null,
    };

    private static bool IsFloatText(string raw) => raw.Contains('.') || raw.Contains('e') || raw.Contains('E');

    internal static JsonNumber Number(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            var raw = element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.ValueKind == JsonValueKind.True ? "1" : "0";
            return IsFloatText(raw)
                ? new JsonNumber(false, BigInteger.Zero, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture))
                : new JsonNumber(true, BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), 0);
        }

        return value.GetValue<object>() switch
        {
            bool b => new JsonNumber(true, b ? BigInteger.One : BigInteger.Zero, 0),
            int i => new JsonNumber(true, i, 0),
            long l => new JsonNumber(true, l, 0),
            short s => new JsonNumber(true, s, 0),
            byte by => new JsonNumber(true, by, 0),
            sbyte sb => new JsonNumber(true, sb, 0),
            ushort us => new JsonNumber(true, us, 0),
            uint ui => new JsonNumber(true, ui, 0),
            ulong ul => new JsonNumber(true, ul, 0),
            BigInteger big => new JsonNumber(true, big, 0),
            double d => new JsonNumber(false, BigInteger.Zero, d),
            float f => new JsonNumber(false, BigInteger.Zero, f),
            decimal m => new JsonNumber(false, BigInteger.Zero, (double)m),
            var other => throw new InvalidOperationException($"{other.GetType().Name} is not a JSON number."),
        };
    }

    private static string StringOf(JsonValue value) =>
        value.TryGetValue<string>(out var text) ? text : value.GetValue<object>().ToString() ?? "";
}

/// <summary>A JSON number as Python sees it: an arbitrary-precision <c>int</c> or a <c>float</c>.</summary>
internal readonly record struct JsonNumber(bool IsInteger, BigInteger Integer, double Float)
{
    public double AsDouble => IsInteger ? (double)Integer : Float;
}
