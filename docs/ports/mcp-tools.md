# Port: Model Context Protocol tool sources

Port of `inspect_ai/tool/_mcp/` (`server.py`, `tools.py`, `connection.py`, `_types.py`, `_config.py`,
`_local.py`, `_remote.py`, `_sandbox.py`, the content helpers of `sampling.py`, and the pieces of
`_util/_json_rpc.py` / `_sandbox_tools_utils/_error_mapper.py` the sandbox transport needs) into
`src/InspectAzureAI.Eval/Tools/Mcp/`, on top of the official C# SDK (`ModelContextProtocol` 2.2.0).
User docs: `docs/tools-mcp.qmd`.

## Public API (namespace `InspectAzureAI.Eval.Tools.Mcp`)

| C# | Python |
|---|---|
| `Mcp.McpServerStdio(command, args, name, cwd, env)` | `mcp_server_stdio()` |
| `Mcp.McpServerHttp(url, name, execution, authorization, headers, timeout, sseReadTimeout)` | `mcp_server_http()` |
| `Mcp.McpServerSse(...)` (same arguments) | `mcp_server_sse()` |
| `Mcp.McpServerSandbox(command, args, name, cwd, env, sandbox, timeout)` | `mcp_server_sandbox()` |
| `Mcp.McpTools(server, tools)` (`tools` null = `"all"`, else names/globs) | `mcp_tools()` |
| `await using var c = await McpConnection.ConnectAsync(tools)` | `async with mcp_connection(tools)` |
| `IToolSource.ToolsAsync()` | `ToolSource.tools()` |
| `McpServer` (abstract: `ToolsAsync`, `EnterAsync`, `ExitAsync`) | `MCPServer` (`tools`, `__aenter__`, `__aexit__`) |
| `McpServerLocal`, `McpServerLocalSession`, `McpServerRemote`, `McpToolSourceLocal` | same names |
| `McpServerConfig`, `McpServerConfigStdio`, `McpServerConfigHttp` (`ToJson()` = `model_dump()`) | `MCPServerConfig*` |
| `McpContent.AsInspectContentList / AsInspectContent / AsMcpContent / ToolResultAsText` | `sampling.py`, `tool_result_as_text` |
| `McpSandboxClientTransport`, `IMcpSandboxRpc`, `McpSandboxExecRpc`, `McpStdioServerParameters` | `sandbox_client`, `SandboxJSONRPCTransport` |
| `McpExecution.Local / Remote` | `execution="local" / "remote"` |

Tools resolve to ordinary `ToolDef`s: the server's `inputSchema` passes through `BridgeJson.ToolParamsFromSchema`
(the existing port of `ToolParams.model_validate`) and a parameter without a description gets its name, as
in Python. Results are `ToolResult.Contents` (text, image data URI, audio, resource link/embedded text);
`isError` results raise `ToolError` with the joined text; JSON-RPC errors map as Python's `_McpErrorMapper`
(server codes and -32603 to `ToolError`, -32602 to `ToolParsingError`, request-oriented codes to
`InvalidOperationException`); a call exceeding the server `timeout` raises
`ToolError("Tool '<name>' timed out before completing.")`.

The sandbox transport implements the SDK's `IClientTransport`/`ITransport` over the same carrier protocol as
Python: `mcp_launch_server` on connect, `mcp_send_request` / `mcp_send_notification` per message (a carrier
failure becomes a JSON-RPC -32603 error on the request id, "MCP request timed out before completing." for
timeouts; a failed notification is logged and dropped), and a best-effort, 30 s bounded, never-throwing
`mcp_kill_server` on dispose. `McpSandboxExecRpc` runs `<cli> exec` with the request on stdin through
`ISandboxEnvironment.ExecAsync`, defaulting the CLI to Python's injected path
(`/var/tmp/.da7be258e003d428/inspect-sandbox-tools`).

## Deviations from Python

- **Sessions are per async flow, not per task.** Python keys `MCPServerLocalSession` by the running task in a
  weak dictionary. .NET has no task identity that survives awaits, so `McpServerLocal` holds the session in an
  `AsyncLocal`. Consequences: a flow started after its parent created a session shares it (parallel tool
  stages, as intended); a flow that creates one first keeps it private (Python's isolation); the public
  entry points (`McpServerLocal.*`, `McpConnection.ConnectAsync`, `McpToolSourceLocal.ToolsAsync`) resolve the
  session synchronously before their first await so it lands in the caller's flow. A subagent running as a child
  flow after the parent connected inherits the parent's connection where Python would give it a fresh one.
- **Sampling is not wired.** `sampling_fn` is not ported: SDK 2.2.0 marks the whole sampling API `[Obsolete]`
  (deprecated by the 2026-07-28 spec, SEP-2577) and the build treats warnings as errors with no suppressions.
  `events` is kept as `McpServerLocal.Events` for argument parity only. `McpContent.AsMcpContent` (the reply
  half of the mapping) is ported and tested.
- **No `ToolSource` plumbing in solvers.** `TaskState.Tools` is a `List<ToolDef>`; callers resolve
  `IToolSource.ToolsAsync()` inside an `McpConnection` and pass the `ToolDef`s on (Python's `react()` does this
  implicitly).
- **Sandbox tools injection is not part of this area.** `McpServerSandbox` resolves the ambient sandbox and
  assumes the CLI is present at the default path (the `sandbox-tools` port owns injection; its transport can be
  adapted to `IMcpSandboxRpc` in a few lines). The chunked-response continuation for oversized carrier
  responses is not ported.
- **Concurrency guard.** `McpServerLocalSession` serializes enter/exit with a `SemaphoreSlim` because child
  flows can share a session; Python relies on per-task exclusivity.
- **HTTP timeouts.** `timeout` maps to `HttpClientTransportOptions.ConnectionTimeout`; the SDK has no SSE idle
  read timeout, so `sseReadTimeout` is applied as the `HttpClient` timeout (best effort).
- **Stdio environment** mirrors mcp's default set plus `env` (`InheritEnvironmentVariables = false`); stderr
  lines go to `ProviderLogger.Info` as `[mcp:<name>] ...` rather than a named Python logger.
- Minor: a `ResourceLink` without a description renders its name (Python prints `None`); `fnmatch` is
  case-sensitive on every platform (Python folds case only on Windows); `rpc_call_description` prints
  containers as JSON rather than Python repr; `execution` is an enum, not a string literal; timeouts are
  `TimeSpan`s.

## Not ported

`_compat.py` (mcp 1.x/2.x shim; the SDK is a single version), `sampling.py`'s `sampling_fn`, the
`_tools_bridge` package (the MCP tools bridge for sandboxed agents), and `verfify_mcp_package` (a NuGet
dependency replaces the runtime check).

## Tests

`tests/InspectAzureAI.Eval.Tests/McpToolsTests.cs` (50 cases) runs against in-process SDK servers joined by
`System.IO.Pipelines` pipes and, for the sandbox path, a fake `ISandboxEnvironment` that answers `<cli> exec`
by relaying to an in-process server: listing and schema passthrough, glob filtering, invocation, `isError` and
JSON-RPC error results, image content, connection reuse across calls and nested scopes, disposal after a
cancelled call, per-call timeout, failed-connect recovery, per-flow session isolation, `McpConnection`
discovery and rollback, the carrier wire format (launch params, session id relay, kill) and its failure modes,
config dumps and `fnmatch` cases cross-checked against the venv Python. `stdio_server_round_trip` is opt-in
via `INSPECT_MCP_STDIO_SERVER=<command line>`; it passed against
`tests/tools/mcp_test_server.py` from the inspect_ai venv.
