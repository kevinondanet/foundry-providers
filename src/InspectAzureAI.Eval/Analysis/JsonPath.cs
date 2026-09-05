using System.Globalization;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// The subset of JSONPath (<c>jsonpath_ng</c>) the column definitions use: an optional <c>$</c> root, dotted field
/// names, <c>*</c> field wildcards, and <c>[n]</c> / <c>[*]</c> array subscripts. Filters, slices, recursive descent
/// and unions are rejected with <see cref="NotSupportedException"/>. <see cref="FindFirst"/> mirrors
/// <c>matches[0].value</c>: the first match in document order, or null when there is none.
/// </summary>
internal static class JsonPath
{
    /// <summary>The value at <paramref name="path"/> in <paramref name="root"/>, or null when nothing matches.</summary>
    public static JsonNode? FindFirst(JsonNode? root, string path) => Find(root, path).FirstOrDefault();

    /// <summary>Every match of <paramref name="path"/>, in document order.</summary>
    public static List<JsonNode?> Find(JsonNode? root, string path)
    {
        var steps = Parse(path);
        var matches = new List<JsonNode?>();
        Walk(root, steps, 0, matches);
        return matches;
    }

    private static void Walk(JsonNode? node, List<Step> steps, int index, List<JsonNode?> matches)
    {
        if (index == steps.Count)
        {
            matches.Add(node);
            return;
        }

        var step = steps[index];
        switch (step.Kind)
        {
            case StepKind.Field when node is JsonObject obj:
                if (obj.TryGetPropertyValue(step.Name!, out var child))
                {
                    Walk(child, steps, index + 1, matches);
                }

                break;
            case StepKind.FieldWildcard when node is JsonObject obj:
                foreach (var pair in obj)
                {
                    Walk(pair.Value, steps, index + 1, matches);
                }

                break;
            case StepKind.Index when node is JsonArray array:
                var position = step.Index < 0 ? array.Count + step.Index : step.Index;
                if (position >= 0 && position < array.Count)
                {
                    Walk(array[position], steps, index + 1, matches);
                }

                break;
            case StepKind.IndexWildcard when node is JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, steps, index + 1, matches);
                }

                break;
            default:
                break;
        }
    }

    private static List<Step> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var text = path.Trim();
        if (text.StartsWith('$'))
        {
            text = text[1..];
            if (text.StartsWith('.'))
            {
                text = text[1..];
            }
        }

        if (text.Contains("..", StringComparison.Ordinal) || text.Contains("[?", StringComparison.Ordinal) || text.Contains(':') || text.Contains('|') || text.Contains('&'))
        {
            throw new NotSupportedException($"JSONPath expression '{path}' uses constructs this port does not support (only fields, wildcards and array subscripts).");
        }

        var steps = new List<Step>();
        var position = 0;
        while (position < text.Length)
        {
            var c = text[position];
            if (c == '.')
            {
                position++;
                continue;
            }

            if (c == '[')
            {
                var close = text.IndexOf(']', position);
                if (close < 0)
                {
                    throw new NotSupportedException($"JSONPath expression '{path}' has an unterminated subscript.");
                }

                var inner = text[(position + 1)..close].Trim();
                steps.Add(Subscript(inner, path));
                position = close + 1;
                continue;
            }

            var end = position;
            while (end < text.Length && text[end] != '.' && text[end] != '[')
            {
                end++;
            }

            var name = text[position..end];
            steps.Add(name == "*" ? new Step(StepKind.FieldWildcard, null, 0) : new Step(StepKind.Field, Unquote(name), 0));
            position = end;
        }

        return steps;
    }

    private static Step Subscript(string inner, string path)
    {
        if (inner == "*")
        {
            return new Step(StepKind.IndexWildcard, null, 0);
        }

        if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            return new Step(StepKind.Index, null, index);
        }

        if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
        {
            return new Step(StepKind.Field, inner[1..^1], 0);
        }

        throw new NotSupportedException($"JSONPath expression '{path}' has an unsupported subscript '[{inner}]'.");
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && (name[0] == '\'' || name[0] == '"') && name[^1] == name[0] ? name[1..^1] : name;

    private enum StepKind
    {
        Field,
        FieldWildcard,
        Index,
        IndexWildcard,
    }

    private sealed record Step(StepKind Kind, string? Name, int Index);
}
