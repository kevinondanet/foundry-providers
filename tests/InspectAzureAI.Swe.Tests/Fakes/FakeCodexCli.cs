using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Swe.CodexCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The stand-in for the <c>codex</c> binary (spec 5.3): the <see cref="CliSandbox.OnExec"/> handler that answers the
/// agent's <c>codex exec</c> launch the way Codex 0.154 does from the outside. It reads <c>CODEX_HOME</c>,
/// <c>OPENAI_BASE_URL</c> and <c>OPENAI_API_KEY</c> from the exec env and checks <c>config.toml</c> points at the
/// <c>openai-proxy</c> Responses provider; it lists the bridged MCP servers' tools (<c>initialize</c>, <c>tools/list</c>)
/// and declares them as <c>mcp__&lt;server&gt;__</c> namespaces; then per turn it POSTs a Codex-shaped streaming
/// <c>/responses</c> request carrying the whole history, keeps only <c>response.output_item.done</c> items, fails
/// without <c>response.completed</c>, runs the calls (<c>exec_command</c> is a no-op, <c>apply_patch</c> is recorded,
/// an MCP namespaced call goes to <c>tools/call</c>) and loops until a response has no calls. The history is saved to
/// a rollout file that <c>resume --last</c> replays. Without <c>--skip-git-repo-check</c> it refuses to run.
/// </summary>
public sealed class FakeCodexCli
{
    public const string Version = "0.154.0";

    public const string Platform = "linux-arm64";

    /// <summary>Where <see cref="CodexCliBinary"/> installs the seeded package in <see cref="CliSandbox"/>.</summary>
    public const string BinaryPath = "/var/tmp/.5c95f967ca830048/codex-0.154.0-linux-arm64/bin/codex";

    public const string Instructions = "You are Codex, based on GPT-5. You are running as a coding agent in the Codex CLI on a user's computer.";

    /// <summary>The rollout file, relative to <c>CODEX_HOME</c>.</summary>
    public const string RolloutPath = "sessions/rollout-fake.jsonl";

    public const string UntrustedDirectoryError = "Not inside a trusted directory and --skip-git-repo-check was not specified.";

    public const string ExecOutput = "ok";

    public const string PatchOutput = "Success. Updated the following files:\nM app.py\n";

    public const string SpawnOutput = """{"agent_id":"thread_fake_1","nickname":"Scout"}""";

    public const int MaxTurns = 16;

    private const string PatchGrammar = "start: begin_patch hunk+ end_patch\nbegin_patch: \"*** Begin Patch\" LF\nend_patch: \"*** End Patch\" LF?";

    private readonly Lock _gate = new();

    private readonly CliSandbox _sandbox;

    private readonly List<JsonObject> _requestBodies = [];

    private readonly List<JsonObject> _outputItems = [];

    private readonly List<string> _patches = [];

    private readonly List<string> _commands = [];

    private readonly string _sessionId = Guid.NewGuid().ToString();

    private int _launches;

    private int _turn;

    private int _rpcId;

    public FakeCodexCli(CliSandbox sandbox)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        sandbox.OnExec = HandleAsync;
    }

    /// <summary>Every request made to the bridge: <c>/v1/responses</c> turns and <c>/mcp/&lt;server&gt;</c> calls.</summary>
    public FakeCliRequestLog Requests { get; } = new();

    public int Launches => Volatile.Read(ref _launches);

    /// <summary>The <c>/responses</c> request bodies, in order.</summary>
    public IReadOnlyList<JsonObject> RequestBodies => Snapshot(_requestBodies);

    /// <summary>Every <c>response.output_item.done</c> item received, in order.</summary>
    public IReadOnlyList<JsonObject> OutputItems => Snapshot(_outputItems);

    /// <summary>The <c>apply_patch</c> inputs received.</summary>
    public IReadOnlyList<string> Patches => Snapshot(_patches);

    /// <summary>The <c>exec_command</c> commands received.</summary>
    public IReadOnlyList<string> Commands => Snapshot(_commands);

    /// <summary>Seeds <paramref name="cacheDir"/> with a verified package archive of <see cref="Version"/> and its model catalog, so installs need no network.</summary>
    public static void SeedCache(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var binary = new CodexCliBinary(cacheDir);
        var archive = Encoding.ASCII.GetBytes("fake codex package archive");
        binary.WriteCachedArchive(archive, Version, Platform, package: true, CodexCliBinary.Sha256Hex(archive));
        File.WriteAllText(binary.CachedCatalogPath(Version), CodexCliModelCatalog.Bundled.ToJsonString());
    }

    /// <summary>Whether <paramref name="call"/> is the agent's headless launch: <c>bash -c LaunchScript bash &lt;codex&gt; exec …</c>.</summary>
    public static bool IsLaunch(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Cmd.Count > 5 && call.Cmd[0] == "bash" && call.Cmd[2] == CodexCliCommand.LaunchScript && call.Cmd[5] == "exec";
    }

    public async Task<ExecResult?> HandleAsync(FakeExecCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Cmd.Count == 2 && call.Cmd[1] == "--version" && call.Cmd[0].EndsWith("/codex", StringComparison.Ordinal))
        {
            return CliSandbox.Ok($"codex-cli {Version}\n");
        }

        if (!IsLaunch(call))
        {
            return null;
        }

        Interlocked.Increment(ref _launches);
        if (!call.Cmd.Contains("--skip-git-repo-check"))
        {
            return CliSandbox.Fail(1, UntrustedDirectoryError);
        }

        try
        {
            return await RunAsync(call, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CliSandbox.Fail(1, $"fake codex failed: {ex.Message}");
        }
    }

    private async Task<ExecResult> RunAsync(FakeExecCall call, CancellationToken cancellationToken)
    {
        var cmd = call.Cmd;
        var codexHome = FakeCliEnv.Require(call, "CODEX_HOME");
        var baseUrl = FakeCliEnv.Require(call, "OPENAI_BASE_URL");
        var apiKey = FakeCliEnv.Require(call, "OPENAI_API_KEY");
        var configText = _sandbox.TextOf($"{codexHome}/config.toml") ?? throw new InvalidOperationException($"no config.toml under CODEX_HOME {codexHome}");
        var config = MiniToml.Parse(configText);
        Expect(Text(config, "", "model_provider") == CodexCliConfig.ProviderId, "config.toml model_provider is not openai-proxy");
        Expect(Text(config, "model_providers.openai-proxy", "wire_api") == "responses", "config.toml wire_api is not responses");
        Expect(Text(config, "model_providers.openai-proxy", "base_url") == baseUrl, $"config.toml base_url is not OPENAI_BASE_URL ({baseUrl})");
        var slug = Flag(cmd, "--model") ?? throw new InvalidOperationException("the launch has no --model");
        var resume = cmd.Count >= 2 && cmd[^2] == "resume" && cmd[^1] == "--last";
        var prompt = resume ? cmd[^3] : cmd[^1];

        var tools = BaseTools();
        var servers = new Dictionary<string, McpServer>(StringComparer.Ordinal);
        foreach (var (table, entries) in config)
        {
            if (!table.StartsWith("mcp_servers.", StringComparison.Ordinal))
            {
                continue;
            }

            var tokenVariable = entries.TryGetValue("bearer_token_env_var", out var variable) ? variable!.GetValue<string>() : null;
            var server = new McpServer(
                table["mcp_servers.".Length..],
                Text(config, table, "url") ?? throw new InvalidOperationException($"[{table}] has no url"),
                tokenVariable is null ? null : FakeCliEnv.Require(call, tokenVariable));
            servers[server.Name] = server;
            tools.Add(await McpNamespaceAsync(server, cancellationToken));
        }

        var rollout = $"{codexHome}/{RolloutPath}";
        var history = resume ? ReadRollout(rollout) : NewSession(call, codexHome);
        history.Add(Message("user", prompt));
        using var client = FakeCliEnv.BearerClient(baseUrl, apiKey);
        var finalText = "";
        for (var turn = 0; turn < MaxTurns; turn++)
        {
            var (items, failure) = await PostTurnAsync(client, slug, history, tools, cancellationToken);
            if (failure is not null)
            {
                await SaveAsync(rollout, history, cancellationToken);
                return CliSandbox.Fail(1, failure);
            }

            var outputs = new List<JsonObject>();
            foreach (var item in items)
            {
                history.Add(item);
                switch (item["type"]?.GetValue<string>())
                {
                    case "message":
                        finalText = item["content"]?[0]?["text"]?.GetValue<string>() ?? finalText;
                        break;
                    case "function_call":
                        outputs.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = item["call_id"]!.DeepClone(),
                            ["output"] = await RunFunctionAsync(item, servers, cancellationToken),
                        });
                        break;
                    case "custom_tool_call":
                        lock (_gate)
                        {
                            _patches.Add(item["input"]!.GetValue<string>());
                        }

                        outputs.Add(new JsonObject
                        {
                            ["type"] = "custom_tool_call_output",
                            ["call_id"] = item["call_id"]!.DeepClone(),
                            ["output"] = PatchOutput,
                        });
                        break;
                }
            }

            if (outputs.Count == 0)
            {
                await SaveAsync(rollout, history, cancellationToken);
                return CliSandbox.Ok(finalText + "\n");
            }

            history.AddRange(outputs);
        }

        await SaveAsync(rollout, history, cancellationToken);
        return CliSandbox.Fail(1, $"fake codex gave up after {MaxTurns} turns");
    }

    private async Task<(List<JsonObject> Items, string? Failure)> PostTurnAsync(HttpClient client, string slug, List<JsonObject> history, JsonArray tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = slug,
            ["instructions"] = Instructions,
            ["input"] = new JsonArray(history.Select(item => (JsonNode?)item.DeepClone()).ToArray()),
            ["tools"] = tools.DeepClone(),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["reasoning"] = new JsonObject { ["effort"] = "medium" },
            ["store"] = false,
            ["stream"] = true,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["prompt_cache_key"] = _sessionId,
        };
        lock (_gate)
        {
            _requestBodies.Add(body.DeepClone().AsObject());
        }

        var turn = Interlocked.Increment(ref _turn);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Requests.Add(new FakeCliRequest("POST", response.RequestMessage?.RequestUri?.AbsolutePath ?? "", slug, (int)response.StatusCode, turn, client.DefaultRequestHeaders.Authorization?.ToString()));
        if (!response.IsSuccessStatusCode)
        {
            return ([], $"the bridge answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var items = new List<JsonObject>();
        var completed = false;
        await foreach (var (_, data) in SseFrames.ReadAsync(stream, cancellationToken))
        {
            if (data == SseFrames.Done)
            {
                continue;
            }

            var frame = JsonNode.Parse(data)!.AsObject();
            switch (frame["type"]?.GetValue<string>())
            {
                case "response.output_item.done":
                    items.Add(frame["item"]!.DeepClone().AsObject());
                    break;
                case "response.completed":
                    completed = true;
                    break;
                case "response.failed":
                    return ([], $"response.failed: {frame["response"]?["error"]?.ToJsonString()}");
            }
        }

        if (!completed)
        {
            return ([], "stream closed before response.completed");
        }

        lock (_gate)
        {
            _outputItems.AddRange(items.Select(item => item.DeepClone().AsObject()));
        }

        return (items, null);
    }

    private async Task<string> RunFunctionAsync(JsonObject item, IReadOnlyDictionary<string, McpServer> servers, CancellationToken cancellationToken)
    {
        var name = item["name"]!.GetValue<string>();
        var ns = item["namespace"]?.GetValue<string>();
        var arguments = JsonNode.Parse(item["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? new JsonObject();
        if (ns is { Length: > 7 } && ns.StartsWith("mcp__", StringComparison.Ordinal) && ns.EndsWith("__", StringComparison.Ordinal))
        {
            var server = servers[ns[5..^2]];
            var result = await McpAsync(server, Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }), cancellationToken);
            return string.Join("\n", (result?["content"] as JsonArray ?? []).OfType<JsonObject>().Select(part => part["text"]?.GetValue<string>()).OfType<string>());
        }

        switch ((ns, name))
        {
            case (null, "exec_command"):
                lock (_gate)
                {
                    _commands.Add(arguments["cmd"]?.GetValue<string>() ?? "");
                }

                return ExecOutput;
            case ("multi_agent_v1", "spawn_agent"):
                return SpawnOutput;
            default:
                return $"unsupported tool {ns}/{name}";
        }
    }

    private async Task<JsonObject> McpNamespaceAsync(McpServer server, CancellationToken cancellationToken)
    {
        await McpAsync(server, Rpc("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "codex-mcp-client", ["version"] = Version },
        }), cancellationToken);
        await McpAsync(server, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancellationToken);
        var listed = await McpAsync(server, Rpc("tools/list", null), cancellationToken);
        var inner = new JsonArray();
        foreach (var tool in (listed?["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            inner.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool["name"]!.DeepClone(),
                ["description"] = tool["description"]?.DeepClone(),
                ["strict"] = false,
                ["parameters"] = tool["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
            });
        }

        return new JsonObject
        {
            ["type"] = "namespace",
            ["name"] = $"mcp__{server.Name}__",
            ["description"] = $"Tools from the {server.Name} MCP server.",
            ["tools"] = inner,
        };
    }

    /// <summary>One streamable-HTTP JSON-RPC exchange; a notification must be accepted with 202.</summary>
    private async Task<JsonObject?> McpAsync(McpServer server, JsonObject message, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (server.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new FakeCliRequest("POST", new Uri(server.Url).AbsolutePath, null, (int)response.StatusCode, Volatile.Read(ref _turn), request.Headers.Authorization?.ToString()));
        var method = message["method"]!.GetValue<string>();
        if (message["id"] is null)
        {
            Expect(response.StatusCode == HttpStatusCode.Accepted, $"MCP {method} answered {(int)response.StatusCode}: {text}");
            return null;
        }

        Expect(response.IsSuccessStatusCode, $"MCP {method} answered {(int)response.StatusCode}: {text}");
        var reply = JsonNode.Parse(text)!.AsObject();
        if (reply["error"] is { } error)
        {
            throw new InvalidOperationException($"MCP {method} failed: {error.ToJsonString()}");
        }

        return reply["result"] as JsonObject;
    }

    private JsonObject Rpc(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _rpcId), ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    /// <summary>A new session: the AGENTS.md developer message when one exists, then the environment context.</summary>
    private List<JsonObject> NewSession(FakeExecCall call, string codexHome)
    {
        var cwd = call.Cwd ?? "/";
        var history = new List<JsonObject>();
        var agentsMd = _sandbox.TextOf($"{codexHome}/AGENTS.md") ?? _sandbox.TextOf(SandboxUtil.JoinPath(cwd, "AGENTS.md"));
        if (agentsMd is not null)
        {
            history.Add(Message("developer", $"# AGENTS.md instructions for {cwd}\n\n<INSTRUCTIONS>\n{agentsMd}\n</INSTRUCTIONS>"));
        }

        history.Add(Message("user", $"<environment_context>\n  <cwd>{cwd}</cwd>\n  <shell>bash</shell>\n</environment_context>"));
        return history;
    }

    private List<JsonObject> ReadRollout(string path)
    {
        var text = _sandbox.TextOf(path) ?? throw new InvalidOperationException("resume --last found no recorded session");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }

    private Task SaveAsync(string path, List<JsonObject> history, CancellationToken cancellationToken) =>
        _sandbox.WriteFileAsync(path, string.Join("\n", history.Select(item => item.ToJsonString())) + "\n", cancellationToken);

    private static JsonArray BaseTools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = "exec_command",
            ["description"] = "Runs a command in a PTY, returning output or a session ID for ongoing interaction.",
            ["strict"] = false,
            ["parameters"] = JsonNode.Parse("""{"type": "object", "properties": {"cmd": {"type": "string", "description": "Shell command to execute."}, "workdir": {"type": "string"}}, "required": ["cmd"], "additionalProperties": false}"""),
        },
        new JsonObject
        {
            ["type"] = "custom",
            ["name"] = "apply_patch",
            ["description"] = "Use the `apply_patch` tool to edit files.",
            ["format"] = new JsonObject { ["type"] = "grammar", ["syntax"] = "lark", ["definition"] = PatchGrammar },
        },
        new JsonObject
        {
            ["type"] = "namespace",
            ["name"] = "multi_agent_v1",
            ["description"] = "Tools for spawning and coordinating sub-agents.",
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["name"] = "spawn_agent",
                ["description"] = "Spawn a sub-agent for a well-scoped task.",
                ["strict"] = false,
                ["parameters"] = JsonNode.Parse("""{"type": "object", "properties": {"agent_type": {"type": "string"}, "message": {"type": "string"}}, "required": ["message"], "additionalProperties": false}"""),
            }),
        },
        new JsonObject
        {
            ["type"] = "tool_search",
            ["execution"] = "client",
            ["description"] = "Search for deferred tools.",
            ["parameters"] = JsonNode.Parse("""{"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}"""),
        },
    ];

    private static JsonObject Message(string role, string text) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
    };

    private static string? Flag(IReadOnlyList<string> cmd, string flag)
    {
        for (var i = 0; i + 1 < cmd.Count; i++)
        {
            if (cmd[i] == flag)
            {
                return cmd[i + 1];
            }
        }

        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode?>> config, string table, string key) =>
        config.TryGetValue(table, out var entries) && entries.TryGetValue(key, out var value) ? value?.GetValue<string>() : null;

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return list.ToList();
        }
    }

    private sealed record McpServer(string Name, string Url, string? Token);
}
