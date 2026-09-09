using System.Text;

namespace InspectAzureAI.Examples.Approval;

/// <summary>The kinds of Python AST node that <c>examples/approval/approval.py</c> <c>python_allowlist</c> inspects.</summary>
internal enum PythonNodeKind
{
    /// <summary><c>ast.Import</c>: one alias of an <c>import a.b as c, d.e</c> statement; <see cref="PythonNode.Name"/> is <c>alias.name</c>.</summary>
    Import,

    /// <summary><c>ast.ImportFrom</c>: <see cref="PythonNode.Name"/> is <c>node.module</c>, rendered <c>"None"</c> for a relative <c>from . import x</c>.</summary>
    ImportFrom,

    /// <summary><c>ast.Call</c> whose <c>func</c> is an <c>ast.Name</c>; <see cref="PythonNode.Name"/> is <c>func.id</c>.</summary>
    Call,

    /// <summary>
    /// An <c>ast.Assign</c> one of whose targets is an <c>ast.Attribute</c> with <c>attr.startswith("__")</c>: an
    /// assignment statement whose target (stripped of grouping parentheses) ends in <c>.__name</c> and is not a
    /// tuple or list; <see cref="PythonNode.Name"/> is the attribute.
    /// </summary>
    DunderAssignment,
}

/// <summary>One finding of <see cref="PythonSyntax.Scan"/>, standing in for the <c>ast</c> node the Python approver matches on.</summary>
internal readonly record struct PythonNode(PythonNodeKind Kind, string Name);

/// <summary>
/// The result of <see cref="PythonSyntax.Scan"/>: <see cref="Error"/> stands in for the <c>SyntaxError</c> of
/// <c>ast.parse</c> (its text is what <c>str(e)</c> would carry, e.g. <c>'(' was never closed (&lt;unknown&gt;, line 1)</c>);
/// otherwise <see cref="Nodes"/> lists the findings in source order.
/// </summary>
internal sealed record PythonScan(string? Error, IReadOnlyList<PythonNode> Nodes)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Port of the <c>ast.parse</c> + <c>ast.walk</c> inspection that <c>examples/approval/approval.py</c> <c>python_allowlist</c>
/// performs, as a small scanner over Python source (there is no Python parser in .NET). It blanks comments and string
/// literals, tokenizes the rest, and reports import statements, bare-name calls and dunder attribute assignments.
/// An assignment target is the token run between a statement start (a newline, or a <c>;</c>, <c>:</c> or <c>=</c>
/// outside brackets) and an <c>=</c> outside brackets, stripped of grouping parentheses; it is an attribute target
/// when it ends in <c>.__name</c> at its own depth with no tuple comma at that depth, which is where <c>ast</c> draws
/// the line too (<c>x, o.__d__ = 1, {}</c> assigns to a tuple, <c>(o.__a__) = 1</c> and <c>f(x).__a__ = 1</c> to an
/// attribute).
/// Deviation: <c>ast.walk</c> is breadth-first while this scanner reports in source order, so when a snippet holds
/// several violations the first one reported can differ from Python's. Deviation: expressions nested inside f-strings
/// are blanked with the string and therefore not scanned, and only unbalanced brackets, unterminated strings and stray
/// line continuations are detected as syntax errors.
/// </summary>
internal static class PythonSyntax
{
    /// <summary>The Python 3 keywords (<c>keyword.kwlist</c>): an identifier that is never an <c>ast.Name</c>.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
        "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    /// <summary>Multi-character operators, longest first, so that <c>==</c>, <c>+=</c> and friends never split into a bare <c>=</c>.</summary>
    private static readonly string[] Operators =
    [
        "**=", "//=", ">>=", "<<=", "...",
        "**", "//", ">>", "<<", "<=", ">=", "==", "!=", "->", ":=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@=",
    ];

    /// <summary>
    /// Pass (1): returns <paramref name="source"/> with every comment and string literal (single, double and triple
    /// quoted, with any <c>r</c>/<c>b</c>/<c>f</c>/<c>u</c>/<c>t</c> prefix combination in any case, honouring backslash
    /// escapes and backslash line continuations) replaced by spaces. Newlines are kept so line numbers survive.
    /// A test seam: the approver goes through <see cref="Scan"/>, which runs this pass first.
    /// </summary>
    public static string BlankStringsAndComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Blank(source, out _);
    }

    /// <summary>Scans <paramref name="source"/>; see <see cref="PythonScan"/>.</summary>
    public static PythonScan Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var blanked = Blank(source, out var error);
        if (error is not null)
        {
            return new PythonScan(error, []);
        }

        var tokens = Tokenize(blanked, out error);
        if (error is not null)
        {
            return new PythonScan(error, []);
        }

        return new PythonScan(null, Walk(tokens));
    }

    private static string Blank(string source, out string? error)
    {
        error = null;
        var chars = source.ToCharArray();
        var n = chars.Length;
        var line = 1;
        var i = 0;
        while (i < n)
        {
            var c = chars[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '#')
            {
                while (i < n && chars[i] != '\n')
                {
                    chars[i++] = ' ';
                }
            }
            else if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < n && IsIdentifierPart(chars[i]))
                {
                    i++;
                }

                if (i < n && IsQuote(chars[i]) && IsStringPrefix(chars, start, i))
                {
                    if (!BlankString(chars, start, i, ref line, out var end, out error))
                    {
                        return new string(chars);
                    }

                    i = end;
                }
            }
            else if (IsQuote(c))
            {
                if (!BlankString(chars, i, i, ref line, out var end, out error))
                {
                    return new string(chars);
                }

                i = end;
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    /// <summary>Blanks the literal whose prefix starts at <paramref name="start"/> and whose opening quote is at <paramref name="quoteAt"/>.</summary>
    private static bool BlankString(char[] chars, int start, int quoteAt, ref int line, out int end, out string? error)
    {
        var n = chars.Length;
        var quote = chars[quoteAt];
        var triple = quoteAt + 2 < n && chars[quoteAt + 1] == quote && chars[quoteAt + 2] == quote;
        var startLine = line;
        var k = quoteAt + (triple ? 3 : 1);
        var hasEscapedQuote = false;
        error = null;
        end = n;
        while (true)
        {
            if (k >= n)
            {
                error = triple
                    ? $"unterminated triple-quoted string literal (detected at line {line}) (<unknown>, line {startLine})"
                    : Unterminated(line, startLine, hasEscapedQuote);
                break;
            }

            var ch = chars[k];
            if (ch == '\\')
            {
                // An escaped character never closes the literal, in raw strings too; an escaped newline continues it.
                if (k + 2 < n && chars[k + 1] == '\r' && chars[k + 2] == '\n')
                {
                    line++;
                    k++;
                }
                else if (k + 1 < n && chars[k + 1] == '\n')
                {
                    line++;
                }
                else if (k + 1 < n && chars[k + 1] == quote)
                {
                    hasEscapedQuote = true;
                }

                k += 2;
            }
            else if (ch == '\n')
            {
                if (!triple)
                {
                    error = Unterminated(line, startLine, hasEscapedQuote);
                    break;
                }

                line++;
                k++;
            }
            else if (ch == quote)
            {
                if (!triple)
                {
                    end = k + 1;
                    break;
                }

                if (k + 2 < n && chars[k + 1] == quote && chars[k + 2] == quote)
                {
                    end = k + 3;
                    break;
                }

                k++;
            }
            else
            {
                k++;
            }
        }

        for (var j = start; j < Math.Min(end, n); j++)
        {
            if (chars[j] != '\n')
            {
                chars[j] = ' ';
            }
        }

        return error is null;
    }

    /// <summary>The tokenizer's message for an unterminated single-line literal, with CPython's hint when the literal contains an escaped quote.</summary>
    private static string Unterminated(int line, int startLine, bool escapedEndQuote) =>
        $"unterminated string literal (detected at line {line}){(escapedEndQuote ? "; perhaps you escaped the end quote?" : "")} (<unknown>, line {startLine})";

    private static bool IsStringPrefix(char[] chars, int start, int end)
    {
        if (end - start > 2)
        {
            return false;
        }

        for (var j = start; j < end; j++)
        {
            if ("rbfutRBFUT".IndexOf(chars[j]) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsQuote(char c) => c is '"' or '\'';

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private enum TokenKind
    {
        Name,
        Number,
        Op,
        Newline,
    }

    /// <summary>A token of the blanked source; <see cref="Depth"/> is the bracket nesting it sits in.</summary>
    private readonly record struct Token(TokenKind Kind, string Text, int Depth);

    private static List<Token> Tokenize(string text, out string? error)
    {
        error = null;
        var tokens = new List<Token>();
        var open = new Stack<(char Bracket, int Line)>();
        var n = text.Length;
        var line = 1;
        var i = 0;
        while (i < n)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                // Newlines inside brackets are implicit line joins, not statement boundaries.
                if (open.Count == 0 && tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
                {
                    tokens.Add(new Token(TokenKind.Newline, "\n", 0));
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\\')
            {
                var j = i + 1;
                if (j < n && text[j] == '\r')
                {
                    j++;
                }

                if (j < n && text[j] == '\n')
                {
                    line++;
                    i = j + 1;
                }
                else
                {
                    error = $"unexpected character after line continuation character (<unknown>, line {line})";
                    return tokens;
                }
            }
            else if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < n && IsIdentifierPart(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Name, text[start..i], open.Count));
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
            {
                // A numeric literal owns its dots (1.5, 1., .5, 1.e5) so that '.' tokens only ever mean attribute access.
                var start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i], open.Count));
            }
            else if (c is '(' or '[' or '{')
            {
                tokens.Add(new Token(TokenKind.Op, c.ToString(), open.Count));
                open.Push((c, line));
                i++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (open.Count == 0)
                {
                    error = $"unmatched '{c}' (<unknown>, line {line})";
                    return tokens;
                }

                var (bracket, _) = open.Pop();
                if (Closing(bracket) != c)
                {
                    error = $"closing parenthesis '{c}' does not match opening parenthesis '{bracket}' (<unknown>, line {line})";
                    return tokens;
                }

                tokens.Add(new Token(TokenKind.Op, c.ToString(), open.Count));
                i++;
            }
            else
            {
                var op = Operators.FirstOrDefault(candidate => string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                    ?? c.ToString();
                tokens.Add(new Token(TokenKind.Op, op, open.Count));
                i += op.Length;
            }
        }

        if (open.Count > 0)
        {
            var (bracket, openedAt) = open.Peek();
            error = $"'{bracket}' was never closed (<unknown>, line {openedAt})";
        }

        return tokens;
    }

    private static char Closing(char bracket) => bracket switch
    {
        '(' => ')',
        '[' => ']',
        _ => '}',
    };

    private static List<PythonNode> Walk(List<Token> tokens)
    {
        var nodes = new List<PythonNode>();
        var statementStart = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Newline || (token.Depth == 0 && token is { Kind: TokenKind.Op, Text: ";" or ":" or "=" }))
            {
                // An `=` outside brackets ends an assignment target (one per `=` of a chained `a = b.__c__ = 1`).
                if (token.Text == "=" && TryDunderAttributeTarget(tokens, statementStart, i, out var attribute))
                {
                    nodes.Add(new PythonNode(PythonNodeKind.DunderAssignment, attribute));
                }

                statementStart = i + 1;
                continue;
            }

            if (token.Kind != TokenKind.Name)
            {
                continue;
            }

            if (token.Text == "from" && TryImportFrom(tokens, i, out var module, out var importAt))
            {
                nodes.Add(new PythonNode(PythonNodeKind.ImportFrom, module));
                i = importAt;
                continue;
            }

            if (token.Text == "import")
            {
                i = ReadImport(tokens, i, nodes) - 1;
                continue;
            }

            if (Keywords.Contains(token.Text))
            {
                continue;
            }

            var previous = i > 0 ? tokens[i - 1] : default;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            var afterDot = previous is { Kind: TokenKind.Op, Text: "." };
            if (next is { Kind: TokenKind.Op, Text: "(" } && !afterDot && previous is not { Kind: TokenKind.Name, Text: "def" or "class" })
            {
                nodes.Add(new PythonNode(PythonNodeKind.Call, token.Text));
            }
        }

        return nodes;
    }

    /// <summary>
    /// Whether the tokens in [<paramref name="start"/>, <paramref name="end"/>) are an assignment target that
    /// <c>ast</c> would parse as an <c>ast.Attribute</c> with a dunder <c>attr</c>: stripped of grouping parentheses,
    /// the run ends in <c>.__name</c> at its own depth and holds no tuple comma at that depth (a subscript, a list or a
    /// bare name is not an attribute; an annotation, <c>for</c> or <c>with</c> target is not an <c>ast.Assign</c>
    /// and never reaches here because its <c>:</c> or the absence of <c>=</c> keeps it out).
    /// </summary>
    private static bool TryDunderAttributeTarget(List<Token> tokens, int start, int end, out string attribute)
    {
        attribute = string.Empty;
        while (end - start >= 2 && IsGroupingPair(tokens, start, end - 1))
        {
            start++;
            end--;
        }

        if (end - start < 3)
        {
            return false;
        }

        var depth = tokens[start].Depth;
        var last = tokens[end - 1];
        var dot = tokens[end - 2];
        if (last.Kind != TokenKind.Name || last.Depth != depth || !last.Text.StartsWith("__", StringComparison.Ordinal)
            || dot is not { Kind: TokenKind.Op, Text: "." } || dot.Depth != depth)
        {
            return false;
        }

        for (var i = start; i < end - 2; i++)
        {
            if (tokens[i].Depth == depth && tokens[i] is { Kind: TokenKind.Op, Text: "," })
            {
                return false;
            }
        }

        attribute = last.Text;
        return true;
    }

    /// <summary>Whether <c>tokens[first]</c> and <c>tokens[last]</c> are a pair of parentheses enclosing everything between them.</summary>
    private static bool IsGroupingPair(List<Token> tokens, int first, int last)
    {
        var open = tokens[first];
        if (open is not { Kind: TokenKind.Op, Text: "(" } || tokens[last] is not { Kind: TokenKind.Op, Text: ")" } || tokens[last].Depth != open.Depth)
        {
            return false;
        }

        for (var i = first + 1; i < last; i++)
        {
            if (tokens[i].Depth <= open.Depth)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>from [dots][module] import ...</c>: yields <c>node.module</c> (<c>"None"</c> when only dots) and the index of its <c>import</c> token.</summary>
    private static bool TryImportFrom(List<Token> tokens, int fromAt, out string module, out int importAt)
    {
        var i = fromAt + 1;
        while (i < tokens.Count && tokens[i] is { Kind: TokenKind.Op, Text: "." or "..." })
        {
            i++;
        }

        module = ReadDottedName(tokens, ref i);
        if (i < tokens.Count && tokens[i] is { Kind: TokenKind.Name, Text: "import" })
        {
            if (module.Length == 0)
            {
                module = "None";
            }

            importAt = i;
            return true;
        }

        importAt = -1;
        return false;
    }

    /// <summary><c>import a.b as c, d.e</c>: adds one <see cref="PythonNodeKind.Import"/> per alias and returns the index after the statement's names.</summary>
    private static int ReadImport(List<Token> tokens, int importAt, List<PythonNode> nodes)
    {
        var i = importAt + 1;
        while (true)
        {
            var name = ReadDottedName(tokens, ref i);
            if (name.Length == 0)
            {
                return i;
            }

            nodes.Add(new PythonNode(PythonNodeKind.Import, name));
            if (i + 1 < tokens.Count && tokens[i] is { Kind: TokenKind.Name, Text: "as" } && tokens[i + 1].Kind == TokenKind.Name)
            {
                i += 2;
            }

            if (i < tokens.Count && tokens[i] is { Kind: TokenKind.Op, Text: "," })
            {
                i++;
                continue;
            }

            return i;
        }
    }

    private static string ReadDottedName(List<Token> tokens, ref int i)
    {
        var name = new StringBuilder();
        while (i < tokens.Count && tokens[i].Kind == TokenKind.Name && !Keywords.Contains(tokens[i].Text))
        {
            name.Append(tokens[i].Text);
            i++;
            if (i + 1 < tokens.Count && tokens[i] is { Kind: TokenKind.Op, Text: "." } && tokens[i + 1].Kind == TokenKind.Name)
            {
                name.Append('.');
                i++;
                continue;
            }

            break;
        }

        return name.ToString();
    }
}
