using InspectAzureAI.Provider.Core;

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

    /// <summary>Approximation of Python <c>repr()</c> for error messages.</summary>
    public static string PythonRepr(object? value) => value switch
    {
        null => "None",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        bool b => b ? "True" : "False",
        _ => value.ToString() ?? "",
    };
}
