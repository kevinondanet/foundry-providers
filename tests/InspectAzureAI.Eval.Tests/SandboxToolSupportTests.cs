using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Shared helpers for the sandbox tools tests: fixtures, JSON-RPC response builders and a scripted sandbox.</summary>
internal static class SandboxToolsFixtures
{
    public const string ChunkField = SandboxJsonRpcTransport.ResponseChunkField;

    public static string FixturePath(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, name);
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("No 'fixtures' directory above " + AppContext.BaseDirectory);
    }

    public static string Success(object? result, int id = 1) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result) }.ToJsonString();

    public static string Error(int code, string message, int id = 1) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    /// <summary>The chunk envelope the sandbox CLI emits for an oversized response (port of the Python test helper).</summary>
    public static string ChunkResponse(byte[] response, string handle, long offset, int chunkSize)
    {
        var chunk = response.Skip((int)offset).Take(chunkSize).ToArray();
        var next = offset + chunk.Length;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            [ChunkField] = new JsonObject
            {
                ["version"] = 1,
                ["handle"] = handle,
                ["offset"] = offset,
                ["next_offset"] = next,
                ["total_size"] = response.Length,
                ["done"] = next == response.Length,
                ["chunk"] = Convert.ToBase64String(chunk),
            },
        }.ToJsonString();
    }

    public static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    public static JsonObject Args(object arguments) => JsonSerializer.SerializeToNode(arguments)!.AsObject();

    public static JsonObject Request(string input) => JsonNode.Parse(input)!.AsObject();

    public static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "inspect-azureai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>An in-memory artifact source: a tiny gzip stands in for the published bundle.</summary>
internal sealed class FakeBinaries : ISandboxToolsBinarySource
{
    public byte[] GzipBytes { get; } = SandboxToolsFixtures.Gzip("launcher bytes");

    public List<(string Arch, bool Musl)> Requests { get; } = [];

    public Task<SandboxToolsArtifact> OpenAsync(string architecture, bool musl, CancellationToken cancellationToken = default)
    {
        Requests.Add((architecture, musl));
        return Task.FromResult(new SandboxToolsArtifact(SandboxToolsBinary.ExecutableName(architecture, musl), GzipBytes));
    }

    public Task<byte[]> UncompressedTarAsync(SandboxToolsArtifact artifact, CancellationToken cancellationToken = default) =>
        SandboxToolsBinary.GunzipAsync(artifact.GzipBytes, cancellationToken);
}

/// <summary>
/// A <see cref="FakeSandboxEnvironment"/> scripted for the injection protocol (detector probe, recon, mkdir,
/// tar, chmod, start-server) and for JSON-RPC <c>exec</c> calls, which are parsed and answered by <see cref="Handlers"/>.
/// </summary>
internal sealed class ScriptedToolsSandbox
{
    public ScriptedToolsSandbox(bool hasTools = true)
    {
        HasTools = hasTools;
        Sandbox = new FakeSandboxEnvironment { OnExecCall = Handle };
        Handlers["version"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("1.2.1"));
    }

    public FakeSandboxEnvironment Sandbox { get; }

    /// <summary>Whether the launcher is present (what <c>test -r</c> answers); extraction flips it on.</summary>
    public bool HasTools { get; set; }

    public bool RootAllowed { get; set; } = true;

    public bool RootProbeThrows { get; set; }

    public bool TarGzFails { get; set; }

    public bool ExtractLeavesNothing { get; set; }

    public bool StartServerFails { get; set; }

    public string Uname { get; set; } = "Linux";

    public string Machine { get; set; } = "aarch64";

    public string Libc { get; set; } = "glibc";

    /// <summary>JSON-RPC method handlers keyed by method name; an unknown method answers -32601.</summary>
    public Dictionary<string, Func<JsonObject, ExecResult>> Handlers { get; } = new(StringComparer.Ordinal);

    /// <summary>Every JSON-RPC request the sandbox CLI received, parsed.</summary>
    public List<JsonObject> Requests { get; } = [];

    /// <summary>The exec calls that carried a JSON-RPC request.</summary>
    public List<FakeExecCall> RpcCalls { get; } = [];

    public JsonObject? Params(int index) => Requests[index]["params"]?.AsObject();

    private ExecResult? Handle(FakeExecCall call)
    {
        var cmd = call.Cmd;
        switch (cmd[0])
        {
            case "test":
                return HasTools ? FakeSandboxEnvironment.Ok() : FakeSandboxEnvironment.Fail(1);
            case "sh":
                var script = cmd[2];
                if (script.Contains("uname -s", StringComparison.Ordinal))
                {
                    return FakeSandboxEnvironment.Ok(Uname + "\n");
                }

                if (script.Contains("uname -m", StringComparison.Ordinal))
                {
                    return FakeSandboxEnvironment.Ok(Machine + "\n");
                }

                if (script.Contains("libc.musl", StringComparison.Ordinal))
                {
                    return FakeSandboxEnvironment.Ok(Libc + "\n");
                }

                if (script.Contains("os-release", StringComparison.Ordinal))
                {
                    return FakeSandboxEnvironment.Ok("PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nID=debian\nVERSION=\"12 (bookworm)\"\n");
                }

                return FakeSandboxEnvironment.Ok();
            case "mkdir":
                if (call.User == "root")
                {
                    if (RootProbeThrows)
                    {
                        throw new InvalidOperationException("runuser: may not be used by non-root users");
                    }

                    return RootAllowed ? FakeSandboxEnvironment.Ok() : FakeSandboxEnvironment.Fail(1, "mkdir: permission denied");
                }

                return FakeSandboxEnvironment.Ok();
            case "tar":
                if (cmd[1] == "xzf" && TarGzFails)
                {
                    return FakeSandboxEnvironment.Fail(1, "tar: invalid option -- 'z'");
                }

                HasTools = !ExtractLeavesNothing;
                return FakeSandboxEnvironment.Ok();
            case "chmod" or "rm":
                return FakeSandboxEnvironment.Ok();
            case SandboxToolSupport.SandboxCli when cmd[1] == "start-server":
                return StartServerFails ? FakeSandboxEnvironment.Fail(1, "server did not start") : FakeSandboxEnvironment.Ok();
            case SandboxToolSupport.SandboxCli when cmd[1] == "exec":
                var request = SandboxToolsFixtures.Request(call.Input ?? throw new InvalidOperationException("exec without a request on stdin"));
                Requests.Add(request);
                RpcCalls.Add(call);
                var method = request["method"]!.GetValue<string>();
                return Handlers.TryGetValue(method, out var handler)
                    ? handler(request)
                    : FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Error(-32601, "Method not found"));
            default:
                return FakeSandboxEnvironment.Ok();
        }
    }
}

/// <summary>Port-level behaviour of <c>_util/_json_rpc.py</c>: request wire format, response parsing, error-code mapping and the typed helpers.</summary>
public class JsonRpcTests
{
    private sealed class RecordingTransport(string response) : IJsonRpcTransport
    {
        public List<(string Method, JsonNode? Params, bool Notification, JsonRpcCallOptions Options)> Calls { get; } = [];

        public Task<string> CallAsync(string method, JsonNode? parameters, bool isNotification, JsonRpcCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, parameters, isNotification, options));
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void create_request_matches_pythons_json_dumps_layout_and_strips_nulls()
    {
        var parameters = new JsonObject { ["command"] = "view", ["path"] = "/tmp/x", ["view_range"] = null, ["file_text"] = null };

        var first = JsonRpc.CreateRequest("text_editor", parameters, isNotification: false);
        var second = JsonRpc.CreateRequest("text_editor", parameters, isNotification: false);

        Assert.Matches("""^\{"jsonrpc": "2\.0", "method": "text_editor", "params": \{"command": "view", "path": "/tmp/x"\}, "id": \d+\}$""", first);
        var firstId = JsonNode.Parse(first)!["id"]!.GetValue<long>();
        var secondId = JsonNode.Parse(second)!["id"]!.GetValue<long>();
        Assert.True(firstId >= 666);
        Assert.Equal(firstId + 1, secondId);
        // the caller's parameters are left untouched
        Assert.Equal(4, parameters.Count);
    }

    [Fact]
    public void empty_params_are_omitted_and_a_notification_has_no_id()
    {
        Assert.Matches("""^\{"jsonrpc": "2\.0", "method": "version", "id": \d+\}$""", JsonRpc.CreateRequest("version", new JsonObject(), isNotification: false));
        Assert.Matches("""^\{"jsonrpc": "2\.0", "method": "version", "id": \d+\}$""", JsonRpc.CreateRequest("version", null, isNotification: false));
        Assert.Equal("""{"jsonrpc": "2.0", "method": "ping", "params": {"a": 1}}""", JsonRpc.CreateRequest("ping", new JsonObject { ["a"] = 1 }, isNotification: true));
    }

    [Fact]
    public void remove_none_values_is_recursive_over_objects_and_lists()
    {
        var node = JsonNode.Parse("""{"a": null, "b": {"c": null, "d": [1, null, {"e": null, "f": 2}]}}""");

        var cleaned = JsonRpc.RemoveNoneValues(node);

        Assert.Equal("""{"b":{"d":[1,{"f":2}]}}""", cleaned!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"minuend": 42, "subtrahend": 23}""", "subtract(minuend: 42, subtrahend: 23)")]
    [InlineData("[42, 23]", "subtract(42, 23)")]
    [InlineData("{}", "subtract()")]
    [InlineData("null", "subtract()")]
    [InlineData("""{"path": "/tmp/x", "flag": true, "n": null}""", "subtract(path: /tmp/x, flag: True, n: None)")]
    public void rpc_call_description_matches_the_python_docstring_examples(string parameters, string expected)
    {
        Assert.Equal(expected, JsonRpc.RpcCallDescription("subtract", JsonNode.Parse(parameters)));
    }

    [Fact]
    public void parse_response_returns_the_result_of_a_success_response()
    {
        Assert.Equal("ok", JsonRpc.ParseResponse(SandboxToolsFixtures.Success("ok"), "m", null, SandboxToolsErrorMapper.Instance)!.GetValue<string>());
        Assert.Null(JsonRpc.ParseResponse(SandboxToolsFixtures.Success(null), "m", null, SandboxToolsErrorMapper.Instance));
        Assert.Equal(7, JsonRpc.ParseResponse("""{"jsonrpc": "2.0", "id": "abc", "result": 7}""", "m", null, SandboxToolsErrorMapper.Instance)!.GetValue<int>());
    }

    public static TheoryData<int, Type> MappedCodes => new()
    {
        { -32099, typeof(ToolError) },
        { -32098, typeof(InvalidOperationException) },
        { -32000, typeof(InvalidOperationException) },
        { -32602, typeof(ToolParsingError) },
        { -32603, typeof(ToolError) },
    };

    [Theory]
    [MemberData(nameof(MappedCodes))]
    public void sandbox_tools_error_codes_map_to_the_tool_layer_exceptions(int code, Type expected)
    {
        var ex = Assert.Throws(expected, () => JsonRpc.ParseResponse(SandboxToolsFixtures.Error(code, "what happened"), "m", new JsonObject { ["a"] = 1 }, SandboxToolsErrorMapper.Instance));

        Assert.Equal("what happened", ex.Message);
    }

    [Fact]
    public void request_oriented_codes_are_a_coding_error_with_pythons_message()
    {
        var withParams = Assert.Throws<InvalidOperationException>(() => JsonRpc.ParseResponse(SandboxToolsFixtures.Error(-32601, "Method not found"), "nope", new JsonObject { ["a"] = 1 }, SandboxToolsErrorMapper.Instance));
        var withoutParams = Assert.Throws<InvalidOperationException>(() => JsonRpc.ParseResponse(SandboxToolsFixtures.Error(-32700, "Parse error"), "nope", null, SandboxToolsErrorMapper.Instance));

        Assert.Equal("Error executing tool command  nope(a: 1): code=-32601 Method not found", withParams.Message);
        Assert.Equal("Error executing tool command: code=-32700 Parse error", withoutParams.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"jsonrpc": "1.0", "id": 1, "result": "x"}""")]
    [InlineData("""{"jsonrpc": "2.0", "result": "x"}""")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1}""")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "error": {"code": "x", "message": "m"}}""")]
    public void anything_that_is_not_a_json_rpc_response_is_unexpected(string response)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonRpc.ParseResponse(response, "m", new JsonObject { ["p"] = "q" }, SandboxToolsErrorMapper.Instance));

        Assert.Equal($"Unexpected JSON RPC response to request m(p: q): {response}", ex.Message);
    }

    [Fact]
    public async Task scalar_requests_check_the_result_type()
    {
        var text = await JsonRpc.ExecScalarRequestAsync<string>("m", null, new RecordingTransport(SandboxToolsFixtures.Success("hi")), SandboxToolsErrorMapper.Instance);
        var number = await JsonRpc.ExecScalarRequestAsync<int>("m", null, new RecordingTransport(SandboxToolsFixtures.Success(5)), SandboxToolsErrorMapper.Instance);
        var flag = await JsonRpc.ExecScalarRequestAsync<bool>("m", null, new RecordingTransport(SandboxToolsFixtures.Success(true)), SandboxToolsErrorMapper.Instance);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonRpc.ExecScalarRequestAsync<string>("m", null, new RecordingTransport(SandboxToolsFixtures.Success(5)), SandboxToolsErrorMapper.Instance));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonRpc.ExecScalarRequestAsync<string>("m", null, new RecordingTransport(SandboxToolsFixtures.Success(null)), SandboxToolsErrorMapper.Instance));

        Assert.Equal("hi", text);
        Assert.Equal(5, number);
        Assert.True(flag);
        Assert.Equal("Expected <class 'str'> result, got <class 'int'>", mismatch.Message);
        Assert.Equal("Expected <class 'str'> result, got <class 'NoneType'>", none.Message);
    }

    [Fact]
    public async Task model_requests_deserialize_snake_case_members_and_reject_missing_ones()
    {
        var transport = new RecordingTransport(SandboxToolsFixtures.Success(new { session_name = "BashSession-1", extra = 1 }));
        var options = new JsonRpcCallOptions(TimeSpan.FromSeconds(3), "root");

        var session = await JsonRpc.ExecModelRequestAsync<BashSession.NewSessionResult>("bash_session_new_session", null, transport, SandboxToolsErrorMapper.Instance, options);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonRpc.ExecModelRequestAsync<BashSession.NewSessionResult>("m", null, new RecordingTransport(SandboxToolsFixtures.Success(new { other = 1 })), SandboxToolsErrorMapper.Instance));
        var scalar = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonRpc.ExecModelRequestAsync<BashSession.NewSessionResult>("m", null, new RecordingTransport(SandboxToolsFixtures.Success("x")), SandboxToolsErrorMapper.Instance));

        Assert.Equal("BashSession-1", session.SessionName);
        Assert.Equal(("bash_session_new_session", false, options), (transport.Calls[0].Method, transport.Calls[0].Notification, transport.Calls[0].Options));
        Assert.Contains("session_name", missing.Message);
        Assert.Equal("Expected NewSessionResult result for m(), got <class 'str'>", scalar.Message);
    }

    [Fact]
    public async Task a_notification_with_output_is_an_error()
    {
        await JsonRpc.ExecNotificationAsync("m", null, new RecordingTransport("  \n"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonRpc.ExecNotificationAsync("m", new JsonObject { ["a"] = 1 }, new RecordingTransport("late")));

        Assert.Equal("Unexpected response to a Notification: m(a: 1): late", ex.Message);
    }
}

/// <summary>Port of <c>tests/util/sandbox/test_json_rpc_transport.py</c> over <see cref="FakeSandboxEnvironment"/>.</summary>
public class SandboxJsonRpcTransportTests
{
    private const string ChunkMethod = SandboxJsonRpcTransport.ResponseChunkMethod;

    private static string Method(FakeExecCall call) => SandboxToolsFixtures.Request(call.Input!)["method"]!.GetValue<string>();

    private static JsonObject Params(FakeExecCall call) => SandboxToolsFixtures.Request(call.Input!)["params"]!.AsObject();

    [Fact]
    public async Task a_plain_response_passes_through_with_the_cli_env_timeout_and_user()
    {
        var response = SandboxToolsFixtures.Success("ok");
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => FakeSandboxEnvironment.Ok(response) };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var result = await transport.CallAsync("plain", new JsonObject { ["x"] = 1 }, isNotification: false, new JsonRpcCallOptions(TimeSpan.FromSeconds(7), "root"));

        Assert.Equal(response, result);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal([SandboxToolSupport.SandboxCli, "exec"], call.Cmd);
        Assert.Equal("plain", Method(call));
        Assert.Equal(TimeSpan.FromSeconds(7), call.Timeout);
        Assert.Equal("root", call.User);
        Assert.Equal(SandboxLimits.MaxExecOutputSize.ToString(), call.Env![SandboxJsonRpcTransport.ResponseMaxBytesEnv]);
    }

    [Fact]
    public async Task an_exec_failure_reports_stderr_then_stdout_then_a_placeholder()
    {
        var results = new Queue<ExecResult>([FakeSandboxEnvironment.Fail(1, stderr: "err text", stdout: "out text"), FakeSandboxEnvironment.Fail(1, stdout: "stdout diagnostic"), FakeSandboxEnvironment.Fail(2)]);
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => results.Dequeue() };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var stderr = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("failing", new JsonObject { ["a"] = 1 }, false, JsonRpcCallOptions.None));
        var stdout = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("failing", null, false, JsonRpcCallOptions.None));
        var silent = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("failing", null, false, JsonRpcCallOptions.None));

        Assert.Equal("Sandbox.exec failure executing failing(a: 1): err text", stderr.Message);
        Assert.Equal("Sandbox.exec failure executing failing(): stdout diagnostic", stdout.Message);
        Assert.Equal("Sandbox.exec failure executing failing(): (no output captured)", silent.Message);
    }

    [Fact]
    public async Task a_sandbox_timeout_propagates_as_is()
    {
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => throw new SandboxTimeoutException("Command timed out after 5 seconds", "partial") };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var ex = await Assert.ThrowsAsync<SandboxTimeoutException>(() => transport.CallAsync("slow", null, false, JsonRpcCallOptions.None));

        Assert.Equal("partial", ex.TruncatedOutput);
    }

    [Fact]
    public async Task a_large_unicode_response_is_reassembled_in_order_and_released()
    {
        var original = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = new JsonObject { ["stdout"] = "stdout-" + string.Concat(Enumerable.Repeat("🙂", 1000)), ["stderr"] = "stderr-" + string.Concat(Enumerable.Repeat("界", 1000)) },
        }.ToJsonString();
        var originalBytes = Encoding.UTF8.GetBytes(original);
        var handle = new string('a', 32);
        var sandbox = new FakeSandboxEnvironment
        {
            OnExecCall = call =>
            {
                var request = SandboxToolsFixtures.Request(call.Input!);
                if (request["method"]!.GetValue<string>() == ChunkMethod)
                {
                    var parameters = request["params"]!.AsObject();
                    if (parameters["release"] is { } release && release.GetValue<bool>())
                    {
                        return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(null));
                    }

                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(originalBytes, handle, parameters["offset"]!.GetValue<long>(), 127));
                }

                return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(originalBytes, handle, 0, 127));
            },
        };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        using var env = new EnvVarScope().Set(SandboxLimits.MaxExecOutputSizeVar, "1024");
        var result = await transport.CallAsync("large", new JsonObject(), false, JsonRpcCallOptions.None);

        Assert.Equal(original, result);
        var continuations = sandbox.Calls.Skip(1).SkipLast(1).Select(call => Params(call)["offset"]!.GetValue<long>()).ToArray();
        Assert.True(continuations.Length > 2);
        Assert.Equal(continuations.OrderBy(o => o), continuations);
        Assert.All(sandbox.Calls, call => Assert.Equal("1024", call.Env![SandboxJsonRpcTransport.ResponseMaxBytesEnv]));
        var release = sandbox.Calls[^1];
        Assert.True(Params(release)["release"]!.GetValue<bool>());
        Assert.Equal(SandboxJsonRpcTransport.ReleaseTimeout, release.Timeout);
    }

    [Fact]
    public async Task a_json_rpc_error_response_is_left_for_the_parser()
    {
        var response = SandboxToolsFixtures.Error(-32099, "tool failed");
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => FakeSandboxEnvironment.Ok(response) };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        Assert.Equal(response, await transport.CallAsync("plain", new JsonObject(), false, JsonRpcCallOptions.None));
    }

    [Fact]
    public async Task an_out_of_order_chunk_is_rejected_after_one_continuation_and_the_handle_is_still_released()
    {
        var originalBytes = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":"abcdefghijklmno"}""");
        var handle = new string('b', 32);
        var continuations = 0;
        var sandbox = new FakeSandboxEnvironment
        {
            OnExecCall = call =>
            {
                var request = SandboxToolsFixtures.Request(call.Input!);
                if (request["method"]!.GetValue<string>() != ChunkMethod)
                {
                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(originalBytes, handle, 0, 10));
                }

                var parameters = request["params"]!.AsObject();
                if (parameters["release"] is { } release && release.GetValue<bool>())
                {
                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(null));
                }

                continuations++;
                return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(originalBytes, handle, parameters["offset"]!.GetValue<long>() + 1, 10));
            },
        };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("large", new JsonObject(), false, JsonRpcCallOptions.None));

        Assert.Equal("Chunked JSON-RPC response chunks arrived out of order", ex.Message);
        Assert.Equal(1, continuations);
        Assert.True(Params(sandbox.Calls[^1])["release"]!.GetValue<bool>());
    }

    [Fact]
    public async Task a_continuation_error_is_surfaced()
    {
        var originalBytes = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":"abcdefghijklmno"}""");
        var handle = new string('c', 32);
        var sandbox = new FakeSandboxEnvironment
        {
            OnExecCall = call =>
            {
                var request = SandboxToolsFixtures.Request(call.Input!);
                if (request["method"]!.GetValue<string>() != ChunkMethod)
                {
                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(originalBytes, handle, 0, 10));
                }

                if (request["params"]!["release"] is { } release && release.GetValue<bool>())
                {
                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(null));
                }

                return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Error(-32000, "chunk missing", id: 2));
            },
        };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("large", new JsonObject(), false, JsonRpcCallOptions.None));

        Assert.Equal("Chunked JSON-RPC response fetch failed: {'code': -32000, 'message': 'chunk missing'}", ex.Message);
    }

    [Fact]
    public async Task concurrent_chunked_responses_keep_their_handles_separate()
    {
        var responses = new Dictionary<string, string>
        {
            ["one"] = SandboxToolsFixtures.Success(string.Concat(Enumerable.Repeat("one", 200))),
            ["two"] = SandboxToolsFixtures.Success(string.Concat(Enumerable.Repeat("two", 200)), id: 2),
        };
        var handles = new Dictionary<string, string> { ["one"] = new string('1', 32), ["two"] = new string('2', 32) };
        var bytesForHandle = responses.ToDictionary(pair => handles[pair.Key], pair => Encoding.UTF8.GetBytes(pair.Value));
        var sandbox = new FakeSandboxEnvironment
        {
            OnExecCall = call =>
            {
                var request = SandboxToolsFixtures.Request(call.Input!);
                if (request["method"]!.GetValue<string>() == ChunkMethod)
                {
                    var parameters = request["params"]!.AsObject();
                    if (parameters["release"] is { } release && release.GetValue<bool>())
                    {
                        return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(null));
                    }

                    var handle = parameters["handle"]!.GetValue<string>();
                    return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(bytesForHandle[handle], handle, parameters["offset"]!.GetValue<long>(), 31));
                }

                var first = handles[request["method"]!.GetValue<string>()];
                return FakeSandboxEnvironment.Ok(SandboxToolsFixtures.ChunkResponse(bytesForHandle[first], first, 0, 31));
            },
        };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var results = await Task.WhenAll(
            transport.CallAsync("one", new JsonObject(), false, JsonRpcCallOptions.None),
            transport.CallAsync("two", new JsonObject(), false, JsonRpcCallOptions.None));

        Assert.Equal([responses["one"], responses["two"]], results);
    }

    [Fact]
    public async Task a_legacy_cli_does_not_enable_the_chunk_protocol()
    {
        var response = SandboxToolsFixtures.Success("legacy");
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => FakeSandboxEnvironment.Ok(response) };
        var transport = new SandboxJsonRpcTransport(sandbox, "inspect-tool-support");

        Assert.Equal(response, await transport.CallAsync("legacy", new JsonObject(), false, JsonRpcCallOptions.None));
        Assert.Null(Assert.Single(sandbox.Calls).Env);
    }

    [Theory]
    [InlineData("""{"jsonrpc": "1.0", "id": 1, "__inspect_json_rpc_response_chunk__": {}}""", "Chunked JSON-RPC response had an invalid protocol version")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "__inspect_json_rpc_response_chunk__": 3}""", "Chunked JSON-RPC response metadata was not an object")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "__inspect_json_rpc_response_chunk__": {"version": 2}}""", "Unsupported chunked JSON-RPC response version")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "__inspect_json_rpc_response_chunk__": {"version": 1, "offset": "0"}}""", "Invalid chunked JSON-RPC response offset")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "__inspect_json_rpc_response_chunk__": {"version": 1, "offset": 0, "next_offset": 1, "total_size": 1, "handle": "nope", "done": true, "chunk": "YQ=="}}""", "Invalid chunked JSON-RPC response handle")]
    [InlineData("""{"jsonrpc": "2.0", "id": 1, "__inspect_json_rpc_response_chunk__": {"version": 1, "offset": 0, "next_offset": 1, "total_size": 1, "handle": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "done": true, "chunk": "@@"}}""", "Invalid base64 in chunked JSON-RPC response")]
    public async Task malformed_chunk_envelopes_are_rejected(string response, string expected)
    {
        var sandbox = new FakeSandboxEnvironment { OnExecCall = _ => FakeSandboxEnvironment.Ok(response) };
        var transport = new SandboxJsonRpcTransport(sandbox, SandboxToolSupport.SandboxCli);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.CallAsync("large", new JsonObject(), false, JsonRpcCallOptions.None));

        Assert.Equal(expected, ex.Message);
    }
}

/// <summary>The injection protocol of <c>sandbox_with_injected_tools</c> against a scripted sandbox.</summary>
public class SandboxToolSupportInjectionTests
{
    private static (string Command, string? User)[] Sequence(ScriptedToolsSandbox scripted) =>
        scripted.Sandbox.Calls.Select(call => (string.Join(" ", call.Cmd.Take(2)), call.User)).ToArray();

    [Fact]
    public async Task tools_already_present_means_no_injection_and_no_artifact()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: true);
        var binaries = new FakeBinaries();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var support = new SandboxToolSupport(binaries);

        var injected = await support.SandboxWithInjectedToolsAsync();

        Assert.Same(scripted.Sandbox, injected.Sandbox);
        Assert.Null(injected.ToolsUser);
        Assert.Empty(binaries.Requests);
        // Python probes once while choosing the target sandbox and again under the inject lock ("refresh the needed injections").
        Assert.Equal([("test -r", null), ("test -r", null)], Sequence(scripted));
        Assert.Null(support.InjectedVersion(scripted.Sandbox));
    }

    [Fact]
    public async Task a_fresh_sandbox_is_injected_as_root_in_pythons_order_and_the_version_is_verified()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false);
        var binaries = new FakeBinaries();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var support = new SandboxToolSupport(binaries);

        var injected = await support.SandboxWithInjectedToolsAsync();

        Assert.Equal("root", injected.ToolsUser);
        Assert.Equal("root", support.ToolsUser(scripted.Sandbox));
        Assert.Equal("1.2.1", support.InjectedVersion(scripted.Sandbox));
        Assert.Equal([("arm64", false)], binaries.Requests);
        var dir = SandboxToolSupport.SandboxToolsDir;
        Assert.Equal(
            [
                ("test -r", null), ("test -r", null),
                ("sh -c", null), ("sh -c", null), ("sh -c", null), ("sh -c", null),
                ($"mkdir -p", "root"),
                ("tar xzf", "root"), ("rm -f", "root"),
                ("chmod 700", "root"),
                ($"{SandboxToolSupport.SandboxCli} start-server", "root"),
                ("test -r", null),
                ($"{SandboxToolSupport.SandboxCli} exec", "root"),
            ],
            Sequence(scripted));
        Assert.Equal(binaries.GzipBytes, scripted.Sandbox.Files[$"{dir}.pkg.tgz"]);
        var tar = scripted.Sandbox.Calls.Single(call => call.Cmd[0] == "tar");
        Assert.Equal(["tar", "xzf", $"{dir}.pkg.tgz", "-C", dir], tar.Cmd);
        Assert.Equal("version", scripted.Requests.Single()["method"]!.GetValue<string>());
        Assert.Equal(SandboxToolSupport.VersionTimeout, scripted.RpcCalls.Single().Timeout);

        // a second call finds the tools and skips everything
        scripted.Sandbox.Calls.Clear();
        await support.SandboxWithInjectedToolsAsync();
        Assert.Equal([("test -r", null), ("test -r", null)], Sequence(scripted));
    }

    [Fact]
    public async Task a_root_probe_that_throws_falls_back_to_the_default_user()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false) { RootProbeThrows = true };
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var support = new SandboxToolSupport(new FakeBinaries());

        var injected = await support.SandboxWithInjectedToolsAsync();

        Assert.Null(injected.ToolsUser);
        var sequence = Sequence(scripted);
        Assert.Contains(("mkdir -p", "root"), sequence);
        Assert.Contains(("mkdir -p", null), sequence);
        Assert.DoesNotContain(sequence, step => step.Command == "chmod 700");
        Assert.Equal(("tar xzf", null), sequence.Single(step => step.Command == "tar xzf"));
        Assert.Equal(($"{SandboxToolSupport.SandboxCli} start-server", null), sequence.Single(step => step.Command.EndsWith("start-server", StringComparison.Ordinal)));
        Assert.Null(scripted.RpcCalls.Single().User);
    }

    [Fact]
    public async Task a_failed_root_mkdir_also_installs_rootless()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false) { RootAllowed = false };
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        var injected = await new SandboxToolSupport(new FakeBinaries()).SandboxWithInjectedToolsAsync();

        Assert.Null(injected.ToolsUser);
    }

    [Fact]
    public async Task a_tar_without_gzip_support_gets_the_uncompressed_tar()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false) { TarGzFails = true };
        var binaries = new FakeBinaries();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        await new SandboxToolSupport(binaries).SandboxWithInjectedToolsAsync();

        var dir = SandboxToolSupport.SandboxToolsDir;
        var tars = scripted.Sandbox.Calls.Where(call => call.Cmd[0] == "tar").Select(call => call.Cmd).ToArray();
        Assert.Equal([["tar", "xzf", $"{dir}.pkg.tgz", "-C", dir], ["tar", "xf", $"{dir}.pkg.tar", "-C", dir]], tars);
        Assert.Equal(await SandboxToolsBinary.GunzipAsync(binaries.GzipBytes), scripted.Sandbox.Files[$"{dir}.pkg.tar"]);
        Assert.Equal(2, scripted.Sandbox.Calls.Count(call => call.Cmd[0] == "rm"));
    }

    [Fact]
    public async Task a_musl_sandbox_gets_the_musl_artifact()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false) { Machine = "x86_64", Libc = "musl" };
        var binaries = new FakeBinaries();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);

        await new SandboxToolSupport(binaries).SandboxWithInjectedToolsAsync();

        Assert.Equal([("amd64", true)], binaries.Requests);
    }

    [Fact]
    public async Task recon_reports_the_distribution_and_rejects_other_systems()
    {
        var debian = new ScriptedToolsSandbox();
        var darwin = new ScriptedToolsSandbox { Uname = "Darwin" };
        var odd = new ScriptedToolsSandbox { Machine = "riscv64" };

        var info = await SandboxRecon.DetectSandboxOsAsync(debian.Sandbox);
        var unsupported = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => SandboxRecon.DetectSandboxOsAsync(darwin.Sandbox));
        var arch = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => SandboxRecon.DetectSandboxOsAsync(odd.Sandbox));

        Assert.Equal(new SandboxOsInfo("Linux", "Debian", "12 (bookworm)", "arm64", "glibc"), info);
        Assert.Equal("Tool support injection is not implemented for OS: Darwin. Only Linux containers are currently supported.", unsupported.Message);
        Assert.Equal("Architecture riscv64 is not supported.", arch.Message);
        Assert.All(debian.Sandbox.Calls, call => Assert.Equal(SandboxRecon.ProbeTimeout, call.Timeout));
    }

    [Fact]
    public async Task injection_failures_are_wrapped_so_they_never_look_like_tool_errors()
    {
        var darwin = new ScriptedToolsSandbox(hasTools: false) { Uname = "Darwin" };
        var noServer = new ScriptedToolsSandbox(hasTools: false) { StartServerFails = true };
        var nothingExtracted = new ScriptedToolsSandbox(hasTools: false) { ExtractLeavesNothing = true };
        var support = new SandboxToolSupport(new FakeBinaries());

        var unsupported = await Assert.ThrowsAsync<SandboxInjectionException>(() => support.SandboxWithInjectedToolsAsync(sandbox: darwin.Sandbox));
        var server = await Assert.ThrowsAsync<SandboxInjectionException>(() => support.SandboxWithInjectedToolsAsync(sandbox: noServer.Sandbox));
        var detector = await Assert.ThrowsAsync<InvalidOperationException>(() => support.SandboxWithInjectedToolsAsync(sandbox: nothingExtracted.Sandbox));

        Assert.IsType<PlatformNotSupportedException>(unsupported.InnerException);
        Assert.StartsWith("Failed to inject sandbox tools into sandbox: Tool support injection is not implemented for OS: Darwin.", unsupported.Message);
        Assert.Equal("Failed to inject sandbox tools into sandbox: Failed to start sandbox tools server: server did not start", server.Message);
        Assert.Equal("Injection failed - detector still returns False after injection", detector.Message);
    }

    [Fact]
    public async Task a_launcher_that_does_not_answer_the_version_request_fails_injection()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false);
        scripted.Handlers["version"] = _ => FakeSandboxEnvironment.Fail(127, "exec format error");
        var support = new SandboxToolSupport(new FakeBinaries());

        var ex = await Assert.ThrowsAsync<SandboxInjectionException>(() => support.SandboxWithInjectedToolsAsync(sandbox: scripted.Sandbox));

        Assert.StartsWith("Injected sandbox tools did not answer the version request: Sandbox.exec failure executing version(): exec format error", ex.Message);
    }

    [Fact]
    public async Task cancellation_is_not_wrapped()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SandboxToolSupport(new FakeBinaries()).SandboxWithInjectedToolsAsync(sandbox: scripted.Sandbox, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task the_detector_falls_back_to_reading_the_launcher_when_test_cannot_run()
    {
        var broken = new FakeSandboxEnvironment { OnExecCall = _ => throw new SandboxUnavailableException("no exec") };
        var present = new FakeSandboxEnvironment { OnExecCall = _ => throw new SandboxUnavailableException("no exec") };
        present.Files[SandboxToolSupport.SandboxCli] = [1, 2, 3];

        Assert.False(await SandboxToolSupport.HasToolsAsync(broken));
        Assert.True(await SandboxToolSupport.HasToolsAsync(present));
    }

    [Fact]
    public async Task the_sample_sandbox_that_already_has_the_tools_is_preferred_and_a_name_is_honoured()
    {
        var first = new ScriptedToolsSandbox(hasTools: false);
        var second = new ScriptedToolsSandbox(hasTools: true);
        var environments = SandboxEnvironments.Create([new("first", first.Sandbox), new("second", second.Sandbox)]);
        var context = new Context.SampleContext { ActiveModel = new Model.Model(new ScriptedModelApi()), Sandboxes = environments };
        using var scope = Context.SampleContext.Begin(context);
        var support = new SandboxToolSupport(new FakeBinaries());

        var preferred = await support.SandboxWithInjectedToolsAsync();
        var named = await support.SandboxWithInjectedToolsAsync(sandboxName: "first");

        Assert.Same(second.Sandbox, preferred.Sandbox);
        Assert.Same(first.Sandbox, named.Sandbox);
        Assert.Equal("root", named.ToolsUser);
    }

    [Fact]
    public async Task no_sandbox_context_is_pythons_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SandboxToolSupport(new FakeBinaries()).SandboxWithInjectedToolsAsync());

        Assert.StartsWith("No sandbox environment has been provided for the current sample or task.", ex.Message);
    }
}

/// <summary>Artifact naming, the digest pin and the download/cache path of <see cref="SandboxToolsBinary"/>.</summary>
public class SandboxToolsBinaryTests : IDisposable
{
    private readonly string _dir = SandboxToolsFixtures.TempDir();

    private sealed class FakeBucket : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var name = request.RequestUri!.Segments[^1];
            return Task.FromResult(Objects.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void executable_names_round_trip_and_every_variant_has_a_pinned_digest()
    {
        Assert.Equal("inspect-sandbox-tools-arm64-v29", SandboxToolsBinary.ExecutableName("arm64", musl: false));
        Assert.Equal("inspect-sandbox-tools-amd64-musl-v29", SandboxToolsBinary.ExecutableName("amd64", musl: true));
        Assert.Equal("inspect-sandbox-tools-amd64-v3-dev", SandboxToolsBinary.ExecutableName("amd64", musl: false, version: 3, suffix: "dev"));
        Assert.Equal(new SandboxToolsBuildConfig("arm64", 29, null, true), SandboxToolsBinary.ParseExecutableName("inspect-sandbox-tools-arm64-musl-v29"));
        Assert.Equal(new SandboxToolsBuildConfig("amd64", 3, "dev", false), SandboxToolsBinary.ParseExecutableName("inspect-sandbox-tools-amd64-v3-dev"));
        Assert.Throws<ArgumentException>(() => SandboxToolsBinary.ParseExecutableName("inspect-sandbox-tools-amd64"));
        foreach (var arch in new[] { "amd64", "arm64" })
        {
            foreach (var musl in new[] { false, true })
            {
                Assert.Matches("^[0-9a-f]{64}$", new SandboxToolsBinary(_dir).LookupDigest(SandboxToolsBinary.ExecutableName(arch, musl)));
            }
        }

        Assert.Equal(4, SandboxToolsBinary.Sha256Sums.Count);
        Assert.StartsWith("No SHA256 entry for inspect-sandbox-tools-amd64-v1", Assert.Throws<InvalidOperationException>(() => new SandboxToolsBinary(_dir).LookupDigest("inspect-sandbox-tools-amd64-v1")).Message);
    }

    [Fact]
    public async Task a_download_is_verified_cached_and_served_from_the_cache_afterwards()
    {
        var bytes = SandboxToolsFixtures.Gzip("bundle");
        var name = SandboxToolsBinary.ExecutableName("arm64", musl: false);
        var bucket = new FakeBucket { Objects = { [name] = bytes } };
        var binary = new SandboxToolsBinary(_dir, bucket, "https://bucket.test/", new Dictionary<string, string> { [name] = SandboxToolsBinary.Sha256Hex(bytes) });

        var first = await binary.OpenAsync("arm64", musl: false);
        var second = await binary.OpenAsync("arm64", musl: false);

        Assert.Equal(name, first.Name);
        Assert.Equal(bytes, first.GzipBytes);
        Assert.Equal(bytes, second.GzipBytes);
        Assert.Equal([new Uri($"https://bucket.test/{name}")], bucket.Requests);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_dir, name)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task a_digest_mismatch_is_fatal_and_leaves_nothing_cached()
    {
        var name = SandboxToolsBinary.ExecutableName("amd64", musl: false);
        var bucket = new FakeBucket { Objects = { [name] = SandboxToolsFixtures.Gzip("tampered") } };
        var binary = new SandboxToolsBinary(_dir, bucket, "https://bucket.test", new Dictionary<string, string> { [name] = new string('0', 64) });

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => binary.OpenAsync("amd64", musl: false));

        Assert.StartsWith($"Digest verification failed for {name} downloaded from S3", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, name)));
    }

    [Fact]
    public async Task a_corrupt_cached_artifact_is_replaced()
    {
        var bytes = SandboxToolsFixtures.Gzip("bundle");
        var name = SandboxToolsBinary.ExecutableName("arm64", musl: true);
        var bucket = new FakeBucket { Objects = { [name] = bytes } };
        var binary = new SandboxToolsBinary(_dir, bucket, "https://bucket.test", new Dictionary<string, string> { [name] = SandboxToolsBinary.Sha256Hex(bytes) });
        await File.WriteAllBytesAsync(Path.Combine(_dir, name), [9, 9, 9]);
        ProviderLogger.Reset();

        var artifact = await binary.OpenAsync("arm64", musl: true);

        Assert.Equal(bytes, artifact.GzipBytes);
        Assert.Single(bucket.Requests);
        Assert.Contains(ProviderLogger.Warnings, warning => warning.Contains("does not match the digest pinned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_missing_object_and_a_missing_digest_are_prerequisite_errors()
    {
        var bucket = new FakeBucket();
        var name = SandboxToolsBinary.ExecutableName("amd64", musl: true);
        var binary = new SandboxToolsBinary(_dir, bucket, "https://bucket.test", new Dictionary<string, string> { [name] = new string('a', 64) });
        var noDigest = new SandboxToolsBinary(_dir, bucket, "https://bucket.test", new Dictionary<string, string>());

        var missing = await Assert.ThrowsAsync<PrerequisiteError>(() => binary.OpenAsync("amd64", musl: true));
        var unpinned = await Assert.ThrowsAsync<InvalidOperationException>(() => noDigest.OpenAsync("amd64", musl: true));

        Assert.StartsWith($"Executable '{name}' not found on S3 (https://bucket.test/{name}).", missing.Message);
        Assert.Contains("--arch amd64 --musl", missing.Message);
        Assert.StartsWith($"No SHA256 entry for {name}", unpinned.Message);
        Assert.DoesNotContain(bucket.Requests, uri => uri.Segments[^1] != name);
    }

    [Fact]
    public async Task the_uncompressed_tar_is_decompressed_once_and_cached_beside_the_artifact()
    {
        var binary = new SandboxToolsBinary(_dir, new FakeBucket());
        var artifact = new SandboxToolsArtifact("inspect-sandbox-tools-arm64-v29", SandboxToolsFixtures.Gzip("tar contents"));

        var first = await binary.UncompressedTarAsync(artifact);
        await File.WriteAllTextAsync(Path.Combine(_dir, artifact.Name + ".tar"), "cached");
        var second = await binary.UncompressedTarAsync(artifact);

        Assert.Equal("tar contents", Encoding.UTF8.GetString(first));
        Assert.Equal("cached", Encoding.UTF8.GetString(second));
    }

    [Fact]
    public void the_binaries_dir_comes_from_the_argument_then_the_environment_then_the_default()
    {
        using var env = new EnvVarScope().Set(SandboxToolsBinary.BinariesDirVar, _dir);

        Assert.Equal("/explicit", new SandboxToolsBinary("/explicit").BinariesDir);
        Assert.Equal(_dir, new SandboxToolsBinary().BinariesDir);
        env.Set(SandboxToolsBinary.BinariesDirVar, null);
        Assert.Equal(SandboxToolsBinary.DefaultBinariesDir, new SandboxToolsBinary().BinariesDir);
    }

    [NetworkFact]
    public async Task the_published_artifact_downloads_from_s3_and_matches_its_pinned_digest()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        var binary = new SandboxToolsBinary(_dir);

        var artifact = await binary.OpenAsync(arch, musl: false);
        var tar = await binary.UncompressedTarAsync(artifact);

        Assert.Equal(SandboxToolsBinary.ExecutableName(arch, musl: false), artifact.Name);
        Assert.True(artifact.GzipBytes.Length > 1_000_000);
        Assert.True(SandboxToolsBinary.VerifyDigest(artifact.GzipBytes, binary.LookupDigest(artifact.Name)));
        Assert.True(tar.Length > artifact.GzipBytes.Length);
        Assert.True(File.Exists(Path.Combine(_dir, artifact.Name)));
    }
}

/// <summary>The <c>text_editor</c> tool over scripted JSON-RPC exchanges.</summary>
public class TextEditorToolTests
{
    private static async Task<ChatMessageTool> Execute(ToolDef tool, JsonObject arguments)
    {
        var call = new ToolCall("c1", tool.Name, arguments);
        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("hi"), new ChatMessageAssistant("", toolCalls: [call])], [tool]);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    [Fact]
    public async Task the_schema_and_description_are_pythons()
    {
        var expected = await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("text_editor_params.json"));
        var description = await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("text_editor_description.txt"));

        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        Assert.Equal("text_editor", tool.Name);
        Assert.Equal(description, tool.Description);
        Assert.Equal(expected, PythonJson.Dumps(tool.ToInfo().Parameters.ToJson()));
    }

    [Fact]
    public async Task a_view_sends_only_the_given_parameters_with_the_default_timeout()
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("Here's the result of running `cat -n` on /etc/passwd:\n     1\troot:x:0:0:root:/root:/bin/bash\n"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var result = await tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/etc/passwd" }), CancellationToken.None);

        Assert.Contains("root:x:0:0:root", result.AsText());
        Assert.Equal("text_editor", scripted.Requests.Single()["method"]!.GetValue<string>());
        Assert.Equal("""{"command":"view","path":"/etc/passwd"}""", scripted.Params(0)!.ToJsonString());
        var call = scripted.RpcCalls.Single();
        Assert.Equal(TextEditor.DefaultTimeout, call.Timeout);
        Assert.Null(call.User);
    }

    [Fact]
    public async Task all_parameters_travel_in_pythons_order_with_the_user_as_run_as_user()
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("ok"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(timeout: TimeSpan.FromSeconds(42), user: "nobody", support: new SandboxToolSupport(new FakeBinaries()));

        await tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f", view_range = new[] { 1, -1 } }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { command = "str_replace", path = "/f", old_str = "a", new_str = "b" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { command = "create", path = "/f", file_text = "text" }), CancellationToken.None);

        Assert.Equal("""{"command":"view","path":"/f","view_range":[1,-1],"_run_as_user":"nobody"}""", scripted.Params(0)!.ToJsonString());
        Assert.Equal("""{"command":"str_replace","path":"/f","new_str":"b","old_str":"a","_run_as_user":"nobody"}""", scripted.Params(1)!.ToJsonString());
        Assert.Equal("""{"command":"create","path":"/f","file_text":"text","_run_as_user":"nobody"}""", scripted.Params(2)!.ToJsonString());
        Assert.All(scripted.RpcCalls, call => Assert.Equal(TimeSpan.FromSeconds(42), call.Timeout));
    }

    [Fact]
    public async Task insert_text_is_rewired_to_new_str_for_insert_only()
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("ok"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        await tool.Execute(SandboxToolsFixtures.Args(new { command = "insert", path = "/f", insert_line = 3, insert_text = "hello" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { command = "insert", path = "/f", insert_line = 3, insert_text = "ignored", new_str = "kept" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { command = "str_replace", path = "/f", old_str = "x", insert_text = "not rewired" }), CancellationToken.None);

        Assert.Equal("""{"command":"insert","path":"/f","insert_line":3,"insert_text":"hello","new_str":"hello"}""", scripted.Params(0)!.ToJsonString());
        Assert.Equal("""{"command":"insert","path":"/f","insert_line":3,"insert_text":"ignored","new_str":"kept"}""", scripted.Params(1)!.ToJsonString());
        Assert.Equal("""{"command":"str_replace","path":"/f","insert_text":"not rewired","old_str":"x"}""", scripted.Params(2)!.ToJsonString());
    }

    [Fact]
    public async Task the_first_call_injects_the_tools_and_later_calls_run_as_the_tools_user()
    {
        var scripted = new ScriptedToolsSandbox(hasTools: false);
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success("ok"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var support = new SandboxToolSupport(new FakeBinaries());
        var tool = TextEditor.Create(support: support);

        await tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f" }), CancellationToken.None);

        Assert.Equal(["version", "text_editor"], scripted.Requests.Select(r => r["method"]!.GetValue<string>()));
        Assert.All(scripted.RpcCalls, call => Assert.Equal("root", call.User));
        Assert.Equal("root", support.ToolsUser(scripted.Sandbox));
    }

    [Fact]
    public async Task container_tool_exceptions_and_invalid_params_are_reported_to_the_model()
    {
        var scripted = new ScriptedToolsSandbox();
        var responses = new Queue<string>([
            SandboxToolsFixtures.Error(-32099, "The path /missing.txt does not exist. Please provide a valid path."),
            SandboxToolsFixtures.Error(-32602, "Invalid parameters: 'view_range' Input should be a valid list"),
            SandboxToolsFixtures.Error(-32098, "KeyError('boom')"),
        ]);
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(responses.Dequeue());
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var missing = await Execute(tool, SandboxToolsFixtures.Args(new { command = "view", path = "/missing.txt" }));
        var invalid = await Execute(tool, SandboxToolsFixtures.Args(new { command = "view", path = "/f", view_range = new[] { 1 } }));
        var crash = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(tool, SandboxToolsFixtures.Args(new { command = "view", path = "/f" })));

        Assert.Equal(("unknown", "The path /missing.txt does not exist. Please provide a valid path."), (missing.Error!.Type, missing.Error.Message));
        Assert.Equal(("parsing", "Invalid parameters: 'view_range' Input should be a valid list"), (invalid.Error!.Type, invalid.Error.Message));
        Assert.Equal("KeyError('boom')", crash.Message);
    }

    [Fact]
    public async Task an_exec_timeout_reaches_the_model_as_a_timeout_error()
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["text_editor"] = _ => throw new SandboxTimeoutException("Command timed out after 180 seconds", "partial output");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var direct = await Assert.ThrowsAsync<SandboxTimeoutException>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f" }), CancellationToken.None));
        var message = await Execute(tool, SandboxToolsFixtures.Args(new { command = "view", path = "/f" }));

        Assert.Equal("partial output", direct.TruncatedOutput);
        Assert.Equal("timeout", message.Error!.Type);
        Assert.Equal("partial output", message.Text);
    }

    [Fact]
    public async Task bad_arguments_are_parsing_errors_and_a_non_string_result_is_fatal()
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["text_editor"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { unexpected = true }));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var command = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "delete", path = "/f" }), CancellationToken.None));
        var path = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "view" }), CancellationToken.None));
        var range = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f", view_range = "1-2" }), CancellationToken.None));
        var line = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "insert", path = "/f", insert_line = "3" }), CancellationToken.None));
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f" }), CancellationToken.None));

        Assert.Equal("Parameter 'command' must be one of 'view', 'create', 'str_replace', 'insert', 'undo_edit', got 'delete'.", command.Message);
        Assert.Equal("Required parameter path not provided to tool call.", path.Message);
        Assert.Equal("Parameter 'view_range' must be a list of integers, got <class 'str'>.", range.Message);
        Assert.Equal("Parameter 'insert_line' must be an integer, got <class 'str'>.", line.Message);
        Assert.Equal("Expected <class 'str'> result, got <class 'dict'>", shape.Message);
        Assert.DoesNotContain(scripted.Requests, r => r["params"]!["command"]!.GetValue<string>() != "view");
    }

    [Fact]
    public async Task a_cancelled_call_stops_before_any_exchange()
    {
        var scripted = new ScriptedToolsSandbox();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = TextEditor.Create(support: new SandboxToolSupport(new FakeBinaries()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/f" }), cts.Token));

        Assert.Empty(scripted.Requests);
    }
}

/// <summary>The <c>bash_session</c> tool over scripted JSON-RPC exchanges.</summary>
public class BashSessionToolTests
{
    private static ScriptedToolsSandbox Scripted(string sessionName = "BashSession-1", string output = "foo\nuser@host:/# ")
    {
        var scripted = new ScriptedToolsSandbox();
        scripted.Handlers["bash_session_new_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { session_name = sessionName }));
        scripted.Handlers["bash_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(output));
        return scripted;
    }

    private static string Timing(long waitSeconds = 30, long? maxOutput = null) =>
        $"\"wait_for_output\":{waitSeconds},\"idle_timeout\":0.5,\"max_output_bytes\":{maxOutput ?? SandboxLimits.MaxExecOutputSize}";

    [Fact]
    public async Task the_schema_and_description_are_pythons()
    {
        var expected = await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("bash_session_params.json"));
        var description = await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("bash_session_description.txt"));

        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        Assert.Equal("bash_session", tool.Name);
        Assert.Equal(description, tool.Description);
        Assert.Equal(expected, PythonJson.Dumps(tool.ToInfo().Parameters.ToJson()));
    }

    [Fact]
    public void the_timeout_defaults_to_wait_plus_transport_and_may_not_be_less()
    {
        var support = new SandboxToolSupport(new FakeBinaries());

        var tooSmall = Assert.Throws<ArgumentException>(() => BashSession.Create(timeout: TimeSpan.FromSeconds(60), support: support));
        var fractional = Assert.Throws<ArgumentException>(() => BashSession.Create(waitForOutput: TimeSpan.FromMilliseconds(1500), support: support));
        BashSession.Create(timeout: TimeSpan.FromSeconds(210), support: support);
        BashSession.Create(timeout: TimeSpan.FromSeconds(190), waitForOutput: TimeSpan.FromSeconds(10), support: support);

        Assert.StartsWith("Timeout must be at least 210 seconds, but got 60.", tooSmall.Message);
        Assert.StartsWith("wait_for_output must be a positive whole number of seconds", fractional.Message);
    }

    [Fact]
    public async Task input_is_validated_per_action_before_anything_runs()
    {
        var scripted = Scripted();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var type = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "type" }), CancellationToken.None));
        var read = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "read", input = "x" }), CancellationToken.None));
        var restart = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "restart", input = "x" }), CancellationToken.None));
        var interrupt = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "interrupt", input = "x" }), CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "dance" }), CancellationToken.None));

        Assert.Equal("'input' is required for 'type' action.", type.Message);
        Assert.Equal("Do not provide 'input' with 'read' action.", read.Message);
        Assert.Equal("Do not provide 'input' with 'restart' action.", restart.Message);
        Assert.Equal("Do not provide 'input' with 'interrupt' action.", interrupt.Message);
        Assert.StartsWith("Parameter 'action' must be one of", unknown.Message);
        Assert.Empty(scripted.Requests);
    }

    [Fact]
    public async Task the_first_call_creates_a_session_stored_under_pythons_keys_and_later_calls_reuse_it()
    {
        var scripted = Scripted();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var first = await tool.Execute(SandboxToolsFixtures.Args(new { action = "type_submit", input = "echo foo" }), CancellationToken.None);
        var second = await tool.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None);

        Assert.Equal("foo\nuser@host:/# ", first.AsText());
        Assert.Equal("foo\nuser@host:/# ", second.AsText());
        Assert.Equal(["bash_session_new_session", "bash_session", "bash_session"], scripted.Requests.Select(r => r["method"]!.GetValue<string>()));
        Assert.False(scripted.Requests[0].ContainsKey("params"));
        Assert.Equal(BashSession.TransportTimeout, scripted.RpcCalls[0].Timeout);
        Assert.Equal($$$"""{"session_name":"BashSession-1","input":"echo foo\n",{{{Timing()}}}}""", scripted.Params(1)!.ToJsonString());
        Assert.Equal($$$"""{"session_name":"BashSession-1",{{{Timing()}}}}""", scripted.Params(2)!.ToJsonString());
        Assert.Equal(TimeSpan.FromSeconds(210), scripted.RpcCalls[1].Timeout);
        Assert.Equal("BashSession-1", scope.Context.Store.Get("BashSessionStore:session_id"));
        Assert.True(scope.Context.Store.Contains("BashSessionStore:instance"));
        Assert.Null(scope.Context.Store.Get("BashSessionStore:instance"));
    }

    [Fact]
    public async Task each_action_sends_pythons_parameters()
    {
        var scripted = Scripted();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = BashSession.Create(waitForOutput: TimeSpan.FromSeconds(5), timeout: TimeSpan.FromSeconds(200), user: "agent", support: new SandboxToolSupport(new FakeBinaries()));

        await tool.Execute(SandboxToolsFixtures.Args(new { action = "type", input = "abc" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { action = "type_submit" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { action = "interrupt" }), CancellationToken.None);
        await tool.Execute(SandboxToolsFixtures.Args(new { action = "restart" }), CancellationToken.None);

        Assert.Equal("""{"user":"agent"}""", scripted.Params(0)!.ToJsonString());
        Assert.Equal($$$"""{"session_name":"BashSession-1","input":"abc",{{{Timing(5)}}}}""", scripted.Params(1)!.ToJsonString());
        Assert.Equal($$$"""{"session_name":"BashSession-1","input":"\n",{{{Timing(5)}}}}""", scripted.Params(2)!.ToJsonString());
        Assert.Equal($$$"""{"session_name":"BashSession-1","input":"\u0003",{{{Timing(5)}}}}""", scripted.Params(3)!.ToJsonString());
        Assert.Equal("""{"session_name":"BashSession-1","restart":true}""", scripted.Params(4)!.ToJsonString());
        Assert.All(scripted.RpcCalls.Skip(1), call => Assert.Equal(TimeSpan.FromSeconds(200), call.Timeout));
    }

    [Fact]
    public async Task instances_have_their_own_sessions()
    {
        var names = new Queue<string>(["BashSession-1", "BashSession-2"]);
        var scripted = Scripted();
        scripted.Handlers["bash_session_new_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { session_name = names.Dequeue() }));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var support = new SandboxToolSupport(new FakeBinaries());
        var shared = BashSession.Create(support: support);
        var isolated = BashSession.Create(instance: "worker", support: support);

        await shared.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None);
        await isolated.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None);
        await isolated.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None);

        Assert.Equal("BashSession-1", scope.Context.Store.Get("BashSessionStore:session_id"));
        Assert.Equal("BashSession-2", scope.Context.Store.Get("BashSessionStore:worker:session_id"));
        Assert.Equal("worker", scope.Context.Store.Get("BashSessionStore:worker:instance"));
        Assert.Equal(["BashSession-1", "BashSession-2", "BashSession-2"], scripted.Requests.Where(r => r["method"]!.GetValue<string>() == "bash_session").Select(r => r["params"]!["session_name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task max_output_bytes_follows_the_effective_exec_output_limit()
    {
        var scripted = Scripted();
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        using var env = new EnvVarScope().Set(SandboxLimits.MaxExecOutputSizeVar, (20 * 1024 * 1024).ToString());
        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        await tool.Execute(SandboxToolsFixtures.Args(new { action = "type_submit", input = "echo ok" }), CancellationToken.None);

        Assert.Equal(20 * 1024 * 1024, scripted.Params(1)!["max_output_bytes"]!.GetValue<long>());
        Assert.Equal("20971520", scripted.RpcCalls[1].Env![SandboxJsonRpcTransport.ResponseMaxBytesEnv]);
    }

    [Fact]
    public async Task a_timeout_creating_the_session_is_pythons_runtime_error_and_a_later_timeout_reaches_the_model()
    {
        var scripted = Scripted();
        scripted.Handlers["bash_session_new_session"] = _ => throw new SandboxTimeoutException("Command timed out after 180 seconds", "");
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var creating = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None));
        Assert.Equal("Timed out creating new session", creating.Message);
        Assert.Equal("", scope.Context.Store.Get("BashSessionStore:session_id"));

        scripted.Handlers["bash_session_new_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Success(new { session_name = "BashSession-1" }));
        scripted.Handlers["bash_session"] = _ => throw new SandboxTimeoutException("Command timed out after 210 seconds", "so far");
        var call = new ToolCall("c1", tool.Name, SandboxToolsFixtures.Args(new { action = "read" }));
        var executed = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("hi"), new ChatMessageAssistant("", toolCalls: [call])], [tool]);

        var message = Assert.IsType<ChatMessageTool>(Assert.Single(executed.Messages));
        Assert.Equal("timeout", message.Error!.Type);
        Assert.Equal("so far", message.Text);
    }

    [Fact]
    public async Task container_errors_are_mapped_like_the_text_editor()
    {
        var scripted = Scripted();
        scripted.Handlers["bash_session"] = _ => FakeSandboxEnvironment.Ok(SandboxToolsFixtures.Error(-32099, "Cannot switch to user 'nobody': server is not running as root"));
        using var scope = new SampleContextScope(sandbox: scripted.Sandbox);
        var tool = BashSession.Create(support: new SandboxToolSupport(new FakeBinaries()));

        var ex = await Assert.ThrowsAsync<ToolError>(() => tool.Execute(SandboxToolsFixtures.Args(new { action = "read" }), CancellationToken.None));

        Assert.Equal("Cannot switch to user 'nobody': server is not running as root", ex.Message);
    }
}

/// <summary>Runs only with Docker and either a cached artifact for the host architecture or network tests enabled (the artifact is ~15 MB).</summary>
public sealed class SandboxToolsDockerFactAttribute : FactAttribute
{
    public SandboxToolsDockerFactAttribute()
    {
        if (!DockerProbe.Available)
        {
            Skip = DockerProbe.SkipReason;
            return;
        }

        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        var cached = Path.Combine(new SandboxToolsBinary().BinariesDir, SandboxToolsBinary.ExecutableName(arch, musl: false));
        if (Environment.GetEnvironmentVariable("INSPECT_SWE_NETWORK_TESTS") != "1" && !File.Exists(cached))
        {
            Skip = $"Needs the inspect-sandbox-tools artifact: set INSPECT_SWE_NETWORK_TESTS=1 to download it, or place it at {cached}.";
        }
    }
}

/// <summary>End to end: inject the published binary into a real container, then edit files and drive a bash session through it.</summary>
public sealed class SandboxToolsDockerTests(DockerContainerFixture fixture) : IClassFixture<DockerContainerFixture>
{
    [SandboxToolsDockerFact]
    public async Task text_editor_and_bash_session_run_against_a_real_container()
    {
        using var scope = new SampleContextScope(sandbox: fixture.Sandbox);
        var support = new SandboxToolSupport();
        var editor = TextEditor.Create(support: support);

        var view = await editor.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/etc/passwd" }), CancellationToken.None);
        var create = await editor.Execute(SandboxToolsFixtures.Args(new { command = "create", path = "/tmp/hello.txt", file_text = "hello\nworld\n" }), CancellationToken.None);
        var replace = await editor.Execute(SandboxToolsFixtures.Args(new { command = "str_replace", path = "/tmp/hello.txt", old_str = "world", new_str = "there" }), CancellationToken.None);
        var undo = await editor.Execute(SandboxToolsFixtures.Args(new { command = "undo_edit", path = "/tmp/hello.txt" }), CancellationToken.None);
        var missing = await Assert.ThrowsAsync<ToolError>(() => editor.Execute(SandboxToolsFixtures.Args(new { command = "view", path = "/missing.txt" }), CancellationToken.None));

        Assert.Contains("root:x:0:0:root", view.AsText());
        Assert.Contains("File created successfully at: /tmp/hello.txt", create.AsText());
        Assert.Contains("there", replace.AsText());
        Assert.Contains("Last edit to /tmp/hello.txt undone successfully", undo.AsText());
        Assert.Contains("/missing.txt", missing.Message);
        Assert.Equal("root", support.ToolsUser(fixture.Sandbox));
        Assert.False(string.IsNullOrWhiteSpace(support.InjectedVersion(fixture.Sandbox)));
        Assert.Equal("hello\nworld\n", await fixture.Sandbox.ReadFileAsync("/tmp/hello.txt"));

        var bash = BashSession.Create(support: support);
        var echo = await bash.Execute(SandboxToolsFixtures.Args(new { action = "type_submit", input = "echo start $(whoami) end" }), CancellationToken.None);
        var restart = await bash.Execute(SandboxToolsFixtures.Args(new { action = "restart" }), CancellationToken.None);
        var after = await bash.Execute(SandboxToolsFixtures.Args(new { action = "type_submit", input = "echo again" }), CancellationToken.None);

        Assert.Contains("start root end", echo.AsText());
        Assert.NotNull(restart.AsText());
        Assert.Contains("again", after.AsText());
        Assert.False(string.IsNullOrEmpty(scope.Context.Store.Get("BashSessionStore:session_id") as string));
    }
}
