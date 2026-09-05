# Port: tool call approval policies (including the agent bridge path)

Source: `inspect_ai/approval/_approval.py`, `_approver.py`, `_apply.py`, `_call.py`, `_policy.py`, `_auto.py`,
`_registry.py`, `_human/approver.py`, `_human/console.py`, `_human/util.py` (console path only),
`tool/_tool_call.py` (`ToolCallViewer`, `substitute_tool_call_content`), `tool/_tool.py` (`ToolApprovalError`),
`event/_approval.py`, `_util/exception.py` (`TerminateSampleError`), the approval sites of
`model/_call_tools.py` (`execute_tools(approval=)`, `call_tool`), `agent/_react.py` (`approval=`),
`agent/_bridge/_approval.py`, `agent/_bridge/util.py` (`bridge_generate`), `agent/_bridge/types.py`
(`AgentBridge.approval`, `request_terminate`), `agent/_bridge/sandbox/{types,bridge}.py` (terminate signalling),
`_eval/eval.py` / `_eval/run.py` / `_eval/task/run.py` (resolution, `init_tool_approval`, config recording, the
`operator` sample limit). Tests: `tests/approval/test_approval.py` (+ its YAML fixtures as JSON),
`tests/agent/test_bridge_approval.py`.

Target: `src/InspectAzureAI.Eval/Approval/` (new). Additive edits: `Tools/ToolDef.cs` (`Viewer`),
`Tools/ToolExecutor.cs` (`approval` parameter, the gate in `RunOneAsync`, the `approval` error mapping),
`Agents/React.cs` (`approval` parameter), `Agents/Bridge/AgentBridge.cs` (`approval` ctor parameter, `Approval`,
`RequestTerminate`, the approve/replay loop in `GenerateAsync`), `Agents/Bridge/SandboxAgentBridge.cs`
(`TerminateError`, `TerminateRequested`), `Runner/EvalOptions.cs` and `Tasks/EvalTask.cs` (`Approval`),
`Runner/Eval.cs` (resolution, `EvalConfig.Approval`, the ambient scope), `Runner/SampleRunner.cs` (the `operator`
limit), `InspectAzureAI.Swe/ClaudeCode/ClaudeCodeAgent.cs` (the CLI is torn down and the termination rethrown, as
for a limit). Tests: `tests/InspectAzureAI.Eval.Tests/ApprovalTests.cs` (71).

## Public API

| Python | C# |
|---|---|
| `ApprovalDecision` literal | `ApprovalDecision` enum; `ApprovalDecisions.ToPython/Parse` for the literal names |
| `Approval(decision, modified, explanation, metadata)` | `Approval` record |
| `Approver` protocol | `Approver` delegate `(message, call, view, history, ct)`; `ApproverDef(Name, Approve) { Params }` is the registry-tagged form (`registry_log_name` / `registry_params`) |
| `@approver(name=...)` registry | `ApproverRegistry.Register(name, factory)` / `IsRegistered` / `Create`; `auto` and `human` are pre-registered |
| `auto_approver(decision)` | `Approvers.Auto(decision)` |
| `human_approver(choices)` | `Approvers.Human(choices, prompter)`; `IApprovalPrompter` + `ApprovalRequest`; `ConsoleApprovalPrompter` (default, `Approvers.DefaultPrompter`); `HumanApprovals` constants |
| `ApprovalPolicy(approver, tools)` | `ApprovalPolicy(approver, string)` / `(approver, IReadOnlyList<string>)` |
| `policy_approver`, `read_approval_policies`, `approval_policies_from_config`, `config_from_approval_policies`, `read_policy_config` | `ApprovalPolicies.PolicyApprover` / `FromFile` / `Resolve` / `FromConfig` / `ToConfig` / `ReadConfig` |
| `ApproverPolicyConfig`, `ApprovalPolicyConfig` | same names (`FromJson`, `ToJson`, `Parse`) |
| `approval(policies)` context manager, `init_tool_approval`, `have_tool_approval`, `apply_tool_approval`, `call_approver`, `record_approval` | `ToolApproval.Begin` / `BeginIfAny` / `Init` / `HaveToolApproval` / `Current` / `ApplyAsync` / `CallApproverAsync` / `RecordApproval` |
| `ToolCallViewer`, `default_tool_call_viewer`, `substitute_tool_call_content` | `ToolCallViewer` delegate, `ToolCallViews.Default` / `Substitute`; `ToolDef.Viewer` |
| `ToolApprovalError`, `TerminateSampleError` | `ToolApprovalError` (a `ToolError`), `TerminateSampleException` |
| `eval(approval=str \| list \| config)`, `Task(approval=)` | `EvalOptions.Approval`, `EvalTask.Approval` of type `ApprovalOption` (implicit from `string`, `ApprovalPolicy`, arrays/lists, `ApprovalPolicyConfig`) |
| `execute_tools(approval=)`, `react(approval=)` | `ToolExecutor.ExecuteToolsAsync(..., approval:)`, `Agents.React(..., approval:)` |
| `AgentBridge(approval=)`, `request_terminate`, `apply_bridge_tool_approval`, `rejection_messages`, `with_modified_arguments` | `AgentBridge(..., approval:)`, `AgentBridge.RequestTerminate`, `BridgeApproval.ApplyAsync` / `RejectionMessages` / `WithModifiedArguments` / `DescribeCall`, `BridgeApprovalResult` |
| `SandboxAgentBridge._terminate_requested` + `_monitor_terminate` | `SandboxAgentBridge.TerminateError` / `TerminateRequested` (the agent links its exec to the token and rethrows, as it does for `LimitError`) |

Semantics kept exactly: prefix globs (`*` appended unless present) matched with `fnmatch` over
`format_function_call(function, arguments, width=maxsize)`, comma-separated specs, first match decides, `escalate`
continues down the chain, `"No approval granted for tool X"` / `"No approvers registered for tool X"` recorded under
approver `policy`; unknown config keys collected into `params` (unknown keys win over `params`); `approve`/`modify`
run the tool (`modify` rebinds only the call the tool receives; the tool message and `ToolEvent` keep the model's call),
`reject` → `ToolCallError("approval", explanation)`, `terminate` → `TerminateSampleException("Tool call approver
requested termination.")` after the `ToolEvent` is recorded, which the runner turns into an `operator` sample limit
(`limit=1`, still scored); the approver's own inference is exempt from token and turn limits; the eval-level policy
replaces the task's and `EvalConfig.approval` carries `config_from_approval_policies` (`params` holds only explicitly
passed arguments, e.g. `auto_approver()` records `{}`), verified against pydantic's `model_dump()` by a `PythonFact`.
Bridge: only the bridge's own policies or the ambient ones apply; a rejection replays the assistant turn plus one
`approval` tool result per call (collateral results name the culprit, bounded to 200 characters) and regenerates,
`MaxConsecutiveRejections` (3) terminates, `terminate` terminates immediately, modifications are adopted only once
the whole response is approved and only for the arguments, multi-choice responses with tool-calling alternates are
reduced to the primary choice (warned once), and state tracking uses the original input.

## Deviations

- Policy files are JSON only (`{"approvers": [{"name", "tools", ...}]}`, the same structure as the YAML docs); a
  YAML file is a `NotSupportedException` (no YAML parser without a new package). Python detects JSON by a leading `{`.
- `ExecuteToolsAsync` takes `approval` after `cancellationToken` so existing positional callers keep compiling.
- The human approver ships the console surface only: no Apprise `notify()`, no `awaiting_human` marker, no ACP or
  fullscreen panel routing, and no `HumanApprovalManager`. A host plugs in its own `IApprovalPrompter`
  (`Approvers.Human(prompter:)` or `Approvers.DefaultPrompter`). A prompter returns a decision, so `modify` is a
  `NotSupportedException` from the human approver (the console never offers it, as in Python).
- Approvers are `ApproverDef`s rather than registry-tagged closures; the deprecated `state` parameter of old approvers
  is not supported. Custom approvers used in policy files register with `ApproverRegistry.Register`.
- `Approval.Metadata` is recorded on the event's base `Metadata` (Python's `ApprovalEvent` inherits it the same way).
  `RecordApproval` is a no-op outside a sample context (Python always has a default transcript).
- `TerminateSampleException` lives in `InspectAzureAI.Eval.Approval` (Python: `_util/exception.py`); like Python's
  `TerminateSampleError` it reaches the executor's unhandled-exception path, so the tool event is recorded with
  `failed: true` before it propagates.
- Python's `ModelOutput.model_copy(deep=True)` for `modify` is a record copy with cloned argument objects.

## Not ported

- The host-tool execution grants of upstream 76f1aa761 (`register_tool_execution_grants` /
  `consume_tool_execution_grant` / the `call_tool` service gate). Python needs them because its sandbox bridge also
  runs host tools (`bridged_tools`, MCP servers) through an RPC service a sandboxed process can reach directly. The
  .NET `SandboxAgentBridge` exposes no host-tool execution surface at all: the only way a bridged agent obtains a tool
  call is the generate response, and that is the gated path (`AgentBridge.GenerateAsync`, shared by both dialects).
  If a host-tool service is added later it must consume a grant registered from the approved response, as upstream does.
- ACP / TUI / panel approval, `human_approver` notifications, `approver` decorator attribs, the `approver_from_config`
  helper, and the CLI `--approval` flag (use `EvalOptions.Approval = "human"` / a file path).
