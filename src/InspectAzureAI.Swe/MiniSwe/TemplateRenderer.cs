using System.Text.RegularExpressions;

namespace InspectAzureAI.Swe.MiniSwe;

/// <summary>
/// The slice of Jinja2 that mini-swe-agent's <c>mini.yaml</c> agent templates need (<c>DefaultAgent._render_template</c>
/// renders with <c>StrictUndefined</c>): <c>{{ name }}</c> substitution, an undefined name is an error, and the
/// single trailing newline is dropped as Jinja does by default (<c>keep_trailing_newline=False</c>).
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    public static string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);
        var source = template.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (source.EndsWith('\n'))
        {
            source = source[..^1];
        }

        return Placeholder().Replace(source, match =>
        {
            var name = match.Groups[1].Value;
            return variables.TryGetValue(name, out var value)
                ? value
                : throw new KeyNotFoundException($"'{name}' is undefined");
        });
    }
}
