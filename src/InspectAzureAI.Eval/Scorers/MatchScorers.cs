using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>scorer/_common.py</c> (<c>str_match_scorer</c>, <c>match_str</c> and the numeric helpers),
/// <c>scorer/_pattern.py</c> and the text helpers of <c>_util/text.py</c> they rely on.
/// </summary>
internal static class MatchScorers
{
    /// <summary>The tuple a matching function returns: the answer extracted from the completion and whether it matched.</summary>
    public readonly record struct MatchResult(string Answer, bool Matched);

    /// <summary>Python's <c>string.punctuation</c>.</summary>
    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>Python's <c>string.whitespace</c>.</summary>
    private const string Whitespace = " \t\n\r\v\f";

    /// <summary>
    /// Sentence and enclosing punctuation trimmed from a numeric word. Operators such as <c>&lt;</c> or <c>~</c>
    /// are deliberately excluded because they express bounds or approximations, not the value itself.
    /// </summary>
    private const string NumericPunctuationTrim = "!?:;()[]{}'\"`";

    private static readonly char[] PunctuationTrimChars = (Whitespace + Punctuation).ToCharArray();
    private static readonly char[] NumericPunctuationTrimChars = NumericPunctuationTrim.ToCharArray();

    private static readonly Regex LatexEscapedSymbol = new(@"\\([$,£,€*_])", RegexOptions.Compiled);
    private static readonly Regex NumericSymbols = new(@"[$,£,€,*,_]", RegexOptions.Compiled);
    private static readonly Regex TrailingDot = new(@"\.(?=\s|$|\D)", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Port of <c>str_match_scorer</c>: the first target that matches is CORRECT; the explanation is always the completion.</summary>
    public static Scorer StrMatchScorer(Func<string, string, MatchResult> match) =>
        (state, target, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            var completion = state.Output.Completion;
            string? answer = null;
            foreach (var value in target.Values)
            {
                var result = match(completion, value);
                answer = result.Answer;
                if (result.Matched)
                {
                    return Task.FromResult(new Score(ScoreConstants.Correct) { Answer = answer, Explanation = completion });
                }
            }

            return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = answer, Explanation = completion });
        };

    /// <summary>Port of <c>match_str</c>.</summary>
    public static MatchResult MatchStr(
        string value,
        string target,
        string location = "end",
        bool ignoreCase = true,
        bool ignorePunctuation = true,
        bool numeric = false)
    {
        var v = value.Trim();
        var t = target.Trim();
        var answer = v;

        if (ignoreCase)
        {
            v = v.ToLowerInvariant();
            t = t.ToLowerInvariant();
        }

        if (numeric)
        {
            t = StripNumericPunctuation(t);
        }

        if (numeric && IsNumber(t))
        {
            // A numeric target never falls through to the text comparison: "25".endswith("5") is true
            // but 25 != 5, so both sides are normalised as numbers and compared for equality.
            v = StripNumericPunctuation(v);
            t = NormalizeNumber(t);
            var words = WhitespaceRun.Split(v);
            switch (location)
            {
                case "begin":
                    v = FirstNumberNormalized(words);
                    break;
                case "end":
                    Array.Reverse(words);
                    v = FirstNumberNormalized(words);
                    break;
                case "exact":
                    // exact stays actually exact: no punctuation trimming, so "(42)" does not match "42"
                    v = NormalizeNumber(v, trimPunctuation: false);
                    break;
                default:
                    foreach (var number in AllNumbersNormalized(words))
                    {
                        if (number == t)
                        {
                            return new MatchResult(number, true);
                        }
                    }

                    return new MatchResult(answer, false);
            }

            return new MatchResult(v, v == t);
        }

        if (ignorePunctuation)
        {
            v = StripPunctuation(v);
            t = StripPunctuation(t);
        }

        return location switch
        {
            "begin" => new MatchResult(answer, v.StartsWith(t, StringComparison.Ordinal)),
            "end" => new MatchResult(answer, v.EndsWith(t, StringComparison.Ordinal)),
            "exact" => new MatchResult(answer, v == t),
            _ => new MatchResult(answer, v.Contains(t, StringComparison.Ordinal)),
        };
    }

    /// <summary>Port of <c>strip_punctuation</c>: trims ASCII whitespace and punctuation from both ends.</summary>
    public static string StripPunctuation(string s) => s.Trim(PunctuationTrimChars);

    /// <summary>Port of <c>strip_numeric_punctuation</c>: un-escapes LaTeX symbols, drops currency/grouping/markdown characters and non-decimal dots.</summary>
    public static string StripNumericPunctuation(string s)
    {
        var unescaped = LatexEscapedSymbol.Replace(s, "$1");
        var stripped = NumericSymbols.Replace(unescaped, "");
        return TrailingDot.Replace(stripped, "");
    }

    /// <summary>Port of <c>_parse_number</c>: plain float syntax or a Unicode number, finite only; null otherwise.</summary>
    public static double? ParseNumber(string s, bool trimPunctuation = true)
    {
        var cleaned = trimPunctuation ? s.Trim(NumericPunctuationTrimChars) : s;
        if (ValueToFloat.TryParseFiniteNumber(cleaned, out var plain))
        {
            return plain;
        }

        return UnicodeNumber.TryParse(cleaned, out var unicode) && double.IsFinite(unicode) ? unicode : null;
    }

    public static bool IsNumber(string s) => ParseNumber(s) is not null;

    public static string FirstNumberNormalized(IEnumerable<string> words)
    {
        var number = words.FirstOrDefault(IsNumber);
        return number is null ? "" : NormalizeNumber(number);
    }

    public static IReadOnlyList<string> AllNumbersNormalized(IEnumerable<string> words) =>
        words.Where(IsNumber).Select(word => NormalizeNumber(word)).ToList();

    /// <summary>Port of <c>normalize_number</c>: Python <c>format(num, ".5g")</c>, or the input when it is not a number.</summary>
    public static string NormalizeNumber(string number, int precision = 5, bool trimPunctuation = true)
    {
        var parsed = ParseNumber(number, trimPunctuation);
        return parsed is null ? number : FormatGeneral(parsed.Value, precision);
    }

    /// <summary>Python's <c>.Ng</c> format: .NET's <c>G</c> differs only in the exponent marker case.</summary>
    internal static string FormatGeneral(double value, int precision) =>
        value.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).Replace('E', 'e');

    /// <summary>Port of <c>pattern()</c> (<c>scorer/_pattern.py</c>).</summary>
    public static Scorer Pattern(string pattern, bool ignoreCase, bool matchAll)
    {
        var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return (state, target, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            var completion = state.Output.Completion;
            var match = regex.Match(completion);
            if (!match.Success)
            {
                // The model was told to answer in a specific format and did not: an instruction-following
                // failure charged to the model under test, not a scoring failure.
                return Task.FromResult(new Score(ScoreConstants.Incorrect)
                {
                    Reason = "invalid_response_format",
                    Explanation = "Scoring pattern not matched in output: " + completion,
                });
            }

            var groups = CaptureGroups(match);
            string? foundMatch;
            string? answer;
            if (matchAll)
            {
                foundMatch = MatchAllGroups(groups, target, ignoreCase);
                answer = foundMatch;
            }
            else
            {
                foundMatch = MatchFirst(groups, target, ignoreCase);
                // A single group usually extracts one templated answer; reporting the failed extraction is
                // useful to the user even though it did not match.
                answer = foundMatch is null && groups.Count == 1 ? groups[0] : foundMatch;
            }

            return Task.FromResult(new Score(string.IsNullOrEmpty(foundMatch) ? ScoreConstants.Incorrect : ScoreConstants.Correct)
            {
                Answer = answer,
                Explanation = completion,
            });
        };
    }

    /// <summary>Python <c>match.groups() or (match.group(0),)</c>: unmatched groups are null.</summary>
    private static IReadOnlyList<string?> CaptureGroups(Match match)
    {
        if (match.Groups.Count == 1)
        {
            return [match.Value];
        }

        var groups = new List<string?>();
        for (var i = 1; i < match.Groups.Count; i++)
        {
            groups.Add(match.Groups[i].Success ? match.Groups[i].Value : null);
        }

        return groups;
    }

    private static bool MatchTarget(string match, Target target, bool ignoreCase)
    {
        if (!ignoreCase)
        {
            return target.Values.Contains(match, StringComparer.Ordinal);
        }

        var lowered = match.ToLowerInvariant();
        return target.Values.Any(t => t.ToLowerInvariant() == lowered);
    }

    private static string? MatchFirst(IReadOnlyList<string?> matches, Target target, bool ignoreCase) =>
        matches.FirstOrDefault(m => m is not null && MatchTarget(m, target, ignoreCase));

    private static string? MatchAllGroups(IReadOnlyList<string?> matches, Target target, bool ignoreCase)
    {
        var matchedAny = false;
        foreach (var match in matches)
        {
            if (match is null)
            {
                continue;
            }

            matchedAny = true;
            if (!MatchTarget(match, target, ignoreCase))
            {
                return null;
            }
        }

        // Every group unmatched (all optional) means nothing was extracted, so this must not be a match.
        return matchedAny ? target.Text : null;
    }
}

/// <summary>
/// Port of <c>scorer/_unicode.py</c> <c>unicode_number_to_float</c>: Unicode signs, vulgar fractions,
/// superscript exponents, digits of any script with locale separators, and Chinese numerals.
/// Failures are reported as <c>false</c> where Python raises <c>ValueError</c>.
/// </summary>
internal static class UnicodeNumber
{
    private const string SuperscriptDigits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
    private const string SuperscriptSigns = "⁻⁺";
    private const string SuperscriptSource = "⁰¹²³⁴⁵⁶⁷⁸⁹⁻⁺";
    private const string SuperscriptAscii = "0123456789-+";

    private static readonly HashSet<char> MinusChars = ['-', '−', '﹣', '－'];
    private static readonly HashSet<char> PlusChars = ['+', '＋', '﹢'];
    private static readonly HashSet<char> DecimalSepExtras = ['٫', '．'];
    private static readonly HashSet<char> GroupSeparators =
        [',', '\uff0c', '\ufe50', '\u3001', '\u066c', ' ', '\u00a0', '\u202f', '\u2009', '\u2007', '\u2008', '\'', '\u2019', '\u02bc', '\uff07'];

    private static readonly Dictionary<char, int> ChDigits = new()
    {
        ['零'] = 0, ['〇'] = 0, ['○'] = 0, ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6,
        ['七'] = 7, ['八'] = 8, ['九'] = 9, ['壹'] = 1, ['贰'] = 2, ['貳'] = 2, ['叁'] = 3, ['參'] = 3, ['肆'] = 4,
        ['伍'] = 5, ['陆'] = 6, ['陸'] = 6, ['柒'] = 7, ['捌'] = 8, ['玖'] = 9, ['两'] = 2, ['兩'] = 2, ['幺'] = 1,
    };

    private static readonly Dictionary<char, int> ChAltTens = new() { ['廿'] = 20, ['卅'] = 30, ['卌'] = 40 };
    private static readonly Dictionary<char, int> ChSmallUnits = new() { ['十'] = 10, ['拾'] = 10, ['百'] = 100, ['佰'] = 100, ['千'] = 1000, ['仟'] = 1000 };
    private static readonly Dictionary<char, long> ChLargeUnits = new() { ['万'] = 10_000, ['萬'] = 10_000, ['亿'] = 100_000_000, ['億'] = 100_000_000, ['兆'] = 1_000_000_000_000 };
    private static readonly HashSet<char> ChNeg = ['负', '負'];
    private static readonly HashSet<char> ChPos = ['正'];
    private static readonly HashSet<char> ChDecWords = ['点', '點'];

    public static bool TryParse(string s, out double result)
    {
        result = 0;
        var text = s.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        (text, var negative) = ConsumeLeadingSigns(text);

        var single = TrySingleCharNumeric(text);
        if (single is not null)
        {
            result = negative ? -single.Value : single.Value;
            return true;
        }

        if (!TrySplitSuperscriptExponent(text, out var baseText, out var exponent))
        {
            return false;
        }

        var baseValue = ParseNumberWithOptionalVulgarFraction(baseText);
        if (baseValue is null)
        {
            return false;
        }

        var value = negative ? -baseValue.Value : baseValue.Value;
        result = exponent is null ? value : Math.Pow(value, exponent.Value);
        return true;
    }

    private static (string Text, bool Negative) ConsumeLeadingSigns(string s)
    {
        var negative = false;
        var text = s.TrimStart();
        while (text.Length > 0)
        {
            var ch = text[0];
            if (MinusChars.Contains(ch) || ChNeg.Contains(ch))
            {
                negative = !negative;
                text = text[1..].TrimStart();
                continue;
            }

            if (PlusChars.Contains(ch) || ChPos.Contains(ch))
            {
                text = text[1..].TrimStart();
                continue;
            }

            break;
        }

        return (text, negative);
    }

    /// <summary>Python <c>unicodedata.numeric(ch)</c> for a single character (digits, fractions, Roman numerals, ...).</summary>
    private static double? TrySingleCharNumeric(string s)
    {
        if (s.Length != 1)
        {
            return null;
        }

        var numeric = CharUnicodeInfo.GetNumericValue(s[0]);
        return numeric < 0 ? null : numeric;
    }

    private static bool TrySplitSuperscriptExponent(string s, out string baseText, out int? exponent)
    {
        baseText = s.Trim();
        exponent = null;
        if (s.Length == 0)
        {
            return true;
        }

        var end = s.Length - 1;
        while (end >= 0 && (char.IsWhiteSpace(s[end]) || GroupSeparators.Contains(s[end])))
        {
            end--;
        }

        if (end < 0)
        {
            return true;
        }

        var j = end;
        while (j >= 0 && SuperscriptDigits.Contains(s[j]))
        {
            j--;
        }

        var start = j + 1;
        if (start > end)
        {
            return true;
        }

        if (j >= 0 && SuperscriptSigns.Contains(s[j]))
        {
            start = j;
        }

        var head = s[..start].TrimEnd();
        if (head.Length == 0)
        {
            return true;
        }

        var ascii = new StringBuilder();
        foreach (var ch in s[start..(end + 1)])
        {
            ascii.Append(SuperscriptAscii[SuperscriptSource.IndexOf(ch)]);
        }

        var exponentText = ascii.ToString();
        if (exponentText.All(c => c is '+' or '-') || !int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        baseText = head;
        exponent = value;
        return true;
    }

    private static double? FractionCharValue(char ch)
    {
        var value = CharUnicodeInfo.GetNumericValue(ch);
        return value > 0.0 && value < 1.0 ? value : null;
    }

    private static double? ParseNumberWithOptionalVulgarFraction(string s)
    {
        if (s.Length == 0)
        {
            return null;
        }

        var hits = new List<(int Index, double Value)>();
        for (var i = 0; i < s.Length; i++)
        {
            if (FractionCharValue(s[i]) is { } fraction)
            {
                hits.Add((i, fraction));
            }
        }

        if (hits.Count == 0)
        {
            return ParseCoreNumberNoLeadingSigns(s);
        }

        if (hits.Count > 1)
        {
            return null;
        }

        var (index, fractionValue) = hits[0];
        var head = s[..index].Trim();
        var tail = s[(index + 1)..].Trim();
        if (tail.Length > 0 && tail.Any(c => !(char.IsWhiteSpace(c) || GroupSeparators.Contains(c))))
        {
            return null;
        }

        var headValue = 0.0;
        if (head.Length > 0)
        {
            var parsedHead = ParseCoreNumberNoLeadingSigns(head);
            if (parsedHead is null)
            {
                return null;
            }

            headValue = parsedHead.Value;
        }

        return headValue + fractionValue;
    }

    private static double? ParseCoreNumberNoLeadingSigns(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (LooksLikeChineseNumeral(s) && ParseChineseNumber(s) is { } chinese)
        {
            return chinese;
        }

        if (ParseGenericUnicodeDigits(s) is { } generic)
        {
            return generic;
        }

        return s.Length == 1 ? TrySingleCharNumeric(s) : null;
    }

    private static bool LooksLikeChineseNumeral(string s) =>
        s.Any(ch => ChDigits.ContainsKey(ch) || ChSmallUnits.ContainsKey(ch) || ChLargeUnits.ContainsKey(ch)
            || ChDecWords.Contains(ch) || ChNeg.Contains(ch) || ChPos.Contains(ch) || ChAltTens.ContainsKey(ch));

    private static bool IsChineseNumeralChar(char ch) =>
        ChDecWords.Contains(ch) || ChDigits.ContainsKey(ch) || ChSmallUnits.ContainsKey(ch) || ChLargeUnits.ContainsKey(ch) || ChAltTens.ContainsKey(ch);

    /// <summary>Port of <c>_parse_generic_unicode_digits</c>: digits across scripts with a decimal-mark heuristic and ASCII e-notation.</summary>
    private static double? ParseGenericUnicodeDigits(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        char? decimalChar = null;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalChar = lastDot > lastComma ? '.' : ',';
        }
        else if (lastDot >= 0)
        {
            decimalChar = '.';
        }
        else if (lastComma >= 0)
        {
            var tailDigits = s[(lastComma + 1)..].Count(ch => CharToAsciiDigit(ch) is not null);
            if (tailDigits is 1 or 2 or 3)
            {
                decimalChar = ',';
            }
        }

        var output = new StringBuilder();
        var seenDecimal = false;
        var seenExponent = false;
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if ((ch is 'e' or 'E') && !seenExponent)
            {
                if (output.Length == 0)
                {
                    return null;
                }

                output.Append(ch);
                seenExponent = true;
                i++;
                if (i < s.Length && (PlusChars.Contains(s[i]) || MinusChars.Contains(s[i])))
                {
                    output.Append(PlusChars.Contains(s[i]) ? '+' : '-');
                    i++;
                }

                continue;
            }

            if (DecimalSepExtras.Contains(ch) || (decimalChar is not null && ch == decimalChar))
            {
                if (seenDecimal)
                {
                    return null;
                }

                output.Append('.');
                seenDecimal = true;
                i++;
                continue;
            }

            if (GroupSeparators.Contains(ch) || char.IsWhiteSpace(ch) || (decimalChar is not null && (ch is ',' or '.') && ch != decimalChar))
            {
                i++;
                continue;
            }

            if (CharToAsciiDigit(ch) is { } digit)
            {
                output.Append(digit);
                i++;
                continue;
            }

            return null;
        }

        var joined = output.ToString();
        if (joined.Length == 0 || joined.All(c => c is '.' or 'e' or 'E' or '+' or '-'))
        {
            return null;
        }

        return double.TryParse(joined, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Python <c>unicodedata.decimal</c> / <c>unicodedata.digit</c> plus the Chinese digit table.</summary>
    private static char? CharToAsciiDigit(char ch)
    {
        if (ch is >= '0' and <= '9')
        {
            return ch;
        }

        var decimalDigit = CharUnicodeInfo.GetDecimalDigitValue(ch);
        if (decimalDigit >= 0)
        {
            return (char)('0' + decimalDigit);
        }

        var digit = CharUnicodeInfo.GetDigitValue(ch);
        if (digit >= 0)
        {
            return (char)('0' + digit);
        }

        return ChDigits.TryGetValue(ch, out var chinese) ? (char)('0' + chinese) : null;
    }

    private static double? ParseChineseNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        var integerPart = s;
        var fractionPart = "";
        foreach (var separator in new[] { '点', '點', '．', '.' })
        {
            var at = s.IndexOf(separator);
            if (at >= 0)
            {
                integerPart = s[..at];
                fractionPart = s[(at + 1)..];
                break;
            }
        }

        double integerValue;
        if (!integerPart.Any(ch => ChSmallUnits.ContainsKey(ch) || ChLargeUnits.ContainsKey(ch) || ChAltTens.ContainsKey(ch)))
        {
            var digits = new StringBuilder();
            foreach (var ch in integerPart.Where(c => !char.IsWhiteSpace(c)))
            {
                if (CharToAsciiDigit(ch) is not { } digit)
                {
                    return null;
                }

                digits.Append(digit);
            }

            integerValue = digits.Length == 0 ? 0 : double.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        else
        {
            var withUnits = ParseChineseIntegerWithUnits(integerPart);
            if (withUnits is null)
            {
                return null;
            }

            integerValue = withUnits.Value;
        }

        var fractionValue = 0.0;
        var scale = 0.1;
        foreach (var ch in fractionPart.Where(c => !char.IsWhiteSpace(c)))
        {
            if (CharToAsciiDigit(ch) is not { } digit)
            {
                return null;
            }

            fractionValue += (digit - '0') * scale;
            scale /= 10.0;
        }

        return integerValue + fractionValue;
    }

    private static long? ParseChineseIntegerWithUnits(string s)
    {
        long total = 0;
        long section = 0;
        long number = 0;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (ChAltTens.TryGetValue(ch, out var altTen))
            {
                section += altTen;
                continue;
            }

            if (ch is '零' or '〇' or '○')
            {
                continue;
            }

            if (ChDigits.TryGetValue(ch, out var chineseDigit))
            {
                number = chineseDigit;
                continue;
            }

            if (CharToAsciiDigit(ch) is { } digit)
            {
                number = digit - '0';
                continue;
            }

            if (ChSmallUnits.TryGetValue(ch, out var smallUnit))
            {
                section += (number == 0 ? 1 : number) * smallUnit;
                number = 0;
                continue;
            }

            if (ChLargeUnits.TryGetValue(ch, out var largeUnit))
            {
                section += number;
                number = 0;
                total += section * largeUnit;
                section = 0;
                continue;
            }

            return null;
        }

        section += number;
        total += section;
        return total;
    }
}
