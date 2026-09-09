using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.McpTools;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/mcp_tools.py</c> as an <see cref="IExample"/>: the <c>mcp_git_tools</c> task
/// (<see cref="McpGitTools"/>) against either a scripted model and the in-process <see cref="FakeGitServer"/>
/// (<c>--fake</c>) or a Foundry deployment and the real <c>python3 -m mcp_server_git</c> stdio server.
/// Deviation: <c>-T repository=&lt;path&gt;</c> replaces the Python's fixed <c>--repository .</c> for live runs
/// started from another directory.
/// </summary>
public sealed class McpToolsExample : IExample
{
    /// <summary>The scripted model's name.</summary>
    public const string FakeModelName = "mcp-tools-scripted";

    /// <summary>A safety net for <c>--fake</c>: the script needs 3 turns (status, log, submit).</summary>
    public const int FakeMessageLimit = 20;

    public string Name => "mcp_tools";

    public string Description => "MCP tools: a react agent (git_worker) answers a git status / recent commits question with the git_log and git_status tools of a stdio MCP server";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(McpGitTools.TaskName, Build, "asks git_worker for the working tree status and a summary of recent commits, via mcp_server_git"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "none", ModelHint: "a tool-calling deployment; live runs need python3 with mcp-server-git installed (pip install mcp-server-git) and git on PATH");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Under --fake the stdio server (python3 -m mcp_server_git --repository .) is replaced by an in-process MCP server (FakeGitServer over InProcessMcpServer) with canned git_status / git_log answers; the engine still speaks MCP to it through McpServerLocal, exactly as it would to the child process.",
        "-T repository=<path> sets the --repository argument of the stdio server (the Python is fixed to \".\", which stays the default).",
        "The scripted model calls git_status, then git_log, then submits a summary; a message limit of 20 guards the fake run.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), 8), FakeModelName));

    /// <summary>No sandbox: the MCP server runs on the host (or in this process).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx)
    {
        var server = ctx.Fake ? FakeGitServer.Create() : McpGitTools.GitServer(ctx.TaskArg("repository", ".")!);
        var task = McpGitTools.Build(server);
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }

    /// <summary>The scripted <c>git_worker</c>: <c>git_status</c>, then <c>git_log</c>, then a submitted summary built from the two tool results.</summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        var output = step switch
        {
            0 => ScriptedTurn.ToolCall("git_status", new { repo_path = "." }, text: "First the working tree status.").Output!,
            1 => ScriptedTurn.ToolCall("git_log", new { repo_path = ".", max_count = 10 }, text: "Now the recent commits.").Output!,
            _ => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = Summary(messages) }, text: "That answers both questions.").Output!,
        };
        return output with { Model = FakeModelName };
    }

    private static string Summary(IReadOnlyList<ChatMessage> messages)
    {
        var results = messages.OfType<ChatMessageTool>().ToList();
        var status = results.FirstOrDefault(message => message.Function == "git_status")?.Text.Trim() ?? "(no git_status result)";
        var log = results.FirstOrDefault(message => message.Function == "git_log")?.Text ?? "";
        var commits = log.Split('\n').Where(line => line.StartsWith("Message: ", StringComparison.Ordinal)).Select(line => line["Message: ".Length..]).ToList();
        var recent = commits.Count == 0 ? "no commits were reported" : $"the {commits.Count} most recent commits are: {string.Join("; ", commits)}";
        return $"Working tree: {status.Replace("Repository status:\n", "", StringComparison.Ordinal).Replace('\n', ' ')}. Recent commits: {recent}.";
    }
}
