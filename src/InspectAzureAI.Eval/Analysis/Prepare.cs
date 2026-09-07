using System.Globalization;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Scorers;
using CostModelInfo = InspectAzureAI.Eval.Model.Cost.ModelInfo;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>Port of <c>analysis/_prepare/operation.py</c> <c>Operation</c>: a transform of a table for analysis.</summary>
public delegate Table Operation(Table table);

/// <summary>Port of <c>analysis/_prepare</c>: <c>prepare()</c> and the built-in operations.</summary>
public static class Prepare
{
    /// <summary>Port of <c>prepare</c>: applies the operations in order.</summary>
    public static Table Apply(Table table, params Operation[] operations) => Apply(table, (IEnumerable<Operation>)operations);

    public static Table Apply(Table table, IEnumerable<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            table = operation(table);
        }

        return table;
    }

    /// <summary>Port of <c>score_to_float</c> for one column.</summary>
    public static Operation ScoreToFloat(string column, Func<ScoreValue, double>? valueToFloat = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        return ScoreToFloat([column], valueToFloat);
    }

    /// <summary>
    /// Port of <c>score_to_float</c>: replaces each score column with its float value under
    /// <paramref name="valueToFloat"/> (default <see cref="ValueToFloat.Default"/>: C → 1, P → 0.5, I/N → 0); missing
    /// values (samples another scorer scored) become NaN. A column that is not in the table is an
    /// <see cref="ArgumentException"/>; an empty table passes through.
    /// </summary>
    public static Operation ScoreToFloat(IEnumerable<string> columns, Func<ScoreValue, double>? valueToFloat = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = columns.ToList();
        var convert = valueToFloat ?? ValueToFloat.Default;
        return table =>
        {
            if (table.IsEmpty)
            {
                return table;
            }

            foreach (var name in names)
            {
                if (!table.HasColumn(name))
                {
                    throw new ArgumentException($"Column '{name}' not found in DataFrame", nameof(columns));
                }
            }

            foreach (var name in names)
            {
                table = table.WithColumn(name, table.Column(name).Select(cell => (object?)ToFloat(cell, convert)).ToList());
            }

            return table;
        };
    }

    /// <summary>
    /// Port of <c>model_info</c>: adds <c>model_organization_name</c>, <c>model_display_name</c> (the model string
    /// when unknown), <c>model_snapshot</c>, <c>model_release_date</c> and <c>model_knowledge_cutoff_date</c> from
    /// <paramref name="modelInfo"/> (checked first) and the built-in model database. A table without a <c>model</c>
    /// column is an <see cref="ArgumentException"/>; an empty table passes through.
    /// </summary>
    public static Operation ModelInfo(IReadOnlyDictionary<string, CostModelInfo>? modelInfo = null) => table =>
    {
        if (table.IsEmpty)
        {
            return table;
        }

        if (!table.HasColumn("model"))
        {
            throw new ArgumentException("Required column 'model' not found in DataFrame", nameof(table));
        }

        var models = table.Column("model").Select(cell => cell is null ? "None" : Table.FormatCell(cell)).ToList();
        var organization = new object?[models.Count];
        var displayName = new object?[models.Count];
        var snapshot = new object?[models.Count];
        var releaseDate = new object?[models.Count];
        var cutoffDate = new object?[models.Count];
        for (var i = 0; i < models.Count; i++)
        {
            displayName[i] = models[i];
            var info = (modelInfo is not null && modelInfo.TryGetValue(models[i], out var provided) ? provided : null) ?? ModelInfoLookup.GetModelInfo(models[i]);
            if (info is null)
            {
                continue;
            }

            organization[i] = info.Organization;
            displayName[i] = info.Model ?? displayName[i];
            snapshot[i] = info.Snapshot;
            releaseDate[i] = info.ReleaseDate;
            cutoffDate[i] = info.KnowledgeCutoffDate;
        }

        return table
            .WithColumn("model_organization_name", organization)
            .WithColumn("model_display_name", displayName)
            .WithColumn("model_snapshot", snapshot)
            .WithColumn("model_release_date", releaseDate)
            .WithColumn("model_knowledge_cutoff_date", cutoffDate);
    };

    /// <summary>
    /// Port of <c>task_info</c>: sets <paramref name="taskDisplayNameColumn"/> from <paramref name="displayNames"/>
    /// (keyed by task name), else the column's existing value, else the task name. A table without
    /// <paramref name="taskNameColumn"/> is an <see cref="ArgumentException"/>; an empty table passes through.
    /// </summary>
    public static Operation TaskInfo(IReadOnlyDictionary<string, string> displayNames, string taskNameColumn = "task_name", string taskDisplayNameColumn = "task_display_name")
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        ArgumentException.ThrowIfNullOrEmpty(taskNameColumn);
        ArgumentException.ThrowIfNullOrEmpty(taskDisplayNameColumn);
        return table =>
        {
            if (table.IsEmpty)
            {
                return table;
            }

            if (!table.HasColumn(taskNameColumn))
            {
                throw new ArgumentException($"The data frame has no column named '{taskNameColumn}'", nameof(table));
            }

            var names = table.Column(taskNameColumn);
            var existing = table.HasColumn(taskDisplayNameColumn) ? table.Column(taskDisplayNameColumn) : null;
            var values = new object?[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                var taskName = names[i] is null ? null : Table.FormatCell(names[i]);
                values[i] = taskName is not null && displayNames.TryGetValue(taskName, out var display) ? display : existing is not null ? existing[i] : names[i];
            }

            return table.WithColumn(taskDisplayNameColumn, values);
        };
    }

    /// <summary>
    /// Port of <c>frontier</c>: adds <paramref name="frontierColumn"/>, true for the rows whose model was the top
    /// scorer on its task among all models released up to its release date (per task: the best score per release
    /// date, in date order, is on the frontier when it beats every earlier date's best). Rows without a task, date
    /// or score are never on the frontier. Requires the three columns (<see cref="ArgumentException"/> otherwise);
    /// an empty table passes through.
    /// </summary>
    public static Operation Frontier(string taskColumn = "task_name", string dateColumn = "model_release_date", string scoreColumn = "score_headline_value", string frontierColumn = "frontier")
    {
        ArgumentException.ThrowIfNullOrEmpty(taskColumn);
        ArgumentException.ThrowIfNullOrEmpty(dateColumn);
        ArgumentException.ThrowIfNullOrEmpty(scoreColumn);
        ArgumentException.ThrowIfNullOrEmpty(frontierColumn);
        return table =>
        {
            if (table.IsEmpty)
            {
                return table;
            }

            foreach (var column in new[] { taskColumn, dateColumn, scoreColumn })
            {
                if (!table.HasColumn(column))
                {
                    throw new ArgumentException($"Required column '{column}' not found in DataFrame", nameof(table));
                }
            }

            var tasks = table.Column(taskColumn);
            var dates = table.Column(dateColumn);
            var scores = table.Column(scoreColumn);
            var frontier = new object?[table.RowCount];
            Array.Fill(frontier, false);

            var byTask = Enumerable.Range(0, table.RowCount)
                .Where(r => tasks[r] is not null)
                .GroupBy(r => Table.FormatCell(tasks[r]), StringComparer.Ordinal);
            foreach (var group in byTask)
            {
                // the best-scoring row of every release date, dates in chronological order
                var bestPerDate = group
                    .Where(r => dates[r] is not null && ScoreOf(scores[r]) is { } score && !double.IsNaN(score))
                    .GroupBy(r => DateKey(dates[r]), DateKeyComparer.Instance)
                    .Select(dateGroup => dateGroup.MaxBy(r => ScoreOf(scores[r])!.Value))
                    .OrderBy(r => DateKey(dates[r]), DateKeyComparer.Instance)
                    .ToList();
                var highest = double.NegativeInfinity;
                foreach (var row in bestPerDate)
                {
                    var score = ScoreOf(scores[row])!.Value;
                    if (score > highest)
                    {
                        highest = score;
                        frontier[row] = true;
                    }
                }
            }

            return table.WithColumn(frontierColumn, frontier);
        };
    }

    private static double ToFloat(object? cell, Func<ScoreValue, double> convert) => cell switch
    {
        null => double.NaN,
        double d when double.IsNaN(d) => double.NaN,
        double d => convert(new ScoreValue.Num(d)),
        long l => convert(new ScoreValue.Num(l)),
        bool b => convert(new ScoreValue.Bool(b)),
        string s => convert(new ScoreValue.Str(s)),
        _ => convert(new ScoreValue.Str(Table.FormatCell(cell))),
    };

    private static double? ScoreOf(object? cell) => cell switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>A comparable form of a date cell: temporal values as UTC instants, other values as their text.</summary>
    private static object DateKey(object? cell) => cell switch
    {
        DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateTimeOffset moment => moment.ToUniversalTime(),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
        _ => Table.FormatCell(cell),
    };

    private sealed class DateKeyComparer : IComparer<object>, IEqualityComparer<object>
    {
        public static readonly DateKeyComparer Instance = new();

        public int Compare(object? x, object? y) => (x, y) switch
        {
            (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),
            (DateTimeOffset, _) => -1,
            (_, DateTimeOffset) => 1,
            _ => string.CompareOrdinal(x?.ToString(), y?.ToString()),
        };

        public new bool Equals(object? x, object? y) => Compare(x, y) == 0;

        public int GetHashCode(object obj) => obj is DateTimeOffset moment ? moment.GetHashCode() : (obj.ToString() ?? "").GetHashCode(StringComparison.Ordinal);
    }
}
