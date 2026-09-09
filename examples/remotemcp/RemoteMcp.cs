using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Mcp;

namespace InspectAzureAI.Examples.RemoteMcp;

/// <summary>
/// Port of <c>examples/remotemcp.py</c> <c>remote_mcp</c>: a <c>react</c> agent answers a question about the
/// 2025-03-26 MCP specification with the public DeepWiki MCP server (<c>https://mcp.deepwiki.com/mcp</c>) passed
/// directly as a tool source with <c>execution="remote"</c>, so the model provider connects to the server and runs
/// its tools itself (the Anthropic route's MCP connector).
/// </summary>
public static class RemoteMcp
{
    /// <summary>The task name (<c>@task def remote_mcp</c>).</summary>
    public const string TaskName = "remote_mcp";

    /// <summary>The <c>Sample(input=...)</c>, verbatim.</summary>
    public const string SampleInput = "What transport protocols are supported in the 2025-03-26 version of the MCP spec?";

    /// <summary>The <c>mcp_server_http(name=...)</c> value.</summary>
    public const string ServerName = "deepwiki";

    /// <summary>The <c>mcp_server_http(url=...)</c> value.</summary>
    public const string ServerUrl = "https://mcp.deepwiki.com/mcp";

    /// <summary>The DeepWiki server (<c>mcp_server_http(name="deepwiki", url=..., execution="remote")</c>); <paramref name="execution"/> Local makes the engine connect and execute the tools itself.</summary>
    public static McpServer DeepWiki(McpExecution execution = McpExecution.Remote) =>
        Mcp.McpServerHttp(name: ServerName, url: ServerUrl, execution: execution);

    /// <summary>The task as the CLI discovers it (<c>inspectai eval remote_mcp</c>): DeepWiki with remote execution.</summary>
    [Task(TaskName)]
    public static EvalTask RemoteMcpTask() => Build(DeepWiki());

    /// <summary>Builds <c>remote_mcp</c> over <paramref name="deepwiki"/>: one sample and <c>react(tools=[deepwiki])</c>, the server itself being the tool source.</summary>
    public static EvalTask Build(McpServer deepwiki)
    {
        ArgumentNullException.ThrowIfNull(deepwiki);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(SampleInput)]),
            Solver = Agents.AsSolver(Agents.React(tools: [deepwiki])),
        };
    }
}
