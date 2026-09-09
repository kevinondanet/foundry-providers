# Remote MCP

A C# port of `examples/remotemcp.py` of the inspect_ai repository. A `react` agent is asked which transport protocols the 2025-03-26 version of the MCP specification supports, with the public DeepWiki MCP server (`https://mcp.deepwiki.com/mcp`) passed directly as a tool source with `execution="remote"`: the model provider connects to the server and runs its tools itself, and the tool calls come back inside the assistant message as `mcp_call` content blocks rather than as tool calls for the engine to execute.

What it demonstrates:

- `Mcp.McpServerHttp(name:, url:, execution: McpExecution.Remote)` (`mcp_server_http(..., execution="remote")`): a server the provider executes; the engine only sends a marker tool (`mcp_server_deepwiki`) carrying the server config.
- An `McpServer` used directly as a tool source in `Agents.React(tools: [deepwiki])`.
- Provider-side execution on the Anthropic route (`mcp_servers` with the `mcp-client-2025-04-04` beta), with the `mcp_tool_use` / `mcp_tool_result` pairs recorded as `ContentToolUse(ToolType = "mcp_call")` in the log.

## Running it

### Offline

```bash
dotnet run --project examples -- remotemcp --fake --sandbox none
```

The scripted model (declared as supporting remote MCP) sees the `mcp_server_deepwiki` marker and answers with an `mcp_call` content block plus the answer text, as a provider that ran the tool would; the react loop then asks it to proceed and it submits. Nothing is contacted.

```bash
dotnet run --project examples -- remotemcp --fake --sandbox none -T execution=local
```

With local execution the engine connects to the server itself; offline, an in-process MCP server (`FakeDeepWikiServer`, with DeepWiki's `read_wiki_structure`, `read_wiki_contents` and `ask_question` tools) stands in for the network and the scripted model calls `ask_question` as an ordinary tool. Add `--display conversation` to watch either variant.

### Against a Foundry deployment

Remote execution, as in Python, needs a claude-* deployment on the Anthropic route:

```bash
dotnet run --project examples -- remotemcp --model <claude deployment> --route anthropic
```

Any tool-calling deployment can run the local variant, where this process connects to `mcp.deepwiki.com` over streamable HTTP and executes the tools:

```bash
dotnet run --project examples -- remotemcp --model <deployment> -T execution=local
```

Requirements: `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), and outbound network access to `https://mcp.deepwiki.com/mcp` (public, no key). The Azure chat route rejects the remote marker with `Remote MCP execution is not supported for <model>. Please use "local" execution instead.`, exactly as Python does for providers without a connector. Whether Foundry's Anthropic route accepts the `mcp_servers` beta was not verified live.

### The `inspectai` CLI

The task is marked `[Task("remote_mcp")]`, so the CLI can discover it in the built assembly:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval remote_mcp \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model anthropic/<claude deployment>
```

## The task

```csharp
[Task("remote_mcp")]
public static EvalTask RemoteMcpTask() => Build(DeepWiki());

public static McpServer DeepWiki(McpExecution execution = McpExecution.Remote) =>
    Mcp.McpServerHttp(name: "deepwiki", url: "https://mcp.deepwiki.com/mcp", execution: execution);

public static EvalTask Build(McpServer deepwiki) => new()
{
    Name = "remote_mcp",
    Dataset = new MemoryDataset(
    [
        new Sample("What transport protocols are supported in the 2025-03-26 version of the MCP spec?"),
    ]),
    Solver = Agents.AsSolver(Agents.React(tools: [deepwiki])),
};
```

## Deviations from Python

- Remote execution is only implemented on the Anthropic route (`mcp_servers` + the `mcp-client-2025-04-04` beta); the Azure chat route rejects the marker with Python's "Remote MCP execution is not supported" error. `-T execution=local` (an addition) makes the engine connect to `mcp.deepwiki.com` itself so any tool-calling deployment can run the task.
- Under `--fake` the scripted model answers the remote server's marker tool with an `mcp_call` content block (what the provider returns for a server-side tool call) and then submits; with `-T execution=local` an in-process MCP server (`FakeDeepWikiServer`) with DeepWiki's three tools stands in for the network and the model calls `ask_question` as an ordinary tool.
- A message limit of 20 guards the fake run.
