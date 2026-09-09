using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.MiniSwe;
using InspectAzureAI.SweShowcase.BuiltinTasks;

namespace InspectAzureAI.SweShowcase;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> whose every turn is computed from the
/// conversation it is asked to continue, so a run is deterministic under any sample concurrency. It solves
/// sample 1 of each task with the turn shape the agent expects (mini-swe: <c>bash</c> calls with <c>command</c>,
/// then the submit marker; basic and maf: <c>bash</c> calls with <c>cmd</c>, then <c>submit</c>), gives up on every other
/// sample, and answers a model-graded judge prompt with <c>GRADE: C</c> when the submission carries the expected answer.
/// </summary>
internal static class FakeScripts
{
    public const string ModelName = "scripted";

    private const string GiveUp = "I was unable to solve this task.";

    /// <summary>Turns per run; every turn is the same conversation-driven factory, so the budget only bounds a pathological loop.</summary>
    private const int TurnBudget = 512;

    /// <param name="task">The built-in task whose sample 1 the script solves.</param>
    /// <param name="agent">The agent whose tool-call shape the turns take.</param>
    /// <param name="modelName">The model name the api reports (the matrix names one per fake deployment so their logs and eval-set identifiers differ).</param>
    public static ScriptedModelApi For(string task, string agent, string modelName = ModelName)
    {
        var solution = SolutionFor(task);
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolInfo>, ModelOutput> respond = agent switch
        {
            AgentChoice.MiniSweName => (messages, _) => MiniSweTurn(solution, messages),
            AgentChoice.BasicName or AgentChoice.MafName => (messages, _) => BasicTurn(solution, messages),
            AgentChoice.ClaudeCodeName => throw new UsageError(
                "--fake cannot drive claude-code: the Claude Code CLI runs inside a real sandbox and needs a real model served through the bridge. "
                + "Use --agent mini-swe or --agent basic with --fake, or drop --fake."),
            AgentChoice.CopilotName => throw new UsageError(
                "--fake cannot drive copilot: the Copilot CLI runs inside a real sandbox and needs a real model served through the bridge. "
                + "Use --agent mini-swe or --agent basic with --fake, or drop --fake."),
            _ => throw new UsageError($"--agent expects {string.Join("|", AgentChoice.Names)}, got '{agent}'"),
        };
        return new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(respond), TurnBudget), modelName);
    }

    private static ModelOutput MiniSweTurn(FakeSolution solution, IReadOnlyList<ChatMessage> messages)
    {
        if (GradeTurn(solution, messages) is { } grade)
        {
            return grade;
        }

        if (!IsTargetSample(solution, messages))
        {
            return WithUsage(messages, ScriptedTurn.ToolCall("bash", new { command = Submit(GiveUp) }, text: "I cannot complete this task; submitting.").Output!);
        }

        var step = messages.Count(message => message is ChatMessageAssistant);
        if (step < solution.Steps.Count)
        {
            return WithUsage(messages, ScriptedTurn.ToolCall("bash", new { command = solution.Steps[step].Command }, text: solution.Steps[step].Thought).Output!);
        }

        var answer = solution.Answer(LastToolOutput(messages));
        return WithUsage(messages, ScriptedTurn.ToolCall("bash", new { command = Submit(answer) }, text: "Submitting the result.").Output!);
    }

    private static ModelOutput BasicTurn(FakeSolution solution, IReadOnlyList<ChatMessage> messages)
    {
        if (GradeTurn(solution, messages) is { } grade)
        {
            return grade;
        }

        if (!IsTargetSample(solution, messages))
        {
            return WithUsage(messages, ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = GiveUp }, text: "I cannot complete this task.").Output!);
        }

        var step = messages.Count(message => message is ChatMessageAssistant);
        if (step < solution.Steps.Count)
        {
            return WithUsage(messages, ScriptedTurn.ToolCall("bash", new { cmd = solution.Steps[step].Command }, text: solution.Steps[step].Thought).Output!);
        }

        var answer = solution.Answer(LastToolOutput(messages));
        return WithUsage(messages, ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer }, text: "Submitting the result.").Output!);
    }

    /// <summary>A model_graded_qa judge prompt is a lone user message built from the grading template; anything else is an agent turn.</summary>
    private static ModelOutput? GradeTurn(FakeSolution solution, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count != 1 || messages[0] is not ChatMessageUser prompt || !prompt.Text.Contains("[BEGIN DATA]", StringComparison.Ordinal))
        {
            return null;
        }

        var submission = Section(prompt.Text, "[Submission]:");
        var correct = submission.Contains(solution.AnswerFragment, StringComparison.OrdinalIgnoreCase);
        var reasoning = correct
            ? "The submission answers the question with the expected value."
            : "The submission does not provide the expected answer.";
        return WithUsage(messages, ScriptedTurn.Text($"{reasoning}\n\nGRADE: {(correct ? "C" : "I")}").Output!);
    }

    /// <summary>The text after <paramref name="label"/> up to the next <c>***</c> separator line of the grading template.</summary>
    private static string Section(string text, string label)
    {
        var start = text.IndexOf(label, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += label.Length;
        var end = text.IndexOf("\n***", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static bool IsTargetSample(FakeSolution solution, IReadOnlyList<ChatMessage> messages) =>
        messages.OfType<ChatMessageUser>().Any(message => message.Text.Contains(solution.PromptMarker, StringComparison.Ordinal));

    /// <summary>The output of the latest tool call: raw text for the basic agent, the <c>output</c> field of the mini-swe JSON observation.</summary>
    private static string LastToolOutput(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages.OfType<ChatMessageTool>().LastOrDefault()?.Text ?? "";
        if (!text.TrimStart().StartsWith('{'))
        {
            return text;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject observation && observation["output"] is JsonValue output && output.TryGetValue<string>(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // an observation that only looks like JSON is used verbatim
        }

        return text;
    }

    /// <summary>The mini-swe submission: the marker on its own line, then the answer.</summary>
    private static string Submit(string answer) => $"printf '%s\\n%s\\n' {MiniSweTemplates.SubmitMarker} {ShellQuote(answer)}";

    private static string ShellQuote(string value) => ClaudeCodeCommand.ShellQuote(value);

    /// <summary>Synthesises usage at about four characters per token so the progress lines and the log carry plausible counts.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var completion = output.Completion.Length + (output.Message.ToolCalls?.Sum(call => call.Arguments.ToJsonString().Length) ?? 0);
        var outputTokens = completion / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }

    private static FakeSolution SolutionFor(string task) => task switch
    {
        HelloSweTask.Name => new FakeSolution(
            "hello.py",
            [
                new FakeStep("cat > hello.py <<'EOF'\nprint(\"Hello, Inspect!\")\nEOF", "I will write hello.py with a single print statement."),
                new FakeStep("python3 hello.py", "Let me run it to confirm the output."),
            ],
            _ => "Created hello.py; running `python3 hello.py` prints Hello, Inspect!",
            "hello.py"),
        PytestFixTask.Name => new FakeSolution(
            "textkit",
            [
                new FakeStep("cat textkit/__init__.py tests/test_slugify.py", "First I will read the implementation and the tests."),
                new FakeStep("python3 -m pytest -q 2>&1 | tail -n 5", "Let me run the suite to see the failures."),
                new FakeStep(
                    "cat > textkit/__init__.py <<'EOF'\n\"\"\"Tiny text helpers.\"\"\"\n\n\ndef slugify(text: str) -> str:\n"
                    + "    \"\"\"Return a URL slug: lower-case words joined by single hyphens.\"\"\"\n    return \"-\".join(text.lower().split())\nEOF",
                    "slugify neither lower-cases nor collapses whitespace; splitting on whitespace and joining with hyphens fixes both."),
                new FakeStep("python3 -m pytest -q 2>&1 | tail -n 3", "Re-running the tests."),
            ],
            _ => "Fixed slugify in textkit/__init__.py (lower-case, split on whitespace, join with hyphens); the test suite passes.",
            "slugify"),
        SystemExplorerTask.Name => new FakeSolution(
            "Python version",
            [new FakeStep("python3 --version 2>&1", "I will ask the interpreter for its version.")],
            output => $"The installed Python version is {output.Trim()}.",
            "Python 3."),
        CtfTask.Name => new FakeSolution(
            "hidden in a file",
            [
                new FakeStep("find challenge -type f", "Let me list every file under the challenge directory, hidden ones included."),
                new FakeStep("cat challenge/.cache/logs/.flag", "A dotfile under a cache directory looks like the hiding place; let me read it."),
            ],
            output => output.Trim(),
            "picoCTF{"),
        _ => throw new UsageError($"--fake has no script for task '{task}' (known tasks: {ShowcaseTasks.Names})"),
    };

    /// <summary>One bash command of the scripted solution and the assistant text that accompanies it.</summary>
    private sealed record FakeStep(string Command, string Thought);

    /// <summary>
    /// How sample 1 of a task is solved: the phrase that identifies its prompt, the commands to run, the answer to
    /// submit (built from the last command's output) and the fragment a judge should find in a correct submission.
    /// </summary>
    private sealed record FakeSolution(string PromptMarker, IReadOnlyList<FakeStep> Steps, Func<string, string> Answer, string AnswerFragment);
}
