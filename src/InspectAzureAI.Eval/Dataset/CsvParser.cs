using System.Text;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>One CSV record with the physical line the reader was on after reading it (Python <c>reader.line_num</c>).</summary>
internal sealed record CsvRecord(IReadOnlyList<string> Fields, int LineNumber);

/// <summary>
/// Minimal RFC 4180 reader matching Python's <c>csv</c> "unix" dialect as read by <c>csv_dataset</c>:
/// double-quote quoting with <c>""</c> escapes, quoted fields may span lines, LF / CRLF / CR terminators,
/// non-strict (a quote inside an unquoted field is literal). A blank line yields an empty record, which
/// <c>DictReader</c> skips.
/// </summary>
internal static class CsvParser
{
    public static IEnumerable<CsvRecord> Parse(string text, char delimiter = ',')
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var inQuotes = false;
        var fieldStarted = false;
        var recordStarted = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    line++;
                }
                else if (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'))
                {
                    line++;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                recordStarted = true;
                i++;
                continue;
            }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                recordStarted = true;
                i++;
                continue;
            }

            if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                i++;
                if (recordStarted || fieldStarted)
                {
                    fields.Add(field.ToString());
                }

                yield return new CsvRecord(fields.ToArray(), line - 1);
                fields.Clear();
                field.Clear();
                fieldStarted = false;
                recordStarted = false;
                continue;
            }

            field.Append(c);
            fieldStarted = true;
            recordStarted = true;
            i++;
        }

        if (recordStarted || fieldStarted)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord(fields.ToArray(), line);
        }
    }
}
