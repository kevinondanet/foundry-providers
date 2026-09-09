using System.Text.Json;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.RemoteMcp;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/remotemcp.py</c> as an <see cref="IExample"/>: the <c>remote_mcp</c> task
/// (<see cref="RemoteMcp"/>) against either a scripted model (<c>--fake</c>) or a Foundry deployment.
/// Deviation: <c>-T execution=local</c> switches the DeepWiki server to local execution (the engine connects to it
/// over streamable HTTP and runs the tools), which any tool-calling deployment can use; the Python's
/// <c>execution="remote"</c> stays the default and needs a claude-* deployment on the Anthropic route.
/// </summary>
public sealed class RemoteMcpExample : IExample
{
    /// <summary>The scripted model's name.</summary>
    public const string FakeModelName = "remotemcp-scripted";

    /// <summary>A safety net for <c>--fake</c>: the script needs 2 turns (the MCP answer, then submit).</summary>
    public const int FakeMessageLimit = 20;

    /// <summary>The question the scripted model puts to DeepWiki's <c>ask_question</c>.</summary>
    public const string Question = "Which transport protocols does the 2025-03-26 version of the MCP specification support?";

    public string Name => "remotemcp";

    public string Description => "Remote MCP: a react agent answers an MCP-spec question with the DeepWiki MCP server executed by the model provider (execution=\"remote\")";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(RemoteMcp.TaskName, Build, "asks which transports the 2025-03-26 MCP spec supports, with mcp.deepwiki.com as a remote tool source"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "none",
        ModelHint: "a claude-* deployment on the Anthropic route (--route anthropic) for remote execution; any tool-calling deployment with -T execution=local");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Remote execution is only implemented on the Anthropic route (mcp_servers + the mcp-client-2025-04-04 beta); the Azure chat route rejects the marker with Python's \"Remote MCP execution is not supported\" error. -T execution=local (an addition) makes the engine connect to mcp.deepwiki.com itself so any tool-calling deployment can run the task.",
        "Under --fake the scripted model answers the remote server's marker tool with an mcp_call content block (what the provider returns for a server-side tool call) and then submits; with -T execution=local an in-process MCP server (FakeDeepWikiServer) with DeepWiki's three tools stands in for the network and the model calls ask_question as an ordinary tool.",
        "A message limit of 20 guards the fake run.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) =>
        new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), 8), FakeModelName) { SupportsRemoteMcp = true });

    /// <summary>No sandbox: the MCP server is remote (or in this process).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>Parses <c>-T execution=remote|local</c> (default remote, as in Python).</summary>
    public static McpExecution ParseExecution(string? value) => (value ?? "remote").ToLowerInvariant() switch
    {
        "remote" => McpExecution.Remote,
        "local" => McpExecution.Local,
        _ => throw new ArgumentException($"-T execution expects remote or local, got '{value}'"),
    };

    private static EvalTask Build(ExampleContext ctx)
    {
        var execution = ParseExecution(ctx.TaskArg("execution"));
        var server = ctx.Fake && execution == McpExecution.Local ? FakeDeepWikiServer.Create() : RemoteMcp.DeepWiki(execution);
        var task = RemoteMcp.Build(server);
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }

    /// <summary>
    /// The scripted model: on its first turn it either calls <c>ask_question</c> (local execution: the tool is in the
    /// list) or, seeing only the <c>mcp_server_deepwiki</c> marker, answers with an <c>mcp_call</c> content block plus
    /// the text, as the provider does when it ran the tool itself; on the next turn it submits the answer.
    /// </summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        if (step == 0)
        {
            if (tools.Any(tool => tool.Name == "ask_question"))
            {
                return ScriptedTurn.ToolCall("ask_question", new { repoName = FakeDeepWikiServer.SpecRepo, question = Question }, text: "Let me ask DeepWiki about the specification.").Output! with { Model = FakeModelName };
            }

            var arguments = JsonSerializer.Serialize(new { repoName = FakeDeepWikiServer.SpecRepo, question = Question });
            var message = new ChatMessageAssistant(
                new Content[]
                {
                    new ContentToolUse("mcp_call", ShortUuid.Generate(), "ask_question", arguments, FakeDeepWikiServer.TransportsAnswer) { Context = RemoteMcp.ServerName },
                    new ContentText(FakeDeepWikiServer.TransportsAnswer),
                },
                model: FakeModelName,
                source: "generate");
            return new ModelOutput { Model = FakeModelName, Choices = [new ChatCompletionChoice(message, StopReason.Stop)] };
        }

        var answer = messages.OfType<ChatMessageTool>().FirstOrDefault(message => message.Function == "ask_question")?.Text
            ?? messages.OfType<ChatMessageAssistant>().SelectMany(message => (message.Content.Items ?? []).OfType<ContentToolUse>()).FirstOrDefault()?.Result
            ?? FakeDeepWikiServer.TransportsAnswer;
        return ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer }, text: "Submitting the answer.").Output! with { Model = FakeModelName };
    }
}
