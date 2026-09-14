using System.Text.RegularExpressions;

namespace InspectAzureAI.Swe.Util;

/// <summary>Port of Python <c>shlex.quote</c> and <c>shlex.join</c>: POSIX shell quoting of one argument or a whole argv.</summary>
public static partial class ShellQuote
{
    /// <summary>
    /// Port of <c>shlex.quote</c>: an empty string becomes <c>''</c>; a string made only of
    /// <c>[A-Za-z0-9_@%+=:,./-]</c> is returned unchanged; anything else is wrapped in single quotes, with each
    /// embedded <c>'</c> written as <c>'"'"'</c>.
    /// </summary>
    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length == 0)
        {
            return "''";
        }

        return SafeRegex().IsMatch(arg) ? arg : "'" + arg.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Port of <c>shlex.join</c>: the quoted arguments separated by single spaces.</summary>
    public static string Join(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(" ", args.Select(Quote));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_@%+=:,./-]+\z")]
    private static partial Regex SafeRegex();
}
