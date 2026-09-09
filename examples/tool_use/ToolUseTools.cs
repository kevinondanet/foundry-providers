using System.ComponentModel;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.ToolUse;

/// <summary>
/// Port of the four <c>@tool</c> functions of <c>examples/tool_use.py</c> (<c>add</c>, <c>list_files</c>,
/// <c>read_file</c>, <c>write_file</c>). Each is a <see cref="ToolDef"/> built by
/// <see cref="ToolDef.FromMethod(Delegate, string?, string?, bool)"/> over a static method: the signature is the
/// parameter schema and the <see cref="DescriptionAttribute"/>s play the role of the docstring summary and its
/// <c>Args:</c> section, so the model sees the same names, descriptions and parameters as in Python. The three
/// sandbox tools reach the sample's sandbox through <see cref="SampleContext.Sandbox"/>, Python's <c>sandbox()</c>.
/// </summary>
public static class ToolUseTools
{
    /// <summary>Port of <c>@tool def add()</c>: returns <c>x + y</c>.</summary>
    public static ToolDef Add() => ToolDef.FromMethod(new Func<int, int, int>(AddExecute), name: "add");

    /// <summary>Port of <c>@tool def list_files()</c>: <c>sandbox().exec(["ls", dir])</c>, stdout on success, else a <see cref="ToolError"/> carrying stderr.</summary>
    public static ToolDef ListFiles() => ToolDef.FromMethod(new Func<string, CancellationToken, Task<string>>(ListFilesExecute), name: "list_files");

    /// <summary>Port of <c>@tool def read_file()</c>: <c>sandbox().read_file(file)</c> (a missing file is reported to the model as a tool error, as in Python).</summary>
    public static ToolDef ReadFile() => ToolDef.FromMethod(new Func<string, CancellationToken, Task<string>>(ReadFileExecute), name: "read_file");

    /// <summary>Port of <c>@tool def write_file()</c>: <c>sandbox().write_file(file, contents)</c>, with an empty result.</summary>
    public static ToolDef WriteFile() => ToolDef.FromMethod(new Func<string, string, CancellationToken, Task>(WriteFileExecute), name: "write_file");

    [Description("Add two numbers.")]
    private static int AddExecute(
        [Description("First number to add.")] int x,
        [Description("Second number to add.")] int y) => x + y;

    [Description("List the files in a directory.")]
    private static async Task<string> ListFilesExecute([Description("Directory")] string dir, CancellationToken cancellationToken)
    {
        var result = await SampleContext.Require().Sandbox().ExecAsync(["ls", dir], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return result.Stdout;
        }

        throw new ToolError(result.Stderr);
    }

    [Description("Read the contents of a file.")]
    private static Task<string> ReadFileExecute([Description("File to read")] string file, CancellationToken cancellationToken) =>
        SampleContext.Require().Sandbox().ReadFileAsync(file, cancellationToken);

    [Description("Write content to a file.")]
    private static Task WriteFileExecute(
        [Description("File to write")] string file,
        [Description("Contents of file")] string contents,
        CancellationToken cancellationToken) =>
        SampleContext.Require().Sandbox().WriteFileAsync(file, contents, cancellationToken);
}
