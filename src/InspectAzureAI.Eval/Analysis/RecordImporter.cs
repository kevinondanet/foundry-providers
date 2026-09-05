using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/record.py</c> (<c>import_record</c>, value coercion, wildcard expansion,
/// duplicate resolution) and the column-ordering helpers of <c>util.py</c>.
/// </summary>
internal static partial class RecordImporter
{
    /// <summary>
    /// Port of <c>import_record</c>: one row (column name → cell) read from <paramref name="target"/>. In strict mode
    /// the first error is a <see cref="ColumnImportException"/>; otherwise errors are appended to
    /// <paramref name="errors"/> and the column is skipped.
    /// </summary>
    public static Dictionary<string, object?> Import(EvalLog log, ImportTarget target, IReadOnlyList<Column> columns, bool strict, List<ColumnError> errors)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        void Fail(ColumnError error)
        {
            if (strict)
            {
                throw new ColumnImportException(error);
            }

            errors.Add(error);
        }

        void SetResult(string name, Column column, JsonNode? value)
        {
            try
            {
                result[name] = ResolveValue(value, column.Type);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidCastException)
            {
                Fail(new ColumnError(name, column.Path, ex, log));
            }
        }

        foreach (var column in columns)
        {
            JsonNode? value;
            try
            {
                value = column.Path is not null ? JsonPath.FindFirst(column.PathRecord(target), column.Path) : column.Extract(target);
                if (value is not null)
                {
                    value = column.Value(value);
                }
            }
            catch (Exception ex) when (ex is not ColumnImportException and not OperationCanceledException)
            {
                Fail(new ColumnError(column.Name, column.Path, ex, log));
                continue;
            }

            if (value is null && column.Default is not null)
            {
                value = column.Default;
            }

            if (column.Required && value is null)
            {
                Fail(new ColumnError(column.Name, column.Path, new KeyNotFoundException("field not found"), log));
            }

            if (column.Name.EndsWith('*'))
            {
                var values = value is JsonArray array ? array.ToList() : [value];
                foreach (var item in values)
                {
                    foreach (var (name, expanded) in ExpandFields(column.Name, item))
                    {
                        SetResult(name, column, expanded);
                    }
                }
            }
            else
            {
                SetResult(column.Name, column, value);
            }
        }

        return result;
    }

    /// <summary>Port of <c>resolve_duplicate_columns</c>: a repeated name keeps the later definition (in its later position).</summary>
    public static List<Column> ResolveDuplicateColumns(IEnumerable<Column> columns)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Column>();
        foreach (var column in columns.Reverse())
        {
            if (seen.Add(column.Name))
            {
                deduped.Add(column);
            }
        }

        deduped.Reverse();
        return deduped;
    }

    /// <summary>
    /// Port of <c>_expand_fields</c>: a name with <c>*</c> wildcards expanded over the keys of a dictionary value,
    /// one wildcard per nesting level (<c>foo_*_*</c> over <c>{"a": {"x": 1}}</c> → <c>foo_a_x</c>). A wildcard over a
    /// non-dictionary expands to nothing; a name without wildcards maps to the value itself.
    /// </summary>
    public static List<KeyValuePair<string, JsonNode?>> ExpandFields(string name, JsonNode? value)
    {
        var result = new List<KeyValuePair<string, JsonNode?>>();
        var asterisk = name.IndexOf('*', StringComparison.Ordinal);
        if (asterisk < 0)
        {
            result.Add(new KeyValuePair<string, JsonNode?>(name, value));
            return result;
        }

        if (value is not JsonObject obj)
        {
            return result;
        }

        var prefix = name[..asterisk];
        var suffix = name[(asterisk + 1)..];
        foreach (var (key, item) in obj)
        {
            var field = prefix + key + suffix;
            if (suffix.Contains('*'))
            {
                if (item is JsonObject)
                {
                    result.AddRange(ExpandFields(field, item));
                }
            }
            else
            {
                result.Add(new KeyValuePair<string, JsonNode?>(field, item));
            }
        }

        return result;
    }

    /// <summary>
    /// Port of <c>_resolve_value</c>: a JSON value as a cell, coerced to <paramref name="type"/> when given. Lists and
    /// dictionaries become JSON text (for no type or <see cref="ColumnType.String"/>); scalars coerce like Python's
    /// constructors, strings YAML-style first (<c>"true"</c>, <c>"1.5"</c>, ISO dates), numbers to temporal types as
    /// POSIX timestamps. An impossible coercion is a <see cref="FormatException"/>.
    /// </summary>
    public static object? ResolveValue(JsonNode? value, ColumnType? type)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonArray or JsonObject)
        {
            if (type is null or ColumnType.String)
            {
                return PythonFormat.Dumps(value);
            }

            throw new FormatException($"Cannot coerce {PythonFormat.Dumps(value)} from type {(value is JsonArray ? "list" : "dict")} to {TypeName(type.Value)}");
        }

        var scalar = PythonFormat.Scalar((JsonValue)value);
        if (type is null)
        {
            return scalar;
        }

        var coerced = Coerce(scalar, type.Value);
        return coerced ?? throw new FormatException($"Cannot coerce {Table.FormatCell(scalar)} from type {ScalarTypeName(scalar)} to {TypeName(type.Value)}");
    }

    /// <summary>
    /// Port of <c>resolve_columns</c>: the actual column names a specification name (possibly with wildcards) selects,
    /// preferring the suffixed form (<c>name{suffix}</c>) a merge may have produced; wildcard matches are sorted.
    /// </summary>
    public static List<string> ResolveColumns(string pattern, string suffix, IReadOnlyList<string> columns, IReadOnlyCollection<string> processed)
    {
        var resolved = new List<string>();
        if (!pattern.Contains('*'))
        {
            var withSuffix = pattern + suffix;
            if (columns.Contains(withSuffix) && !processed.Contains(withSuffix))
            {
                resolved.Add(withSuffix);
            }
            else if (columns.Contains(pattern) && !processed.Contains(pattern))
            {
                resolved.Add(pattern);
            }
        }
        else
        {
            var matched = MatchColumnPattern(pattern + suffix, columns, processed).Concat(MatchColumnPattern(pattern, columns, processed));
            resolved.AddRange(matched.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
        }

        return resolved;
    }

    /// <summary>Port of <c>match_col_pattern</c>.</summary>
    public static List<string> MatchColumnPattern(string pattern, IReadOnlyList<string> columns, IReadOnlyCollection<string> processed)
    {
        var regex = new Regex("^" + string.Concat(Regex.Split(pattern, @"(\*)").Select(part => part == "*" ? ".*" : Regex.Escape(part))) + "$", RegexOptions.Singleline);
        return columns.Where(column => regex.IsMatch(column) && !processed.Contains(column)).ToList();
    }

    /// <summary>Port of <c>add_unreferenced_columns</c>: the referenced columns followed by the rest, sorted.</summary>
    public static List<string> AddUnreferencedColumns(IReadOnlyList<string> columns, IReadOnlyList<string> referenced)
    {
        var set = referenced.ToHashSet(StringComparer.Ordinal);
        return [.. referenced, .. columns.Where(column => !set.Contains(column)).OrderBy(column => column, StringComparer.Ordinal)];
    }

    private static object? Coerce(object scalar, ColumnType type) => type switch
    {
        ColumnType.Int => scalar switch
        {
            bool b => b ? 1L : 0L,
            long l => l,
            double d => double.IsFinite(d) ? (long)Math.Truncate(d) : null,
            string s => ParseYaml(s) switch
            {
                long l => l,
                double d when double.IsFinite(d) => (long)Math.Truncate(d),
                bool b => b ? 1L : 0L,
                _ => null,
            },
            _ => null,
        },
        ColumnType.Float => scalar switch
        {
            bool b => b ? 1.0 : 0.0,
            long l => (double)l,
            double d => d,
            string s => ParseYaml(s) switch
            {
                long l => (double)l,
                double d => d,
                bool b => b ? 1.0 : 0.0,
                _ => null,
            },
            _ => null,
        },
        ColumnType.Bool => scalar switch
        {
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            string s => ParseYaml(s) switch
            {
                bool b => b,
                long l => l != 0,
                double d => d != 0,
                _ => null,
            },
            _ => null,
        },
        ColumnType.String => scalar switch
        {
            string s => s,
            bool b => b ? "True" : "False",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => PythonFormat.FloatRepr(d),
            _ => null,
        },
        ColumnType.DateTime or ColumnType.Date or ColumnType.Time => Temporal(scalar, type),
        _ => null,
    };

    private static object? Temporal(object scalar, ColumnType type)
    {
        DateTimeOffset? moment = scalar switch
        {
            long l => DateTimeOffset.FromUnixTimeMilliseconds(checked(l * 1000)),
            double d when double.IsFinite(d) => DateTimeOffset.UnixEpoch.AddTicks((long)(d * TimeSpan.TicksPerSecond)),
            string s => ParseTemporalString(s),
            _ => null,
        };
        if (moment is null)
        {
            return null;
        }

        var utc = moment.Value.ToUniversalTime();
        return type switch
        {
            ColumnType.DateTime => utc,
            ColumnType.Date => DateOnly.FromDateTime(utc.UtcDateTime),
            _ => TimeOnly.FromDateTime(utc.UtcDateTime),
        };
    }

    private static DateTimeOffset? ParseTemporalString(string text)
    {
        var trimmed = text.Trim();
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp) && double.IsFinite(timestamp))
        {
            return DateTimeOffset.UnixEpoch.AddTicks((long)(timestamp * TimeSpan.TicksPerSecond));
        }

        return null;
    }

    /// <summary>The YAML 1.1 scalar reading of a string (<c>yaml.safe_load</c>): booleans, integers, floats; anything else stays a string.</summary>
    private static object ParseYaml(string text)
    {
        var trimmed = text.Trim();
        switch (trimmed)
        {
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" or "y" or "Y":
                return true;
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" or "n" or "N":
                return false;
            default:
                break;
        }

        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (YamlFloat().IsMatch(trimmed) && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return trimmed switch
        {
            ".nan" or ".NaN" or ".NAN" => double.NaN,
            ".inf" or ".Inf" or ".INF" => double.PositiveInfinity,
            "-.inf" or "-.Inf" or "-.INF" => double.NegativeInfinity,
            _ => text,
        };
    }

    private static string TypeName(ColumnType type) => type switch
    {
        ColumnType.Int => "int",
        ColumnType.Float => "float",
        ColumnType.Bool => "bool",
        ColumnType.String => "str",
        ColumnType.Date => "date",
        ColumnType.Time => "time",
        _ => "datetime",
    };

    private static string ScalarTypeName(object scalar) => scalar switch
    {
        bool => "bool",
        long => "int",
        double => "float",
        _ => "str",
    };

    [GeneratedRegex(@"^[-+]?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?$")]
    private static partial Regex YamlFloat();
}
