using System.Text.RegularExpressions;

namespace InspectAzureAI.Provider.Util;

/// <summary>Port of <c>textwrap.dedent</c>, needed to reproduce the Llama 3.1 tool prompt byte-for-byte.</summary>
public static partial class TextWrap
{
    [GeneratedRegex(@"^[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex WhitespaceOnly();

    [GeneratedRegex(@"(^[ \t]*)(?:[^ \t\n])", RegexOptions.Multiline)]
    private static partial Regex LeadingWhitespace();

    /// <summary>
    /// Removes the common leading whitespace from every line. Lines consisting solely of whitespace
    /// are normalised to empty (they neither contribute to nor receive the margin), as in Python.
    /// </summary>
    public static string Dedent(string text)
    {
        text = WhitespaceOnly().Replace(text, "");
        string? margin = null;
        foreach (Match m in LeadingWhitespace().Matches(text))
        {
            var indent = m.Groups[1].Value;
            if (margin is null)
            {
                margin = indent;
            }
            else if (indent.StartsWith(margin, StringComparison.Ordinal))
            {
                // current margin still fits
            }
            else if (margin.StartsWith(indent, StringComparison.Ordinal))
            {
                margin = indent;
            }
            else
            {
                var common = 0;
                while (common < margin.Length && common < indent.Length && margin[common] == indent[common])
                {
                    common++;
                }

                margin = margin[..common];
            }
        }

        if (string.IsNullOrEmpty(margin))
        {
            return text;
        }

        return Regex.Replace(text, "^" + Regex.Escape(margin), "", RegexOptions.Multiline);
    }
}
