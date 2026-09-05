# Wiring the ported subsystems into the console apps

No Python was ported here; this note records how `InspectAzureAI.SweShowcase`, `InspectAzureAI.ModelMatrix`,
`InspectAzureAI.Sample` and `InspectAzureAI.Swe` now use the subsystems the earlier port branches added (eval sets,
the `.eval` format, approval, hooks, the prompt cache, compaction, cost) and where they still deviate from the
Python apps they stand in for (`inspect eval`, `inspect eval-set`, `inspect view`).

## What changed, where

| App | Wiring | Files |
|---|---|---|
| SweShowcase `run` | `--log-format eval\|json` (default `eval`, as `inspect eval`), `--approval <policy.json\|approver>`, `--cache <expiry\|off>`, `--compaction edit\|summary\|trim\|auto[:threshold]`, `--hooks sample-log[=file]`, `--cost-limit`, `--model-cost-config`; the header prints the wiring, the summary prints the run's cost | `RunOptions.cs` (parse, all validated up front as usage errors), `RunWiring.cs` (the `EvalOptions`, hooks, header lines), `CompactionChoice.cs`, `ShowcaseHooks.cs` (`HookChoice`, `SampleLoggingHooks`), `AgentChoice.cs` (`Solver(agent, attempts, debug, cache, compaction)`, `RejectCompaction`), `Cli.cs` |
| SweShowcase `show` | reads either format through `EvalLogFiles.ReadEvalLog` (`read_eval_log`), prints the format, the recorded approval config and cost limit, per-sample cost, and what the subsystems did per sample (cache hits, approvals by decision, compactions) | `Cli.cs` |
| ModelMatrix | every deployment runs as its own eval set in `<log-dir>/<deployment>/` (`EvalSet.RunAsync([task], …)` with `MaxTasks = 1`, `LogDirAllowDirty = true`, `--retry-attempts` immediate retries, default 2); rows gain `cost`, `tok/s` and `reused`; the JSON config records every new flag; hooks lines are prefixed with the deployment | `MatrixRunner.cs`, `MatrixReport.cs`, `MatrixOptions.cs`, `MatrixCli.cs` |
| Sample | `cache` (two generations under a `CachePolicy`, the second a `cache=read` event), `cost` (model database, prices from `--model-cost-config`, one priced generation with the arithmetic shown), `structured` (`GenerateConfig.ResponseSchema` from `JsonSchemaOf<CityFacts>()`, the schema on the wire, the reply parsed back); the fake transport answers a `response_format` request with JSON | `Demos.cs`, `Program.cs`, `InspectAzureAI.Sample.csproj` (now references `InspectAzureAI.Eval`) |
| Swe | `MiniSweAgentOptions.Cache` / `.Compaction`, `ClaudeCodeOptions.Cache`; the mini-swe loop applies the ambient approval policies to each bash call, compacts its model input and recovers a context overflow by forced compaction | `MiniSweAgent.cs`, `MiniSweAgentOptions.cs`, `ClaudeCodeOptions.cs`, `ClaudeCodeAgent.cs` |
| Eval (additive seams) | `Solvers.BasicAgent(cache:)`, `AgentBridge(cache:)`; `eval.dataset.samples` now records the whole dataset size (Python's `len(task.dataset)`) and a reused sample is re-logged into the retry's log (Python's reuse sweep) — both needed for eval-set resume of a limited run | `Solvers/BasicAgent.cs`, `Agents/Bridge/AgentBridge.cs`, `Runner/Eval.cs` |

## How each flag reaches the agents

- **Approval** is not passed to the agents: `EvalOptions.Approval` makes the runner install the policies ambiently
  (`ToolApproval.Init`), exactly as `eval(approval=)` does. The basic agent's `ToolExecutor` gate, the Claude Code
  bridge (`BridgeApproval` on every bridged response, rejections replayed to the CLI) and, new here, the mini-swe loop
  all consult that scope. mini-swe mirrors `execute_tools`: `ToolApproval.ApplyAsync` per bash call with the
  assistant text and the trajectory as history; `reject` skips the command and answers it with an observation
  (`returncode -1`, the explanation as `exception_info`) whose tool message carries a `ToolCallError("approval")`;
  `modify` runs the approver's `command`; `terminate` throws `TerminateSampleException`, which the runner records as
  the `operator` sample limit (the sample is still scored). Every decision is an `ApprovalEvent` on the transcript.
- **Cache** is Python's `generate(cache=)`: mini-swe and the basic agent pass the policy to `Model.GenerateAsync`;
  Claude Code's bridge passes it on every bridged generation (port-only — Python's bridge takes no cache).
- **Compaction** is the react agent's seam (`CompactionHook`): the basic agent already had it; mini-swe now compacts
  the input of every step and, on a `model_length` stop, retries after a forced compaction instead of rendering the
  upstream format error. Claude Code compacts its own context, so `--compaction` with `--agent claude-code` is a
  usage error rather than a silently ignored flag.
- **Hooks**: `sample-log` is a `Hooks` subclass registered per run (`EvalOptions.Hooks`), not process-wide, so two
  matrix deployments in flight each get their own instance writing through the matrix's locked console writer.
- **Cost**: `--model-cost-config` is `EvalOptions.ModelCostConfig` (applied before the run, validated at parse time
  so a malformed file is exit 2); `--cost-limit` is `EvalOptions.CostLimit` (a limit without prices is the runner's
  `PrerequisiteError`, exit 2). The showcase prints `total_cost` sums from `EvalStats.ModelUsage`; the matrix adds a
  per-deployment `cost` column and total.

## Why mini-swe keeps its own loop

`Agents.React` was considered as the mini-swe engine and rejected: upstream mini-swe-agent's semantics are not the
react loop's. It has a single `bash` tool and no submit tool (the run ends when a command prints
`COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT` with exit 0, the rest of the output being the submission); a response
without a well-formed bash call is answered with the Jinja format-error template and the malformed assistant turn
is dropped from the trajectory, with `max_consecutive_format_errors` ending the run; observations are the
`{"returncode", "output", "exception_info"}` JSON of `LocalEnvironment`, with `not_executed` padding after a
submission; it has `step_limit` / `wall_time_limit`, its own `role: exit` statuses in the store, and inspect_swe's
resume reloads the saved trajectory and appends the resume reminder. React would need most of those overridden
(`AgentSubmit.Disabled`, `onContinueFn`, a custom executor for the observation format, no tool-call error replay).
Instead the loop reuses the shared pieces through their seams — approval (`ToolApproval`), compaction
(`CompactionHook`) and the cache (`CachePolicy`) — which is what the react loop itself does.

## Deviations from Python

- `--compaction`, `--cache` and `--hooks` are showcase flags without an `inspect eval` counterpart (Python sets these
  in code: `react(compaction=)`, `generate(cache=)`, `@hooks`); their shapes follow the Python objects they build.
- Python's CLI accepts YAML policy and price files; these apps accept JSON only (no YAML parser in the solution).
- The matrix's per-deployment eval sets use `LogDirAllowDirty` so a changed `--limit` / `--epochs` (a new task
  identifier) runs fresh next to the old logs instead of failing the prerequisite check, and default
  `RetryAttempts` to 2 (Python: 10). Running the matrix twice in one log directory is a resume, as with
  `inspect eval-set`; there is no flag to force a fresh run other than a new `--log-dir`.
- A reused matrix row's `time` is the log's own `started_at`→`completed_at` span; `tok/s` is total tokens over wall
  time (Python's display shows output tokens per second of model time).
- The Sample app's `cost` demo exits 0 for an unpriced model and says so; the eval runner's cost limit is the strict path.

## Not done

- The matrix does not expose `retry_wait` / `retry_connections` / batch-mode retries (`RetryImmediate` is always on).
- `--approval human` works (console prompter) but is untested offline; the bridge path for Claude Code is covered by
  `ApprovalTests`, not by the showcase's offline tests (Claude Code cannot run under `--fake`).
- `MiniSweAgent` records no `ToolEvent` for a rejected call (it never did for approved ones either; commands are
  `InfoEvent`s), so the viewer shows the rejection on the tool message and the `ApprovalEvent`.

## Tests

`tests/InspectAzureAI.Swe.Tests/ShowcaseWiringTests.cs` (approval reject/terminate on both native loops, the cache
replaying a second run, compaction accepted/refused, hooks to console and file, `--log-format` with `show` reading
both, pricing and the cost limit, the usage errors), `MiniSweWiringTests.cs` (reject/modify/terminate, the cache
with zero provider calls, threshold and forced compaction), `tests/InspectAzureAI.ModelMatrix.Tests/MatrixWiringTests.cs`
(eval-set directories, reuse on a second run, resume of an errored log under the same task id, cost and throughput,
prefixed hooks, flag parsing, path-safe directories) plus `ReportTests`, and the new
`tests/InspectAzureAI.Sample.Tests` (cache write/read, cost with and without prices, structured output).
