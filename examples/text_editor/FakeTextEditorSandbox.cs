using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.TextEditor;

/// <summary>
/// The sandbox behind <c>--fake</c> for <c>examples/text_editor.py</c>: a <see cref="FakeSandboxScript"/> that
/// plays the injected <c>inspect-sandbox-tools</c> launcher. It reports the launcher as already present
/// (<c>test -r</c> succeeds, so nothing is downloaded or injected) and answers <c>&lt;cli&gt; exec</c> by reading the
/// JSON-RPC request from stdin and running the <c>text_editor</c> method against the sample's file store through
/// <see cref="TextEditorEmulator"/>, so the <c>verify_edit</c> scorer's <c>sandbox().read_file</c> sees the edits.
/// </summary>
public static class FakeTextEditorSandbox
{
    /// <summary>What the <c>version</c> RPC reports.</summary>
    public const string Version = "1.0.0 (scripted)";

    public static FakeSandboxScript Create()
    {
        var script = new FakeSandboxScript();
        return script
            .OnExact(FakeSandboxScript.Ok(), "test", "-r", SandboxToolSupport.SandboxCli)
            .OnExact(call => Handle(script, call), SandboxToolSupport.SandboxCli, "exec");
    }

    /// <summary>Answers one <c>exec</c> call: a JSON-RPC success or error response on stdout (the launcher exits 0 either way).</summary>
    private static ExecResult Handle(FakeSandboxScript script, FakeExecCall call)
    {
        JsonObject? request;
        try
        {
            request = string.IsNullOrWhiteSpace(call.Input) ? null : JsonNode.Parse(call.Input) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return FakeSandboxScript.Fail(1, "inspect-sandbox-tools exec: expected a JSON-RPC request on stdin");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        try
        {
            var result = method switch
            {
                "version" => Version,
                "text_editor" => TextEditorEmulator.Execute(FilesOf(script, call), request["params"] as JsonObject ?? new JsonObject()),
                _ => throw new JsonRpcFailure(-32601, "Method not found"),
            };
            return FakeSandboxScript.Ok(Response(id, "result", result));
        }
        catch (JsonRpcFailure failure)
        {
            return FakeSandboxScript.Ok(Response(id, "error", new JsonObject { ["code"] = failure.Code, ["message"] = failure.Message }));
        }
    }

    private static string Response(JsonNode? id, string member, JsonNode? value) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, [member] = value }.ToJsonString();

    /// <summary>
    /// The file store of the sample that made <paramref name="call"/> (<see cref="FakeSandboxScript.EnvironmentOf"/>).
    /// </summary>
    private static IDictionary<string, byte[]> FilesOf(FakeSandboxScript script, FakeExecCall call) =>
        script.EnvironmentOf(call)?.Files
        ?? throw new InvalidOperationException("The exec call was not recorded on a scripted sandbox environment.");
}

/// <summary>A JSON-RPC error to send back: <see cref="SandboxToolsErrorMapper.ToolExceptionCode"/> reaches the model as a tool error, other codes fail the call.</summary>
internal sealed class JsonRpcFailure(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// An in-memory port of the sandbox-side editor (<c>inspect_sandbox_tools/_in_process_tools/_text_editor/text_editor.py</c>,
/// itself adapted from Anthropic's computer-use <c>edit.py</c>) over a path-to-bytes file store: <c>view</c>,
/// <c>create</c>, <c>str_replace</c> and <c>insert</c> with the launcher's exact result and error messages
/// (<c>cat -n</c> style numbering, four-line snippets, tab expansion). <c>undo_edit</c> keeps no history and
/// reports none, and directories are not modelled.
/// </summary>
public static class TextEditorEmulator
{
    private const int SnippetLines = 4;

    public static string Execute(IDictionary<string, byte[]> files, JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(parameters);
        var command = Required(parameters, "command");
        var path = Required(parameters, "path");
        return command switch
        {
            "view" => View(files, path, parameters["view_range"] as JsonArray),
            "create" => Create(files, path, Required(parameters, "file_text")),
            "str_replace" => StrReplace(files, path, parameters["old_str"]?.GetValue<string>(), parameters["new_str"]?.GetValue<string>()),
            "insert" => Insert(files, path, parameters["insert_line"]?.GetValue<int>() ?? throw new JsonRpcFailure(-32602, "insert_line is required for the insert command"), Required(parameters, "new_str")),
            "undo_edit" => throw ToolException($"No edit history found for {path}. The text editor only retains the last 10 edits per file."),
            _ => throw new JsonRpcFailure(-32602, $"Unrecognized command {command}. The allowed commands for the text_editor tool are: view, create, str_replace, insert, undo_edit"),
        };
    }

    /// <summary>Port of <c>_make_output</c>: the <c>cat -n</c> rendering with six-wide line numbers.</summary>
    public static string MakeOutput(string content, string descriptor, int initLine = 1)
    {
        ArgumentNullException.ThrowIfNull(content);
        var numbered = string.Join("\n", ExpandTabs(content).Split('\n').Select((line, index) => $"{index + initLine,6}\t{line}"));
        return $"Here's the result of running `cat -n` on {descriptor}:\n{numbered}\n";
    }

    /// <summary>Python's <c>str.expandtabs()</c> with the default tab size of 8.</summary>
    public static string ExpandTabs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\t'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var column = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\t':
                    var spaces = 8 - (column % 8);
                    builder.Append(' ', spaces);
                    column += spaces;
                    break;
                case '\n' or '\r':
                    builder.Append(ch);
                    column = 0;
                    break;
                default:
                    builder.Append(ch);
                    column++;
                    break;
            }
        }

        return builder.ToString();
    }

    private static string View(IDictionary<string, byte[]> files, string path, JsonArray? viewRange)
    {
        var content = Read(files, path);
        var initLine = 1;
        if (viewRange is not null)
        {
            if (viewRange.Count != 2 || viewRange.Any(item => item is not JsonValue value || !value.TryGetValue<int>(out _)))
            {
                throw ToolException("Invalid `view_range`. It should be a list of two integers.");
            }

            var lines = content.Split('\n');
            var count = lines.Length;
            initLine = viewRange[0]!.GetValue<int>();
            var finalLine = viewRange[1]!.GetValue<int>();
            var range = $"[{initLine}, {finalLine}]";
            if (initLine < 1 || initLine > count)
            {
                throw ToolException($"Invalid `view_range`: {range}. Its first element `{initLine}` should be within the range of lines of the file: [1, {count}]");
            }

            if (finalLine > count)
            {
                finalLine = -1;
            }

            if (finalLine != -1 && finalLine < initLine)
            {
                throw ToolException($"Invalid `view_range`: {range}. Its second element `{finalLine}` should be larger or equal than its first `{initLine}`");
            }

            content = string.Join("\n", finalLine == -1 ? lines.Skip(initLine - 1) : lines.Skip(initLine - 1).Take(finalLine - initLine + 1));
        }

        return MakeOutput(content, path, initLine);
    }

    private static string Create(IDictionary<string, byte[]> files, string path, string fileText)
    {
        if (files.ContainsKey(path))
        {
            throw ToolException($"File already exists at: {path}. Cannot overwrite files using command `create`.");
        }

        Write(files, path, fileText);
        return $"File created successfully at: {path}";
    }

    private static string StrReplace(IDictionary<string, byte[]> files, string path, string? oldStr, string? newStr)
    {
        if (string.IsNullOrEmpty(oldStr))
        {
            throw ToolException("str_replace: The `old_str` parameter cannot be empty. Consider using the `insert` command instead.");
        }

        var content = ExpandTabs(Read(files, path));
        oldStr = ExpandTabs(oldStr);
        newStr = newStr is null ? "" : ExpandTabs(newStr);

        var occurrences = CountOccurrences(content, oldStr);
        if (occurrences == 0)
        {
            throw ToolException($"No replacement was performed, old_str `{oldStr}` did not appear verbatim in {path}.");
        }

        if (occurrences > 1)
        {
            var lines = content.Split('\n').Select((line, index) => (line, number: index + 1)).Where(pair => pair.line.Contains(oldStr, StringComparison.Ordinal)).Select(pair => pair.number);
            throw ToolException($"No replacement was performed. Multiple occurrences of old_str `{oldStr}` in lines [{string.Join(", ", lines)}]. Please ensure it is unique");
        }

        var updated = content.Replace(oldStr, newStr, StringComparison.Ordinal);
        Write(files, path, updated);

        var replacementLine = content[..content.IndexOf(oldStr, StringComparison.Ordinal)].Count(ch => ch == '\n');
        var startLine = Math.Max(0, replacementLine - SnippetLines);
        var endLine = replacementLine + SnippetLines + newStr.Count(ch => ch == '\n');
        var snippet = string.Join("\n", updated.Split('\n').Skip(startLine).Take(endLine + 1 - startLine));

        return $"The file {path} has been edited. "
            + MakeOutput(snippet, $"a snippet of {path}", startLine + 1)
            + "Review the changes and make sure they are as expected. Edit the file again if necessary.";
    }

    private static string Insert(IDictionary<string, byte[]> files, string path, int insertLine, string newStr)
    {
        var lines = ExpandTabs(Read(files, path)).Split('\n');
        newStr = ExpandTabs(newStr);
        var count = lines.Length;
        if (insertLine < 0 || insertLine > count)
        {
            throw ToolException($"Invalid `insert_line` parameter: {insertLine}. It should be within the range of lines of the file: [0, {count}]");
        }

        var newLines = newStr.Split('\n');
        var updatedLines = lines.Take(insertLine).Concat(newLines).Concat(lines.Skip(insertLine));
        var snippetLines = lines.Skip(Math.Max(0, insertLine - SnippetLines)).Take(insertLine - Math.Max(0, insertLine - SnippetLines))
            .Concat(newLines)
            .Concat(lines.Skip(insertLine).Take(SnippetLines));

        Write(files, path, string.Join("\n", updatedLines));

        return $"The file {path} has been edited. "
            + MakeOutput(string.Join("\n", snippetLines), "a snippet of the edited file", Math.Max(1, insertLine - SnippetLines + 1))
            + "Review the changes and make sure they are as expected (correct indentation, no duplicate lines, etc). Edit the file again if necessary.";
    }

    private static string Read(IDictionary<string, byte[]> files, string path) =>
        files.TryGetValue(path, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : throw ToolException($"The path {path} does not exist. Please provide a valid path.");

    private static void Write(IDictionary<string, byte[]> files, string path, string content) => files[path] = Encoding.UTF8.GetBytes(content);

    private static string Required(JsonObject parameters, string name) =>
        parameters[name]?.GetValue<string>() ?? throw new JsonRpcFailure(-32602, $"Required parameter {name} not provided to tool call.");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The launcher's <c>ToolException</c>: JSON-RPC code <see cref="SandboxToolsErrorMapper.ToolExceptionCode"/>, which the tool side turns into a <c>ToolError</c> for the model.</summary>
    private static JsonRpcFailure ToolException(string message) => new(SandboxToolsErrorMapper.ToolExceptionCode, message);
}
