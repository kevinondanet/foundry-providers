# InspectAzureAI.HveDemo: the GitHub Copilot CLI running HVE Core as an Inspect agent

A console app that runs the GitHub Copilot CLI inside a Docker sandbox with a vendored subset of
[Microsoft HVE Core](https://github.com/microsoft/hve-core) loaded as a Copilot plugin, points the CLI's
model calls back at an Azure AI Foundry deployment through the port's sandbox agent bridge, and scores
what comes out with four Inspect scorers. It is the sibling of `InspectAzureAI.CtfSample`: every Inspect
component (dataset, solver, scorer, task) is one file under `Components/`, documented as such, and the
app has an offline `--fake` mode that exercises the whole pipeline without a network or Docker.

## What HVE Core is, and what is vendored

HVE Core ("Hypervelocity Engineering", MIT) is Microsoft's collection of GitHub Copilot customisations:
custom agents (`.github/agents/*.agent.md`), skills (`.github/skills/<pkg>/<skill>/SKILL.md` plus
reference files and templates), prompt commands (`.github/prompts/*.prompt.md`) and path-scoped
instruction files (`.github/instructions/*.instructions.md`). Upstream ships 60 agents, 77 skills, 48
commands and 60 instruction files with a root `plugin.json` so the whole repository installs as one
Copilot CLI plugin.

The demo vendors a self-consistent subset (`hve/plugin/`, 60 files, unmodified copies in the upstream
`.github/` layout so relative `#file:` and `references/` paths resolve), chosen to cover the three things
a plugin can do for an agent:

| Piece | Vendored | Exercised by |
|---|---|---|
| Agents | `rpi-agent` and its sub-agents `rpi-researcher`, `rpi-planner`, `rpi-review-builder`; `code-review` and its sub-agents `code-review-functional`, `code-review-standards` | `--agent hve-core:<id>` on the RPI and review samples |
| Skills | `code-review`, `python-foundational`, `documentation`, `rpi-quick`, `rpi-research`, `rpi-plan`, `rpi-plan-critique`, `rpi-implement`, `rpi-review` | the model's `skill` tool |
| Prompt command | `git-commit-message.prompt.md` | the commit-message sample |
| Instructions | `python-script`, `python-tests`, `bash`, `markdown`, `commit-message`, `copilot-tracking`, `diff-computation`, `review-artifacts`, `disclaimer-language` | the first six are copied into every sample workspace under `.github/instructions/` (the CLI does not surface a plugin's `rules`; it does table the workspace's instruction files in the system prompt); the last three stay plugin-only, imported by `code-review.agent.md` through `#file:` references |

The plugin keeps upstream's `LICENSE` and a `NOTICE.md` inventory; the hand-written `plugin.json` lists the
subset and sets `"rules": [".github/instructions"]` (the CLI reads `rules` entries as directories).

## How the Copilot CLI is injected and pointed at the bridge

The sandbox image (`hve/sandbox/Dockerfile`, `python:3.12-slim-bookworm` plus git; bash and tar come with
the base image) contains no Copilot CLI and no download tooling. The `CopilotCli` agent of `InspectAzureAI.Swe`
installs it at sample start the way the Claude Code agent installs its binary: the pinned release tarball
(`copilot-linux-{x64,arm64}.tar.gz`, 1.0.83 by default) is downloaded once to
`~/.cache/inspect-azureai/copilot-cli-downloads/` on the host, verified against the release's
`SHA256SUMS.txt`, written into the container with `WriteFileAsync` and extracted under
`/var/tmp/.5c95f967ca830048/`. `--copilot-version sandbox` skips the install and uses a `copilot` already
on the image's PATH.

The CLI never talks to GitHub. It runs in bring-your-own-key mode, offline, with the sandbox agent bridge
as its provider:

```
COPILOT_PROVIDER_TYPE=openai                       # or anthropic on the Claude route
COPILOT_PROVIDER_BASE_URL=http://host.docker.internal:<port>/v1
COPILOT_PROVIDER_API_KEY=<the bridge's per-instance token>
COPILOT_MODEL=inspect  COPILOT_OFFLINE=true  COPILOT_AUTO_UPDATE=false
COPILOT_HOME=/workspace/.copilot  COPILOT_ALLOW_ALL=true
copilot -p "<system message + task>" --session-id <id> --output-format json --model inspect \
        --no-auto-update --no-ask-user --disable-builtin-mcps --yolo \
        [--agent hve-core:<id>] --plugin-dir /opt/hve-core --log-level error --log-dir /workspace/.copilot/logs
```

The bridge (`SandboxAgentBridge`) listens on the host, resolves the `inspect` alias to the sample's model,
records one `ModelEvent` per call and applies the sample's approval policy to every tool call the model
returns. The CLI's own JSONL output is folded onto the transcript as `copilot_cli` info events (skills
loaded, tool executions, sub-agent starts, the final `result`); the streaming-only `ephemeral` lines
(per-token deltas, partial tool output, background-task ticks) are left out unless
`CopilotCliOptions.RecordStreamingLines` is set, which keeps the log at a few dozen info events per sample
instead of a few hundred.

## The four components

**Dataset** (`Components/HveDataset.cs`): `Datasets.Json` over `hve/dataset.json` with a `FieldSpec`
naming the `id`, `input` and `target` columns; the record's `metadata` object carries `kind`
(`implement` | `review` | `skill`), `agent` (an `hve-core:` id or null), `artefact`, `check`, `rubric`,
`hve_components` and an optional `setup`. The loader attaches the per-sample `Files`: the shared
`.github` overlay, `hve/workspace/<id>/` and the plugin (to `/opt/hve-core`). What the loader does not
provision are the check assets under `hve/checks/<id>/` (grader scripts, the review answer keys, pristine
copies of the tests the agent is shown): the check scorer writes them into the sandbox right before the
check runs, so the agent can neither read the answer key nor edit its own grader.

```csharp
public static readonly FieldSpec Fields = new(Input: "input", Target: "target", Id: "id");

var loaded = Datasets.Json(datasetPath, fields: Fields, name: Name);
IDataset dataset = new MemoryDataset(loaded.Select(sample => Provision(sample, workspaceRoot, pluginDirectory, pluginSandboxPath)), loaded.Name, loaded.Location);
```

Eight samples: four `implement` (a slugify module, an RPI-driven config loader, a log-rotation shell
script, a unittest suite), two `review` (functional and standards findings for planted defects), two
`skill` (a conventional commit message from staged changes, a how-to guide from the documentation skill).
Every `check` was validated to pass on the reference solution and fail on the empty workspace.

**Solver** (`Components/HveSolvers.cs`): a `Solvers.Chain` of `Solvers.SystemMessage` (naming the plugin's
agents, skills and instruction files) and the Copilot CLI agent through `Agents.AsSolver`. The custom agent
is a per-sample choice, so the `AgentDef` is built when the sample runs:

```csharp
public static Solver Copilot(HveSolverOptions options) =>
    Solvers.Chain(Solvers.SystemMessage(SystemPrompt), CopilotAgent(options));

public static Solver CopilotAgent(HveSolverOptions options) => (state, generate, cancellationToken) =>
{
    var agent = CopilotCli.Agent(CopilotOptions(options, HveDataset.Agent(state.Metadata)));
    return Agents.AsSolver(agent)(state, generate, cancellationToken);
};
```

`--solver basic` swaps in `Solvers.BasicAgent(tools: [SandboxTools.Bash(...)])` with the same briefing and
no plugin, as a baseline.

**Scorers** (`Components/HveScorers.cs`), all four run on every sample:

```csharp
public static IReadOnlyList<ScorerDef> All(Model? grader = null, string? pluginDirectory = null) =>
    [ExecCheck(), ArtefactReported(), ArtefactQuality(grader), ArtefactUsed(pluginDirectory)];
```

* `hve_check`: restores `hve/checks/<id>/**` into the sandbox (over whatever the agent left at those paths,
  recording any file it had modified), then runs `metadata.check` with `bash -c`; exit 0 is `C`. The review
  checker counts a planted defect only for a finding that names the file, cites an overlapping line range,
  quotes a line of the diff in `current_code` and matches a word-bounded keyword, one defect per finding.
* `artefact_reported`: the built-in `Scorers.Includes()` over the final assistant message, with the
  artefact path as its target (both system messages ask the agent to name what it produced).
* `artefact_quality`: the built-in `Scorers.ModelGradedQa(...)` over the artefact read back from the
  sandbox plus the final message, graded against the sample's target with the `rubric` added to the
  default instructions and partial credit on.
* `hve_artefact_used`: a custom scorer that reads the transcript. For each `hve_components` entry it
  looks for strong evidence (the `skill` tool invoked it, a `subagent.started` event named it, the
  `--agent` body reached a model event's system prompt, a `view`/`bash` call read the instruction file) or
  weak evidence (announced in `session.skills_loaded`, listed in the system prompt) and scores the mean
  weight, so a run that passes the check by ignoring the plugin scores 0 here.

**Tasks** (`Components/HveTasks.cs`): `[Task("hve_implement")]`, `[Task("hve_review")]`, `[Task("hve_skill")]`
and `[Task("hve_suite")]`, all built by one `Build(kind, solverName, sandbox, options)`:

```csharp
var task = new EvalTask
{
    Name = name, Version = "1",
    Dataset = HveDataset.Load(filter, pluginSandboxPath),
    Solver = HveSolvers.ByName(solverName, options),
    Scorers = HveScorers.All(grader, groupBy: filter is null ? "kind" : null),
    Sandbox = sandbox,
    MessageLimit = 200, TimeLimit = TimeSpan.FromMinutes(25),
    FailOnError = false,
};
```

The suite reports every scorer's own headline metric overall and per `kind` (Python's
`grouped(accuracy(), "kind")`, `grouped(mean(), "kind")` for `hve_artefact_used`); a task-level `Metrics`
override would replace every scorer's metrics and relabel the evidence fraction as accuracy.

## Running it

Offline (no network, no Docker; needs `python3`, `bash` and `git` on the host):

```bash
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --fake --sandbox fake --log-dir logs
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --solver basic --fake --limit 2
dotnet test tests/InspectAzureAI.HveDemo.Tests
```

`--fake` uses `FakeHveModel`, a `ScriptedModelApi` that loads the sample's skill (or reads its instruction
file), creates the artefact from `hve/reference/<id>/`, runs the check and reports the file. The fake
sandbox (`Fake/ScriptedSandbox.cs`, a copy of the examples' scripted sandbox with a host mirror directory
that plays `/workspace`) hands the CLI launch to `Fake/FakeCopilotCli.cs`, which speaks the real BYOK wire
to the real bridge (`POST /v1/chat/completions`, bearer token, `stream: true`, the probe's request keys),
executes `bash`/`view`/`create`/`edit`/`skill` tool calls in the mirror, and prints the namespaced JSONL
the agent parses. Setup scripts, checks and the baseline's `bash` tool run for real in the mirror.

Live, against Foundry (`az login`, `AZUREAI_BASE_URL` set; Docker Desktop running):

```bash
# every sample, gpt-5.4-mini through the chat-completions wire (four containers at a time; --max-samples 8 for all at once)
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --model gpt-5.4-mini --log-dir logs

# the review tasks on a Claude deployment: the CLI speaks the Anthropic wire to the bridge
dotnet run --project src/InspectAzureAI.HveDemo -- --task review --model claude-sonnet-4-6 --route anthropic

# the baseline for comparison, and the CLI's raw output kept in the store
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --solver basic --model gpt-4o
dotnet run --project src/InspectAzureAI.HveDemo -- --task skill --debug --no-cleanup

# the real CLI in Docker driven by the scripted model (no Azure, but the tarball download and the image build)
dotnet run --project src/InspectAzureAI.HveDemo -- --task skill --fake --sandbox docker --limit 1
```

Exit codes: 0 the eval succeeded, 1 it did not, 2 a usage or prerequisite error, 3 cancelled or crashed.
The summary prints per-sample scores, every metric (with the suite's per-kind groups) and a legend of
which dataset, solver, scorers and task ran.

## Live results (2026-09-08, Docker Desktop on macOS arm64, Foundry)

Every run below used the real Copilot CLI 1.0.83 (`copilot-linux-arm64.tar.gz`, served from the host
cache) injected into a container built from `hve/sandbox/Dockerfile`, the vendored plugin at
`/opt/hve-core`, and the sandbox agent bridge on `host.docker.internal`. Time is wall clock for the
whole `dotnet run`, including the image build check and the container start; tokens are the bridged
model's totals (the CLI's ~32 KB system prompt is resent on every turn, prompt caching absorbed most of it).

| Task / run | Model, wire | Samples | hve_check | artefact_reported | artefact_quality | hve_artefact_used | Tokens | Time |
|---|---|---|---|---|---|---|---|---|
| `--task implement --limit 1` | gpt-5.4-mini, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 172,854 | 59 s |
| `--task review --limit 1` | gpt-5.4-mini, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 185,252 | 57 s |
| `--task skill --limit 1` | gpt-5.4-mini, openai | 1 | 0/1 | 1/1 | P | 1.00 | 94,036 | 39 s |
| `--task implement --limit 1 --route anthropic` | claude-sonnet-4-6, anthropic | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 224,996 | 88 s |
| `--task suite` | gpt-5.4-mini, openai | 8 | 7/8 (implement 4/4, review 1/2, skill 2/2) | 8/8 | 6/8 (2 P) | 0.93 | 1,257,053 | 153 s |
| `--task suite --solver basic --limit 2` (baseline) | gpt-5.4-mini | 2 | 2/2 | 2/2 | 2/2 | 0.00 | 66,072 | 28 s |
| `--task skill --limit 1` (after the fixes below) | gpt-5.4-mini, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 61,437 | 32 s |
| `--task implement --limit 1` | MAI-Thinking-1, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 295,029 | 1,116 s (shared the deployment's rate limit with five concurrent suite runs; 8 HTTP retries) |

The two failures are model misses, not infrastructure: in the single-sample skill run gpt-5.4-mini loaded
`git-commit-message.prompt` and read `commit-message.instructions.md` but wrote a one-line
`COMMIT_MSG.txt` without the required footer (the same sample passed in the suite run); in the suite,
`review-standards-export` produced `findings: []` with verdict `approve`. `implement-config-loader-rpi`
scored 0.57 on `hve_artefact_used` because the model chose the `rpi-quick` skill over the four
`rpi-research/plan/implement/review` skills the sample expects (they were listed, never loaded). No run
dispatched a sub-agent (`task` tool / `subagent.started`): the custom agents were always embedded as
`<agent_instructions>` in the system prompt instead. The suite's eight containers ran concurrently (the
default is now four at a time, `--max-samples` sets it); the runner's retry handled six HTTP 429s from the
deployment. `docker ps -a` was empty after every run. The review checks in this table were the earlier,
substring-matched version; the current checker (per-finding, location-aware, diff-quoting) was validated
against the reference findings and the offline suite, not rerun live.

Wire facts confirmed live (in addition to the host probe): with `--agent hve-core:<id>` the CLI embeds
the agent body in an `<agent_instructions>` block and advertises only the tools the agent's front matter
maps to (`view`, `create`, `skill`, `sql` for the review sub-agents); without `--agent` all 17 built-in
tools are advertised; `session.skills_loaded` lists the plugin's skills with `commandName`
`hve-core:<skill>` and their `/opt/hve-core/...` paths; `session.mcp_servers_loaded` reports
`github-mcp-server` disabled; the final `result` line carries `codeChanges.filesModified`. On the
Anthropic wire the CLI sends `system` as two text blocks, which the bridge hoists into two system
messages (6,145 + 26,465 chars, the same 32,612-char prompt the OpenAI wire sends as one string), and
logs a harmless `session.warning` (`unknown_token_count_multiplier`) for the alias model name `inspect`.

## Deviations and caveats

* `artefact_reported` is the built-in `includes` scorer, but with the artefact path as its target rather
  than the sample's descriptive target; the dataset has no literal-answer samples, so this is where the
  built-in string scorer earns its place. `artefact_quality` likewise wraps `model_graded_qa` to feed it
  the artefact's text, since the final message alone would not show the reviewer's findings.
* `hve_artefact_used` recognises a selected custom agent by finding the first body line of its
  `*.agent.md` (its H1) as a whole line of a bridged system prompt; the CLI embeds the whole body in an
  `<agent_instructions>` block, which the fake reproduces. Sub-agent dispatch (`subagent.started`) and
  instruction-file reads are recognised from the CLI's JSONL; `session.skills_loaded`, although the CLI
  flags it `ephemeral`, is kept on the transcript as the weak evidence of a skill that was offered.
* The fake CLI supports the chat-completions wire only (`COPILOT_PROVIDER_TYPE=openai`); `--route
  anthropic` is ignored under `--fake`. Its `create` tool overwrites an existing file (the real one refuses).
* `--sandbox local` runs the host's `copilot` (or downloads a Linux tarball it cannot run on macOS); it
  points `--plugin-dir` at the host copy of the plugin and copies nothing to `/opt`. It is a demo path,
  not verified here.
* The self-contained Linux binary writes `Package extraction took NNNNms` to **stderr** when its first
  start is slow (seen once with eight containers starting at once). The agent's exit rules ignore that
  notice (`CopilotCliExit.SignificantStderr`), otherwise an exit 1 after it would be a hard failure
  instead of a resumable retry.
* The bridge forwards no request-level generation config and the CLI sends no `max_tokens` on the OpenAI
  wire, so the provider's 2048-token default would cap every turn; `Program` sets `MaxTokens = 8192`
  (what the CLI asks for on the Anthropic wire). No live turn exceeded 1,406 output tokens.
* The review answer keys (`tests/planted.json`) and every grader script live under `hve/checks/<id>/`
  and reach the sandbox only when the check scorer writes them, right before the check runs; an earlier
  live run had shown gpt-5.4-mini opening the key when it sat in the workspace. Files the agent is told
  to run (`tests/test_rotate.sh`, the unit tests) are in the workspace too, and the scorer restores the
  pristine copy over them, noting in the score metadata when the agent had changed one.
* Whether `gpt-5.4-mini` follows the full RPI lifecycle is still open: it passed the RPI sample through
  `rpi-quick`, not the four-phase skills, and never delegated to a sub-agent.
* The Copilot CLI's stdout schema is undocumented; the demo relies on the shapes recorded by the 1.0.83
  wire probe (`session.skills_loaded`, `assistant.message.toolRequests`, `tool.execution_start/complete`,
  `subagent.started`, `result`).
