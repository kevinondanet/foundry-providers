# Deferred from the inspect_ai port

State as of 2026-09-05, branch `port/inspect-full`. Everything listed here was consciously
left out of the full port. Python paths are relative to `src/inspect_ai` in the inspect_ai
repo; line counts are from upstream commit 76f1aa761. Each of the six subsystems below is a
project on its own and was deferred so the first batch could be reviewed before spending more.

## Deferred subsystems

### 1. Checkpointing (restic snapshots of sandbox state)

- Python: `util/_checkpoint/` (6,045 lines: checkpointer, config, triggers, snapshot, layout,
  hydrate, repo_ops, dump_events, report, plans, sandbox_paths), `util/_restic/` (591 lines,
  a vendored restic binary with SHA256SUMS and a version pin), `event/_checkpoint.py`
  (`CheckpointEvent`, already ported as a record), the CLI `--checkpoint` option.
- Design: `design/checkpoint-snapshot-strategy.md`.
- Needs: restic binary acquisition and verification (same shape as the Claude Code binary
  download in `src/InspectAzureAI.Swe/ClaudeCode`), the sandbox path layout, time/token/turn
  triggers (the scoped limits in `Eval/Context/Limits.cs` provide the counters), the compaction
  handler-state checkpoint (`docs/ports/compaction.md` lists the `_CompactionState` gap), and
  hydration on retry in `Eval/Runner/EvalSet`.
- Why deferred: large, needs a vendored binary and sandbox filesystem semantics; no showcase
  scenario needed it.

### 2. ACP (Agent Client Protocol) integration

- Python: `agent/_acp/` (20,969 lines: client registry, config, guards, connection, discovery,
  event mapping, inspect_ext, picker, server, session router, stdio and live transports, tool
  content, a TUI), `_cli/acp.py`, and `agent/_channel/` (AgentChannel, not ported).
- Design: `design/acp/agent-acp.md`, `agent-acp-tui.md`, `agent_channel_brief.md`,
  `elicitation.md`.
- Needs: an ACP protocol implementation in C# (check for an official or community SDK first),
  the agent channel abstraction, and a terminal UI (Spectre.Console or Terminal.Gui would be
  new NuGet dependencies).
- Why deferred: the largest single subsystem, and an external-agent path already exists (the
  agent bridge in `Eval/Agents/Bridge`).

### 3. Control server (`inspect ctl`)

- Python: `_control/` (11,277 lines: FastAPI over a Unix domain socket with peer-UID checks,
  CONTROL_API_VERSION 8; pause and resume, cancel of task, sample or tool call, sample requeue,
  live max_tasks and concurrency retune, interim scoring, log flush, paged reads of events,
  messages and store), `_cli/ctl/` (9,633 lines: the CLI client with the `--json` error
  envelope).
- Design: `design/ctl/*.md` (14 notes: control-channel, pause-resume, sample-requeue,
  tool-call-cancel, interim-scoring, max-tasks-retune, security, and others).
- Needs: an HTTP server over a Unix socket (`UnixDomainSocketEndPoint` is in the BCL; ASP.NET
  minimal API would be a new dependency), runner seams for pause, cancel and requeue (the
  concurrency port left the hard-pause gate seam on `ConnectionSlot`, see
  `docs/ports/concurrency.md`; `AdaptiveConcurrencyController.SetMax` exists for retune), and
  the sample buffer (see smaller gaps) for paged reads.
- Why deferred: depends on runner seams that do not exist yet; the value is operational rather
  than eval-functional.

### 4. Deep agent harness

- Python: `agent/_deepagent/` (2,667 lines: deepagent, subagent, research, plan, general,
  agent_tool, lifecycle_tools, prompt).
- Design: `design/deepagents.md`, `design/deepagent-background.md`.
- Needs: builds on the ported react loop, handoff and as_tool (`Eval/Agents`), the todo_write
  and update_plan tools (`Eval/Tools/Builtin`), plus background dispatch (`util/_background.py`,
  not ported).
- Why deferred: the newest upstream subsystem and still moving; medium size.

### 5. Human agent (terminal takeover)

- Python: `agent/_human/` (1,401 lines: `_human_agent.py`, the installed command set, service,
  panel and state), `approval/_human/` panel routing (`acp.py`, `manager.py`, `panel.py`; only
  the `console.py` path was ported, behind `IApprovalPrompter`), `util/_input`, `util/_panel.py`.
- Needs: a sandbox-installed command set and an input/panel abstraction; the approval console
  prompter is the seam to extend.
- Why deferred: interactive and TUI-heavy; no showcase scenario.

### 6. Batch API support

- Python: `model/_providers/util/batch.py`, `batch_log.py`, `file_batcher.py`,
  `batch_readme.md` (the generic batcher), and per-provider `_anthropic_batch.py`,
  `_openai_batch.py`, `_google_batch.py`, `_grok_batch.py`, `_together_batch.py`.
  `GenerateConfig.batch` is already present as an opaque `Batch` field (model-extras), and the
  concurrency port honors a batch flag for the max_samples precedence rule
  (`docs/ports/concurrency.md`).
- Needs: Azure OpenAI Batch (or Foundry batch endpoints) request, poll and result plumbing
  behind `IModelApi`; the generic batcher is provider-agnostic and ports cleanly.
- Why deferred: the Azure AI inference route this solution uses has no batch endpoint, so it
  is only worthwhile once an Azure OpenAI provider exists.

## Smaller gaps recorded in the port notes

Collected from `docs/ports/*.md`; see each note for detail and rationale.

- Log: the realtime sample buffer (SQLite; powers the live viewer and paged reads), the
  chunked-format writer (the reader exists), S3 and other remote filesystems, zstd writing
  (deflate is written; both are read), blake2s stable eval ids for logs missing `eval_id`,
  viewer asset bundling (assets resolve from `INSPECT_VIEW_DIST_DIR`).
- Runner: task registry and file-based task loading (`_eval/loader.py`, `@task`), the trace
  subsystem (`inspect trace`), sandbox and subprocess resizable limiters, `sample_shuffle`,
  scanners, TaskSource and SampleSource, waiting-time accounting.
- Model: tiktoken-based token counting (a chars/4 heuristic is used), native compaction on
  Azure (no endpoint), cache TTL pricing, a model-info catalog for context windows.
- Scoring: the SymPy symbolic math scorer (the numeric subset is ported), perplexity scorers
  (need logprobs), Mersenne Twister compatible seeded shuffles.
- Tools: web_search internal providers on the Azure route, the computer tool, the memory,
  ask_user, notify_user and skills tools, YAML config files (JSON only for approval policies
  and cost config).
- Apps: no `[Task]`-attributed tasks ship in the app projects (the CLI needs `--assembly`);
  `inspect view` is delegated to Python.

## How to resume

Same approach as the port: one worktree per area branched from `port/inspect-full`
(`git worktree add ../inspect-azureai-dotnet-worktrees/<area> -b port/<area> port/inspect-full`),
new code in a new folder, additive edits to shared files, a `docs/ports/<area>.md` note, tests
that run offline against `ScriptedModelApi` and the fakes, then an integration pass that
builds and runs the full suite after each merge, then a fresh-context review. The inspect_ai
venv (`inspect_ai/.venv/bin/python`) is the reference for interop checks.
