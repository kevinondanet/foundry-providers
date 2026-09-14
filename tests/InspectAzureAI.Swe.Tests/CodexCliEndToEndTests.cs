using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The Codex CLI agent end to end (spec 5.3): <see cref="FakeCodexCli"/> in <see cref="CliSandbox"/> talks to the real
/// <see cref="Eval.Agents.Bridge.SandboxAgentBridge"/> Responses route, which serves a <see cref="ScriptedModelApi"/>.
/// Offline: a pinned version with a seeded, verified cache and a strict HTTP handler that must see no request.
/// </summary>
public sealed class CodexCliEndToEndTests : IDisposable
{
    private const string Prompt = "Fix the failing test in tests/test_app.py.";

    private const string Patch = "*** Begin Patch\n*** Update File: app.py\n@@\n-    return 1\n+    return 2\n*** End Patch\n";

    private const string SpawnPrompt = "Survey the repository layout and report back.";

    private const string IncorrectMessage = "The test still fails; keep going.";

    private const string Summary = "Run the test, patch app.py and delegate a survey.";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "codex-e2e-" + Guid.NewGuid().ToString("N"));

    private readonly StrictHttpHandler _http = new();

    public CodexCliEndToEndTests()
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

    private CodexCliOptions Options() => new()
    {
        Version = FakeCodexCli.Version,
        CacheDir = _cacheDir,
        HttpHandler = _http,
        ReleaseApiBaseUrl = $"https://api.github.test/{Guid.NewGuid():N}/repos/openai/codex",
        CatalogBaseUrl = $"https://raw.github.test/{Guid.NewGuid():N}/openai/codex",
    };

    private static ModelOutput FirstTurn()
    {
        var reasoning = new ContentReasoning("ENCRYPTED-REASONING", "rs_fake_1", Redacted: true) { Summary = Summary };
        var message = new ChatMessageAssistant(
            new Content[] { reasoning, new ContentText("Looking at the failing test.") },
            [
                new ToolCall("call_exec", "exec_command", new JsonObject { ["cmd"] = "pytest -x" }),
                new ToolCall("call_patch", "apply_patch", new JsonObject { ["input"] = Patch }),
                new ToolCall("call_spawn", "spawn_agent", new JsonObject { ["agent_type"] = "explorer", ["message"] = SpawnPrompt }),
            ],
            model: "gpt-5.4");
        return new ModelOutput { Model = "gpt-5.4", Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)], Usage = new ModelUsage(100, 20, 120) };
    }

    [Fact]
    public async Task two_model_turns_and_a_scored_resume_run_through_the_real_bridge()
    {
        var api = new ScriptedModelApi(
            [
                ScriptedTurn.From(FirstTurn()),
                ScriptedTurn.Text("Fixed: app.py now returns 2.", new ModelUsage(150, 10, 160)),
                ScriptedTurn.Text("Verified the fix.", new ModelUsage(170, 5, 175)),
            ],
            "gpt-5.4");
        var sandbox = new CliSandbox();
        var fake = new FakeCodexCli(sandbox);
        var verdicts = new Queue<double>([0, 1]);
        var context = new SampleContext
        {
            ActiveModel = new Model(api),
            Sandboxes = SandboxEnvironments.Single(sandbox),
            Scorer = _ => Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]),
        };
        using var scope = SampleContext.Begin(context);
        var agent = new CodexCliAgent(Options() with { SystemPrompt = "Keep changes minimal.", Attempts = new AgentAttempts(3, IncorrectMessage) });
        var state = new AgentState([new ChatMessageUser(Prompt)]);

        var result = await agent.ExecuteAsync(state);

        // two launches: the first runs the prompt, the scorer's 0 resumes the session with the incorrect message
        Assert.Same(state, result);
        Assert.Empty(verdicts);
        var launches = sandbox.Calls.Where(FakeCodexCli.IsLaunch).ToList();
        Assert.Equal(2, launches.Count);
        Assert.Equal(2, fake.Launches);
        Assert.Equal((FakeCodexCli.BinaryPath, "--model", "gpt-5.4"), (launches[0].Cmd[4], launches[0].Cmd[9], launches[0].Cmd[10]));
        Assert.Equal(Prompt, launches[0].Cmd[^1]);
        Assert.Equal([IncorrectMessage, "resume", "--last"], launches[1].Cmd.TakeLast(3));
        var token = launches[0].Env!["OPENAI_API_KEY"];

        // every turn reached the bridge with the token and was answered 200
        var responses = fake.Requests.Snapshot().Where(r => r.Path == "/v1/responses").ToList();
        Assert.Equal(3, responses.Count);
        Assert.All(responses, request => Assert.Equal((200, $"Bearer {token}", "gpt-5.4"), (request.Status, request.Authorization, request.Model)));
        Assert.Equal(3, api.Requests.Count);

        // the served model saw Codex's tools, AGENTS.md, the reasoning replay and the incorrect message
        var firstTools = api.Requests[0].Tools;
        var applyPatch = Assert.Single(firstTools, tool => tool.Name == "apply_patch");
        Assert.Equal(["input"], applyPatch.Parameters.Required);
        Assert.Contains(firstTools, tool => tool.Name == "exec_command");
        Assert.Contains(firstTools, tool => tool.Name == "spawn_agent");
        Assert.Contains(api.Requests[0].Input, message => message is ChatMessageSystem && message.Text.Contains("Keep changes minimal.", StringComparison.Ordinal));
        var replayed = api.Requests[1].Input.OfType<ChatMessageAssistant>().Last();
        var replayedReasoning = Assert.Single(replayed.ContentList.OfType<ContentReasoning>());
        Assert.Equal(("rs_fake_1", Summary), (replayedReasoning.Signature, replayedReasoning.Summary));
        Assert.Equal(["call_exec", "call_patch", "call_spawn"], replayed.ToolCalls!.Select(call => call.Id));
        Assert.Equal(IncorrectMessage, api.Requests[2].Input.OfType<ChatMessageUser>().Last().Text);

        // the wire: spawn_agent keeps its namespace, apply_patch comes back as a custom tool call
        var spawn = Assert.Single(fake.OutputItems, item => item["type"]!.GetValue<string>() == "function_call" && item["name"]!.GetValue<string>() == "spawn_agent");
        Assert.Equal("multi_agent_v1", spawn["namespace"]!.GetValue<string>());
        var patch = Assert.Single(fake.OutputItems, item => item["type"]!.GetValue<string>() == "custom_tool_call");
        Assert.Equal(("apply_patch", Patch), (patch["name"]!.GetValue<string>(), patch["input"]!.GetValue<string>()));
        Assert.Equal([Patch], fake.Patches);
        Assert.Equal(["pytest -x"], fake.Commands);

        // the bridge state holds the prompt, both turns and the final answer
        Assert.Equal("Verified the fix.", result.Messages[^1].Text);
        Assert.Contains(result.Messages, message => message is ChatMessageUser && message.Text == Prompt);
        Assert.Contains(result.Messages, message => message is ChatMessageAssistant assistant && assistant.ToolCalls?.Any(call => call.Function == "exec_command") == true);
        Assert.Contains(result.Messages, message => message is ChatMessageTool tool && tool.ToolCallId == "call_exec" && tool.Text == FakeCodexCli.ExecOutput);
        Assert.Contains(result.Messages, message => message.Text == "Fixed: app.py now returns 2.");

        // one model event per request, with tool views, and a balanced sub-agent span
        var modelEvents = context.Transcript.Events.OfType<ModelEvent>().ToList();
        Assert.Equal(responses.Count, modelEvents.Count);
        Assert.Equal("exec_command", modelEvents[0].Output.Message.ToolCalls!.Single(call => call.Function == "exec_command").View!.Title);
        var begin = Assert.Single(context.Transcript.Events.OfType<SpanBeginEvent>(), e => e.Type == "agent");
        Assert.Equal(("agent-call_spawn", "explorer"), (begin.Id, begin.Name));
        Assert.Single(context.Transcript.Events.OfType<SpanEndEvent>(), e => e.Id == begin.Id);

        Assert.Equal("Keep changes minimal.", sandbox.TextOf("/workspace/AGENTS.md"));
        Assert.NotNull(sandbox.TextOf("/workspace/.codex/" + FakeCodexCli.RolloutPath));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task the_fake_refuses_to_run_without_skip_git_repo_check()
    {
        var sandbox = new CliSandbox();
        var fake = new FakeCodexCli(sandbox);
        var call = new FakeExecCall(
            ["bash", "-c", CodexCliCommand.LaunchScript, "bash", FakeCodexCli.BinaryPath, "exec", "--model", "gpt-5.4", "go"],
            null,
            "/workspace",
            new Dictionary<string, string>(),
            null,
            null);

        var result = await fake.HandleAsync(call, CancellationToken.None);

        Assert.Equal((false, 1, FakeCodexCli.UntrustedDirectoryError), (result!.Success, result.ReturnCode, result.Stderr));
        Assert.Equal($"codex-cli {FakeCodexCli.Version}\n", (await fake.HandleAsync(new FakeExecCall([FakeCodexCli.BinaryPath, "--version"], null, null, null, null, null), CancellationToken.None))!.Stdout);
        Assert.Null(await fake.HandleAsync(new FakeExecCall(["uname", "-s"], null, null, null, null, null), CancellationToken.None));
    }
}
