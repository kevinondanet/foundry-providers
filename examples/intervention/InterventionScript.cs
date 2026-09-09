using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Intervention;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>--fake</c> script of one intervention mode, an addition for running the demonstration offline: the lines the
/// operator "types" at the input screens (<see cref="OperatorLines"/>), the model's turns (<see cref="Respond"/>, keyed on
/// how many assistant messages the conversation holds so approvals and rejections do not change the path) and the
/// sandbox's answers to the tools (<see cref="Sandbox"/>). The three are kept together so they agree: the shell script
/// lists files, counts them with python and checks the python version; the computer script takes a screenshot,
/// double-clicks the terminal icon and looks again; the multi-tool script writes a file in a bash session, views it
/// with the text editor and browses to example.com.
/// </summary>
public sealed class InterventionScript
{
    /// <summary>The scripted model's name, as it appears in the banner and the log.</summary>
    public const string FakeModelName = "intervention-scripted";

    /// <summary>A 1x1 transparent PNG, the "screenshot" the fake computer service returns.</summary>
    public const string ScreenshotPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    /// <summary>What <c>ls -la</c> prints in the fake shell sandbox.</summary>
    public const string ShellListing =
        "total 12\n"
        + "drwxr-xr-x 1 root root 4096 Sep  8 09:00 .\n"
        + "drwxr-xr-x 1 root root 4096 Sep  8 09:00 ..\n"
        + "-rw-r--r-- 1 root root  128 Sep  8 09:00 README.md\n"
        + "drwxr-xr-x 2 root root 4096 Sep  8 09:00 data\n"
        + "-rw-r--r-- 1 root root   42 Sep  8 09:00 notes.txt\n";

    /// <summary>What <c>python3 --version</c> prints in the fake shell sandbox.</summary>
    public const string ShellPythonVersion = "Python 3.12.3\n";

    /// <summary>The page the fake browser serves for every navigation.</summary>
    public const string ExampleMainContent = "Example Domain\n\nThis domain is for use in illustrative examples in documents. You may use this domain in literature without prior coordination or asking for permission.\n\nMore information...";

    public const string ExampleWebAt =
        "RootWebArea \"Example Domain\" [focused: True, url: https://example.com/]\n"
        + "  heading \"Example Domain\" [level: 1]\n"
        + "  paragraph \"This domain is for use in illustrative examples in documents. You may use this domain in literature without prior coordination or asking for permission.\"\n"
        + "  link \"More information...\" [url: https://www.iana.org/domains/example]";

    /// <summary>The file the multi-tool script writes and views.</summary>
    public const string MultiToolFile = "/tmp/hello.txt";

    /// <summary>More turns than any run needs.</summary>
    private const int FakeTurnBudget = 256;

    private InterventionScript(string mode, IReadOnlyList<string> operatorLines, Func<int, string?, ModelOutput> turn, Action<FakeSandboxScript> sandbox)
    {
        Mode = mode;
        OperatorLines = operatorLines;
        _turn = turn;
        _sandbox = sandbox;
    }

    private readonly Func<int, string?, ModelOutput> _turn;

    private readonly Action<FakeSandboxScript> _sandbox;

    /// <summary>The mode this script drives (<c>shell</c>, <c>computer</c> or <c>multi-tool</c> for anything else, as the task does).</summary>
    public string Mode { get; }

    /// <summary>What the operator types, in order: the initial prompt, then one answer per "Next Action" screen (the last is <c>exit</c>).</summary>
    public IReadOnlyList<string> OperatorLines { get; }

    /// <summary>The script for <paramref name="mode"/>.</summary>
    public static InterventionScript For(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return mode switch
        {
            Intervention.ShellMode => Shell(),
            Intervention.ComputerMode => Computer(),
            _ => MultiTool(),
        };
    }

    /// <summary>The operator's console: <see cref="OperatorLines"/> read in a loop (every sample and epoch gets the same session), each line echoed to <paramref name="output"/> as a terminal would.</summary>
    public InputConsole Console(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new InputConsole(new CyclicLineReader(OperatorLines, output), output);
    }

    /// <summary>The scripted model: <see cref="Respond"/> on every request.</summary>
    public Model Model() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), FakeTurnBudget), FakeModelName));

    /// <summary>The scripted sandbox: the mode's tool answers.</summary>
    public FakeSandboxScript Sandbox()
    {
        var script = new FakeSandboxScript();
        _sandbox(script);
        return script;
    }

    /// <summary>The model's reply for the conversation so far: the turn is the number of assistant messages, and the last tool output feeds the text replies.</summary>
    public ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var step = messages.Count(message => message is ChatMessageAssistant);
        var lastTool = messages.OfType<ChatMessageTool>().LastOrDefault()?.Text.Trim();
        return _turn(step, lastTool) with { Model = FakeModelName };
    }

    private static ModelOutput Call(string function, object args, string text) => ScriptedTurn.ToolCall(function, args, text: text).Output!;

    private static ModelOutput Text(string text) => ScriptedTurn.Text(text).Output!;

    private static InterventionScript Shell() => new(
        Intervention.ShellMode,
        [
            "List the files in the working directory and tell me how many entries there are.",
            "",
            "Which Python version is installed?",
            "exit",
        ],
        (step, lastTool) => step switch
        {
            0 => Call("bash", new { cmd = "ls -la" }, "I'll start by listing the working directory."),
            1 => Text($"Here is the listing:\n\n{lastTool}\n\nCounting the entries other than . and .. there are 3: README.md, data and notes.txt."),
            2 => Call("python", new { code = "import os\nprint(len(os.listdir('.')))" }, "Continuing: I'll confirm the count with Python."),
            3 => Text($"Python counts {lastTool} entries, which matches the listing."),
            4 => Call("bash", new { cmd = "python3 --version" }, "I'll check the interpreter version."),
            5 => Text($"The installed interpreter is {lastTool}."),
            _ => Text("I have nothing further to do."),
        },
        script => script
            .OnExact(FakeSandboxScript.Ok(ShellListing), "bash", "--login", "-c", "ls -la")
            .OnExact(FakeSandboxScript.Ok(ShellPythonVersion), "bash", "--login", "-c", "python3 --version")
            .OnExact(call => call.Input?.Contains("os.listdir", StringComparison.Ordinal) == true ? FakeSandboxScript.Ok("3\n") : null, "bash", "--login", "-c", "python3 -")
            .WithDefault(FakeSandboxScript.Ok()));

    private static InterventionScript Computer() => new(
        Intervention.ComputerMode,
        [
            "Open a terminal window on the desktop.",
            "",
            "exit",
        ],
        (step, _) => step switch
        {
            0 => Call("computer", new { action = "screenshot" }, "Let me look at the screen first."),
            1 => Call("computer", new { action = "double_click", coordinate = new[] { 40, 60 } }, "I can see the Terminal icon on the desktop; icons need a double click to open."),
            2 => Text("I have evaluated step 1: a terminal window is now open on the desktop."),
            3 => Call("computer", new { action = "screenshot" }, "Continuing: I'll take another look to confirm the state of the screen."),
            _ => Text("I have evaluated step 2: the terminal is open and nothing else remains to be done."),
        },
        script => script
            .OnExact(FakeSandboxScript.Ok(), "test", "-r", ComputerTool.ToolPath)
            .OnPrefix(call => FakeSandboxScript.Ok(ComputerResult(call.Cmd[2], call.Cmd.Skip(3).ToList())), "python3", ComputerTool.ToolPath)
            .WithDefault(FakeSandboxScript.Ok()));

    private static InterventionScript MultiTool() => new(
        Intervention.MultiToolMode,
        [
            $"Create {MultiToolFile} containing 'hello world', show it to me, then open https://example.com.",
            "exit",
        ],
        (step, _) => step switch
        {
            0 => Call("bash_session", new { action = "type_submit", input = $"echo 'hello world' > {MultiToolFile}" }, "I'll write the file in the bash session."),
            1 => Call("text_editor", new { command = "view", path = MultiToolFile }, "Now I'll show the file with the text editor."),
            2 => Call("web_browser_go", new { url = "https://example.com" }, "Finally I'll open example.com in the browser."),
            _ => Text($"Done: {MultiToolFile} contains 'hello world' and https://example.com shows the Example Domain page."),
        },
        script => script
            .OnExact(FakeSandboxScript.Ok(), "test", "-r", SandboxToolSupport.SandboxCli)
            .OnExact(FakeSandboxScript.Ok($"/usr/local/bin/{LegacyToolSupport.LegacySandboxCli}\n"), "which", LegacyToolSupport.LegacySandboxCli)
            .OnExact(call => FakeJsonRpc.Respond(call, SandboxToolsMethod), SandboxToolSupport.SandboxCli, "exec")
            .OnExact(call => FakeJsonRpc.Respond(call, WebBrowserMethod), LegacyToolSupport.LegacySandboxCli, "exec")
            .WithDefault(FakeSandboxScript.Ok()));

    /// <summary>The fake computer service's JSON: a screenshot for <c>screenshot</c>, a description plus a screenshot for the other actions.</summary>
    public static string ComputerResult(string action, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new JsonObject { ["base64_image"] = ScreenshotPng };
        if (action != "screenshot")
        {
            result["output"] = $"Performed {action}{(arguments.Count > 0 ? " " + string.Join(" ", arguments) : "")}";
        }

        return result.ToJsonString();
    }

    /// <summary>The injected sandbox tools (<c>bash_session</c>, <c>text_editor</c>) the multi-tool script answers.</summary>
    public static JsonNode? SandboxToolsMethod(string method, JsonObject? parameters) => method switch
    {
        "version" => "1.0.0",
        "bash_session_new_session" => new JsonObject { ["session_name"] = "fake-bash-session" },
        "bash_session" => BashSessionOutput(parameters?["input"]?.GetValue<string>()),
        "text_editor" => TextEditorOutput(parameters?["command"]?.GetValue<string>(), parameters?["path"]?.GetValue<string>()),
        _ => throw new InvalidOperationException($"the fake sandbox tools do not implement '{method}'"),
    };

    /// <summary>The legacy <c>inspect-tool-support</c> service (the web browser) the multi-tool script answers.</summary>
    public static JsonNode? WebBrowserMethod(string method, JsonObject? parameters) => method switch
    {
        "version" => "1.0.0",
        "web_new_session" => new JsonObject { ["session_name"] = "fake-browser" },
        _ when method.StartsWith("web_", StringComparison.Ordinal) => new JsonObject
        {
            ["web_url"] = parameters?["url"]?.GetValue<string>() ?? "https://example.com/",
            ["main_content"] = ExampleMainContent,
            ["web_at"] = ExampleWebAt,
            ["error"] = "",
        },
        _ => throw new InvalidOperationException($"the fake web browser does not implement '{method}'"),
    };

    private static string BashSessionOutput(string? input)
    {
        var command = (input ?? "").TrimEnd('\n');
        return command.Length == 0 ? "$ " : $"$ {command}\n$ ";
    }

    private static string TextEditorOutput(string? command, string? path) => command switch
    {
        "view" => $"Here's the result of running `cat -n` on {path}:\n     1\thello world\n",
        "create" => $"File created successfully at: {path}",
        _ => $"The file {path} has been edited.",
    };

    /// <summary>A reader that hands out <paramref name="lines"/> in order and starts over after the last one, echoing each line to <paramref name="echo"/> (the way a terminal shows what was typed); safe to share between samples.</summary>
    public sealed class CyclicLineReader(IReadOnlyList<string> lines, TextWriter? echo = null) : TextReader
    {
        private readonly IReadOnlyList<string> _lines = lines ?? throw new ArgumentNullException(nameof(lines));

        private readonly object _sync = new();

        private int _next;

        /// <summary>How many lines were read so far.</summary>
        public int LinesRead { get; private set; }

        public override string? ReadLine()
        {
            lock (_sync)
            {
                if (_lines.Count == 0)
                {
                    return null;
                }

                var line = _lines[_next];
                _next = (_next + 1) % _lines.Count;
                LinesRead++;
                echo?.WriteLine(line);
                return line;
            }
        }

        public override Task<string?> ReadLineAsync() => Task.FromResult(ReadLine());

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadLine());
        }

        public override int Read() => throw new NotSupportedException("the operator script is read line by line");

        public override int Peek() => throw new NotSupportedException("the operator script is read line by line");
    }
}

/// <summary>
/// Answers the JSON-RPC requests the sandbox tool CLIs (<c>inspect-sandbox-tools exec</c>, <c>inspect-tool-support exec</c>)
/// receive on stdin: parses the request, calls <c>handler(method, params)</c> and writes a JSON-RPC 2.0 response
/// carrying the request's id (a handler exception becomes a JSON-RPC error). An addition for the fake sandbox.
/// </summary>
public static class FakeJsonRpc
{
    public static ExecResult Respond(FakeExecCall call, Func<string, JsonObject?, JsonNode?> handler)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(handler);
        if (JsonNode.Parse(call.Input ?? "") is not JsonObject request)
        {
            return FakeSandboxScript.Fail(1, "the fake tool CLI expects a JSON-RPC request on stdin");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        try
        {
            response["result"] = handler(method, request["params"] as JsonObject);
        }
        catch (InvalidOperationException ex)
        {
            response["error"] = new JsonObject { ["code"] = -32601, ["message"] = ex.Message };
        }

        return id is null ? FakeSandboxScript.Ok() : FakeSandboxScript.Ok(response.ToJsonString());
    }
}
