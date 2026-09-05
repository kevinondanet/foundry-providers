using InspectAzureAI.Provider.Core;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Provider.Util;

/// <summary>Port of the helpers in <c>src/inspect_ai/model/_providers/util/util.py</c> used by the azureai provider.</summary>
public static class ProviderUtil
{
    /// <summary>Env var consulted last by <see cref="ModelBaseUrl"/>.</summary>
    public const string InspectEvalModelBaseUrl = "INSPECT_EVAL_MODEL_BASE_URL";

    /// <summary>
    /// Port of <c>normalize_stream_arg</c>: null → null (auto); bool → itself; strings
    /// (<c>auto</c>/<c>true</c>/<c>false</c>, case-insensitive, trimmed) → null/true/false; anything
    /// else raises <see cref="ArgumentException"/> with the Python message.
    /// </summary>
    public static bool? NormalizeStreamArg(object? value, string argName = "stream")
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return b;
            case string s:
                var lowered = s.Trim().ToLowerInvariant();
                switch (lowered)
                {
                    case "auto":
                        return null;
                    case "true":
                        return true;
                    case "false":
                        return false;
                }

                break;
        }

        throw new ArgumentException(
            $"Unrecognized value for the {argName} model arg: {PythonRepr(value)} (expected true, false, or \"auto\")");
    }

    /// <summary>
    /// Port of <c>model_base_url</c>: explicit value first, then the env vars in order, then
    /// <c>INSPECT_EVAL_MODEL_BASE_URL</c>. Empty strings are falsy, like Python.
    /// </summary>
    public static string? ModelBaseUrl(string? baseUrl, IReadOnlyList<string> envVars)
    {
        if (!string.IsNullOrEmpty(baseUrl))
        {
            return baseUrl;
        }

        foreach (var envVar in envVars)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return Environment.GetEnvironmentVariable(InspectEvalModelBaseUrl) is { Length: > 0 } fallback ? fallback : null;
    }

    /// <summary>Port of <c>environment_prerequisite_error</c> (including the Rich markup and Oxford comma).</summary>
    public static PrerequisiteError EnvironmentPrerequisiteError(string client, IReadOnlyList<string> envVars)
    {
        static string Fmt(string key) => $"[bold][blue]{key}[/blue][/bold]";

        string list;
        if (envVars.Count == 1)
        {
            list = Fmt(envVars[0]);
        }
        else
        {
            list = string.Join(", ", envVars.Take(envVars.Count - 1).Select(Fmt))
                   + (envVars.Count > 2 ? "," : "")
                   + " or "
                   + Fmt(envVars[^1]);
        }

        return new PrerequisiteError($"ERROR: Unable to initialise {client} client\n\nNo {list} defined in the environment.");
    }

    private static readonly Regex RichMarkupTag = new(@"\[/?[a-z][a-z0-9 _#]*\]|\[/\]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips the Rich console markup (<c>[bold]</c>, <c>[/blue]</c>, …) that the ported error messages carry.
    /// Inspect renders those tags through Rich; a console app writing to a plain <see cref="Console"/> shows them
    /// literally, so it should print <c>StripRichMarkup(ex.Message)</c>.
    /// </summary>
    public static string StripRichMarkup(string text) => RichMarkupTag.Replace(text, "");

    /// <summary>
    /// Parses a <c>-M key=value</c> model-arg value the way Inspect's CLI does (YAML-ish scalars): <c>true</c>/<c>false</c>,
    /// integers, decimals, <c>null</c>; a value starting with <c>{</c> or <c>[</c> is parsed as JSON (so
    /// <c>thinking={"type":"enabled"}</c> becomes an object); anything else stays a string.
    /// </summary>
    /// <exception cref="ArgumentException">The value looks like JSON but does not parse.</exception>
    public static object? ParseModelArgValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(trimmed);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException($"model arg value is not valid JSON: {ex.Message}", nameof(value));
            }
        }

        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return bool.TryParse(trimmed, out var b) ? b
            : int.TryParse(trimmed, out var i) ? i
            : double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : value;
    }

    /// <summary>Approximation of Python <c>repr()</c> for error messages.</summary>
    public static string PythonRepr(object? value) => value switch
    {
        null => "None",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        bool b => b ? "True" : "False",
        _ => value.ToString() ?? "",
    };
}
