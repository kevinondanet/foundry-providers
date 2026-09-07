# Port: react agent, handoff, as_tool, run, message filters

Source: `inspect_ai/agent/_react.py`, `_types.py`, `_handoff.py`, `_as_tool.py`, `_filter.py`, `_run.py`,
`_agent.py` (`agent_with`, `is_agent`), the `agent_handoff` / `prepend_agent_name` half of
`model/_call_tools.py`, `model/_trim.py` (`trim_messages`, `partition_messages`), `scorer/_score.py`
(`score(AgentState)`) and the `apply_limits` / `message_limit` / `token_limit` / `time_limit` scoping of
`util/_limit.py`. Tests: `tests/agent/test_agent_react.py`, `test_agent_handoff.py`, `test_agent_execute.py`,
`test_agent_content_only.py`, `test_agent_name.py`.

Target: `src/InspectAzureAI.Eval/Agents/` (new files `AgentTypes.cs`, `AgentCompaction.cs`, `MessageFilters.cs`,
`AgentLimits.cs`, `React.cs`, `Handoff.cs`, `AsTool.cs`, `Run.cs`; `Agents` became a partial class).
`Solvers.BasicAgent` is untouched.

## Public API

| Python | C# |
|---|---|
| `react(name, description, prompt, tools, model, attempts, submit, on_continue, retry_refusals, compaction, truncation)` | `Agents.React(name, description, prompt, tools, model, modelAgent, attempts, submit, onContinue, onContinueFn, retryRefusals, compaction, truncation)` → `AgentDef` |
| `AgentPrompt`, `DEFAULT_*_PROMPT`, `PARALLEL_TOOLS_PROMPT` | `AgentPrompt` record + constants; `AgentPrompt.Default`, `AgentPrompt.None` (Python `prompt=None`), implicit from `string` |
| `AgentSubmit` (`submit=False`) | `AgentSubmit` record (`AgentSubmit.Disabled`) |
| `AgentAttempts.incorrect_message` callable | `AgentAttempts.IncorrectMessageFn` (`AgentIncorrectMessage` delegate) |
| `AgentContinue` returning `bool \| str \| AgentState` | `AgentContinue` delegate returning `AgentContinueResult` (implicit from `bool`, `string`, `AgentState`) |
| `handoff(agent, description, input_filter, output_filter, tool_name, limits)` | `Agents.Handoff(...)` → `ToolDef` with `ToolDef.Handoff` (`AgentHandoff`); `Agents.HasHandoff` |
| `as_tool(agent, description, limits, max_output)` | `Agents.AsTool(...)` → `ToolDef` |
| `run(agent, input, limits, name)` | `Agents.RunAsync(agent, input, limits, name, ct)` → `AgentRunResult(State, LimitError)` |
| `agent_with`, `is_agent`, `agent_display_name` | `Agents.AgentWith`, `Agents.IsAgent`, `AgentDef.Name` |
| `score(AgentState)` | `Agents.ScoreAsync(AgentState, ct)` |
| `content_only`, `remove_tools`, `last_message`, `MessageFilter` | `MessageFilters.ContentOnly/RemoveTools/LastMessage/Identity`, `MessageFilter` delegate |
| `trim_messages`, `partition_messages` | `MessageFilters.TrimMessages` (filter) / `TrimMessagesTo`, `PartitionMessages` |
| `apply_limits([message_limit, token_limit, time_limit])` | `AgentLimits` record + `AgentLimitScope.Apply` (ambient AsyncLocal stack) |
| `sanitize_tool_name`, `agent_tool_name` | `Agents.SanitizeToolName`, `Agents.AgentToolName` |

Hooks into existing code (all additive): `AgentLimitScope` enters `TokenLimit`/`MessageLimit`/`TimeLimit` nodes on the
shared `Context` limit trees, which `Model.GenerateAsync` checks after the sample-level checks; `ToolExecutor` recognises `ToolDef.Handoff`, wraps the
tool span in a `handoff` span, appends the agent's messages after the tool message, returns the agent's output
as `ExecuteToolsResult.Output` and stamps `ToolEvent.Agent` (written/read as `agent` in the log JSON);
`LimitExceededException.SourceLimit` identifies the limit that raised (Python `source`); `AgentLimitScope.Owns` tests it.

## Compaction seam

Another port supplies compaction. `React(compaction: CreateAgentCompaction)` takes a factory
`(prefix, tools, model) => AgentCompaction`, mirroring `compaction(strategy, prefix=system+input, tools, model)`.
`AgentCompaction(CompactInput, RecordOutput)` is the `Compact` protocol surface react uses:
`CompactInput(messages, force, ct) → CompactedInput(Messages, Message)` before every generate (`force: false`)
and during overflow recovery (`force: true`); `RecordOutput(input, output, ct)` after each generate. A notice
message returned from `CompactInput` is appended to the conversation exactly as Python does.

## Deviations

- `prompt`: null means the default prompt (Python's default is `AgentPrompt()`); `AgentPrompt.None` is Python's
  `None`, compared by reference. `submit`: null is the default, `AgentSubmit.Disabled` is `False`.
- `on_continue` is two parameters (`onContinue` string, `onContinueFn` callable); `model` is two (`model`,
  `modelAgent`). Passing both is an `ArgumentException`. A model name string is not accepted (no model registry).
- `AgentSubmit.Name/Description` override a caller-supplied `ToolDef` too (Python ignores them for a `ToolDef`
  and applies them only to a plain `Tool`).
- `Handoff(outputFilter: null)` keeps the `ContentOnly` default; use `MessageFilters.Identity` for Python's `None`.
- Agents take no parameters, so `handoff()`/`as_tool()` expose none, curried `**agent_kwargs` do not exist and the
  handoff tool's schema is empty; use closures / options records (as `MiniSwe.Agent` does).
- `AgentWith` returns a copy (records) instead of mutating in place. `RunAsync` always returns `AgentRunResult`.
- Scoped limits: `AgentLimits` (message, token, time) replaces the list of `Limit` objects; working/cost limits and
  token-limit formulas are not ported. Python's `check_message_limit` checks only the innermost message limit and
  skips the sample's; here the sample `Limits` are always checked as well (stricter). `ChatMessageList`'s
  append-time check is not ported; limits are checked at each generate. No `SampleLimitEvent` is recorded.
- `content_only`: no `internal` attribute or `ContentToolUse` in this content model, so only reasoning removal,
  tool-call rendering and tool→user conversion apply. `trim_messages` skips `strip_citations` (no citations).
- `ToolEvent.agent_span_id`, `approval`, checkpoints (`checkpointer`), the operator channel (`agent_channel`,
  `AgentInterrupted`) and MCP tool sources are not ported. The compaction-failure warning goes to
  `ProviderLogger.Warning` (Python: `logger.warning`).

## Not ported

`react()`'s `approval` parameter, checkpoint/resume, the ACP/operator channel, `ToolSource`/MCP tools,
`registry` identity (`as_solver_spec` replay), `working_limit`/`cost_limit`, `ContentToolUse` rendering.
