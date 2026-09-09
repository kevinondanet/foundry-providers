using System.Text;

namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of Python's <c>shlex.join</c> / <c>shlex.quote</c>: argv words joined into one POSIX shell command
/// line, quoting every word that carries a character outside <c>[A-Za-z0-9_@%+=:,./-]</c> with single quotes
/// (an embedded single quote becomes <c>'"'"'</c>, an empty word becomes <c>''</c>).
/// </summary>
internal static class ShellWords
{
    public static string Join(IEnumerable<string> words) => string.Join(" ", words.Select(Quote));

    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length == 0)
        {
            return "''";
        }

        if (word.All(IsSafe))
        {
            return word;
        }

        var builder = new StringBuilder(word.Length + 2);
        builder.Append('\'');
        foreach (var c in word)
        {
            if (c == '\'')
            {
                builder.Append("'\"'\"'");
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private static bool IsSafe(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '@' or '%' or '+' or '=' or ':' or ',' or '.' or '/' or '-';
}
