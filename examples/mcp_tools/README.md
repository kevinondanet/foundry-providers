# MCP Tools

A C# port of `examples/mcp_tools.py` of the inspect_ai repository. A `react` agent named `git_worker` is asked for the status of the git working tree and a summary of recent commits, and may only use the `git_log` and `git_status` tools of an MCP server started over stdio (`python3 -m mcp_server_git --repository .`); parallel tool calls are disabled through the task's `GenerateConfig`.

What it demonstrates:

- `Mcp.McpServerStdio` (`mcp_server_stdio`): an MCP server run as a child process and reached over stdin/stdout.
- `Mcp.McpTools(server, ["git_log", "git_status"])` (`mcp_tools`): a tool source that exposes only a named subset of the server's tools.
- `Agents.React(tools: [source])`: the react agent resolves tool sources itself and holds the MCP connection for the whole loop, as Python's `react` does.
- `EvalTask.Config = new GenerateConfig { ParallelToolCalls = false }`.

## Running it

### Offline

```bash
dotnet run --project examples -- mcp_tools --fake --sandbox none
```

A scripted model calls `git_status`, then `git_log`, then submits a summary of both results. The stdio server is replaced by an in-process MCP server (`FakeGitServer`, hosted by `InProcessMcpServer` on the MCP C# SDK) with canned answers, so no python process or network is involved; the engine still talks MCP to it through `McpServerLocal`. Add `--display conversation` to watch the tool calls.

### Against a Foundry deployment

```bash
pip install mcp-server-git      # the stdio server the Python example uses
dotnet run --project examples -- mcp_tools --model <deployment>
```

Requirements: `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), a tool-calling deployment, `python3` with `mcp-server-git` importable, and `git` on PATH. The server is started with `--repository .`, the current directory of the run, as in Python; `-T repository=<path>` points it elsewhere.

### The `inspectai` CLI

The task is marked `[Task("mcp_git_tools")]`, so the CLI can discover it in the built assembly, as `inspect eval mcp_tools.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval mcp_git_tools \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("mcp_git_tools")]
public static EvalTask McpGitToolsTask() => Build(GitServer());

public static McpServer GitServer(string repository = ".") =>
    Mcp.McpServerStdio(command: "python3", args: ["-m", "mcp_server_git", "--repository", repository]);

public static EvalTask Build(McpServer gitServer) => new()
{
    Name = "mcp_git_tools",
    Dataset = new MemoryDataset(
    [
        new Sample("What is the status of the git working tree for the current directory?. Additionally, could you summarise recent commits that have been made to the reposiotry?"),
    ]),
    Solver = Agents.AsSolver(Agents.React(
        name: "git_worker",
        prompt: new AgentPrompt(Instructions: "Please use the git tools to solve the problems."),
        tools: [Mcp.McpTools(gitServer, ["git_log", "git_status"])])),
    Config = new GenerateConfig { ParallelToolCalls = false },
};
```

## Deviations from Python

- Under `--fake` the stdio server (`python3 -m mcp_server_git --repository .`) is replaced by an in-process MCP server (`FakeGitServer` over `InProcessMcpServer`) with canned `git_status` / `git_log` answers; the engine still speaks MCP to it through `McpServerLocal`, exactly as it would to the child process.
- `-T repository=<path>` sets the `--repository` argument of the stdio server (the Python is fixed to `"."`, which stays the default).
- The scripted model calls `git_status`, then `git_log`, then submits a summary; a message limit of 20 guards the fake run.
