// ============================================================================
//  LAYER 7 (bottom): RUNTIME SERVICES — the part that runs INSIDE the container
//  Python: the separate `inspect_sandbox_tools` package
//
//  Notice: this namespace does not reference `inspect_ai` at all. That is the
//  whole point. inspect_sandbox_tools is a second, self-contained package that
//  Inspect copies into every sandbox container and launches with `docker exec`.
//  It has to run where inspect_ai is NOT installed (a bare Linux image), so it
//  depends on nothing but the standard library.
//
//  The eval process talks to it over JSON-RPC on stdin/stdout. The stateful
//  tools — `bash_session`, `text_editor`, `web_browser` — are implemented on
//  this side of the process boundary, which is why they keep their state
//  (a live shell, an open file) between tool calls.
//
//  The demo models the container as an in-memory dictionary of files and the
//  JSON-RPC transport as a string in / string out call. The layer guard in
//  Program.cs verifies that nothing here refers to any inspect_ai type.
// ============================================================================
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace inspect_sandbox_tools;

/// <summary>The in-container RPC server. One instance per container.</summary>
public sealed class SandboxToolsServer
{
    // The container's file system, as far as the demo is concerned.
    private readonly Dictionary<string, string> _files = new();
    private int _nextId;

    /// <summary>
    /// Handle one JSON-RPC request and return the JSON-RPC response.
    /// Real transport: one line of JSON per request on the process's stdin,
    /// one line per response on stdout.
    /// </summary>
    public string Handle(string requestJson)
    {
        var request = JsonNode.Parse(requestJson)!.AsObject();
        var id = request["id"]?.GetValue<int>() ?? ++_nextId;
        var method = request["method"]?.GetValue<string>() ?? "";
        var @params = request["params"]?.AsObject() ?? new JsonObject();

        JsonNode result = method switch
        {
            "bash" => Bash(@params["cmd"]!.GetValue<string>()),
            "text_editor" => TextEditor(@params),
            _ => throw new InvalidOperationException($"unknown method '{method}'"),
        };

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    }

    /// <summary>A toy shell: enough commands, plus `&&` / `||` / `;` chaining, to
    /// make the demo tasks solvable by a real model that spells things its own way.</summary>
    private JsonNode Bash(string commandLine)
    {
        string stdout = "", stderr = "";
        var code = 0;
        var op = ";";
        foreach (var token in Regex.Split(commandLine, @"(&&|\|\||;)").Select(t => t.Trim()))
        {
            if (token is "&&" or "||" or ";") { op = token; continue; }
            if (token.Length == 0) continue;
            if (op == "&&" && code != 0) continue;   // run only if the previous command succeeded
            if (op == "||" && code == 0) continue;   // run only if it failed

            var words = token.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.StartsWith("2>") && !w.StartsWith(">"))   // redirections: ignored, the demo has no /dev/null
                .ToArray();
            var (o, e, c) = Run(words);
            stdout += o;
            stderr += e;
            code = c;
        }
        return new JsonObject { ["stdout"] = stdout, ["stderr"] = stderr, ["returncode"] = code };
    }

    private (string Stdout, string Stderr, int Code) Run(string[] parts) => parts switch
    {
        ["cat", .. var files] when files.Length > 0 => Cat(files),
        ["ls", ..] => (string.Join('\n', _files.Keys.OrderBy(k => k)) + "\n", "", 0),   // flags ignored
        ["pwd"] => ("/workspace\n", "", 0),
        ["echo", .. var rest] => (string.Join(' ', rest).Trim('"', '\'') + "\n", "", 0),
        ["wc", "-l", var file] => _files.TryGetValue(Normalize(file), out var text)
            ? ($"{text.Count(ch => ch == '\n')} {file}\n", "", 0)
            : ("", $"wc: {file}: No such file or directory\n", 1),
        [var other, ..] => ("", $"bash: {other}: command not found\n", 127),
        _ => ("", "", 0),
    };

    private (string, string, int) Cat(string[] files)
    {
        string stdout = "", stderr = "";
        var code = 0;
        foreach (var file in files)
        {
            if (_files.TryGetValue(Normalize(file), out var text)) stdout += text;
            else { stderr += $"cat: {file}: No such file or directory\n"; code = 1; }
        }
        return (stdout, stderr, code);
    }

    /// <summary>The working directory is /workspace; accept the ways a model may spell a path in it.</summary>
    private static string Normalize(string path)
    {
        path = path.Trim('"', '\'');
        if (path.StartsWith("/workspace/")) path = path["/workspace/".Length..];
        if (path.StartsWith("./")) path = path[2..];
        return path;
    }

    /// <summary>The `text_editor` tool's `create` and `view` commands.</summary>
    private JsonNode TextEditor(JsonObject p)
    {
        var command = p["command"]!.GetValue<string>();
        var path = p["path"]!.GetValue<string>();
        switch (command)
        {
            case "create":
                _files[path] = p["file_text"]!.GetValue<string>();
                return JsonValue.Create($"File created successfully at: {path}");
            case "view":
                return JsonValue.Create(_files.TryGetValue(path, out var text) ? text : $"{path}: not found");
            default:
                throw new InvalidOperationException($"text_editor: unknown command '{command}'");
        }
    }
}
