using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Args;

/// <summary>A parsed <c>--limit</c>: a count, or Python's <c>(start - 1, stop)</c> range.</summary>
public readonly record struct SamplesLimit(int? Count, int? Start, int? Stop)
{
    public bool IsRange => Count is null;
}

/// <summary>
/// Ports of the CLI argument helpers: <c>_util/config.py</c> (<c>parse_cli_args</c>, <c>resolve_args</c>,
/// <c>read_config_object</c>), <c>_util/flag_values.py</c>, <c>_util/samples.py</c> and the parsers of <c>_cli/util.py</c>
/// and <c>_cli/eval.py</c> (<c>parse_comma_separated</c>, <c>parse_sandbox</c>).
/// </summary>
public static class CliArgs
{
    /// <summary>
    /// Port of <c>parse_cli_args</c>: each <c>key=value</c> argument (an argument without <c>=</c> is ignored, as in
    /// Python) is parsed as YAML; a string value is split on commas into a list when it holds more than one item;
    /// <c>-</c> in the key becomes <c>_</c>.
    /// </summary>
    public static Dictionary<string, object?> ParseCliArgs(IEnumerable<string>? args)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is null)
        {
            return parameters;
        }

        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = arg[..separator].Replace('-', '_');
            var value = YamlValue.Parse(arg[(separator + 1)..]);
            if (value is string text)
            {
                var parts = text.Split(',');
                value = parts.Length > 1 ? parts.Cast<object?>().ToList() : parts[0];
            }

            parameters[key] = value;
        }

        return parameters;
    }

    /// <summary>
    /// Parses the <c>--env NAME=value</c> entries of <c>process_common_options</c>: the name before the first <c>=</c>
    /// (<c>-</c> replaced by <c>_</c>, as <c>parse_cli_args</c> does), the value as the raw text after it, entries
    /// without <c>=</c> ignored. Deviation: Python YAML-parses the value and stores <c>str()</c> of the result, so
    /// <c>NAME=a,b</c> becomes <c>['a', 'b']</c>, <c>NAME=3.10</c> becomes <c>3.1</c> and <c>NAME=</c> becomes
    /// <c>None</c> — repr artifacts no consumer of an environment variable expects; the raw text is kept instead.
    /// </summary>
    public static Dictionary<string, string> ParseEnvArgs(IEnumerable<string>? args)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args is null)
        {
            return variables;
        }

        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            variables[arg[..separator].Replace('-', '_')] = arg[(separator + 1)..];
        }

        return variables;
    }

    /// <summary>Port of <c>parse_cli_config</c>: the config file's values (when given) overridden by the <c>key=value</c> arguments.</summary>
    public static Dictionary<string, object?> ParseCliConfig(IEnumerable<string>? args, string? configFile)
    {
        var config = configFile is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : ResolveArgs(configFile);
        foreach (var (key, value) in ParseCliArgs(args))
        {
            config[key] = value;
        }

        return config;
    }

    /// <summary>Port of <c>resolve_args</c> for a file path: a missing file is a <see cref="PrerequisiteError"/>; the content is read with <see cref="ReadConfigObject"/>.</summary>
    public static Dictionary<string, object?> ResolveArgs(string configFile)
    {
        ArgumentNullException.ThrowIfNull(configFile);
        if (!File.Exists(configFile))
        {
            throw new PrerequisiteError($"The config file {configFile} does not exist.");
        }

        return ReadConfigObject(File.ReadAllText(configFile));
    }

    /// <summary>Port of <c>read_config_object</c>: JSON when the text starts with <c>{</c>, else YAML; anything but a mapping is an <see cref="ArgumentException"/>.</summary>
    public static Dictionary<string, object?> ReadConfigObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        object? config = text.TrimStart().StartsWith('{') ? JsonToValue(JsonNode.Parse(text)) : YamlValue.Parse(text);
        return config as Dictionary<string, object?> ?? throw new ArgumentException($"The config is not a valid object: {text}");
    }

    /// <summary>Converts a JSON tree into the plain values <see cref="YamlValue"/> produces.</summary>
    public static object? JsonToValue(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => JsonToValue(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(JsonToValue).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.String => value.TryGetValue<string>(out var text) ? text : value.ToString(),
            JsonValueKind.Number => NumberOf(value),
            _ => throw new ArgumentException($"Unsupported JSON value: {node.ToJsonString()}"),
        },
        _ => throw new ArgumentException($"Unsupported JSON value: {node.ToJsonString()}"),
    };

    private static object NumberOf(JsonValue value)
    {
        var text = value.ToJsonString();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? (object)whole : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Converts a plain value back into a JSON node (the inverse of <see cref="JsonToValue"/>).</summary>
    public static JsonNode? ValueToJson(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        System.Numerics.BigInteger big => JsonValue.Create((double)big),
        string s => JsonValue.Create(s),
        IReadOnlyDictionary<string, object?> dict => new JsonObject(dict.Select(pair => KeyValuePair.Create(pair.Key, ValueToJson(pair.Value)))),
        IEnumerable<object?> list => new JsonArray(list.Select(ValueToJson).ToArray()),
        _ => JsonSerializer.SerializeToNode(value),
    };

    /// <summary>
    /// Port of <c>int_or_bool_value</c>: null (the bare flag) or <c>true</c>/<c>yes</c> (and <c>1</c> when
    /// <paramref name="isOneTrue"/>) is <paramref name="trueValue"/>, <c>false</c>/<c>no</c>/<c>0</c> is
    /// <paramref name="falseValue"/>, otherwise the integer; anything else is a <see cref="FormatException"/>.
    /// </summary>
    public static int IntOrBoolValue(string? value, int trueValue, int falseValue = 0, bool isOneTrue = true)
    {
        if (value is null)
        {
            return trueValue;
        }

        var lowered = value.ToLowerInvariant();
        if (lowered is "true" or "yes" || (isOneTrue && lowered == "1"))
        {
            return trueValue;
        }

        if (lowered is "false" or "no" or "0")
        {
            return falseValue;
        }

        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>Port of <c>int_bool_or_str_value</c>: like <see cref="IntOrBoolValue"/> but text that is not an integer is returned as itself (a config-file path).</summary>
    public static object? IntBoolOrStrValue(string? value, int trueValue, int? falseValue = null)
    {
        if (value is null)
        {
            return trueValue;
        }

        var lowered = value.ToLowerInvariant();
        if (lowered is "true" or "yes" or "1")
        {
            return trueValue;
        }

        if (lowered is "false" or "no" or "0")
        {
            return falseValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : value;
    }

    /// <summary>Port of <c>parse_samples_limit</c>: <c>10</c> is a count, <c>10-20</c> the range <c>(9, 20)</c>; anything else is a <see cref="FormatException"/>.</summary>
    public static SamplesLimit? ParseSamplesLimit(string? limit)
    {
        if (limit is null)
        {
            return null;
        }

        if (!limit.Contains('-', StringComparison.Ordinal))
        {
            return new SamplesLimit(ParseIntStrict(limit, "sample limit"), null, null);
        }

        var parts = limit.Split('-');
        if (parts.Length != 2)
        {
            throw new FormatException($"Invalid sample limit '{limit}': expected a single number or a range like '10-20'.");
        }

        return new SamplesLimit(null, ParseIntStrict(parts[0], "sample limit") - 1, ParseIntStrict(parts[1], "sample limit"));
    }

    /// <summary>Port of <c>parse_sample_id</c>: the comma-separated ids, each trimmed.</summary>
    public static IReadOnlyList<string>? ParseSampleId(string? sampleId) =>
        sampleId?.Split(',').Select(id => id.Trim()).ToList();

    /// <summary>Port of <c>parse_comma_separated</c>: a plain split on commas (no trimming, as in Python).</summary>
    public static IReadOnlyList<string>? ParseCommaSeparated(string? value) => value?.Split(',');

    /// <summary>Port of <c>parse_sandbox</c>: <c>type</c> or <c>type:config</c>.</summary>
    public static SandboxSpec? ParseSandbox(string? sandbox)
    {
        if (sandbox is null)
        {
            return null;
        }

        var parts = sandbox.Split(':', 2);
        return parts.Length == 1 ? new SandboxSpec(sandbox) : new SandboxSpec(parts[0], parts[1]);
    }

    private static int ParseIntStrict(string text, string what)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Invalid {what} '{text}': expected an integer.");
        }

        return value;
    }
}
