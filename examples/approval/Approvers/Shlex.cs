using System.Text;

namespace InspectAzureAI.Examples.Approval;

/// <summary>
/// Port of Python's <c>shlex.split</c> as <c>examples/approval/approval.py</c> <c>bash_allowlist</c> calls it
/// (<c>posix=True</c>, <c>whitespace_split=True</c>, <c>comments=False</c>): the <c>shlex.shlex.read_token</c> state
/// machine with no comment or punctuation characters. Whitespace separates tokens; single quotes are literal until
/// the closing quote; inside double quotes a backslash escapes only <c>"</c> and <c>\</c> (any other backslash is
/// kept); outside quotes a backslash escapes the next character, newline included; adjacent quoted and unquoted
/// parts join into one token and empty quotes yield an empty token. Errors are <see cref="FormatException"/>s
/// carrying Python's <c>ValueError</c> messages, so the approver's <c>Invalid command syntax: ...</c> explanation
/// matches the original.
/// </summary>
internal static class Shlex
{
    /// <summary>The <c>read_token</c> states: <c>' '</c>, <c>'a'</c>, the two quote states and the escape state.</summary>
    private enum State
    {
        Whitespace,
        Word,
        SingleQuote,
        DoubleQuote,
        Escape,
    }

    /// <summary>Splits <paramref name="command"/> into shell words; malformed quoting or escaping is a <see cref="FormatException"/>.</summary>
    public static IReadOnlyList<string> Split(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tokens = new List<string>();
        var token = new StringBuilder();
        var state = State.Whitespace;

        // Python's `escapedstate`: the state an escape sequence returns to (the word, or the double-quoted string).
        var escapedState = State.Word;

        foreach (var c in command)
        {
            switch (state)
            {
                case State.Whitespace:
                    if (IsWhitespace(c))
                    {
                        // Nothing pending in this state: the previous word was emitted when it ended.
                    }
                    else if (c == '\\')
                    {
                        escapedState = State.Word;
                        state = State.Escape;
                    }
                    else if (c == '\'')
                    {
                        state = State.SingleQuote;
                    }
                    else if (c == '"')
                    {
                        state = State.DoubleQuote;
                    }
                    else
                    {
                        token.Append(c);
                        state = State.Word;
                    }

                    break;

                case State.SingleQuote:
                    if (c == '\'')
                    {
                        state = State.Word;
                    }
                    else
                    {
                        token.Append(c);
                    }

                    break;

                case State.DoubleQuote:
                    if (c == '"')
                    {
                        state = State.Word;
                    }
                    else if (c == '\\')
                    {
                        escapedState = State.DoubleQuote;
                        state = State.Escape;
                    }
                    else
                    {
                        token.Append(c);
                    }

                    break;

                case State.Escape:
                    // In posix shells, only the quote itself or the escape character may be escaped within quotes.
                    if (escapedState == State.DoubleQuote && c != '\\' && c != '"')
                    {
                        token.Append('\\');
                    }

                    token.Append(c);
                    state = escapedState;
                    break;

                case State.Word:
                    // A word is never empty-and-unquoted here (it started with a character, an escape or a quote),
                    // so whitespace always emits it (Python's `if self.token or (self.posix and quoted)`).
                    if (IsWhitespace(c))
                    {
                        tokens.Add(token.ToString());
                        token.Clear();
                        state = State.Whitespace;
                    }
                    else if (c == '\'')
                    {
                        state = State.SingleQuote;
                    }
                    else if (c == '"')
                    {
                        state = State.DoubleQuote;
                    }
                    else if (c == '\\')
                    {
                        escapedState = State.Word;
                        state = State.Escape;
                    }
                    else
                    {
                        token.Append(c);
                    }

                    break;
            }
        }

        switch (state)
        {
            case State.SingleQuote:
            case State.DoubleQuote:
                throw new FormatException("No closing quotation");
            case State.Escape:
                throw new FormatException("No escaped character");
            case State.Word:
                tokens.Add(token.ToString());
                break;
        }

        return tokens;
    }

    /// <summary>Python's <c>shlex.whitespace</c> (<c>" \t\r\n"</c>), not the Unicode notion.</summary>
    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';
}
