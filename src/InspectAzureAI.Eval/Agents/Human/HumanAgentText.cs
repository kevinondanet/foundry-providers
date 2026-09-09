using System.Globalization;
using System.Text;
using InspectAzureAI.Eval.Agents.Human.Commands;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Agents.Human;

/// <summary>
/// The text the human agent's commands print in the container: port of <c>_util/format.py</c>
/// <c>format_progress_time</c>, <c>commands/status.py</c> <c>render_status</c> and the instructions panel of
/// <c>commands/instructions.py</c>. Deviation: Python renders through <c>rich</c> (bold markup, ANSI styles for
/// status/score/validation messages, box-drawn panels and tables); this port emits plain text with the same
/// wording and a hand-drawn approximation of the two instruction panels at the same 90-column width.
/// </summary>
internal static class HumanAgentText
{
    /// <summary>Width Python passes to <c>render_text</c> for the instructions.</summary>
    public const int InstructionsWidth = 90;

    private const string Intro =
        "You will be completing a task based on the instructions presented below. You can use the following commands as you work on the task:";

    /// <summary>Port of <c>format_progress_time</c>: <c>H:MM:SS</c> of the whole seconds (hours right-aligned to 2 when padded).</summary>
    public static string FormatProgressTime(double time, bool padHours = true)
    {
        var totalSeconds = (long)time;
        var minutes = Math.DivRem(totalSeconds, 60, out var seconds);
        var hours = Math.DivRem(minutes, 60, out minutes);
        var hoursText = padHours ? hours.ToString(CultureInfo.InvariantCulture).PadLeft(2) : hours.ToString(CultureInfo.InvariantCulture);
        return $"{hoursText}:{minutes:00}:{seconds:00}";
    }

    /// <summary>Port of the score value display: a string value as is, anything else as JSON (<c>to_json_str_safe</c>).</summary>
    public static string ScoreValueText(Score score) =>
        score.Value is ScoreValue.Str text ? text.Value : score.Value.ToJson()?.ToJsonString() ?? "null";

    /// <summary>Port of the <c>[bold]FAILED:[/bold] reason</c> message of <c>validate</c>.</summary>
    public static string Failed(string reason) => $"FAILED: {reason}";

    /// <summary>Port of <c>render_status</c>: the clock line plus an intermediate scores table when there are any.</summary>
    public static string RenderStatus(HumanAgentState state)
    {
        var lines = new List<string>
        {
            $"Status: {(state.IsRunning() ? "Running" : "Stopped")}  Time: {FormatProgressTime(state.Time(), padHours: false)}",
        };

        var scorings = state.Scorings;
        if (scorings.Count > 0)
        {
            lines.Add("");
            lines.Add("Intermediate Scores");
            var rows = scorings
                .Select(scoring => (Answer: scoring.Scores[0].Answer ?? "", Score: ScoreValueText(scoring.Scores[0]), Time: FormatProgressTime(scoring.Time)))
                .ToList();
            lines.AddRange(Table(["Answer", "Score", "Time"], rows.Select(row => new[] { row.Answer, row.Score, row.Time }).ToList(), minWidth: 35));
        }

        return string.Join("\n", lines).Trim();
    }

    /// <summary>Port of the <c>instructions</c> service handler's rendering: the command reference panel followed by the task instructions panel.</summary>
    public static string RenderInstructions(IReadOnlyList<HumanAgentCommand> commands, string? extraInstructions, string taskInstructions)
    {
        var lines = new List<string> { Rule("Human Agent Task", InstructionsWidth), "" };
        lines.AddRange(Wrap(Intro, InstructionsWidth - 2).Select(line => " " + line));
        lines.Add("");

        var cliCommands = commands.Where(command => command.RunsIn(HumanAgentCommandContext.Cli)).ToList();
        var nameWidth = cliCommands.Count == 0 ? 0 : cliCommands.Max(command => command.Name.Length) + "task ".Length;
        for (var group = 1; group <= 3; group++)
        {
            foreach (var command in cliCommands.Where(command => command.Group == group))
            {
                lines.Add($" {("task " + command.Name).PadRight(nameWidth)}  {command.Description}");
            }

            if (group != 3)
            {
                lines.Add("");
            }
        }

        if (!string.IsNullOrEmpty(extraInstructions))
        {
            lines.Add("");
            lines.AddRange(extraInstructions.Split('\n').SelectMany(paragraph => Wrap(paragraph, InstructionsWidth - 2)).Select(line => " " + line));
        }

        lines.Add("");
        lines.AddRange(Panel("Task Instructions", taskInstructions.Trim(), InstructionsWidth));
        return string.Join("\n", lines).Trim();
    }

    /// <summary>Rich's <c>DOUBLE_LINE</c> header: a double rule carrying a centred title (the box has no sides).</summary>
    private static string Rule(string title, int width)
    {
        var text = $" {title} ";
        var remaining = Math.Max(0, width - 2 - text.Length);
        var left = remaining / 2;
        var right = remaining - left;
        return " " + new string('═', left) + text + new string('═', right) + " ";
    }

    /// <summary>Rich's rounded <c>Panel</c> with padding (1, 1).</summary>
    private static IEnumerable<string> Panel(string title, string content, int width)
    {
        var inner = width - 4;
        var header = $"╭─ {title} ";
        yield return header + new string('─', Math.Max(0, width - header.Length - 1)) + "╮";
        yield return "│" + new string(' ', width - 2) + "│";
        foreach (var paragraph in content.Split('\n'))
        {
            foreach (var line in Wrap(paragraph, inner))
            {
                yield return "│ " + line.PadRight(inner) + " │";
            }
        }

        yield return "│" + new string(' ', width - 2) + "│";
        yield return "╰" + new string('─', width - 2) + "╯";
    }

    /// <summary>A borderless table: left-aligned first column, centred middle, right-aligned last.</summary>
    private static IEnumerable<string> Table(string[] header, List<string[]> rows, int minWidth)
    {
        var widths = header.Select((cell, column) => Math.Max(cell.Length, rows.Count == 0 ? 0 : rows.Max(row => row[column].Length))).ToArray();
        var total = widths.Sum() + (widths.Length - 1) * 2;
        if (total < minWidth)
        {
            widths[1] += minWidth - total;
        }

        yield return Row(header, widths);
        foreach (var row in rows)
        {
            yield return Row(row, widths);
        }
    }

    private static string Row(string[] cells, int[] widths)
    {
        var builder = new StringBuilder();
        for (var column = 0; column < cells.Length; column++)
        {
            if (column > 0)
            {
                builder.Append("  ");
            }

            var cell = cells[column];
            var width = widths[column];
            builder.Append(column switch
            {
                0 => cell.PadRight(width),
                _ when column == cells.Length - 1 => cell.PadLeft(width),
                _ => Center(cell, width),
            });
        }

        return builder.ToString().TrimEnd();
    }

    private static string Center(string text, int width)
    {
        var padding = Math.Max(0, width - text.Length);
        var left = padding / 2;
        return new string(' ', left) + text + new string(' ', padding - left);
    }

    /// <summary>Greedy word wrap; a word longer than the width stands on its own line.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }

        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
