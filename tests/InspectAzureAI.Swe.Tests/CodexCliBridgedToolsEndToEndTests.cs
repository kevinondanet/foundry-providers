using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Bridged host tools through Codex end to end: <see cref="FakeCodexCli"/> reads the <c>[mcp_servers.secrets]</c>
/// table the agent wrote, lists and calls the tool on the real <c>/mcp/secrets</c> endpoint with the bearer token from
/// <c>OPENAI_API_KEY</c>, and the host <see cref="ToolDef"/> runs once, with or without an approval policy (whose grant
/// the bridge registers for the bare tool name).
/// </summary>
public sealed class CodexCliBridgedToolsEndToEndTests : IDisposable
{
    private static readonly ToolParams LookupParams = new()
    {
        Properties = new Dictionary<string, ToolParam> { ["key"] = ToolParam.Of("string", "The secret's key.") },
        Required = ["key"],
    };

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "codex-mcp-" + Guid.NewGuid().ToString("N"));

    private readonly StrictHttpHandler _http = new();

    public CodexCliBridgedToolsEndToEndTests()
    {
        CodexCliBinary.ResetForTests();
        ProviderLogger.Reset();
        FakeCodexCli.SeedCache(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_bridged_host_tool_runs_once_through_the_mcp_endpoint(bool withApproval)
    {
        var executions = new List<JsonObject>();
        var tool = new ToolDef("secret_lookup", "Look up a secret.", LookupParams, (args, _) =>
        {
            lock (executions)
            {
                executions.Add(args.DeepClone().AsObject());
            }

            return Task.FromResult<ToolResult>($"secret for {args["key"]}");
        });
        var api = new ScriptedModelApi([ScriptedTurn.ToolCall("secret_lookup", new { key = "db" }, id: "call_secret"), ScriptedTurn.Text("The db secret was found.")], "gpt-5.4");
        var sandbox = new CliSandbox();
        var fake = new FakeCodexCli(sandbox);
        var context = new SampleContext { ActiveModel = new Model(api), Sandboxes = SandboxEnvironments.Single(sandbox) };
        using var scope = SampleContext.Begin(context);
        using var approval = withApproval ? ToolApproval.Begin([new ApprovalPolicy(Approvers.Auto(), "*")]) : null;
        var agent = new CodexCliAgent(new CodexCliOptions
        {
            Version = FakeCodexCli.Version,
            CacheDir = _cacheDir,
            HttpHandler = _http,
            ReleaseApiBaseUrl = $"https://api.github.test/{Guid.NewGuid():N}/repos/openai/codex",
            CatalogBaseUrl = $"https://raw.github.test/{Guid.NewGuid():N}/openai/codex",
            BridgedTools = [new BridgedToolsSpec("secrets", [tool])],
        });

        var result = await agent.ExecuteAsync(new AgentState([new ChatMessageUser("Find the database password.")]));

        var executed = Assert.Single(executions);
        Assert.Equal("db", executed["key"]!.GetValue<string>());

        // Codex saw the MCP tool in its own namespace; the served model and the state see the bare name
        Assert.Contains(api.Requests[0].Tools, info => info.Name == "secret_lookup");
        var call = Assert.Single(fake.OutputItems, item => item["type"]!.GetValue<string>() == "function_call");
        Assert.Equal(("secret_lookup", "mcp__secrets__"), (call["name"]!.GetValue<string>(), call["namespace"]!.GetValue<string>()));
        Assert.Contains(result.Messages, message => message is ChatMessageAssistant assistant && assistant.ToolCalls?.Any(c => c.Function == "secret_lookup") == true);
        Assert.Contains(result.Messages, message => message is ChatMessageTool toolMessage && toolMessage.ToolCallId == "call_secret" && toolMessage.Text == "secret for db");
        Assert.Equal("The db secret was found.", result.Messages[^1].Text);

        // config.toml carries the endpoint and the token variable, never the token
        var configText = sandbox.TextOf("/workspace/.codex/config.toml")!;
        var server = MiniToml.Parse(configText)["mcp_servers.secrets"];
        var launch = sandbox.Calls.Single(FakeCodexCli.IsLaunch);
        var token = launch.Env!["OPENAI_API_KEY"];
        Assert.Equal(launch.Env["OPENAI_BASE_URL"][..^"/v1".Length] + "/mcp/secrets", server["url"]!.GetValue<string>());
        Assert.Equal("OPENAI_API_KEY", server["bearer_token_env_var"]!.GetValue<string>());
        Assert.DoesNotContain(token, configText, StringComparison.Ordinal);

        // initialize, the initialized notification, tools/list and tools/call, all authenticated
        var mcp = fake.Requests.Snapshot().Where(request => request.Path == "/mcp/secrets").ToList();
        Assert.Equal([200, 202, 200, 200], mcp.Select(request => request.Status));
        Assert.All(mcp, request => Assert.Equal($"Bearer {token}", request.Authorization));
        Assert.Empty(_http.Requests);
    }
}
