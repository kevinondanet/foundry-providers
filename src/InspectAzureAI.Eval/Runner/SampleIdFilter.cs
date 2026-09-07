using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of the <c>sample_id</c> filter of <c>_eval/task/util.py</c>: ids are normalised
/// (<see cref="Normalise"/>) and matched against the requested ids as <c>fnmatch</c> glob patterns, patterns
/// scoped to a task with a <c>task:</c> prefix apply only to that task, a pattern matching nothing is a
/// warning and a filter matching nothing at all is a <see cref="PrerequisiteError"/>.
/// </summary>
internal static class SampleIdFilter
{
    /// <summary>
    /// Port of <c>normalise_sample_id</c>: digit strings are treated as ints, and ints are zero-padded to 20
    /// places so that they compare and glob-match textually.
    /// </summary>
    public static string Normalise(object? id)
    {
        if (id is string text)
        {
            return text.Length > 0 && text.All(char.IsAsciiDigit) ? ZeroFill(text.TrimStart('0') is { Length: > 0 } digits ? digits : "0") : text;
        }

        return ZeroFill(Convert.ToString(id, CultureInfo.InvariantCulture) ?? "None");
    }

    /// <summary>
    /// Port of <c>resolve_task_sample_ids</c>: a <c>task:id</c> string is kept (without its prefix) only when the
    /// prefix names <paramref name="task"/> (case-insensitively); other strings and ints pass through.
    /// </summary>
    public static IReadOnlyList<object> ResolveForTask(string task, IReadOnlyList<object> ids)
    {
        var resolved = new List<object>(ids.Count);
        foreach (var id in ids)
        {
            if (id is string text && text.Split(':', 2) is [var scope, var rest])
            {
                if (string.Equals(scope, task, StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Add(rest);
                }
            }
            else
            {
                resolved.Add(id);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Port of <c>slice_dataset</c>'s <c>sample_id</c> branch: the samples whose normalised id matches any of
    /// <paramref name="ids"/> (as glob patterns), in dataset order. Unmatched patterns are reported through
    /// <paramref name="warn"/>; no match at all is a <see cref="PrerequisiteError"/>.
    /// </summary>
    public static List<Sample> Filter(IReadOnlyList<Sample> samples, string? datasetName, IReadOnlyList<object> ids, Action<string>? warn)
    {
        var patterns = ids.Select(Normalise).ToList();
        var matchers = patterns.Select(GlobToRegex).ToList();
        var normalised = samples.Select(sample => Normalise(sample.Id)).ToList();
        foreach (var pattern in patterns.Where(pattern => !normalised.Contains(pattern, StringComparer.Ordinal)))
        {
            warn?.Invoke($"sample id '{pattern}' not found in dataset '{datasetName}'.");
        }

        var filtered = samples.Where((_, index) => matchers.Any(matcher => matcher.IsMatch(normalised[index]))).ToList();
        if (filtered.Count == 0)
        {
            var filter = string.Join(",", patterns);
            throw new PrerequisiteError($"No matches in dataset '{datasetName}' for sample_id filter '{filter}'\n({datasetName} ids: {Repr(samples.Select(sample => sample.Id))})");
        }

        return filtered;
    }

    /// <summary>Port of <c>fnmatch.translate</c>: <c>*</c>, <c>?</c> and <c>[seq]</c> / <c>[!seq]</c>, anchored, case-sensitive.</summary>
    public static Regex GlobToRegex(string pattern)
    {
        var result = new StringBuilder();
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i++];
            switch (c)
            {
                case '*':
                    // consecutive stars are one
                    while (i < n && pattern[i] == '*')
                    {
                        i++;
                    }

                    result.Append(".*");
                    break;
                case '?':
                    result.Append('.');
                    break;
                case '[':
                    var j = i;
                    if (j < n && pattern[j] == '!')
                    {
                        j++;
                    }

                    if (j < n && pattern[j] == ']')
                    {
                        j++;
                    }

                    while (j < n && pattern[j] != ']')
                    {
                        j++;
                    }

                    if (j >= n)
                    {
                        result.Append("\\[");
                    }
                    else
                    {
                        var stuff = pattern[i..j].Replace("\\", "\\\\", StringComparison.Ordinal);
                        i = j + 1;
                        if (stuff.StartsWith('!'))
                        {
                            stuff = "^" + stuff[1..];
                        }
                        else if (stuff.StartsWith('^') || stuff.StartsWith('['))
                        {
                            stuff = "\\" + stuff;
                        }

                        result.Append('[').Append(stuff).Append(']');
                    }

                    break;
                default:
                    result.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return new Regex($"^(?s:{result})\\z", RegexOptions.CultureInvariant);
    }

    private static string ZeroFill(string text)
    {
        const int width = 20;
        if (text.Length >= width)
        {
            return text;
        }

        var sign = text.StartsWith('-') || text.StartsWith('+') ? text[..1] : "";
        return sign + text[sign.Length..].PadLeft(width - sign.Length, '0');
    }

    /// <summary>An approximation of <c>reprlib.Repr(maxlist=8)</c> over the raw ids: Python literals, at most eight, then "...".</summary>
    private static string Repr(IEnumerable<object?> ids)
    {
        var items = ids.Take(9).Select(id => id is string text ? $"'{text}'" : Convert.ToString(id, CultureInfo.InvariantCulture) ?? "None").ToList();
        if (items.Count > 8)
        {
            items[8] = "...";
        }

        return "[" + string.Join(", ", items) + "]";
    }
}
