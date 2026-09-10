using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// A stand-in for the <c>claude</c> CLI, installed as the <see cref="CliSandbox.OnExec"/> handler, that plays Claude Code
/// against the real sandbox agent bridge. It reads <c>ANTHROPIC_BASE_URL</c>, <c>ANTHROPIC_AUTH_TOKEN</c>, <c>--model</c>,
/// <c>--append-system-prompt</c>, the <c>--mcp-config</c> file (from <see cref="CliSandbox.Files"/>) and the prompt after
/// <c>--</c>. At startup it lists each configured MCP server's tools (<c>initialize</c>, <c>notifications/initialized</c>,
/// <c>tools/list</c>) and declares them as <c>mcp__{server}__{tool}</c>. Then it runs the agent loop over streaming
/// <c>POST /v1/messages</c>, parsing the Anthropic SSE.
/// </summary>
/// <remarks>
/// Tool calls are handled like this:
/// <list type="bullet">
/// <item><c>Agent</c>/<c>Task</c> run a sub-agent conversation whose first user message is the prompt. Parallel calls run concurrently.</item>
/// <item><c>mcp__…</c> calls go to MCP <c>tools/call</c> with the configured headers.</item>
/// <item><c>Write</c> writes the file into the sandbox; anything else answers <c>ok</c>.</item>
/// </list>
/// It prints stream-json lines: <c>system/init</c>, an <c>assistant</c> line per main turn, a <c>user</c> line with the
/// <c>tool_result</c> blocks, then <see cref="TrailingLines"/>, then <c>result</c>. A final <c>refusal</c> stop exits 1 with
/// empty stderr, as Claude Code does. A protocol failure exits 1 with the reason on stderr.
/// </remarks>
public sealed class FakeClaudeCodeCli
{
    /// <summary>What <c>which claude</c> answers.</summary>
    public const string BinaryPath = "/usr/local/bin/claude";

    /// <summary>The system prompt a sub-agent conversation carries.</summary>
    public const string SubagentSystemPrompt = "You are a Claude Code sub-agent.";

    private static readonly JsonArray BuiltinTools = JsonNode.Parse("""
        [
          {"name": "Agent", "description": "Launch a sub-agent.", "input_schema": {"type": "object", "properties": {"subagent_type": {"type": "string"}, "description": {"type": "string"}, "prompt": {"type": "string"}}, "required": ["prompt"]}},
          {"name": "Write", "description": "Write a file.", "input_schema": {"type": "object", "properties": {"file_path": {"type": "string"}, "content": {"type": "string"}}, "required": ["file_path", "content"]}},
          {"name": "Bash", "description": "Run a command.", "input_schema": {"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}}
        ]
        """)!.AsArray();

    private readonly Lock _gate = new();

    private readonly List<IReadOnlyList<string>> _launches = [];

    private int _turn;

    public FakeClaudeCodeCli(CliSandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        Sandbox = sandbox;
        sandbox.WhichPaths["claude"] = BinaryPath;
        sandbox.OnExec = HandleAsync;
    }

    public CliSandbox Sandbox { get; }

    /// <summary>Every bridge request the fake made: messages turns and MCP JSON-RPC posts.</summary>
    public FakeCliRequestLog Requests { get; } = new();

    /// <summary>Lines printed after the conversation, before the <c>result</c> line (e.g. a <c>compact_boundary</c>).</summary>
    public List<JsonObject> TrailingLines { get; } = [];

    /// <summary>When set, MCP discovery first posts <c>tools/list</c> without the configured headers and logs the status.</summary>
    public bool ProbeMcpWithoutHeaders { get; init; }

    /// <summary>The argv of every launch.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Launches
    {
        get
        {
            lock (_gate)
            {
                return _launches.ToList();
            }
        }
    }

    /// <summary>Whether <paramref name="call"/> is an unattended Claude Code launch.</summary>
    public static bool IsLaunch(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Cmd.Count > 2 && call.Cmd[2] == ClaudeCodeCommand.LaunchScript && call.Cmd.Contains("--print");
    }

    /// <summary>The value following <paramref name="flag"/>, or null.</summary>
    public static string? Flag(IReadOnlyList<string> cmd, string flag)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        for (var i = 0; i + 1 < cmd.Count; i++)
        {
            if (cmd[i] == flag)
            {
                return cmd[i + 1];
            }
        }

        return null;
    }

    private async Task<ExecResult?> HandleAsync(FakeExecCall call, CancellationToken cancellationToken)
    {
        if (CliSandbox.ShellScript(call) is { } script && script.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal))
        {
            return CliSandbox.Ok();
        }

        if (!IsLaunch(call))
        {
            return null;
        }

        lock (_gate)
        {
            _launches.Add(call.Cmd);
        }

        try
        {
            return await RunAsync(call, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return CliSandbox.Fail(1, $"fake claude: {ex.Message}\n");
        }
    }

    private async Task<ExecResult> RunAsync(FakeExecCall call, CancellationToken cancellationToken)
    {
        var cmd = call.Cmd;
        if (cmd.Count < 2 || cmd[^2] != "--")
        {
            throw new InvalidOperationException("the prompt must follow a bare --");
        }

        var session = new Session(
            this,
            FakeCliEnv.Require(call, "ANTHROPIC_BASE_URL"),
            FakeCliEnv.Require(call, "ANTHROPIC_AUTH_TOKEN"),
            Flag(cmd, "--model") ?? throw new InvalidOperationException("no --model"),
            cancellationToken);
        using var disposeSession = session;
        var sessionId = Flag(cmd, "--session-id") ?? Flag(cmd, "--resume") ?? throw new InvalidOperationException("no session id");
        var tools = BuiltinTools.DeepClone().AsArray();
        if (Flag(cmd, "--mcp-config") is { } configPath)
        {
            var config = JsonNode.Parse(Sandbox.TextOf(configPath) ?? throw new InvalidOperationException($"no MCP config at {configPath}"))!;
            foreach (var (server, node) in config["mcpServers"]!.AsObject())
            {
                foreach (var tool in await session.DiscoverMcpToolsAsync(server, node!.AsObject()))
                {
                    tools.Add(tool);
                }
            }
        }

        var lines = new List<JsonNode> { new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["model"] = session.Model, ["cwd"] = call.Cwd } };
        var conversation = new JsonArray { UserMessage(cmd[^1]) };
        var system = Flag(cmd, "--append-system-prompt");
        JsonObject reply;
        while (true)
        {
            reply = await session.MessagesAsync(system, conversation, tools);
            lines.Add(new JsonObject { ["type"] = "assistant", ["message"] = reply.DeepClone(), ["session_id"] = sessionId });
            conversation.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply["content"]!.DeepClone() });
            var toolResults = await RunToolsAsync(session, reply, allowSubagents: true);
            if (toolResults is null)
            {
                break;
            }

            lines.Add(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = toolResults.DeepClone() }, ["session_id"] = sessionId });
            conversation.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        }

        lines.AddRange(TrailingLines.Select(line => line.DeepClone()));
        var refused = reply["stop_reason"]?.GetValue<string>() == "refusal";
        lines.Add(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = refused, ["result"] = TextOf(reply), ["session_id"] = sessionId });
        var stdout = string.Join("\n", lines.Select(line => line.ToJsonString())) + "\n";
        return refused ? CliSandbox.Fail(1, "", stdout) : CliSandbox.Ok(stdout);
    }

    /// <summary>Runs the reply's tool calls (concurrently) and returns the <c>tool_result</c> blocks, or null when there were none.</summary>
    private async Task<JsonArray?> RunToolsAsync(Session session, JsonObject reply, bool allowSubagents)
    {
        var toolUses = reply["content"]!.AsArray().OfType<JsonObject>().Where(block => block["type"]?.GetValue<string>() == "tool_use").ToList();
        if (toolUses.Count == 0)
        {
            return null;
        }

        var outputs = await Task.WhenAll(toolUses.Select(toolUse => RunToolAsync(session, toolUse, allowSubagents)));
        return new JsonArray(toolUses.Select((toolUse, i) => (JsonNode?)new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolUse["id"]!.GetValue<string>(),
            ["content"] = outputs[i],
        }).ToArray());
    }

    private async Task<string> RunToolAsync(Session session, JsonObject toolUse, bool allowSubagents)
    {
        var name = toolUse["name"]!.GetValue<string>();
        var input = toolUse["input"] as JsonObject ?? new JsonObject();
        if (name is "Agent" or "Task" && allowSubagents)
        {
            return await RunSubagentAsync(session, input["prompt"]!.GetValue<string>());
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = name["mcp__".Length..];
            var separator = rest.IndexOf("__", StringComparison.Ordinal);
            return await session.CallMcpToolAsync(rest[..separator], rest[(separator + 2)..], input);
        }

        if (name == "Write")
        {
            Sandbox.Files[input["file_path"]!.GetValue<string>()] = Encoding.UTF8.GetBytes(input["content"]!.GetValue<string>());
            return "File written.";
        }

        return "ok";
    }

    /// <summary>A sub-agent conversation: the prompt as the first user message, looping until a reply has no tool calls.</summary>
    private async Task<string> RunSubagentAsync(Session session, string prompt)
    {
        var conversation = new JsonArray { UserMessage(prompt) };
        while (true)
        {
            var reply = await session.MessagesAsync(SubagentSystemPrompt, conversation, BuiltinTools);
            conversation.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply["content"]!.DeepClone() });
            var toolResults = await RunToolsAsync(session, reply, allowSubagents: false);
            if (toolResults is null)
            {
                return TextOf(reply);
            }

            conversation.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        }
    }

    private static JsonObject UserMessage(string text) => new() { ["role"] = "user", ["content"] = text };

    private static string TextOf(JsonObject message) =>
        string.Concat(message["content"]!.AsArray().OfType<JsonObject>()
            .Where(block => block["type"]?.GetValue<string>() == "text")
            .Select(block => block["text"]?.GetValue<string>() ?? ""));

    private int NextTurn() => Interlocked.Increment(ref _turn);

    /// <summary>One launch's connections to the bridge.</summary>
    private sealed class Session(FakeClaudeCodeCli cli, string baseUrl, string token, string model, CancellationToken cancellationToken) : IDisposable
    {
        private readonly HttpClient _client = FakeCliEnv.BearerClient(baseUrl, token);

        private readonly HttpClient _mcp = new() { Timeout = TimeSpan.FromMinutes(2) };

        private readonly Dictionary<string, JsonObject> _servers = new(StringComparer.Ordinal);

        private int _rpcId;

        public string Model { get; } = model;

        public async Task<JsonObject> MessagesAsync(string? system, JsonArray messages, JsonArray tools)
        {
            var body = new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = 32000,
                ["stream"] = true,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
            };
            if (system is not null)
            {
                body["system"] = system;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Add("anthropic-version", "2023-06-01");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cli.Requests.Add(new FakeCliRequest("POST", "/v1/messages", Model, (int)response.StatusCode, cli.NextTurn(), request.Headers.Authorization?.ToString()));
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"bridge answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadMessageAsync(stream);
        }

        /// <summary>MCP startup for one configured server: <c>initialize</c>, <c>notifications/initialized</c> and <c>tools/list</c>, as <c>mcp__{server}__{tool}</c> tool declarations.</summary>
        public async Task<IReadOnlyList<JsonNode>> DiscoverMcpToolsAsync(string server, JsonObject config)
        {
            _servers[server] = config;
            if (cli.ProbeMcpWithoutHeaders)
            {
                await PostRpcAsync(server, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_rpcId, ["method"] = "tools/list" }, withHeaders: false);
            }

            await RpcAsync(server, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "fake-claude", ["version"] = "0" } });
            var (status, _) = await PostRpcAsync(server, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, withHeaders: true);
            if (status != HttpStatusCode.Accepted)
            {
                throw new InvalidOperationException($"notifications/initialized answered {(int)status}");
            }

            var listed = await RpcAsync(server, "tools/list", new JsonObject());
            return listed["tools"]!.AsArray().OfType<JsonObject>().Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = $"mcp__{server}__{tool["name"]!.GetValue<string>()}",
                ["description"] = tool["description"]?.DeepClone(),
                ["input_schema"] = tool["inputSchema"]!.DeepClone(),
            }).ToList();
        }

        public async Task<string> CallMcpToolAsync(string server, string tool, JsonObject arguments)
        {
            var result = await RpcAsync(server, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() });
            return string.Concat(result["content"]!.AsArray().OfType<JsonObject>().Select(block => block["text"]?.GetValue<string>() ?? ""));
        }

        public void Dispose()
        {
            _client.Dispose();
            _mcp.Dispose();
        }

        private async Task<JsonObject> RpcAsync(string server, string method, JsonObject parameters)
        {
            var id = ++_rpcId;
            var (status, reply) = await PostRpcAsync(server, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, withHeaders: true);
            if (status != HttpStatusCode.OK || reply is null)
            {
                throw new InvalidOperationException($"MCP {method} answered {(int)status}");
            }

            if (reply["error"] is JsonObject error)
            {
                throw new InvalidOperationException($"MCP {method} failed: {error["message"]}");
            }

            return reply["result"]!.AsObject();
        }

        private async Task<(HttpStatusCode Status, JsonObject? Reply)> PostRpcAsync(string server, JsonObject message, bool withHeaders)
        {
            var config = _servers[server];
            var url = config["url"]!.GetValue<string>();
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (withHeaders && config["headers"] is JsonObject headers)
            {
                foreach (var (name, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value!.GetValue<string>());
                }
            }

            using var response = await _mcp.SendAsync(request, cancellationToken);
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
            cli.Requests.Add(new FakeCliRequest("POST", new Uri(url).AbsolutePath, null, (int)response.StatusCode, cli.NextTurn(), authorization));
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.StatusCode, text.Length == 0 || response.StatusCode != HttpStatusCode.OK ? null : JsonNode.Parse(text)!.AsObject());
        }

        /// <summary>Folds the Anthropic stream back into a message: <c>message_start</c>, block start/delta/stop, <c>message_delta</c>, <c>message_stop</c>.</summary>
        private async Task<JsonObject> ReadMessageAsync(Stream stream)
        {
            JsonObject? message = null;
            var blocks = new SortedDictionary<int, JsonObject>();
            var partialJson = new Dictionary<int, StringBuilder>();
            var stopped = false;
            await foreach (var (_, data) in SseFrames.ReadAsync(stream, cancellationToken))
            {
                var payload = JsonNode.Parse(data)!.AsObject();
                var index = payload["index"]?.GetValue<int>() ?? -1;
                switch (payload["type"]?.GetValue<string>())
                {
                    case "message_start":
                        message = payload["message"]!.DeepClone().AsObject();
                        break;
                    case "content_block_start":
                        blocks[index] = payload["content_block"]!.DeepClone().AsObject();
                        break;
                    case "content_block_delta":
                        var delta = payload["delta"]!.AsObject();
                        var block = blocks[index];
                        switch (delta["type"]!.GetValue<string>())
                        {
                            case "text_delta":
                                block["text"] = (block["text"]?.GetValue<string>() ?? "") + delta["text"]!.GetValue<string>();
                                break;
                            case "thinking_delta":
                                block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + delta["thinking"]!.GetValue<string>();
                                break;
                            case "signature_delta":
                                block["signature"] = delta["signature"]!.GetValue<string>();
                                break;
                            case "input_json_delta":
                                (partialJson.TryGetValue(index, out var sb) ? sb : partialJson[index] = new StringBuilder()).Append(delta["partial_json"]!.GetValue<string>());
                                break;
                        }

                        break;
                    case "content_block_stop":
                        if (partialJson.Remove(index, out var json))
                        {
                            blocks[index]["input"] = JsonNode.Parse(json.Length == 0 ? "{}" : json.ToString());
                        }

                        break;
                    case "message_delta":
                        message!["stop_reason"] = payload["delta"]!["stop_reason"]?.DeepClone();
                        break;
                    case "message_stop":
                        stopped = true;
                        break;
                    case "error":
                        throw new InvalidOperationException($"bridge stream error: {data}");
                }
            }

            if (message is null || !stopped)
            {
                throw new InvalidOperationException("the bridge stream ended without message_stop");
            }

            message["content"] = new JsonArray(blocks.Values.Select(block => (JsonNode?)block).ToArray());
            return message;
        }
    }
}
