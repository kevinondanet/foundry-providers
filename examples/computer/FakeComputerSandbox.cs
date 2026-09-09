using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Computer;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;

/// <summary>
/// The sandbox behind <c>--sandbox fake</c> for <c>examples/computer/computer.py</c>: a <see cref="FakeSandboxScript"/>
/// that plays the <c>aisiuk/inspect-computer-tool</c> image. <c>test -r /opt/inspect/tool/computer_tool.py</c>
/// succeeds, and every <c>python3 /opt/inspect/tool/computer_tool.py &lt;action&gt; ...</c> call answers the JSON the
/// real service prints (<c>output</c>, <c>error</c>, <c>base64_image</c>): a description of what the screen shows
/// and a 1x1 PNG in place of a screenshot. The desktop has a Terminal icon at (<see cref="TerminalIcon"/>) and a
/// Calculator icon at (<see cref="CalculatorIcon"/>); double-clicking one opens it, and text typed into it followed
/// by Return "runs": the terminal answers <c>cat /tmp/flag.txt</c> from the sample's own files (the flag Python's
/// <c>Sample(files=...)</c> writes there), reports any other word as <c>bash: &lt;word&gt;: command not found</c>, and
/// the calculator evaluates <c>a*b</c>. Each sample's state is read back from that sample's recorded calls, so
/// concurrent samples never share a desktop.
/// </summary>
internal static class FakeComputerSandbox
{
    /// <summary>A 1x1 transparent PNG, the "screenshot".</summary>
    public const string ScreenshotPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    public static readonly IReadOnlyList<int> TerminalIcon = [40, 60];

    public static readonly IReadOnlyList<int> CalculatorIcon = [40, 140];

    public const string DesktopScreen = "Desktop with icons: 'Terminal' at (40, 60) and 'Calculator' at (40, 140). No window is open.";

    public const string TerminalScreen = "A 'Terminal' window is open and active. It shows a bash prompt: $";

    public const string CalculatorScreen = "A 'Calculator' window is open and active. Its display shows 0.";

    /// <summary>The script: the tool file is readable, and every service call is answered by the scripted desktop.</summary>
    public static FakeSandboxScript Create()
    {
        var script = new FakeSandboxScript();
        script
            .OnExact(FakeSandboxScript.Ok(), "test", "-r", ComputerTool.ToolPath)
            .OnPrefix(call => Answer(script, call), "python3", ComputerTool.ToolPath)
            .WithDefault(FakeSandboxScript.Fail(127, "command not found\n"));
        return script;
    }

    /// <summary>The actions (argv[2]) of every service call a sample's environment made.</summary>
    public static IReadOnlyList<string> Actions(ScriptedSandboxEnvironment environment) =>
        environment.Calls.Where(IsServiceCall).Select(call => call.Cmd[2]).ToList();

    /// <summary>Answers one service call: the screen after this action, as the service's JSON.</summary>
    private static ExecResult Answer(FakeSandboxScript script, FakeExecCall call)
    {
        // The environment that made this call: that sample's desktop history (null for a call no environment recorded).
        var environment = script.EnvironmentOf(call);
        var history = environment?.Calls.Where(IsServiceCall).ToList() ?? [call];
        var screen = Screen(history, environment);
        var json = new JsonObject
        {
            ["output"] = screen,
            ["error"] = null,
            ["base64_image"] = ScreenshotPng,
        };
        return FakeSandboxScript.Ok(json.ToJsonString() + "\n");
    }

    private static bool IsServiceCall(FakeExecCall call) =>
        call.Cmd.Count >= 3 && call.Cmd[0] == "python3" && call.Cmd[1] == ComputerTool.ToolPath;

    /// <summary>Replays the sample's actions so far to describe what the screen shows now.</summary>
    private static string Screen(IReadOnlyList<FakeExecCall> history, ScriptedSandboxEnvironment? environment)
    {
        string? application = null;
        var typed = new List<string>();
        var pending = "";
        var transcript = new List<string>();
        foreach (var call in history)
        {
            var argv = call.Cmd;
            switch (argv[2])
            {
                case "double_click" when application is null:
                    var point = Coordinate(argv);
                    application = point is null ? null
                        : point.SequenceEqual(TerminalIcon) ? "terminal"
                        : point.SequenceEqual(CalculatorIcon) ? "calculator"
                        : null;
                    break;
                case "type" when application is not null:
                    pending += Text(argv);
                    break;
                case "key" when application is not null && Text(argv) is ("Return" or "KP_Enter"):
                    typed.Add(pending);
                    transcript.Add(Run(application, pending, environment));
                    pending = "";
                    break;
            }
        }

        return application switch
        {
            null => DesktopScreen,
            "terminal" when transcript.Count == 0 && pending.Length == 0 => TerminalScreen,
            "terminal" => "A 'Terminal' window is open and active. It shows:\n"
                + string.Join("\n", typed.Zip(transcript, (command, result) => $"$ {command}\n{result}".TrimEnd('\n')))
                + $"\n$ {pending}",
            _ when transcript.Count == 0 && pending.Length == 0 => CalculatorScreen,
            _ => $"A 'Calculator' window is open and active. Its display shows {(transcript.Count > 0 ? transcript[^1] : pending)}.",
        };
    }

    /// <summary>What the application answers to a line of input followed by Return.</summary>
    private static string Run(string application, string line, ScriptedSandboxEnvironment? environment)
    {
        var trimmed = line.Trim();
        if (application == "calculator")
        {
            var factors = trimmed.Split(['*', 'x', 'X'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return factors.Length == 2
                && long.TryParse(factors[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                && long.TryParse(factors[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
                ? (a * b).ToString(CultureInfo.InvariantCulture)
                : "Error";
        }

        if (trimmed.Length == 0)
        {
            return "";
        }

        var words = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words[0] == "cat" && words.Length == 2)
        {
            var path = words[1].Trim();
            return environment?.FileText(path) is { } contents ? contents : $"cat: {path}: No such file or directory";
        }

        return $"bash: {words[0].TrimEnd('.')}: command not found";
    }

    private static IReadOnlyList<int>? Coordinate(IReadOnlyList<string> argv)
    {
        var index = argv.ToList().IndexOf("--coordinate");
        return index >= 0 && argv.Count > index + 2
            && int.TryParse(argv[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(argv[index + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? [x, y]
            : null;
    }

    /// <summary>The <c>--text</c> value: <c>--text=value</c> for type, <c>--text value</c> for key.</summary>
    private static string Text(IReadOnlyList<string> argv)
    {
        for (var i = 3; i < argv.Count; i++)
        {
            if (argv[i].StartsWith("--text=", StringComparison.Ordinal))
            {
                return argv[i]["--text=".Length..];
            }

            if (argv[i] == "--text" && i + 1 < argv.Count)
            {
                return argv[i + 1];
            }
        }

        return "";
    }
}
