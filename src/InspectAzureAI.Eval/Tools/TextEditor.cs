using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_text_editor.py</c> <c>text_editor()</c>: a custom editing tool for viewing,
/// creating and editing files in the sample sandbox (e.g. "docker"). The editing itself runs inside the
/// injected <c>inspect-sandbox-tools</c> launcher (an in-process tool, so <c>user</c> is honoured by a
/// setuid in the short-lived CLI); this side only marshals the call over JSON-RPC.
/// </summary>
public static class TextEditor
{
    public const string Name = "text_editor";

    /// <summary>The tool description the model sees (Python's <c>execute</c> docstring summary).</summary>
    public const string Description = "Use this function to execute text editing commands.";

    /// <summary>Python: <c>timeout = timeout or 180</c> (a null or zero timeout means this default).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    public static readonly IReadOnlyList<string> Commands = ["view", "create", "str_replace", "insert", "undo_edit"];

    /// <summary>The parameter schema, byte-for-byte what Python's <c>ToolDef(text_editor()).parameters</c> dumps.</summary>
    public static ToolParams Parameters { get; } = new()
    {
        Properties = new Dictionary<string, ToolParam>
        {
            ["command"] = new()
            {
                Type = ["string"],
                Description = "The command to execute. Note: `undo_edit` retains only the last 10 edits per file.",
                Enum = Commands.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray(),
            },
            ["path"] = ToolParam.Of("string", "Path to file or directory, e.g. `/repo/file.py` or `../repo`."),
            ["file_text"] = Nullable("string", "Required parameter of `create` command, with the content of the file to be created."),
            ["insert_line"] = Nullable("integer", "Required parameter of `insert` command. The `new_str` will be inserted AFTER the line `insert_line` of `path`."),
            ["insert_text"] = Nullable("string", "Required parameter of `insert` command containing the string to insert."),
            ["new_str"] = Nullable("string", "Optional parameter of `str_replace` command containing the new string (if not given, no string will be added)."),
            ["old_str"] = Nullable("string", "Required parameter of `str_replace` command containing the string in `path` to replace."),
            ["view_range"] = new()
            {
                Description = "Optional parameter of `view` command when `path` points to a file. If none is given, the full file is shown. If provided, the file will be shown in the indicated line number range, e.g. [11, 12] will show lines 11 and 12. Indexing at 1 to start. Setting `[start_line, -1]` shows all lines from `start_line` to the end of the file.",
                AnyOf = [new ToolParam { Type = ["array"], Items = ToolParam.Of("integer") }, ToolParam.Of("null")],
            },
        },
        Required = ["command", "path"],
    };

    /// <summary>
    /// Port of <c>text_editor(timeout, user)</c>.
    /// </summary>
    /// <param name="timeout">Timeout for the command; null or zero means 180 seconds.</param>
    /// <param name="user">User to execute commands as (passed through the reserved <c>_run_as_user</c> parameter).</param>
    /// <param name="support">The injection facade; defaults to <see cref="SandboxToolSupport.Default"/>.</param>
    public static ToolDef Create(TimeSpan? timeout = null, string? user = null, SandboxToolSupport? support = null)
    {
        var effectiveTimeout = timeout is { Ticks: > 0 } given ? given : DefaultTimeout;
        var toolSupport = support ?? SandboxToolSupport.Default;
        return new ToolDef(Name, Description, Parameters, async (arguments, cancellationToken) =>
        {
            var command = ToolArguments.RequiredChoice(arguments, "command", Commands);
            var path = ToolArguments.RequiredString(arguments, "path");
            var fileText = ToolArguments.OptionalString(arguments, "file_text");
            var insertLine = ToolArguments.OptionalInteger(arguments, "insert_line");
            var insertText = ToolArguments.OptionalString(arguments, "insert_text");
            var newStr = ToolArguments.OptionalString(arguments, "new_str");
            var oldStr = ToolArguments.OptionalString(arguments, "old_str");
            var viewRange = ToolArguments.OptionalIntegerList(arguments, "view_range");

            var injected = await toolSupport.SandboxWithInjectedToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // re-wire insert_text => new_str
            if (command == "insert" && newStr is null && insertText is not null)
            {
                newStr = insertText;
            }

            // The same members, in the same order, as Python's locals() sweep of execute()'s signature; nulls are dropped on the wire.
            var parameters = new JsonObject
            {
                ["command"] = command,
                ["path"] = path,
                ["file_text"] = fileText,
                ["insert_line"] = insertLine,
                ["insert_text"] = insertText,
                ["new_str"] = newStr,
                ["old_str"] = oldStr,
                ["view_range"] = viewRange is null ? null : new JsonArray(viewRange.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            };
            if (user is not null)
            {
                parameters["_run_as_user"] = user;
            }

            var output = await JsonRpc.ExecScalarRequestAsync<string>(
                Name,
                parameters,
                injected.Transport,
                SandboxToolsErrorMapper.Instance,
                injected.CallOptions(effectiveTimeout),
                cancellationToken).ConfigureAwait(false);
            return output;
        });
    }

    private static ToolParam Nullable(string type, string description) => new()
    {
        Description = description,
        AnyOf = [ToolParam.Of(type), ToolParam.Of("null")],
    };
}
