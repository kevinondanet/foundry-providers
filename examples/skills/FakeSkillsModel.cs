using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Skills;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/skills/task.py</c>: a <see cref="ScriptedModelApi"/> whose turns
/// are computed from the conversation. As the agent it follows the prompt: <c>skill(&lt;the skill matching the
/// question&gt;)</c>, then <c>bash(./skills/&lt;skill&gt;/scripts/&lt;script&gt;.sh)</c> (the command the skill's
/// instructions suggest), then <c>submit</c> with the lines of the script's output that answer the question. As
/// <c>model_graded_qa</c>'s grader (the task names no grading model, so the active model grades) it recognises the
/// grading template and answers <c>GRADE: C</c>.
/// </summary>
internal static class FakeSkillsModel
{
    public const string ModelName = "skills-scripted";

    public const string Grade = "GRADE: C";

    /// <summary>Three agent turns and one grading turn per sample, five samples; the budget leaves room to spare.</summary>
    private const int TurnBudget = 40;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>The skill and helper script for a question.</summary>
    internal static (string Skill, string Script) SkillFor(string input)
    {
        var question = input.ToLowerInvariant();
        return question.Contains("ip address", StringComparison.Ordinal) || question.Contains("network", StringComparison.Ordinal)
            ? ("network-info", "netinfo.sh")
            : question.Contains("disk", StringComparison.Ordinal) || question.Contains("filesystem", StringComparison.Ordinal)
                ? ("disk-usage", "diskinfo.sh")
                : ("system-info", "sysinfo.sh");
    }

    /// <summary>One turn: a grade when asked to grade, else the next step of the agent's script.</summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var lastUser = messages.OfType<ChatMessageUser>().LastOrDefault()?.Text ?? "";
        if (tools.Count == 0 && lastUser.Contains("[BEGIN DATA]", StringComparison.Ordinal))
        {
            return ModelOutput.FromContent(ModelName, $"The submission answers the question with the information the system reported. {Grade}");
        }

        var input = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var (skill, script) = SkillFor(input);
        var step = messages.Count(message => message is ChatMessageAssistant);
        var results = messages.OfType<ChatMessageTool>().ToList();
        var output = step switch
        {
            0 => ScriptedTurn.ToolCall("skill", new { command = skill }, text: $"I'll read the {skill} skill's instructions first.").Output!,
            1 => ScriptedTurn.ToolCall("bash", new { cmd = $"./skills/{skill}/scripts/{script}" }, text: $"The skill suggests running its {script} script.").Output!,
            _ => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = Answer(input, results.LastOrDefault()) }, text: "The script's output answers the question.").Output!,
        };
        return output with { Model = ModelName };
    }

    /// <summary>The answer submitted for a question, quoted from the script's output (or the bash tool's error).</summary>
    internal static string Answer(string input, ChatMessageTool? bashResult)
    {
        if (bashResult is null)
        {
            return "The helper script could not be run, so I have no answer.";
        }

        if (bashResult.Error is { } error)
        {
            return $"The helper script failed: {error.Message}";
        }

        var lines = bashResult.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var question = input.ToLowerInvariant();
        var relevant = question.Contains("distribution", StringComparison.Ordinal)
            ? Section(lines, "--- Operating System ---")
            : question.Contains("cpu", StringComparison.Ordinal)
                ? Section(lines, "--- CPU Information ---")
                : question.Contains("memory", StringComparison.Ordinal) || question.Contains("ram", StringComparison.Ordinal)
                    ? Section(lines, "--- Memory Information ---")
                    : question.Contains("ip address", StringComparison.Ordinal)
                        ? Section(lines, "--- IP Addresses ---")
                        : Section(lines, "--- Filesystem Usage ---").Where(line => line.EndsWith(" /", StringComparison.Ordinal) || line.StartsWith("Filesystem", StringComparison.Ordinal)).ToList();
        return relevant.Count == 0
            ? $"The script reported: {string.Join(" | ", lines.Take(6))}"
            : $"According to the system: {string.Join(" | ", relevant)}";
    }

    /// <summary>The lines between a <c>--- heading ---</c> and the next one.</summary>
    private static List<string> Section(List<string> lines, string heading)
    {
        var start = lines.IndexOf(heading);
        if (start < 0)
        {
            return [];
        }

        return lines.Skip(start + 1).TakeWhile(line => !line.StartsWith("---", StringComparison.Ordinal)).ToList();
    }
}
