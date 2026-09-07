using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// A lightweight tabular result: ordered column names plus rows of cells. It stands in for the pandas
/// <c>DataFrame</c> that the Python <c>inspect_ai.analysis</c> functions return. A cell is <c>null</c>, a
/// <see cref="bool"/>, a <see cref="long"/>, a <see cref="double"/>, a <see cref="string"/>, a
/// <see cref="DateTimeOffset"/>, a <see cref="DateOnly"/> or a <see cref="TimeOnly"/> (the <see cref="ColumnType"/>
/// set); lists and dictionaries read from logs arrive already serialised as JSON strings, as they do in Python.
/// </summary>
public sealed class Table
{
    private readonly List<string> _columns;
    private readonly Dictionary<string, int> _index;
    private readonly List<object?[]> _rows;

    /// <summary>Creates a table; every row must have one cell per column and cells must be of a supported type.</summary>
    public Table(IEnumerable<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        _columns = columns.ToList();
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _columns.Count; i++)
        {
            if (!_index.TryAdd(_columns[i], i))
            {
                throw new ArgumentException($"Duplicate column name '{_columns[i]}'.", nameof(columns));
            }
        }

        _rows = [];
        foreach (var row in rows)
        {
            if (row.Count != _columns.Count)
            {
                throw new ArgumentException($"Row {_rows.Count} has {row.Count} cells but the table has {_columns.Count} columns.", nameof(rows));
            }

            var cells = new object?[row.Count];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = NormalizeCell(row[i]);
            }

            _rows.Add(cells);
        }
    }

    /// <summary>A table with no columns and no rows (what the Python functions return for an empty log set).</summary>
    public static Table Empty { get; } = new([], []);

    /// <summary>Column names in order.</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>The rows, each a list of cells in column order.</summary>
    public IReadOnlyList<IReadOnlyList<object?>> Rows => _rows;

    public int RowCount => _rows.Count;

    public int ColumnCount => _columns.Count;

    /// <summary>pandas' <c>DataFrame.empty</c>: true when there are no rows or no columns.</summary>
    public bool IsEmpty => _rows.Count == 0 || _columns.Count == 0;

    /// <summary>The cell at <paramref name="row"/> in <paramref name="column"/>; an unknown column is a <see cref="KeyNotFoundException"/>.</summary>
    public object? this[int row, string column] => _rows[row][ColumnIndex(column)];

    public bool HasColumn(string name) => _index.ContainsKey(name);

    /// <summary>Index of <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name) => _index.TryGetValue(name, out var index) ? index : -1;

    /// <summary>All cells of a column, in row order.</summary>
    public IReadOnlyList<object?> Column(string name)
    {
        var index = ColumnIndex(name);
        return _rows.Select(row => row[index]).ToList();
    }

    /// <summary>A row as a name-to-cell dictionary (in column order).</summary>
    public IReadOnlyDictionary<string, object?> Record(int row)
    {
        var cells = _rows[row];
        var record = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < _columns.Count; i++)
        {
            record[_columns[i]] = cells[i];
        }

        return record;
    }

    /// <summary>The table restricted to <paramref name="columns"/>, in that order (pandas <c>df[cols]</c>); an unknown name is an <see cref="ArgumentException"/>.</summary>
    public Table Select(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = columns.ToList();
        var indexes = names.Select(name => _index.TryGetValue(name, out var index) ? index : throw new ArgumentException($"Unknown column '{name}'.", nameof(columns))).ToList();
        return new Table(names, _rows.Select(row => (IReadOnlyList<object?>)indexes.Select(index => row[index]).ToArray()));
    }

    /// <summary>The table with <paramref name="name"/> added (at the end) or replaced (in place) by <paramref name="values"/>, one per row.</summary>
    public Table WithColumn(string name, IReadOnlyList<object?> values)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != _rows.Count)
        {
            throw new ArgumentException($"Column '{name}' has {values.Count} values but the table has {_rows.Count} rows.", nameof(values));
        }

        var existing = IndexOf(name);
        var columns = existing >= 0 ? _columns : [.. _columns, name];
        var rows = new List<IReadOnlyList<object?>>(_rows.Count);
        for (var r = 0; r < _rows.Count; r++)
        {
            object?[] cells;
            if (existing >= 0)
            {
                cells = (object?[])_rows[r].Clone();
                cells[existing] = values[r];
            }
            else
            {
                cells = [.. _rows[r], values[r]];
            }

            rows.Add(cells);
        }

        return new Table(columns, rows);
    }

    /// <summary>The rows for which <paramref name="predicate"/> (given the row index) is true.</summary>
    public Table Where(Func<int, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new Table(_columns, Enumerable.Range(0, _rows.Count).Where(predicate).Select(r => (IReadOnlyList<object?>)_rows[r]));
    }

    /// <summary>pandas <c>drop_duplicates(subset=column, keep="first")</c>: the first row of every distinct value of <paramref name="column"/>.</summary>
    public Table DistinctBy(string column)
    {
        var index = ColumnIndex(column);
        var seen = new HashSet<object>(CellComparer.Instance);
        return Where(r => seen.Add(_rows[r][index] ?? CellComparer.NullKey));
    }

    /// <summary>
    /// pandas <c>left.merge(right, on=on, how="left", suffixes=(leftSuffix, rightSuffix))</c>: every left row joined
    /// with the right rows sharing its <paramref name="on"/> value (nulls when there is none); columns present on both
    /// sides get the suffixes.
    /// </summary>
    public Table LeftMerge(Table right, string on, string leftSuffix, string rightSuffix)
    {
        ArgumentNullException.ThrowIfNull(right);
        var leftKey = ColumnIndex(on);
        var rightKey = right.ColumnIndex(on);
        var overlap = _columns.Where(right.HasColumn).Where(name => name != on).ToHashSet(StringComparer.Ordinal);
        var columns = _columns.Select(name => overlap.Contains(name) ? name + leftSuffix : name)
            .Concat(right._columns.Where(name => name != on).Select(name => overlap.Contains(name) ? name + rightSuffix : name))
            .ToList();
        var rightIndexes = Enumerable.Range(0, right._columns.Count).Where(i => i != rightKey).ToList();
        var lookup = new Dictionary<object, List<object?[]>>(CellComparer.Instance);
        foreach (var row in right._rows)
        {
            var key = row[rightKey] ?? CellComparer.NullKey;
            if (!lookup.TryGetValue(key, out var matches))
            {
                matches = [];
                lookup[key] = matches;
            }

            matches.Add(row);
        }

        var rows = new List<IReadOnlyList<object?>>();
        foreach (var row in _rows)
        {
            if (lookup.TryGetValue(row[leftKey] ?? CellComparer.NullKey, out var matches))
            {
                foreach (var match in matches)
                {
                    rows.Add([.. row, .. rightIndexes.Select(i => match[i])]);
                }
            }
            else
            {
                rows.Add([.. row, .. rightIndexes.Select(_ => (object?)null)]);
            }
        }

        return new Table(columns, rows);
    }

    /// <summary>
    /// pandas <c>DataFrame(records)</c>: columns in order of first appearance across the records, missing keys as
    /// null. No records gives <see cref="Empty"/>.
    /// </summary>
    public static Table FromRecords(IEnumerable<IReadOnlyDictionary<string, object?>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records.ToList();
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in list)
        {
            foreach (var key in record.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(key);
                }
            }
        }

        return new Table(columns, list.Select(record => (IReadOnlyList<object?>)columns.Select(column => record.TryGetValue(column, out var value) ? value : null).ToArray()));
    }

    /// <summary>Writes the table as RFC 4180 CSV (header row first, <c>\n</c> line endings, minimal quoting); null and NaN are empty fields.</summary>
    public void WriteCsv(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(string.Join(',', _columns.Select(CsvField)));
        writer.Write('\n');
        foreach (var row in _rows)
        {
            writer.Write(string.Join(',', row.Select(cell => CsvField(FormatCell(cell)))));
            writer.Write('\n');
        }
    }

    public string ToCsv()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(writer);
        return writer.ToString();
    }

    public void WriteCsv(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteCsv(writer);
    }

    /// <summary>Writes one JSON object per row (JSON Lines); non-finite numbers are null, temporal cells are ISO 8601 strings.</summary>
    public void WriteJsonLines(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (var row in _rows)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                for (var i = 0; i < _columns.Count; i++)
                {
                    json.WritePropertyName(_columns[i]);
                    WriteJsonCell(json, row[i]);
                }

                json.WriteEndObject();
            }

            writer.Write(Encoding.UTF8.GetString(stream.ToArray()));
            writer.Write('\n');
        }
    }

    public string ToJsonLines()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteJsonLines(writer);
        return writer.ToString();
    }

    public void WriteJsonLines(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteJsonLines(writer);
    }

    /// <summary>The CSV text of a cell: booleans as <c>True</c>/<c>False</c>, floats in Python's <c>repr</c> form, temporal values in ISO 8601, null and NaN empty.</summary>
    public static string FormatCell(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => double.IsNaN(d) ? "" : PythonFormat.FloatRepr(d),
        string s => s,
        DateTimeOffset dto => PythonJsonFormat.FormatIso(dto),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => PythonFormat.TimeIso(time),
        _ => throw new ArgumentException($"Unsupported cell type {value.GetType().Name}.", nameof(value)),
    };

    /// <summary>Coerces CLR numerics to the cell types (ints to long, floats to double) and rejects anything else.</summary>
    internal static object? NormalizeCell(object? value) => value switch
    {
        null or bool or long or double or string or DateTimeOffset or DateOnly or TimeOnly => value,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        uint u => (long)u,
        ulong ul when ul <= long.MaxValue => (long)ul,
        float f => (double)f,
        decimal m => (double)m,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
        _ => throw new ArgumentException($"Unsupported cell type {value.GetType().Name}; cells must be null, bool, long, double, string, DateTimeOffset, DateOnly or TimeOnly.", nameof(value)),
    };

    private int ColumnIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _index.TryGetValue(name, out var index) ? index : throw new KeyNotFoundException($"The table has no column '{name}'.");
    }

    private static string CsvField(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var needsQuotes = text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r');
        return needsQuotes ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private static void WriteJsonCell(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case long l:
                json.WriteNumberValue(l);
                break;
            case double d when double.IsFinite(d):
                json.WriteNumberValue(d);
                break;
            case double:
                json.WriteNullValue();
                break;
            default:
                json.WriteStringValue(FormatCell(value));
                break;
        }
    }

    /// <summary>Equality for cells used as keys: nulls equal (via <see cref="NullKey"/> where a non-null key is required), numbers compared by value across long/double.</summary>
    private sealed class CellComparer : IEqualityComparer<object>
    {
        public static readonly CellComparer Instance = new();

        /// <summary>Stands in for a null cell in dictionaries, whose keys cannot be null.</summary>
        public static readonly object NullKey = new();

        public new bool Equals(object? x, object? y) => (x, y) switch
        {
            (null, null) => true,
            (long a, double b) => a == b,
            (double a, long b) => a == b,
            _ => object.Equals(x, y),
        };

        public int GetHashCode(object obj) => obj switch
        {
            long l => ((double)l).GetHashCode(),
            _ => obj.GetHashCode(),
        };
    }
}
