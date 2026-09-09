using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.McpTools;

/// <summary>
/// Port of <c>examples/mcp_tools.py</c> <c>mcp_git_tools</c>: a <c>react</c> agent named <c>git_worker</c> answers a
/// git working-tree / recent-commits question with only the <c>git_log</c> and <c>git_status</c> tools of a stdio MCP
/// server (<c>python3 -m mcp_server_git --repository .</c>), with parallel tool calls disabled through the task config.
/// </summary>
public static class McpGitTools
{
    /// <summary>The task name (<c>@task def mcp_git_tools</c>).</summary>
    public const string TaskName = "mcp_git_tools";

    /// <summary>The <c>Sample(...)</c> input, verbatim (typos included).</summary>
    public const string SampleInput =
        "What is the status of the git working tree for the current directory?. Additionally, could you summarise recent commits that have been made to the reposiotry?";

    /// <summary>The <c>react(name=...)</c> value.</summary>
    public const string AgentName = "git_worker";

    /// <summary>The <c>react(prompt=...)</c> value.</summary>
    public const string Prompt = "Please use the git tools to solve the problems.";

    /// <summary>The <c>mcp_tools(git_server, tools=[...])</c> selection.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["git_log", "git_status"];

    /// <summary>The <c>mcp_server_stdio(command="python3", args=[...])</c> command line for <paramref name="repository"/> (Python: <c>"."</c>).</summary>
    public static McpServer GitServer(string repository = ".") =>
        Mcp.McpServerStdio(command: "python3", args: ["-m", "mcp_server_git", "--repository", repository]);

    /// <summary>The task as the CLI discovers it (<c>inspectai eval mcp_git_tools</c>): the stdio git server against the current directory.</summary>
    [Task(TaskName)]
    public static EvalTask McpGitToolsTask() => Build(GitServer());

    /// <summary>
    /// Builds <c>mcp_git_tools</c> over <paramref name="gitServer"/> (the stdio server in Python; the examples runner
    /// substitutes an in-process server under <c>--fake</c>). The sample, the agent's name and prompt, the tool
    /// selection and <c>GenerateConfig(parallel_tool_calls=False)</c> mirror the Python.
    /// </summary>
    public static EvalTask Build(McpServer gitServer)
    {
        ArgumentNullException.ThrowIfNull(gitServer);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(SampleInput)]),
            Solver = Agents.AsSolver(Agents.React(
                name: AgentName,
                prompt: new AgentPrompt(Instructions: Prompt),
                tools: [Mcp.McpTools(gitServer, ToolNames)])),
            Config = new GenerateConfig { ParallelToolCalls = false },
        };
    }
}
