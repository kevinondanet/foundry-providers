using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Bridged host tools for <c>claude_code(bridged_tools=...)</c> end to end, offline: the agent writes the MCP config file,
/// <see cref="FakeClaudeCodeCli"/> reads it and drives the bridge's <c>/mcp/{server}</c> endpoint with the configured
/// bearer header, and the host <see cref="ToolDef"/> runs.
/// </summary>
[Collection("ClaudeCode")]
public class ClaudeCodeBridgedToolsEndToEndTests
{
    private static readonly ToolParams LookupParams = new()
    {
        Properties = new Dictionary<string, ToolParam> { ["key"] = ToolParam.Of("string", "The secret's key.") },
        Required = ["key"],
    };

    private sealed class Run : IDisposable
    {
        private readonly IDisposable _scope;

        public Run()
        {
            Api = new ScriptedModelApi(
                ScriptedTurn.ToolCall("mcp__secrets__secret_lookup", new { key = "db" }, id: "toolu_1"),
                ScriptedTurn.Text("The database secret was retrieved."));
            Cli = new FakeClaudeCodeCli(Sandbox) { ProbeMcpWithoutHeaders = true };
            Context = new SampleContext { ActiveModel = new Model(Api), Sandboxes = SandboxEnvironments.Single(Sandbox) };
            _scope = SampleContext.Begin(Context);
            Tool = new ToolDef("secret_lookup", "Look up a secret.", LookupParams, (args, _) =>
            {
                Executions.Enqueue(args.DeepClone().AsObject());
                return Task.FromResult<ToolResult>($"secret for {args["key"]}");
            });
        }

        public CliSandbox Sandbox { get; } = new();

        public FakeClaudeCodeCli Cli { get; }

        public ScriptedModelApi Api { get; }

        public SampleContext Context { get; }

        public StrictHttpHandler Http { get; } = new();

        public ToolDef Tool { get; }

        public ConcurrentQueue<JsonObject> Executions { get; } = new();

        public ClaudeCodeAgent Agent() => new(new ClaudeCodeOptions
        {
            BridgedTools = [new BridgedToolsSpec("secrets", [Tool])],
            HttpHandler = Http,
            CacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N")),
        });

        public string LaunchToken => Sandbox.Calls.Single(FakeClaudeCodeCli.IsLaunch).Env!["ANTHROPIC_AUTH_TOKEN"];

        public void Dispose()
        {
            _scope.Dispose();
            Assert.Empty(Http.Requests);
        }
    }

    private static void AssertCallRanOnce(Run run, AgentState state)
    {
        var execution = Assert.Single(run.Executions);
        Assert.Equal("db", execution["key"]!.GetValue<string>());
        var call = Assert.Single(state.Messages.OfType<ChatMessageAssistant>().SelectMany(m => m.ToolCalls ?? []));
        Assert.Equal(("toolu_1", "mcp__secrets__secret_lookup"), (call.Id, call.Function));
        Assert.Equal("secret for db", Assert.Single(state.Messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal("The database secret was retrieved.", state.Messages[^1].Text);
    }

    [Fact]
    public async Task claude_code_reads_the_mcp_config_file_and_calls_the_bridged_tool_with_its_bearer_header()
    {
        using var run = new Run();
        var agent = run.Agent();

        var state = await agent.ExecuteAsync(new AgentState([new ChatMessageUser("Find the database secret.")]));

        AssertCallRanOnce(run, state);

        // the config file: 0600, bridged server URL on this bridge with the launch token as bearer
        var path = ClaudeCodeMcp.ConfigPath(agent.SessionId);
        var config = JsonNode.Parse(run.Sandbox.TextOf(path)!)!["mcpServers"]!["secrets"]!;
        var token = run.LaunchToken;
        Assert.Equal("http", config["type"]!.GetValue<string>());
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/mcp/secrets$", config["url"]!.GetValue<string>());
        Assert.Equal($"Bearer {token}", config["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Single(run.Sandbox.Calls, c => c.Cmd.SequenceEqual(["chmod", "600", path]));
        var launch = Assert.Single(run.Cli.Launches);
        Assert.Equal(path, FakeClaudeCodeCli.Flag(launch, "--mcp-config"));
        Assert.Equal("mcp__secrets__*", FakeClaudeCodeCli.Flag(launch, "--allowed-tools"));

        // MCP traffic: a header-less probe is refused, everything else carries the bearer header
        var mcp = run.Cli.Requests.Snapshot().Where(r => r.Path == "/mcp/secrets").ToList();
        Assert.Equal((401, null), (mcp[0].Status, mcp[0].Authorization));
        Assert.Equal([200, 202, 200, 200], mcp.Skip(1).Select(r => r.Status));
        Assert.All(mcp.Skip(1), r => Assert.Equal($"Bearer {token}", r.Authorization));
        Assert.All(run.Cli.Requests.Snapshot().Where(r => r.Path == "/v1/messages"), r => Assert.Equal(200, r.Status));

        // the bridged tool was declared to the model under its Claude Code name
        var firstRequest = run.Api.Requests[0];
        var declared = Assert.Single(firstRequest.Tools, t => t.Name == "mcp__secrets__secret_lookup");
        Assert.Equal(["key"], declared.Parameters.Required);
    }

    [Fact]
    public async Task under_an_approval_policy_the_approved_call_gets_a_grant_and_runs_once()
    {
        using var run = new Run();
        using var approval = ToolApproval.Begin([new ApprovalPolicy(Approvers.Auto(), "*")]);

        var state = await run.Agent().ExecuteAsync(new AgentState([new ChatMessageUser("Find the database secret.")]));

        AssertCallRanOnce(run, state);
        Assert.Contains(run.Cli.Requests.Snapshot(), r => r.Path == "/mcp/secrets" && r.Status == 200);
    }
}
