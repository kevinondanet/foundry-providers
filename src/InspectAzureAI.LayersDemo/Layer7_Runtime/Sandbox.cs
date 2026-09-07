// ============================================================================
//  LAYER 7 (bottom): RUNTIME SERVICES — the part that runs in the eval process
//  Python: inspect_ai/util  (sandbox, store, subprocess, concurrency, display)
//
//  `inspect_ai.util` is the public "services" package. Its best-known member
//  is `sandbox()`: the handle a tool uses to run commands and move files in an
//  isolated environment. Sandbox *providers* (docker, local, k8s, ...) are
//  registered by name, just like everything else, and the engine creates one
//  per sample before the solver starts.
//
//  Runtime services depend on nothing above them. Sandbox.Exec() does not
//  know what a solver or a task is; it knows how to send one JSON-RPC request
//  to the in-container server (SandboxTools.cs) and record the exchange in the
//  transcript. That transcript event is its only message to the world above.
// ============================================================================
using System.Text.Json;
using System.Text.Json.Nodes;
using inspect_ai._util.display;
using inspect_ai.log;
using inspect_sandbox_tools;

namespace inspect_ai.util;

/// <summary>What a command execution returns (Python: ExecResult).</summary>
public sealed record ExecResult(bool Success, int ReturnCode, string Stdout, string Stderr);

/// <summary>The contract every sandbox provider implements (Python: SandboxEnvironment).</summary>
public abstract class SandboxEnvironment
{
    public abstract Task<ExecResult> Exec(string command);
    public abstract Task WriteFile(string path, string contents);
}

/// <summary>Ambient access to the current sample's sandbox, like `transcript()`.</summary>
public static class Sandboxes
{
    private static readonly AsyncLocal<SandboxEnvironment?> Current = new();

    /// <summary>Python: `from inspect_ai.util import sandbox; await sandbox().exec(...)`.</summary>
    public static SandboxEnvironment sandbox()
        => Current.Value ?? throw new InvalidOperationException("sandbox() called for a sample that has no sandbox.");

    /// <summary>Engine-only: bind the sandbox for the current sample.</summary>
    internal static IDisposable Begin(SandboxEnvironment env)
    {
        var previous = Current.Value;
        Current.Value = env;
        return new Restore(() => Current.Value = previous);
    }

    /// <summary>Engine-only: build the provider named in the task's `sandbox=` setting.</summary>
    internal static SandboxEnvironment Create(string provider, string sampleId) => provider switch
    {
        "container" => new ContainerSandbox(sampleId),
        _ => throw new NotSupportedException($"sandbox provider '{provider}' (the demo only ships 'container')"),
    };

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
}

/// <summary>
/// Stand-in for the Docker provider. Owns one "container" whose only process
/// is the inspect_sandbox_tools RPC server, and talks to it the way Inspect
/// does: serialise a JSON-RPC request, hand it across the process boundary,
/// parse the JSON-RPC response.
/// </summary>
internal sealed class ContainerSandbox(string sampleId) : SandboxEnvironment
{
    private readonly SandboxToolsServer _inContainer = new();   // "docker exec ... inspect_sandbox_tools"
    private int _nextId;

    public override async Task<ExecResult> Exec(string command)
    {
        Display.Step("L7 util (sandbox)", $"[{sampleId}] exec in container: {command}");
        var result = await Rpc("bash", new JsonObject { ["cmd"] = command });
        var exec = new ExecResult(
            Success: result["returncode"]!.GetValue<int>() == 0,
            ReturnCode: result["returncode"]!.GetValue<int>(),
            Stdout: result["stdout"]!.GetValue<string>(),
            Stderr: result["stderr"]!.GetValue<string>());

        // The only upward communication: an event in the sample transcript.
        Transcript.transcript().Emit(new SandboxEvent("L7 util", "exec", command, exec.Success ? exec.Stdout : exec.Stderr));
        return exec;
    }

    public override async Task WriteFile(string path, string contents)
    {
        Display.Step("L7 util (sandbox)", $"[{sampleId}] write {path} into container ({contents.Length} chars)");
        var result = await Rpc("text_editor", new JsonObject { ["command"] = "create", ["path"] = path, ["file_text"] = contents });
        Transcript.transcript().Emit(new SandboxEvent("L7 util", "write_file", path, result.GetValue<string>()));
    }

    /// <summary>One JSON-RPC round trip across the process boundary.</summary>
    private async Task<JsonNode> Rpc(string method, JsonObject @params)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_nextId, ["method"] = method, ["params"] = @params };
        var requestJson = request.ToJsonString();
        Display.Step("L7 -> container", $"JSON-RPC >> {Truncate(requestJson)}");

        await Task.Yield();   // a real transport awaits stdout; yield so the loop can interleave other samples
        var responseJson = _inContainer.Handle(requestJson);

        Display.Step("L7 <- container", $"JSON-RPC << {Truncate(responseJson)}");
        return JsonNode.Parse(responseJson)!["result"]!;
    }

    private static string Truncate(string s) => s.Length <= 96 ? s : s[..93] + "...";
}
