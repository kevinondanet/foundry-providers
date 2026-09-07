using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_math.py</c> <c>math()</c> over the numeric-equivalence subset (see <see cref="MathAnswer"/>).</summary>
public static partial class Scorers
{
    /// <summary>
    /// Port of <c>math()</c>: extracts a bounded final answer from the completion (the last <c>\boxed{}</c>, an
    /// "answer is" marker, delimited math, the last line, the last number) and compares it with each target. Python
    /// parses both sides with SymPy; this port evaluates the numeric subset exactly (integers, fractions, powers,
    /// roots, factorials, binomials, percentages, tuples and sets of those) and compares everything else as
    /// normalized text, so algebraically equivalent but differently written symbolic answers are not recognised.
    /// A target that cannot be parsed leaves the sample unscored; the score metadata records
    /// <c>math_scorer_status</c>.
    /// </summary>
    public static ScorerDef Math() =>
        new("math", (state, target, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            return Task.FromResult(MathAnswer.ScoreCompletion(state.Output.Completion, target.Values));
        }, [Metrics.Accuracy(), Metrics.Stderr()]);
}

/// <summary>A candidate answer that could not be parsed; the base of the limit and unsafe variants as in Python.</summary>
internal class MathParseException(string message) : Exception(message);

/// <summary>A candidate answer that exceeded a complexity limit.</summary>
internal sealed class MathLimitException(string message) : MathParseException(message);

/// <summary>A candidate answer that contains non-mathematical code syntax.</summary>
internal sealed class MathUnsafeException(string message) : MathParseException(message);

/// <summary>An exact rational number with arbitrary precision.</summary>
internal readonly record struct Rational
{
    public Rational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("rational denominator is zero");
        }

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        if (!gcd.IsOne && !gcd.IsZero)
        {
            numerator /= gcd;
            denominator /= gcd;
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public bool IsInteger => Denominator.IsOne;

    public static Rational operator +(Rational a, Rational b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Rational operator -(Rational a, Rational b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Rational operator *(Rational a, Rational b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);

    public static Rational operator /(Rational a, Rational b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);

    public static Rational operator -(Rational a) => new(-a.Numerator, a.Denominator);

    public double ToDouble()
    {
        if (IsInteger)
        {
            return (double)Numerator;
        }

        // scale so the quotient keeps ~16 significant digits even when both parts overflow a double
        var scale = BigInteger.Max(Numerator.IsZero ? 1 : BigInteger.Abs(Numerator), Denominator).GetBitLength() - 1000;
        if (scale > 0)
        {
            return (double)(Numerator >> (int)scale) / (double)(Denominator >> (int)scale);
        }

        return (double)Numerator / (double)Denominator;
    }
}

/// <summary>The value a mathematical answer evaluates to in the numeric subset.</summary>
internal abstract record MathValue
{
    /// <summary>An exact rational (integers, fractions, perfect roots, factorials).</summary>
    public sealed record Exact(Rational Value) : MathValue;

    /// <summary>A floating-point approximation (decimals, irrational roots, π, e).</summary>
    public sealed record Approx(double Value) : MathValue;

    /// <summary>An ordered tuple <c>(a, b, ...)</c>.</summary>
    public sealed record Tuple(IReadOnlyList<MathValue> Items) : MathValue;

    /// <summary>An unordered set <c>{a, b, ...}</c>.</summary>
    public sealed record Set(IReadOnlyList<MathValue> Items) : MathValue;

    public static MathValue Of(BigInteger value) => new Exact(new Rational(value, BigInteger.One));

    public double ToDouble() => this switch
    {
        Exact e => e.Value.ToDouble(),
        Approx a => a.Value,
        _ => double.NaN,
    };
}

/// <summary>A parsed answer or target: a numeric value, or opaque normalized text (prose, symbolic answers).</summary>
internal sealed record MathParsedValue(MathValue? Value, string? Text, string Source);

/// <summary>
/// Port of the extraction, validation and comparison pipeline of <c>scorer/_math.py</c>. Answer and target
/// candidates are extracted exactly as in Python; parsing replaces SymPy with an exact rational evaluator over the
/// numeric subset and falls back to normalized text for anything symbolic.
/// </summary>
internal static partial class MathAnswer
{
    private const int MaxCompletionChars = 1_000_000;
    private const int MaxCandidateChars = 4_096;
    private const int MaxNesting = 64;
    private const int MaxCommands = 256;
    private const int MaxOperators = 1_024;
    private const int MaxDigitRun = 256;
    private const int MaxTotalDigits = 2_048;
    private const int MaxMatrixCells = 256;
    private const int MaxFactorialArgument = 10_000;
    private const int SymbolicPowerExponentLimit = 10_000;
    private const long MaxResultBits = 200_000;

    private static readonly Regex BoxStart = new(@"(?:\\(?:beginboxed|boxed|fbox)|(?<![A-Za-z\\])(?:boxed|fbox|oxed))\s*\{", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnswerMarker = new(@"(?:final\s+answer|answer|result)\s*(?:is\b|[:=])\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberPattern = new(@"[-+]?(?:(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+)(?:[eE][-+]?\d+)?\s*%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PercentSuffix = new(@"\s*(?:\\?%|\\text\s*\{\s*(?:percent(?:age)?|pct)\s*\}|\s+(?:percent(?:age)?|pct))\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommaNumber = new(@"^\s*-?\d{1,3}(?:,\d{3})+(?:\.\d+)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ThinSpaceNumber = new(@"^\s*-?\d{1,3}(?:\\,\d{3})+(?:\.\d+)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Command = new(@"\\[A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Word = new(@"[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodeShaped = new(@"__|\b(?:breakpoint|compile|eval|exec|getattr|globals|import|locals|open|setattr)\s*\(|\b(?:builtins|os|pathlib|subprocess|sys)\s*\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EagerParserOperation = new(
        @"\\begin\{[vV]matrix\}|\\(?:det|gcd|lcm)\b|\\xrightarrow|\\\||\\operatorname\s*\{\s*(?:cols|diag|diagonalize|eig|eigen|eigenvals|eigenvalues|eigenvects|eigenvectors|eye|gcd|hstack|lcm|nullspace|norm|ones|orth|ortho|orthogonal|orthogonalize|rank|ref|rows|rref|svd|trace|tr|vstack|zeros)\s*\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0b-\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingSpacing = new(@"^(?:(?:\\[,;:!]|\\quad|\\qquad)\s*)+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingSpacing = new(@"(?:(?:\\[,;:!]|\\quad|\\qquad)\s*)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MathSpans = new(@"\$\$.*?\$\$|\$.*?\$|\\\(.*?\\\)|\\\[.*?\\\]", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TextCommand = new(@"\\(?:text|mathrm|mbox)\s*\{([^{}]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SizeCommands = new(@"\\(?:left|right|Bigl|Bigr|bigl|bigr|Big|big|Large|large)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpacingCommands = new(@"\\[,;:!]|\\quad|\\qquad", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnitText = new(@"\s*\\(?:text|mathrm|mbox|textbf|textrm)\s*\{\s*[A-Za-z][A-Za-z ^0-9]*\}\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DegreeSuffix = new(@"\s*(?:\^\s*(?:\\circ|\{\s*\\circ\s*\})|\\degree)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Source, string Replacement)[] UnicodeReplacements =
    [
        ("\u23a7", "\\boxed{"),
        ("\u23ab", "}"),
        ("\n\u2502", "\\boxed{"),
        ("\u2502", "}"),
        ("\n\u2503", "\\boxed{"),
        ("\u2503", "}"),
        ("\n\uf8f0", "\\boxed{"),
        ("\uf8fb", "}"),
        ("\u221a", "\\sqrt"),
        ("\u00d7", "\\cdot"),
        ("\u00f7", "/"),
        ("\u202f", " "),
        ("\u2212", "-"),
        ("\u2013", "-"),
        ("\u03c0", "\\pi"),
        ("\u00b0", "^\\circ"),
        ("\u221e", "\\infty"),
        ("\u2264", "\\le"),
        ("\u2265", "\\ge"),
        ("\u2260", "\\ne"),
        ("\u222a", "\\cup"),
        ("\u2229", "\\cap"),
    ];

    /// <summary>Port of the <c>score()</c> closure of <c>math()</c> together with its two worker functions.</summary>
    public static Score ScoreCompletion(string completion, IReadOnlyList<string> targets)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(targets);
        var (parsedTargets, targetError) = ParseTargets(targets);
        if (targetError is not null)
        {
            return Score.Unscored(explanation: $"Could not parse mathematical target: {targetError}.", metadata: StatusMetadata("target_parse_error"));
        }

        MathParsedValue answer;
        try
        {
            answer = ParseFirst(AnswerCandidates(completion));
        }
        catch (MathLimitException ex)
        {
            return new Score(ScoreConstants.Incorrect)
            {
                Explanation = $"Mathematical answer exceeded a complexity limit: {ex.Message}.",
                Metadata = StatusMetadata("answer_limit"),
            };
        }
        catch (MathParseException ex)
        {
            return new Score(ScoreConstants.Incorrect)
            {
                Explanation = $"Could not parse mathematical answer: {ex.Message}.",
                Metadata = StatusMetadata("answer_parse_error"),
            };
        }

        var correct = parsedTargets.Any(target => Equivalent(target, answer));
        if (!correct && answer.Value is null)
        {
            var fallback = MatchingExpressionCandidate(AnswerCandidates(completion), parsedTargets);
            if (fallback is not null)
            {
                correct = true;
                answer = fallback;
            }
        }

        var source = answer.Source.Length > MaxCandidateChars ? answer.Source[..MaxCandidateChars] : answer.Source;
        return new Score(correct ? ScoreConstants.Correct : ScoreConstants.Incorrect)
        {
            Answer = source,
            Explanation = completion,
            Metadata = StatusMetadata(correct ? "correct" : "incorrect"),
        };
    }

    private static Dictionary<string, object?> StatusMetadata(string status) =>
        new(StringComparer.Ordinal) { ["math_scorer_status"] = status };

    /// <summary>Port of <c>_parse_targets_worker</c>: every target that parses, or the last error when none does.</summary>
    private static (List<MathParsedValue> Parsed, string? Error) ParseTargets(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            return ([], "target is empty");
        }

        var parsed = new List<MathParsedValue>();
        string? lastError = null;
        foreach (var target in targets)
        {
            try
            {
                parsed.Add(ParseFirst(TargetCandidates(target)));
            }
            catch (MathParseException ex)
            {
                lastError = ex.Message;
            }
        }

        return parsed.Count > 0 ? (parsed, null) : ([], lastError ?? "could not parse mathematical target");
    }

    // ---- candidate extraction -------------------------------------------------------------------------

    /// <summary>Port of <c>_replace_unicode</c>.</summary>
    internal static string ReplaceUnicode(string text)
    {
        text = ControlChars.Replace(text, "");
        foreach (var (source, replacement) in UnicodeReplacements)
        {
            text = text.Replace(source, replacement, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Port of <c>_balanced_content</c>: the text inside the brace at <paramref name="opening"/> and the index after its close.</summary>
    private static (string Content, int End)? BalancedContent(string text, int opening)
    {
        var depth = 0;
        for (var index = opening; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '{')
            {
                depth++;
                if (depth > MaxNesting)
                {
                    return null;
                }
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var end = Math.Min(index, opening + MaxCandidateChars + 2);
                    return (text[(opening + 1)..end], index + 1);
                }
            }
        }

        return null;
    }

    /// <summary>Port of <c>_boxed_candidates</c>: the most recent <c>\boxed{...}</c> only.</summary>
    internal static List<string> BoxedCandidates(string text)
    {
        string? last = null;
        var position = 0;
        while (true)
        {
            var match = BoxStart.Match(text, position);
            if (!match.Success)
            {
                break;
            }

            var parsed = BalancedContent(text, match.Index + match.Length - 1);
            if (parsed is null)
            {
                position = match.Index + match.Length;
                continue;
            }

            (last, position) = parsed.Value;
        }

        return last is null ? [] : [last];
    }

    /// <summary>Port of <c>_last_single_dollar_math</c>.</summary>
    private static string? LastSingleDollarMath(string text)
    {
        var positions = new List<int>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '$' || (index > 0 && text[index - 1] == '\\'))
            {
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '$')
            {
                continue;
            }

            if (index > 0 && text[index - 1] == '$')
            {
                continue;
            }

            positions.Add(index);
        }

        string? lastMatch = null;
        for (var index = 0; index < positions.Count - 1; index += 2)
        {
            lastMatch = text[(positions[index] + 1)..positions[index + 1]];
        }

        return lastMatch;
    }

    /// <summary>Port of <c>_last_delimited_math</c>: the content of the last <c>$$</c>, <c>\[</c>, <c>\(</c> or <c>$</c> span.</summary>
    private static string? LastDelimitedMath(string text)
    {
        var matches = new List<(int End, string Content)>();
        foreach (var (opening, closing) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)") })
        {
            var end = text.LastIndexOf(closing, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var start = text.LastIndexOf(opening, end, StringComparison.Ordinal);
            if (start >= 0 && start + opening.Length <= end)
            {
                matches.Add((end, text[(start + opening.Length)..end]));
            }
        }

        var singleDollar = LastSingleDollarMath(text);
        if (singleDollar is not null)
        {
            matches.Add((text.LastIndexOf('$'), singleDollar));
        }

        return matches.Count == 0 ? null : matches.MaxBy(item => item.End).Content;
    }

    /// <summary>Port of <c>_strip_delimiters</c>: bold markers, math delimiters, spacing commands and trailing sentence punctuation.</summary>
    internal static string StripDelimiters(string text)
    {
        text = text.Trim();
        while (text.Length >= 4 && text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal))
        {
            text = text[2..^2].Trim();
        }

        foreach (var (opening, closing) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            if (text.StartsWith(opening, StringComparison.Ordinal) && text.EndsWith(closing, StringComparison.Ordinal)
                && text.Length >= opening.Length + closing.Length
                && (opening != "$" || text.Count(c => c == '$') == 2))
            {
                text = text[opening.Length..^closing.Length].Trim();
            }
        }

        text = LeadingSpacing.Replace(text, "");
        text = TrailingSpacing.Replace(text, "");
        return text.TrimEnd(' ', '\t', '\r', '\n', '.', ',', ';', ':');
    }

    private static void AppendCandidate(List<string> candidates, string? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        candidate = StripDelimiters(candidate);
        if (candidate.Length > 0 && !candidates.Contains(candidate, StringComparer.Ordinal))
        {
            candidates.Add(candidate);
        }
    }

    /// <summary>Port of <c>_answer_candidates</c>: the ordered final-answer candidates of a completion.</summary>
    internal static List<string> AnswerCandidates(string text)
    {
        text = ReplaceUnicode(text);
        if (text.Length > MaxCompletionChars)
        {
            throw new MathLimitException("model output is too long");
        }

        var candidates = new List<string>();
        var boxes = BoxedCandidates(text);
        if (boxes.Count > 0)
        {
            AppendCandidate(candidates, boxes[^1]);
        }

        Match? lastMarker = null;
        foreach (Match match in AnswerMarker.Matches(text))
        {
            lastMarker = match;
        }

        if (lastMarker is not null)
        {
            var suffix = text[(lastMarker.Index + lastMarker.Length)..];
            AppendCandidate(candidates, suffix.Length > 0 ? SplitLines(suffix)[0] : null);
        }

        if (IsShortCompositeAnswer(text))
        {
            AppendCandidate(candidates, text);
        }

        AppendCandidate(candidates, LastDelimitedMath(text));

        if (text.Length <= MaxCandidateChars && !IsShortCompositeAnswer(text))
        {
            AppendCandidate(candidates, text);
        }

        var nonemptyLines = SplitLines(text).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (nonemptyLines.Count > 0)
        {
            AppendCandidate(candidates, nonemptyLines[^1]);
        }

        string? lastNumber = null;
        foreach (Match match in NumberPattern.Matches(text))
        {
            lastNumber = match.Value;
        }

        AppendCandidate(candidates, lastNumber);
        return candidates;
    }

    /// <summary>Port of <c>_target_candidates</c>: the last box, then the whole target.</summary>
    internal static List<string> TargetCandidates(string text)
    {
        text = ReplaceUnicode(text);
        var candidates = new List<string>();
        var boxes = BoxedCandidates(text);
        if (boxes.Count > 0)
        {
            AppendCandidate(candidates, boxes[^1]);
        }

        AppendCandidate(candidates, text);
        return candidates;
    }

    /// <summary>Python <c>str.splitlines()</c> over the line breaks that matter here.</summary>
    private static string[] SplitLines(string text) => text.Split(["\r\n", "\n", "\r", "\v", "\f", "\x1c", "\x1d", "\x1e", "\x85", "\u2028", "\u2029"], StringSplitOptions.None);

    // ---- validation and shape checks ----------------------------------------------------------------

    /// <summary>Port of <c>_contains_nested_latex_power</c>.</summary>
    private static bool ContainsNestedLatexPower(string text)
    {
        var index = 0;
        while (true)
        {
            index = text.IndexOf('^', index);
            if (index < 0)
            {
                return false;
            }

            var cursor = index + 1;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            if (cursor < text.Length && text[cursor] == '{')
            {
                var parsed = BalancedContent(text, cursor);
                if (parsed is not null && parsed.Value.Content.Contains('^') && !parsed.Value.Content.Any(char.IsAsciiLetter))
                {
                    return true;
                }
            }

            index++;
        }
    }

    /// <summary>Port of <c>_validate_candidate</c>: the static complexity and safety limits.</summary>
    internal static void ValidateCandidate(string candidate)
    {
        if (candidate.Length == 0)
        {
            throw new MathParseException("answer is empty");
        }

        if (candidate.Length > MaxCandidateChars)
        {
            throw new MathLimitException("expression is too long");
        }

        if (CodeShaped.IsMatch(candidate))
        {
            throw new MathUnsafeException("expression contains non-mathematical code syntax");
        }

        if (EagerParserOperation.IsMatch(candidate))
        {
            throw new MathLimitException("expression requests an eager mathematical operation");
        }

        if (ContainsNestedLatexPower(candidate))
        {
            throw new MathLimitException("expression contains a nested exponent");
        }

        var digitRuns = DigitRun.Matches(candidate).Select(m => m.Length).ToList();
        if (digitRuns.Any(run => run > MaxDigitRun))
        {
            throw new MathLimitException("expression contains an oversized numeric literal");
        }

        if (digitRuns.Sum() > MaxTotalDigits)
        {
            throw new MathLimitException("expression contains too many digits");
        }

        if (Command.Matches(candidate).Count > MaxCommands)
        {
            throw new MathLimitException("expression contains too many LaTeX commands");
        }

        if (candidate.Count(ch => "+-*/^=!<>".Contains(ch, StringComparison.Ordinal)) > MaxOperators)
        {
            throw new MathLimitException("expression contains too many operators");
        }

        var depth = 0;
        var maxDepth = 0;
        var stack = new Stack<char>();
        foreach (var ch in candidate)
        {
            if (ch is '(' or '[' or '{')
            {
                stack.Push(ch);
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
            }
            else if (ch is ')' or ']' or '}')
            {
                var opening = ch switch { ')' => '(', ']' => '[', _ => '{' };
                if (stack.Count > 0 && stack.Peek() == opening)
                {
                    stack.Pop();
                    depth--;
                }
            }
        }

        if (maxDepth > MaxNesting)
        {
            throw new MathLimitException("expression is nested too deeply");
        }

        var matrixRows = CountOccurrences(candidate, "\\\\") + 1;
        var matrixColumns = candidate.Count(ch => ch == '&') + 1;
        if (matrixRows * matrixColumns > MaxMatrixCells)
        {
            throw new MathLimitException("expression contains an oversized matrix");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Port of <c>_looks_like_prose</c>: no math syntax and at least two words.</summary>
    internal static bool LooksLikeProse(string candidate)
    {
        if (candidate.Contains('\\') || candidate.Any(ch => "=+-*/^<>[]{}()".Contains(ch, StringComparison.Ordinal)))
        {
            return false;
        }

        return Word.Matches(candidate).Count >= 2;
    }

    /// <summary>Port of <c>_is_short_composite_answer</c>: short text mixing prose with delimited math.</summary>
    internal static bool IsShortCompositeAnswer(string candidate)
    {
        if (candidate.Length > 512 || candidate.Count(ch => ch == '\n') > 2)
        {
            return false;
        }

        if (!candidate.Contains('$') && !candidate.Contains("\\(", StringComparison.Ordinal) && !candidate.Contains("\\[", StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.Contains('\n') && candidate.Count(ch => ch == '$') >= 4)
        {
            return true;
        }

        var outsideMath = MathSpans.Replace(candidate, " ");
        return Word.IsMatch(outsideMath);
    }

    /// <summary>Port of <c>_normalize_text</c>: the opaque text form of a non-numeric answer.</summary>
    internal static string NormalizeText(string candidate)
    {
        candidate = StripDelimiters(candidate);
        candidate = TextCommand.Replace(candidate, "$1");
        candidate = candidate.Replace("\\ ", " ", StringComparison.Ordinal);
        candidate = WhitespaceRun.Replace(candidate, " ");
        return candidate.Trim(' ', '\t', '\r', '\n', '.', ',', ';', ':').ToLowerInvariant();
    }

    /// <summary>Port of <c>_split_plain_equation</c>: the two sides of a single depth-0 <c>=</c> (not <c>==</c>, <c>&lt;=</c>, ...).</summary>
    internal static (string Left, string Right)? SplitPlainEquation(string text)
    {
        var depth = 0;
        int? splitAt = null;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch is '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (ch == '=' && depth == 0)
            {
                var previous = index > 0 ? text[index - 1] : '\0';
                var following = index + 1 < text.Length ? text[index + 1] : '\0';
                if (previous is '<' or '>' or '!' or '=' || following == '=')
                {
                    continue;
                }

                if (splitAt is not null)
                {
                    return null;
                }

                splitAt = index;
            }
        }

        return splitAt is { } at ? (text[..at], text[(at + 1)..]) : null;
    }

    /// <summary>Port of <c>_percentage_base</c>: the text before a percent suffix, when there is one.</summary>
    private static string? PercentageBase(string candidate)
    {
        var match = PercentSuffix.Match(candidate);
        if (!match.Success)
        {
            return null;
        }

        var head = candidate[..match.Index].TrimEnd();
        return head.Length > 0 ? head : null;
    }

    // ---- parsing ------------------------------------------------------------------------------------

    /// <summary>Port of <c>_parse_candidate</c> with the numeric evaluator in place of SymPy.</summary>
    internal static MathParsedValue ParseCandidate(string candidate)
    {
        candidate = StripDelimiters(candidate);
        ValidateCandidate(candidate);

        MathValue? value = null;
        var percentageBase = PercentageBase(candidate);
        if (percentageBase is not null)
        {
            value = Evaluate(percentageBase);
            if (value is not null)
            {
                value = Multiply(value, new MathValue.Exact(new Rational(1, 100)));
            }
        }

        if (value is null && (LooksLikeProse(candidate) || IsShortCompositeAnswer(candidate)))
        {
            return new MathParsedValue(null, NormalizeText(candidate), candidate);
        }

        value ??= Evaluate(candidate);
        if (value is not null)
        {
            return new MathParsedValue(value, null, candidate);
        }

        var equation = SplitPlainEquation(candidate);
        if (equation is { } sides)
        {
            try
            {
                return ParseCandidate(sides.Right);
            }
            catch (MathLimitException)
            {
                throw;
            }
            catch (MathParseException)
            {
                // fall through to the text form of the whole candidate
            }
        }

        var normalized = NormalizeText(candidate);
        if (normalized.Length > 0)
        {
            return new MathParsedValue(null, normalized, candidate);
        }

        throw new MathParseException("could not parse mathematical answer");
    }

    /// <summary>Port of <c>_parse_first</c>: the first candidate that parses; a limit or unsafe candidate aborts.</summary>
    internal static MathParsedValue ParseFirst(IReadOnlyList<string> candidates)
    {
        MathParseException? parseError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return ParseCandidate(candidate);
            }
            catch (MathLimitException)
            {
                throw;
            }
            catch (MathUnsafeException)
            {
                throw;
            }
            catch (MathParseException ex)
            {
                parseError = ex;
            }
        }

        throw parseError ?? new MathParseException("could not extract mathematical answer");
    }

    /// <summary>Port of <c>_matching_expression_candidate</c>: a numeric candidate matching a target, tried after opaque text failed.</summary>
    private static MathParsedValue? MatchingExpressionCandidate(IReadOnlyList<string> candidates, IReadOnlyList<MathParsedValue> parsedTargets)
    {
        foreach (var candidate in candidates)
        {
            MathParsedValue parsed;
            try
            {
                parsed = ParseCandidate(candidate);
            }
            catch (MathLimitException)
            {
                throw;
            }
            catch (MathUnsafeException)
            {
                throw;
            }
            catch (MathParseException)
            {
                continue;
            }

            if (parsed.Value is not null && parsedTargets.Any(target => Equivalent(target, parsed)))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates a candidate in the numeric subset: null when it is not numeric (a symbolic or textual answer); a
    /// <see cref="MathLimitException"/> when it is numeric but too expensive to evaluate exactly.
    /// </summary>
    internal static MathValue? Evaluate(string candidate)
    {
        var text = NormalizeExpression(candidate);
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            return new MathExpressionParser(text).ParseTop();
        }
        catch (NotNumericException)
        {
            return null;
        }
        catch (DivideByZeroException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The LaTeX normalisation Python delegates to <c>normalize_latex</c>: size and spacing commands, currency, units, degrees, synonyms.</summary>
    internal static string NormalizeExpression(string candidate)
    {
        var text = SizeCommands.Replace(candidate, "");
        if (ThinSpaceNumber.IsMatch(text))
        {
            text = text.Replace("\\,", ",", StringComparison.Ordinal);
        }

        text = SpacingCommands.Replace(text, "").Replace("\\ ", " ", StringComparison.Ordinal);
        text = text.Replace("\\$", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal);
        text = text.Replace("\\dfrac", "\\frac", StringComparison.Ordinal).Replace("\\tfrac", "\\frac", StringComparison.Ordinal).Replace("\\cfrac", "\\frac", StringComparison.Ordinal);
        text = text.Replace("\\times", "*", StringComparison.Ordinal).Replace("\\cdot", "*", StringComparison.Ordinal).Replace("\\div", "/", StringComparison.Ordinal);
        text = text.Replace("**", "^", StringComparison.Ordinal);
        text = DegreeSuffix.Replace(text, "");
        var withoutUnits = UnitText.Replace(text, " ");
        if (withoutUnits.Trim().Length > 0)
        {
            text = withoutUnits;
        }

        text = text.Trim();
        if (CommaNumber.IsMatch(text))
        {
            text = text.Replace(",", "", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    // ---- comparison ---------------------------------------------------------------------------------

    /// <summary>Port of <c>_expression_equivalent</c> over the numeric subset.</summary>
    internal static bool Equivalent(MathParsedValue left, MathParsedValue right)
    {
        if (left.Text is not null || right.Text is not null)
        {
            return left.Text is not null && right.Text is not null && string.Equals(left.Text, right.Text, StringComparison.Ordinal);
        }

        return left.Value is not null && right.Value is not null && ValuesEquivalent(left.Value, right.Value);
    }

    private static bool ValuesEquivalent(MathValue left, MathValue right)
    {
        switch (left, right)
        {
            case (MathValue.Tuple a, MathValue.Tuple b):
                return a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => ValuesEquivalent(pair.First, pair.Second));
            case (MathValue.Set a, MathValue.Set b):
                // set semantics: duplicates collapse, as in SymPy's FiniteSet
                return a.Items.All(item => b.Items.Any(other => ValuesEquivalent(item, other)))
                    && b.Items.All(item => a.Items.Any(other => ValuesEquivalent(item, other)));
            case (MathValue.Exact a, MathValue.Exact b):
                return a.Value == b.Value;
            case (MathValue.Tuple or MathValue.Set, _):
            case (_, MathValue.Tuple or MathValue.Set):
                return false;
            default:
                return NumericEquivalent(left.ToDouble(), right.ToDouble());
        }
    }

    /// <summary>Port of <c>_numeric_equivalent</c>: absolute or relative agreement within 1e-10 of finite values.</summary>
    private static bool NumericEquivalent(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
        {
            return false;
        }

        var error = Math.Abs(left - right);
        var scale = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 1e-10);
        return error < 1e-10 || error / scale < 1e-10;
    }

    // ---- arithmetic ---------------------------------------------------------------------------------

    internal static MathValue Add(MathValue a, MathValue b) => Arithmetic(a, b, (x, y) => x + y, (x, y) => x + y);

    internal static MathValue Subtract(MathValue a, MathValue b) => Arithmetic(a, b, (x, y) => x - y, (x, y) => x - y);

    internal static MathValue Multiply(MathValue a, MathValue b) => Arithmetic(a, b, (x, y) => x * y, (x, y) => x * y);

    internal static MathValue Divide(MathValue a, MathValue b)
    {
        if (b is MathValue.Exact { Value.Numerator.IsZero: true })
        {
            throw new DivideByZeroException();
        }

        return Arithmetic(a, b, (x, y) => x / y, (x, y) => x / y);
    }

    internal static MathValue Negate(MathValue a) => a switch
    {
        MathValue.Exact e => new MathValue.Exact(-e.Value),
        MathValue.Approx x => new MathValue.Approx(-x.Value),
        _ => throw new NotNumericException(),
    };

    private static MathValue Arithmetic(MathValue a, MathValue b, Func<Rational, Rational, Rational> exact, Func<double, double, double> approx)
    {
        if (a is MathValue.Exact ea && b is MathValue.Exact eb)
        {
            return new MathValue.Exact(exact(ea.Value, eb.Value));
        }

        if (a is MathValue.Tuple or MathValue.Set || b is MathValue.Tuple or MathValue.Set)
        {
            throw new NotNumericException();
        }

        return new MathValue.Approx(approx(a.ToDouble(), b.ToDouble()));
    }

    internal static MathValue Power(MathValue baseValue, MathValue exponent)
    {
        if (baseValue is MathValue.Tuple or MathValue.Set || exponent is MathValue.Tuple or MathValue.Set)
        {
            throw new NotNumericException();
        }

        if (baseValue is MathValue.Exact b && exponent is MathValue.Exact { Value.IsInteger: true } e)
        {
            var n = e.Value.Numerator;
            if (BigInteger.Abs(n) > SymbolicPowerExponentLimit)
            {
                throw new MathLimitException("expression contains an oversized exponent");
            }

            var power = (int)n;
            var bits = BigInteger.Max(BigInteger.Abs(b.Value.Numerator), b.Value.Denominator).GetBitLength() * (long)Math.Abs(power);
            if (bits > MaxResultBits)
            {
                throw new MathLimitException("expression is too expensive to evaluate exactly");
            }

            var numerator = BigInteger.Pow(b.Value.Numerator, Math.Abs(power));
            var denominator = BigInteger.Pow(b.Value.Denominator, Math.Abs(power));
            return new MathValue.Exact(power >= 0 ? new Rational(numerator, denominator) : new Rational(denominator, numerator));
        }

        if (baseValue is MathValue.Exact eb2 && exponent is MathValue.Exact { Value: { Denominator: var q, Numerator: var p } } && q == 2 && !eb2.Value.Numerator.IsZero)
        {
            // an exact square root when the base is a perfect square: sqrt(4)^3 stays exact
            var root = ExactRoot(eb2.Value, 2);
            if (root is not null)
            {
                return Power(new MathValue.Exact(root.Value), MathValue.Of(p));
            }
        }

        return new MathValue.Approx(Math.Pow(baseValue.ToDouble(), exponent.ToDouble()));
    }

    internal static MathValue Root(MathValue value, MathValue degree)
    {
        if (degree is MathValue.Exact { Value.IsInteger: true } d && d.Value.Numerator > 0 && d.Value.Numerator < 64 && value is MathValue.Exact e)
        {
            var root = ExactRoot(e.Value, (int)d.Value.Numerator);
            if (root is not null)
            {
                return new MathValue.Exact(root.Value);
            }
        }

        return Power(value, Divide(MathValue.Of(1), degree));
    }

    /// <summary>The exact n-th root of a non-negative rational when both parts are perfect powers.</summary>
    private static Rational? ExactRoot(Rational value, int degree)
    {
        if (value.Numerator.Sign < 0)
        {
            return null;
        }

        var numerator = IntegerRoot(value.Numerator, degree);
        var denominator = IntegerRoot(value.Denominator, degree);
        return numerator is not null && denominator is not null ? new Rational(numerator.Value, denominator.Value) : null;
    }

    private static BigInteger? IntegerRoot(BigInteger value, int degree)
    {
        if (value.IsZero || value.IsOne)
        {
            return value;
        }

        var estimate = (BigInteger)Math.Round(Math.Pow((double)value, 1.0 / degree));
        for (var candidate = BigInteger.Max(estimate - 1, BigInteger.One); candidate <= estimate + 1; candidate++)
        {
            if (BigInteger.Pow(candidate, degree) == value)
            {
                return candidate;
            }
        }

        return null;
    }

    internal static MathValue Factorial(MathValue value)
    {
        if (value is not MathValue.Exact { Value.IsInteger: true } e || e.Value.Numerator.Sign < 0)
        {
            throw new NotNumericException();
        }

        if (e.Value.Numerator > MaxFactorialArgument)
        {
            throw new MathLimitException("factorial argument is too large");
        }

        var n = (int)e.Value.Numerator;
        var result = BigInteger.One;
        for (var i = 2; i <= n; i++)
        {
            result *= i;
        }

        return MathValue.Of(result);
    }

    internal static MathValue Binomial(MathValue n, MathValue k)
    {
        if (n is not MathValue.Exact { Value.IsInteger: true } en || k is not MathValue.Exact { Value.IsInteger: true } ek || en.Value.Numerator.Sign < 0 || ek.Value.Numerator.Sign < 0)
        {
            throw new NotNumericException();
        }

        if (en.Value.Numerator > MaxFactorialArgument)
        {
            throw new MathLimitException("binomial argument is too large");
        }

        var total = (int)en.Value.Numerator;
        if (ek.Value.Numerator > total)
        {
            return MathValue.Of(BigInteger.Zero);
        }

        var choose = (int)ek.Value.Numerator;
        choose = Math.Min(choose, total - choose);
        var result = BigInteger.One;
        for (var i = 1; i <= choose; i++)
        {
            result = result * (total - choose + i) / i;
        }

        return MathValue.Of(result);
    }

    /// <summary>Raised inside the parser when a candidate is not a numeric expression (symbols, unsupported syntax).</summary>
    internal sealed class NotNumericException() : Exception("not a numeric expression");
}

/// <summary>A recursive-descent evaluator for the numeric subset of plain and LaTeX mathematical notation.</summary>
internal sealed class MathExpressionParser(string text)
{
    private enum Kind
    {
        Number,
        Name,
        Command,
        Symbol,
        EscapedBrace,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private static readonly HashSet<string> Functions = ["sqrt", "abs", "floor", "ceil", "factorial", "binomial", "exp", "ln", "log", "sin", "cos", "tan", "min", "max"];

    private readonly List<Token> _tokens = Tokenize(text);
    private int _index;

    /// <summary>Parses a whole candidate: a set or tuple literal, an equation whose value side is numeric, or an expression.</summary>
    public MathValue ParseTop()
    {
        var trimmed = text.Trim();
        var collection = ParseCollection(trimmed);
        if (collection is not null)
        {
            return collection;
        }

        var equation = MathAnswer.SplitPlainEquation(trimmed);
        if (equation is { } sides)
        {
            var right = TryParse(sides.Right);
            var left = TryParse(sides.Left);
            return right ?? left ?? throw new MathAnswer.NotNumericException();
        }

        var value = ParseExpression();
        Expect(Kind.End);
        return value;
    }

    private static MathValue? TryParse(string part)
    {
        var trimmed = part.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            var parser = new MathExpressionParser(trimmed);
            var value = parser.ParseExpression();
            parser.Expect(Kind.End);
            return value;
        }
        catch (MathAnswer.NotNumericException)
        {
            return null;
        }
    }

    /// <summary>A <c>\{...\}</c> / <c>{...}</c> set or a <c>(...)</c> / <c>[...]</c> tuple with a depth-0 comma.</summary>
    private static MathValue? ParseCollection(string trimmed)
    {
        foreach (var (opening, closing, isSet) in new[] { ("\\{", "\\}", true), ("{", "}", true), ("(", ")", false), ("[", "]", false) })
        {
            if (!trimmed.StartsWith(opening, StringComparison.Ordinal) || !trimmed.EndsWith(closing, StringComparison.Ordinal) || trimmed.Length < opening.Length + closing.Length)
            {
                continue;
            }

            var inner = trimmed[opening.Length..^closing.Length];
            var parts = SplitTopLevel(inner);
            if (parts is null || (!isSet && parts.Count < 2))
            {
                continue;
            }

            var items = new List<MathValue>();
            foreach (var part in parts)
            {
                var item = part.Trim();
                if (item.Length == 0)
                {
                    throw new MathAnswer.NotNumericException();
                }

                items.Add(new MathExpressionParser(item).ParseTop());
            }

            return isSet ? new MathValue.Set(items) : new MathValue.Tuple(items);
        }

        return null;
    }

    /// <summary>Splits at depth-0 commas; null when the brackets do not balance as one enclosing group.</summary>
    private static List<string>? SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch is '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is ')' or ']' or '}')
            {
                depth--;
                if (depth < 0)
                {
                    return null;
                }
            }
            else if (ch == ',' && depth == 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }

        if (depth != 0)
        {
            return null;
        }

        parts.Add(inner[start..]);
        return parts;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }

                if (i < text.Length && text[i] is 'e' or 'E')
                {
                    var j = i + 1;
                    if (j < text.Length && text[j] is '+' or '-')
                    {
                        j++;
                    }

                    var digitsStart = j;
                    while (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        j++;
                    }

                    if (j > digitsStart && (j >= text.Length || !char.IsAsciiLetter(text[j])))
                    {
                        i = j;
                    }
                }

                tokens.Add(new Token(Kind.Number, text[start..i], start));
                continue;
            }

            if (ch == '\\')
            {
                if (i + 1 < text.Length && text[i + 1] is '{' or '}')
                {
                    tokens.Add(new Token(Kind.EscapedBrace, text[i + 1].ToString(), i));
                    i += 2;
                    continue;
                }

                var start = i + 1;
                var end = start;
                while (end < text.Length && char.IsAsciiLetter(text[end]))
                {
                    end++;
                }

                if (end == start)
                {
                    throw new MathAnswer.NotNumericException();
                }

                tokens.Add(new Token(Kind.Command, text[start..end], i));
                i = end;
                continue;
            }

            if (char.IsAsciiLetter(ch))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiLetter(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Name, text[start..i], start));
                continue;
            }

            if ("+-*/^!(){}[],|=<>".Contains(ch, StringComparison.Ordinal))
            {
                tokens.Add(new Token(Kind.Symbol, ch.ToString(), i));
                i++;
                continue;
            }

            throw new MathAnswer.NotNumericException();
        }

        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    private Token Current => _tokens[_index];

    private Token Advance() => _tokens[_index++];

    private bool IsSymbol(string symbol) => Current.Kind == Kind.Symbol && Current.Text == symbol;

    private void Expect(Kind kind, string? symbol = null)
    {
        if (Current.Kind != kind || (symbol is not null && Current.Text != symbol))
        {
            throw new MathAnswer.NotNumericException();
        }

        _index++;
    }

    private MathValue ParseExpression()
    {
        var value = ParseTerm();
        while (IsSymbol("+") || IsSymbol("-"))
        {
            var op = Advance().Text;
            var right = ParseTerm();
            value = op == "+" ? MathAnswer.Add(value, right) : MathAnswer.Subtract(value, right);
        }

        return value;
    }

    private MathValue ParseTerm()
    {
        var value = ParseUnary();
        while (true)
        {
            if (IsSymbol("*") || IsSymbol("/"))
            {
                var op = Advance().Text;
                var right = ParseUnary();
                value = op == "*" ? MathAnswer.Multiply(value, right) : MathAnswer.Divide(value, right);
            }
            else if (StartsImplicitFactor())
            {
                value = MathAnswer.Multiply(value, ParseUnaryNoSign());
            }
            else
            {
                return value;
            }
        }
    }

    private bool StartsImplicitFactor() => Current.Kind switch
    {
        Kind.Command => Current.Text is "frac" or "sqrt" or "pi" or "binom",
        Kind.Name => Current.Text is "pi" or "e" || Functions.Contains(Current.Text),
        Kind.Symbol => Current.Text == "(",
        _ => false,
    };

    private MathValue ParseUnary()
    {
        if (IsSymbol("-"))
        {
            Advance();
            return MathAnswer.Negate(ParseUnary());
        }

        if (IsSymbol("+"))
        {
            Advance();
            return ParseUnary();
        }

        return ParseUnaryNoSign();
    }

    private MathValue ParseUnaryNoSign()
    {
        var value = ParsePostfix();
        if (IsSymbol("^"))
        {
            Advance();
            var exponent = ParseExponent();
            value = MathAnswer.Power(value, exponent);
        }

        return value;
    }

    private MathValue ParseExponent()
    {
        if (IsSymbol("{"))
        {
            Advance();
            var inner = ParseExpression();
            Expect(Kind.Symbol, "}");
            return inner;
        }

        if (IsSymbol("("))
        {
            return ParseUnary();
        }

        if (IsSymbol("-") || IsSymbol("+"))
        {
            var negative = Advance().Text == "-";
            var operand = ParseExponentAtom();
            return negative ? MathAnswer.Negate(operand) : operand;
        }

        return ParseExponentAtom();
    }

    private MathValue ParseExponentAtom()
    {
        var value = ParsePrimary();
        if (IsSymbol("^"))
        {
            Advance();
            value = MathAnswer.Power(value, ParseExponent());
        }

        return value;
    }

    private MathValue ParsePostfix()
    {
        var value = ParsePrimary();
        while (IsSymbol("!"))
        {
            Advance();
            value = MathAnswer.Factorial(value);
        }

        return value;
    }

    private MathValue ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case Kind.Number:
                Advance();
                var number = ParseNumber(token.Text);
                if (Current.Kind == Kind.Command && Current.Text == "frac" && number is MathValue.Exact { Value.IsInteger: true } && !token.Text.Contains('.'))
                {
                    // LaTeX mixed fraction "1\frac{1}{2}" (latex2sympy's interpret_as_mixed_fractions)
                    return MathAnswer.Add(number, ParsePrimary());
                }

                return number;
            case Kind.Symbol when token.Text == "(":
                Advance();
                var grouped = ParseExpression();
                Expect(Kind.Symbol, ")");
                return grouped;
            case Kind.Symbol when token.Text == "{":
                Advance();
                var braced = ParseExpression();
                Expect(Kind.Symbol, "}");
                return braced;
            case Kind.Symbol when token.Text == "|":
                Advance();
                var inner = ParseExpression();
                Expect(Kind.Symbol, "|");
                return Abs(inner);
            case Kind.Command:
                return ParseCommand();
            case Kind.Name:
                return ParseName();
            default:
                throw new MathAnswer.NotNumericException();
        }
    }

    private static MathValue ParseNumber(string text)
    {
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            return new MathValue.Approx(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return MathValue.Of(BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private MathValue ParseCommand()
    {
        var command = Advance().Text;
        switch (command)
        {
            case "frac":
                var numerator = ParseGroup();
                var denominator = ParseGroup();
                return MathAnswer.Divide(numerator, denominator);
            case "sqrt":
                MathValue degree = MathValue.Of(2);
                if (IsSymbol("["))
                {
                    Advance();
                    degree = ParseExpression();
                    Expect(Kind.Symbol, "]");
                }

                return MathAnswer.Root(ParseGroup(), degree);
            case "binom":
                var n = ParseGroup();
                var k = ParseGroup();
                return MathAnswer.Binomial(n, k);
            case "pi":
                return new MathValue.Approx(Math.PI);
            default:
                throw new MathAnswer.NotNumericException();
        }
    }

    /// <summary>A LaTeX argument: a braced expression, or a single number/constant token (<c>\frac12</c>, <c>\sqrt2</c>).</summary>
    private MathValue ParseGroup()
    {
        if (IsSymbol("{"))
        {
            Advance();
            var value = ParseExpression();
            Expect(Kind.Symbol, "}");
            return value;
        }

        if (Current.Kind == Kind.Number)
        {
            var token = Advance();
            return ParseNumber(token.Text);
        }

        if (Current.Kind is Kind.Command or Kind.Name || IsSymbol("("))
        {
            return ParsePrimary();
        }

        throw new MathAnswer.NotNumericException();
    }

    private MathValue ParseName()
    {
        var name = Advance().Text;
        switch (name)
        {
            case "pi":
                return new MathValue.Approx(Math.PI);
            case "e":
                return new MathValue.Approx(Math.E);
        }

        if (!Functions.Contains(name) || !IsSymbol("("))
        {
            throw new MathAnswer.NotNumericException();
        }

        Advance();
        var args = new List<MathValue>();
        if (!IsSymbol(")"))
        {
            args.Add(ParseExpression());
            while (IsSymbol(","))
            {
                Advance();
                args.Add(ParseExpression());
            }
        }

        Expect(Kind.Symbol, ")");
        return name switch
        {
            "sqrt" when args.Count == 1 => MathAnswer.Root(args[0], MathValue.Of(2)),
            "abs" when args.Count == 1 => Abs(args[0]),
            "factorial" when args.Count == 1 => MathAnswer.Factorial(args[0]),
            "binomial" when args.Count == 2 => MathAnswer.Binomial(args[0], args[1]),
            "floor" when args.Count == 1 => Rounded(args[0], Math.Floor),
            "ceil" when args.Count == 1 => Rounded(args[0], Math.Ceiling),
            "exp" when args.Count == 1 => new MathValue.Approx(Math.Exp(args[0].ToDouble())),
            "ln" when args.Count == 1 => new MathValue.Approx(Math.Log(args[0].ToDouble())),
            "log" when args.Count == 1 => new MathValue.Approx(Math.Log(args[0].ToDouble())),
            "log" when args.Count == 2 => new MathValue.Approx(Math.Log(args[0].ToDouble()) / Math.Log(args[1].ToDouble())),
            "sin" when args.Count == 1 => new MathValue.Approx(Math.Sin(args[0].ToDouble())),
            "cos" when args.Count == 1 => new MathValue.Approx(Math.Cos(args[0].ToDouble())),
            "tan" when args.Count == 1 => new MathValue.Approx(Math.Tan(args[0].ToDouble())),
            "min" when args.Count > 0 => args.MinBy(a => a.ToDouble())!,
            "max" when args.Count > 0 => args.MaxBy(a => a.ToDouble())!,
            _ => throw new MathAnswer.NotNumericException(),
        };
    }

    private static MathValue Abs(MathValue value) => value switch
    {
        MathValue.Exact e => e.Value.Numerator.Sign < 0 ? new MathValue.Exact(-e.Value) : e,
        MathValue.Approx a => new MathValue.Approx(Math.Abs(a.Value)),
        _ => throw new MathAnswer.NotNumericException(),
    };

    private static MathValue Rounded(MathValue value, Func<double, double> round) => value switch
    {
        MathValue.Exact { Value.IsInteger: true } e => e,
        MathValue.Exact e => MathValue.Of(new BigInteger(round(e.Value.ToDouble()))),
        MathValue.Approx a => MathValue.Of(new BigInteger(round(a.Value))),
        _ => throw new MathAnswer.NotNumericException(),
    };
}
