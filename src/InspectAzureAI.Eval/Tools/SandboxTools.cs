using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>Port of <c>tool/_tools/_execute.py</c>: the <c>bash</c> and <c>python</c> tools running in the sample sandbox.</summary>
public static class SandboxTools
{
    public const string BashDescription = "Use this function to execute bash commands.";

    public const string PythonDescription =
        "Use the python function to execute Python code.\n\n"
        + "The Python tool executes single-run Python scripts. Important notes:\n"
        + "1. Each execution is independent - no state is preserved between runs\n"
        + "2. You must explicitly use print() statements to see any output\n"
        + "3. Simply writing expressions (like in notebooks) will not display results\n"
        + "4. The script cannot accept interactive input during execution\n"
        + "5. Return statements alone won't produce visible output\n"
        + "6. All variables and imports are cleared between executions\n"
        + "7. Standard output (via print()) is the only way to see results";

    /// <summary>Port of <c>bash()</c>: runs <c>bash --login -c cmd</c>; stderr (with a newline) precedes stdout as in Python.</summary>
    public static ToolDef Bash(TimeSpan? timeout = null, string? user = null, string? sandbox = null) =>
        new("bash", BashDescription, StringParam("cmd", "The bash command to execute."), async (arguments, cancellationToken) =>
        {
            var cmd = StringArgument(arguments, "cmd");
            var result = await SampleContext.Require().Sandbox(sandbox)
                .ExecAsync(["bash", "--login", "-c", cmd], timeout: timeout, user: user, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Output(result);
        });

    /// <summary>Port of <c>python()</c>: pipes the code to <c>python3 -</c> under a login shell.</summary>
    public static ToolDef Python(TimeSpan? timeout = null, string? user = null, string? sandbox = null) =>
        new("python", PythonDescription, StringParam("code", "The python code to execute."), async (arguments, cancellationToken) =>
        {
            var code = StringArgument(arguments, "code");
            var result = await SampleContext.Require().Sandbox(sandbox)
                .ExecAsync(["bash", "--login", "-c", "python3 -"], input: code, timeout: timeout, user: user, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Output(result);
        });

    private static ToolResult Output(ExecResult result)
    {
        var output = result.Stderr.Length > 0 ? result.Stderr + "\n" : "";
        return output + result.Stdout;
    }

    private static ToolParams StringParam(string name, string description) => new()
    {
        Properties = new Dictionary<string, ToolParam> { [name] = ToolParam.Of("string", description) },
        Required = [name],
    };

    private static string StringArgument(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            null => throw new ToolParsingError($"Required parameter {name} not provided to tool call."),
            _ => node.ToJsonString(),
        };
    }
}
