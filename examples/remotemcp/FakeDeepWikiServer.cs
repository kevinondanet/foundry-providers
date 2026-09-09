using System.ComponentModel;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Examples.Runner;
using SdkMcpServerTool = ModelContextProtocol.Server.McpServerTool;
using SdkMcpServerToolCreateOptions = ModelContextProtocol.Server.McpServerToolCreateOptions;

namespace InspectAzureAI.Examples.RemoteMcp;

/// <summary>
/// The <c>--fake -T execution=local</c> stand-in for <c>https://mcp.deepwiki.com/mcp</c>: an
/// <see cref="InProcessMcpServer"/> with DeepWiki's three tools (<c>read_wiki_structure</c>, <c>read_wiki_contents</c>,
/// <c>ask_question</c>; <c>repoName</c> / <c>question</c> parameters) and canned answers about the MCP specification
/// repository. Under remote execution nothing stands in for the server: the scripted model answers with an
/// <c>mcp_call</c> content block, as the provider would.
/// </summary>
public static class FakeDeepWikiServer
{
    /// <summary>The repository the scripted model asks about.</summary>
    public const string SpecRepo = "modelcontextprotocol/modelcontextprotocol";

    /// <summary>What <c>ask_question</c> answers about the 2025-03-26 transports.</summary>
    public const string TransportsAnswer =
        "The 2025-03-26 revision of the MCP specification defines two standard transports: stdio and Streamable HTTP. "
        + "Streamable HTTP replaces the HTTP+SSE transport of the 2024-11-05 revision (servers may keep the old SSE endpoint for backwards compatibility), "
        + "and custom transports are permitted as long as they carry JSON-RPC messages.";

    /// <summary>What <c>read_wiki_structure</c> answers.</summary>
    public const string Structure = "1. Overview\n2. Architecture\n3. Base Protocol\n4. Transports\n5. Server Features\n6. Client Features\n7. Schema Reference";

    /// <summary>A fresh in-process transport; wrap it with <see cref="InProcessMcpServer.AsMcpServer"/> for the task.</summary>
    public static InProcessMcpServer CreateTransport() => new(RemoteMcp.ServerName, Tools);

    /// <summary>The engine-side server the task takes in place of <see cref="RemoteMcp.DeepWiki"/> with local execution.</summary>
    public static McpServerLocal Create() => CreateTransport().AsMcpServer();

    private static IEnumerable<SdkMcpServerTool> Tools()
    {
        yield return SdkMcpServerTool.Create(
            ([Description("GitHub repository: owner/repo (e.g. \"facebook/react\")")] string repoName) => Structure,
            new SdkMcpServerToolCreateOptions { Name = "read_wiki_structure", Description = "Get a list of documentation topics for a GitHub repository" });
        yield return SdkMcpServerTool.Create(
            ([Description("GitHub repository: owner/repo (e.g. \"facebook/react\")")] string repoName) => $"# {repoName}\n\n## Transports\n\n{TransportsAnswer}",
            new SdkMcpServerToolCreateOptions { Name = "read_wiki_contents", Description = "View documentation about a GitHub repository" });
        yield return SdkMcpServerTool.Create(
            ([Description("GitHub repository: owner/repo (e.g. \"facebook/react\")")] string repoName, [Description("The question to ask about the repository")] string question) => TransportsAnswer,
            new SdkMcpServerToolCreateOptions { Name = "ask_question", Description = "Ask any question about a GitHub repository" });
    }
}
