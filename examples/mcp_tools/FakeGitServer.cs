using System.ComponentModel;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Examples.Runner;
using SdkMcpServerTool = ModelContextProtocol.Server.McpServerTool;
using SdkMcpServerToolCreateOptions = ModelContextProtocol.Server.McpServerToolCreateOptions;

namespace InspectAzureAI.Examples.McpTools;

/// <summary>
/// The <c>--fake</c> stand-in for <c>python3 -m mcp_server_git --repository .</c>: an <see cref="InProcessMcpServer"/>
/// exposing the git server's <c>git_status</c>, <c>git_log</c> and <c>git_diff_unstaged</c> tools (names, parameters and
/// the <c>Repository status:</c> / <c>Commit history:</c> result prefixes of <c>mcp_server_git</c>) with canned
/// answers. The third tool is there so the <c>mcp_tools(..., tools=["git_log", "git_status"])</c> filter has something
/// to leave out.
/// </summary>
public static class FakeGitServer
{
    /// <summary>The server's name (the runner's banner and the log's tool events show it).</summary>
    public const string Name = "git";

    /// <summary>What <c>git_status</c> answers.</summary>
    public const string Status = "Repository status:\nOn branch main\nnothing to commit, working tree clean";

    /// <summary>What <c>git_log</c> answers (three commits, newest first).</summary>
    public const string Log =
        "Commit history:\n"
        + "Commit: 3f2a1c9\nAuthor: Example Author\nDate: 2026-09-07 10:15:00+00:00\nMessage: feat(layers-demo): add the expenses task\n\n"
        + "Commit: 7c1d4bb\nAuthor: Example Author\nDate: 2026-09-06 18:40:00+00:00\nMessage: feat: add inspect layers and MAF integration\n\n"
        + "Commit: ff58b49\nAuthor: Example Author\nDate: 2026-09-05 09:05:00+00:00\nMessage: docs: add the Inspect components guide\n";

    /// <summary>What <c>git_diff_unstaged</c> answers (never called: the task filters it out).</summary>
    public const string DiffUnstaged = "Unstaged changes:\n";

    /// <summary>A fresh in-process transport; wrap it with <see cref="InProcessMcpServer.AsMcpServer"/> for the task.</summary>
    public static InProcessMcpServer CreateTransport() => new(Name, Tools);

    /// <summary>The engine-side server the task takes in place of <see cref="McpGitTools.GitServer"/>.</summary>
    public static McpServerLocal Create() => CreateTransport().AsMcpServer();

    private static IEnumerable<SdkMcpServerTool> Tools()
    {
        yield return SdkMcpServerTool.Create(
            ([Description("Path to the git repository")] string repo_path) => Status,
            new SdkMcpServerToolCreateOptions { Name = "git_status", Description = "Shows the working tree status" });
        yield return SdkMcpServerTool.Create(
            ([Description("Path to the git repository")] string repo_path, [Description("Maximum number of commits to show")] int max_count = 10) => Log,
            new SdkMcpServerToolCreateOptions { Name = "git_log", Description = "Shows the commit logs" });
        yield return SdkMcpServerTool.Create(
            ([Description("Path to the git repository")] string repo_path) => DiffUnstaged,
            new SdkMcpServerToolCreateOptions { Name = "git_diff_unstaged", Description = "Shows changes in the working directory that are not yet staged" });
    }
}
