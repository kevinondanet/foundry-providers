using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Computer;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/computer/computer.py</c>: a <see cref="ScriptedModelApi"/> whose
/// turns are computed from the conversation, one script per sample recognised by its input. Each script follows the
/// system message's protocol (state the intent, act, evaluate the screenshot): a screenshot of the desktop, a
/// double-click on the application's icon, the command typed with <c>press_enter</c>, another screenshot, then
/// <c>submit</c> with what the screen (the tool result's text) shows. The flag sample reports the terminal's
/// output of <c>cat /tmp/flag.txt</c>, the terminal sample the shell's reply to the sentence, the calculator sample
/// the display after <c>123*456</c>.
/// </summary>
internal static class FakeComputerModel
{
    public const string ModelName = "computer-scripted";

    /// <summary>Five turns per sample, three samples; the budget leaves room to spare.</summary>
    private const int TurnBudget = 30;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>One turn of the sample's script, chosen by how many assistant turns have gone before.</summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var input = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var step = messages.Count(message => message is ChatMessageAssistant);
        var screen = LastScreen(messages);
        var (icon, command, application) = input switch
        {
            ComputerExample.FlagInput => (FakeComputerSandbox.TerminalIcon, "cat /tmp/flag.txt", "terminal"),
            ComputerExample.TerminalInput => (FakeComputerSandbox.TerminalIcon, "Trudging across the tundra. Mile after Mile.", "terminal"),
            ComputerExample.CalculatorInput => (FakeComputerSandbox.CalculatorIcon, "123*456", "calculator"),
            _ => (FakeComputerSandbox.TerminalIcon, "", "terminal"),
        };

        var output = step switch
        {
            0 => Call(new { action = "screenshot" }, "I expect tool command screenshot to show me the desktop and where the application icons are."),
            1 => Call(new { action = "double_click", coordinate = icon }, $"I have evaluated step 1: the desktop is visible. I expect tool command double_click to launch the {application} from its icon."),
            2 => Call(new { action = "type", text = command, press_enter = true }, $"I have evaluated step 2: the {application} window is open and active. I expect tool command type to enter '{command}' and press enter."),
            3 => Call(new { action = "screenshot" }, "I have evaluated step 3: the text was entered. I expect tool command screenshot to show the result."),
            _ => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = Answer(input, screen) }, text: "I have evaluated step 4: the result is on screen. Submitting it.").Output!,
        };
        return output with { Model = ModelName };
    }

    /// <summary>The answer submitted for a sample, read off the last screen description.</summary>
    internal static string Answer(string input, string screen)
    {
        var lines = screen.Split('\n');
        return input switch
        {
            ComputerExample.FlagInput => $"The contents of /tmp/flag.txt are: {LineAfter(lines, "$ cat /tmp/flag.txt") ?? "(not visible on screen)"}",
            ComputerExample.TerminalInput => LineAfter(lines, "$ Trudging across the tundra. Mile after Mile.") ?? "(no output visible on screen)",
            ComputerExample.CalculatorInput => $"123 x 456 = {Display(screen)}",
            _ => screen,
        };
    }

    private static ModelOutput Call(object arguments, string thought) => ScriptedTurn.ToolCall(ComputerTool.Name, arguments, text: thought).Output!;

    /// <summary>The text of the last tool result (the fake desktop's screen description; a redacted screenshot has none).</summary>
    private static string LastScreen(IReadOnlyList<ChatMessage> messages) =>
        messages.OfType<ChatMessageTool>().LastOrDefault()?.Text ?? "";

    private static string? LineAfter(string[] lines, string marker)
    {
        var index = Array.IndexOf(lines, marker);
        return index >= 0 && index + 1 < lines.Length ? lines[index + 1] : null;
    }

    private static string Display(string screen)
    {
        const string marker = "display shows ";
        var start = screen.IndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? "(not visible on screen)" : screen[(start + marker.Length)..].TrimEnd('.', '\n');
    }
}
