# InspectAzureAI.HveDemo: the HVE Core tasks under interchangeable harnesses and frameworks

A console app that runs eight software-engineering samples in a Docker sandbox and scores what comes out
with Inspect scorers. The samples never change; what runs over them is two orthogonal switches, as
interchangeable as the `--model` that decides which model answers:

* `--harness copilot|generic` — the agent runtime. `copilot` (the default) is the GitHub Copilot CLI
  running *inside* the sandbox, its model calls pointed back at an Azure AI Foundry deployment through
  the port's sandbox agent bridge; `generic` is Inspect's own agent loop (`Solvers.BasicAgent` with the
  sandbox `bash` tool and its `submit` tool), no external CLI.
* `--framework hve|none` — the engineering framework layered on the harness. `hve` (the default)
  provisions the vendored subset of [Microsoft HVE Core](https://github.com/microsoft/hve-core) (custom
  agents, skills, prompt commands, instruction files) into the sandbox and briefs the agent to use it;
  `none` provisions and briefs nothing.

The `.github` instruction overlay of every sample workspace is *not* part of the framework: it is the
repository's own conventions, so it stays in all four cells. `--solver copilot|basic` survives as a
deprecated alias (`copilot` = `--harness copilot --framework hve`, `basic` = `--harness generic
--framework none`): it prints one note on stderr, and a `--harness`/`--framework` given after it wins.

It is the sibling of `InspectAzureAI.CtfSample`: every Inspect component (dataset, solver, scorer, task)
is one file under `Components/`, documented as such, and the app has an offline `--fake` mode that
exercises all four cells without a network or Docker.

## Harnesses and frameworks

Neither harness has a system-prompt flag of its own, so in every cell the solver is a chain of the cell's
briefing and the cell's runtime, and the framework axis lives in that briefing (plus, under `hve`, the
plugin files provisioned into the sandbox). The task names are the same in all four cells, so two runs
compare directly; which cell ran is in the task metadata (`harness`, `framework`, `solver` =
`<harness>+<framework>`) and in the two lines the header prints.

| | `--framework hve` | `--framework none` |
|---|---|---|
| **`--harness copilot`** | **Runs** the Copilot CLI in the sandbox, bridged to the model (`Agents.AsSolver(CopilotCli.Agent(...))`).<br>**Framework** through the CLI itself: `--plugin-dir /opt/hve-core` and, when the sample names one, `--agent hve-core:<id>` — the CLI announces the plugin's skills, offers them on its `skill` tool and embeds the agent body as `<agent_instructions>`.<br>**Scorers** all four.<br>**Fake** calls `skill` for the skill the sample names (or the `<name>.prompt` command, or `view`s its instruction file), `create`s the artefact from the reference solution, runs the check, reports the path. | **Runs** the same CLI, launched with neither flag.<br>**Framework** none: an empty skill library (`session.skills_loaded` with no skills), no `<agent_instructions>`, all 17 built-in tools advertised, `metadata.agent` ignored, and a briefing that says no plugin is installed.<br>**Scorers** three (`hve_artefact_used` is left out).<br>**Fake** `view`s the sample's instruction file only — never the `skill` tool, never an agent body — then creates, checks and reports. |
| **`--harness generic`** | **Runs** `Solvers.BasicAgent` with the sandbox `bash` tool and `submit`, no external CLI.<br>**Framework** through the briefing, because no plugin runtime is listening: the same files are copied to `/opt/hve-core` (the host copy under `--sandbox local`), the briefing names that directory, describes the layout, says that loading a skill means reading its `SKILL.md` with `bash`, and embeds the sample's agent body in `<agent_instructions>` itself.<br>**Scorers** all four; the evidence is Inspect's own `ToolEvent`s instead of the CLI's JSONL.<br>**Fake** `cat`s every skill, prompt and instruction file the sample names in one `bash` call, writes the artefact with a heredoc, runs the check, `submit`s. | **Runs** the same loop.<br>**Framework** none: nothing at `/opt`, a plain briefing.<br>**Scorers** three.<br>**Fake** writes the artefact with a heredoc, runs the check, `submit`s. |

Read across a row for what the framework adds to one harness, down a column for what the harness adds to
one framework: `copilot+hve` is the original demo (CLI plus plugin), `generic+none` the original baseline,
`copilot+none` isolates what the CLI alone contributes and `generic+hve` what the plugin content alone
contributes.

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
| Agents | `rpi-agent` and its sub-agents `rpi-researcher`, `rpi-planner`, `rpi-review-builder`; `code-review` and its sub-agents `code-review-functional`, `code-review-standards` | `--agent hve-core:<id>` on the RPI and review samples (copilot); the same body embedded in the briefing as `<agent_instructions>` (generic) |
| Skills | `code-review`, `python-foundational`, `documentation`, `rpi-quick`, `rpi-research`, `rpi-plan`, `rpi-plan-critique`, `rpi-implement`, `rpi-review` | the model's `skill` tool (copilot); a `cat` of the skill's `SKILL.md` (generic) |
| Prompt command | `git-commit-message.prompt.md` | the commit-message sample |
| Instructions | `python-script`, `python-tests`, `bash`, `markdown`, `commit-message`, `copilot-tracking`, `diff-computation`, `review-artifacts`, `disclaimer-language` | the first six are copied into every sample workspace under `.github/instructions/` (the CLI does not surface a plugin's `rules`; it does table the workspace's instruction files in the system prompt); the last three stay plugin-only, imported by `code-review.agent.md` through `#file:` references |

The plugin keeps upstream's `LICENSE` and a `NOTICE.md` inventory; the hand-written `plugin.json` lists the
subset and sets `"rules": [".github/instructions"]` (the CLI reads `rules` entries as directories).

## How the Copilot CLI is injected and pointed at the bridge (the copilot harness)

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
        [--agent hve-core:<id>] [--plugin-dir /opt/hve-core] --log-level error --log-dir /workspace/.copilot/logs
```

The two bracketed flags are the framework axis: `--framework hve` passes `--plugin-dir` and, when the
sample names one, `--agent`; `--framework none` passes neither, so the CLI starts with an empty skill
library (`session.skills_loaded` with no skills) and no `<agent_instructions>` in its system prompt.

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
`.github` overlay, `hve/workspace/<id>/` and, under `--framework hve`, the plugin (to `/opt/hve-core`). What the loader does not
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

**Solver** (`Components/HveSolvers.cs`): one chain per cell of the matrix, picked by `HveSolvers.For`. Every
chain is the cell's briefing followed by its harness; the briefing is where the framework axis lives, since
neither harness has a system-prompt flag of its own:

```csharp
public static Solver For(HveVariant variant, HveSolverOptions options) =>
    variant.IsCopilot ? Copilot(options, variant.Framework) : Generic(options, variant.Framework);

public static Solver Copilot(HveSolverOptions options, string framework = HveFramework.Hve) =>
    Solvers.Chain(
        Solvers.SystemMessage(framework == HveFramework.Hve ? CopilotHveBriefing : CopilotPlainBriefing),
        CopilotAgent(options, framework));

public static Solver Generic(HveSolverOptions options, string framework = HveFramework.None) =>
    Solvers.Chain(
        framework == HveFramework.Hve
            ? LiteralSystemMessage(state => GenericHveBriefing(options.PluginDir, HveDataset.Agent(state.Metadata), options.HostPluginDirectory))
            : Solvers.SystemMessage(GenericPlainBriefing),
        Solvers.BasicAgent(tools: [SandboxTools.Bash(options.BashTimeout)], maxAttempts: 1));
```

Under the copilot harness the custom agent is a per-sample choice, so the `AgentDef` is built when the
sample runs (`CopilotAgent`, which hands it to `Agents.AsSolver`); with `--framework none` `CopilotOptions`
leaves `PluginDirs` empty and `CustomAgent` null, so the CLI is launched with neither flag and the sample's
`metadata.agent` is ignored.

Under `generic+hve` no plugin runtime loads anything, so the briefing does the plugin's job in text
(`GenericHveBriefing`): it names the plugin directory (one line `HVE plugin directory: <path>`), describes
the layout (`.github/agents/**/<id>.agent.md`, `.github/skills/**/<name>/SKILL.md` with its `references/`
and `templates/`, `.github/prompts/**/<name>.prompt.md`, `.github/instructions/**/<name>.instructions.md`),
says that "loading a skill" means reading its `SKILL.md` with `bash`, and asks the agent to read what the
task names before writing anything. When the sample names an agent, that agent file's markdown body (front
matter stripped) is embedded in an `<agent_instructions>` block, exactly what the CLI's `--agent` does — so
the same evidence scorer works for both harnesses. That briefing is inserted with `LiteralSystemMessage`
rather than `Solvers.SystemMessage`, because an agent body may contain braces and the template formatter
would read them as placeholders. When the host cannot read the plugin (a `--plugin-dir` that only exists in
the sandbox), the briefing instead tells the agent to `cat` its own agent file first.

**Scorers** (`Components/HveScorers.cs`): four scorers, of which the task keeps three under
`--framework none` (there is nothing to have used):

```csharp
public static IReadOnlyList<ScorerDef> All(Model? grader = null, string? pluginDirectory = null, string? checksRoot = null, string? groupBy = null)
{
    ScorerDef[] scorers = [ExecCheck(checksRoot), ArtefactReported(), ArtefactQuality(grader), ArtefactUsed(pluginDirectory)];
    return groupBy is null ? scorers : scorers.Select(scorer => scorer with { Metrics = [.. scorer.Metrics, Metrics.Grouped(scorer.Metrics[0], groupBy)] }).ToList();
}
```

* `hve_check`: restores `hve/checks/<id>/**` into the sandbox (over whatever the agent left at those paths,
  recording any file it had modified), then runs `metadata.check` with `bash -c`; exit 0 is `C`. The review
  checker counts a planted defect only for a finding that names the file, cites an overlapping line range,
  quotes a line of the diff in `current_code` and matches a word-bounded keyword, one defect per finding.
* `artefact_reported`: the built-in `Scorers.Includes()` over the final assistant message, with the
  artefact path as its target (every briefing, in all four cells, ends by asking the agent to name what it
  produced by its path).
* `artefact_quality`: the built-in `Scorers.ModelGradedQa(...)` over the artefact read back from the
  sandbox plus the final message, graded against the sample's target with the `rubric` added to the
  default instructions and partial credit on.
* `hve_artefact_used` (only under `--framework hve`): a custom scorer that reads the transcript. The two
  harnesses leave two different trails and every rule reads both — the CLI's own JSONL (`copilot_cli` info
  events: `tool.execution_start`, `session.skills_loaded`, `subagent.started`) and Inspect's own
  `ToolEvent`s (the generic harness's `bash` calls; the bridge records the CLI's calls this way too) plus
  the system messages of the model events. For each `hve_components` entry:

| | strong (1.0) | weak (0.5) |
|---|---|---|
| `skill/x` | the `skill` tool was invoked for it; a read tool named `/x/SKILL.md`, `/x/references/` or `/x/templates/`; a read tool's result carries the front-matter line `name: x` (a `cd` then a relative `cat`, a globbed `*/*/SKILL.md`, a `find -exec cat` — reads that never spell the path) | `session.skills_loaded` announced it, or a briefing named it as a whole token |
| `prompt/x` | the `skill` tool was invoked for `x.prompt`; a read tool named `/x.prompt.md` | announced, or listed in a briefing |
| `agent/x` | a `subagent.started` named `hve-core:x`; a system prompt embeds the agent body (the CLI's `--agent` and the generic briefing both do this); a read tool named `/x.agent.md` or its result carries the body's first line | the `task` tool asked for it; the briefing told the agent to read its definition |
| `instructions/x` | a read tool named `x.instructions.md` | a briefing listed the file |

What counts as reading a path is deliberately narrow, because under the generic harness *every write is a
`bash` call too* and the plugin's own instructions tell the agent to cite the files it worked from: the
rules match the call's arguments with heredoc bodies cut out and the CLI's prose `description` field left
out, `submit` is not a read tool, and a call that failed — or whose result reports `No such file or
directory` for that very path — earns nothing. The score is the mean weight over the sample's components,
so a run that passes the check by ignoring the framework stays at the floor its briefing bought it.

That floor is not zero under `generic+hve`: the briefing lists every skill, the prompt command and the six
overlay instruction files, and embeds the agent body, so a run that reads nothing still scores 0.5 on
`implement-slugify` (two listed components) and (1 + 6 × 0.5) / 7 = 0.57 on `implement-config-loader-rpi`
(an embedded agent plus six listed components); a compliant run scores 1.0, which the offline suite does
on all eight samples. The copilot harness has the same shape of floor, since `session.skills_loaded`
names every plugin skill and the CLI tables every workspace instruction file in its system prompt.

**Tasks** (`Components/HveTasks.cs`): `[Task("hve_implement")]`, `[Task("hve_review")]`, `[Task("hve_skill")]`
and `[Task("hve_suite")]`, all built by one `Build(kind, variant, sandbox, options)`. The task names are the
same in every cell, so two runs of different cells compare directly; which cell ran is in the task metadata
and in the summary's `harness`/`framework` lines:

```csharp
var scorers = HveScorers.All(grader, groupBy: filter is null ? "kind" : null);
var task = new EvalTask
{
    Name = name, Version = "1",
    Dataset = HveDataset.Load(filter, variant.UsesFramework ? pluginSandboxPath : null),
    Solver = HveSolvers.For(variant, options),
    Scorers = variant.UsesFramework ? scorers : scorers.Where(scorer => scorer.Name != HveScorers.ArtefactUsedName).ToList(),
    Sandbox = sandbox,
    MessageLimit = 200, TimeLimit = TimeSpan.FromMinutes(25),
    FailOnError = false,
    Metadata = new Dictionary<string, object?>
    {
        ["harness"] = variant.Harness, ["framework"] = variant.Framework, ["solver"] = variant.Label,
        ["plugin"] = variant.UsesFramework ? "hve-core (vendored subset of microsoft/hve-core 3.2.2, MIT)" : "none",
        ["kind"] = filter ?? "suite", ["copilot_version"] = options.CopilotVersion,
    },
};
```

The four registered `[Task]` methods run the default cell (`HveVariant.Default`, copilot+hve); `Program`
builds the other three from the command line. The grouping is applied before the scorer cut, so the three
scorers that survive `--framework none` keep their per-kind metric in the suite.

The suite reports every scorer's own headline metric overall and per `kind` (Python's
`grouped(accuracy(), "kind")`, `grouped(mean(), "kind")` for `hve_artefact_used`); a task-level `Metrics`
override would replace every scorer's metrics and relabel the evidence fraction as accuracy.

## Running it

Offline (no network, no Docker; needs `python3`, `bash` and `git` on the host):

```bash
# the four cells, one command each (the first is the default, so both switches may be left out)
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --fake --sandbox fake --harness copilot --framework hve --log-dir logs
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --fake --sandbox fake --harness copilot --framework none
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --fake --sandbox fake --harness generic --framework hve
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --fake --sandbox fake --harness generic --framework none

# the deprecated alias: the same as --harness generic --framework none, plus one note on stderr
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --fake --sandbox fake --limit 2 --solver basic

dotnet test tests/InspectAzureAI.HveDemo.Tests
```

Run them one at a time: the fake sandbox mirrors `/workspace` on the host, and a suite takes a couple of
minutes.

`--fake` uses `FakeHveModel`, a `ScriptedModelApi` that plays all four cells deterministically: it reads
the briefing to tell which harness and framework it is in, then loads the sample's skill with the `skill`
tool (copilot+hve), reads its instruction file with `view` (copilot+none) or `cat`s the skill, prompt and
instruction files the sample names with one `bash` call (generic+hve), or reads nothing first
(generic+none), then creates the artefact from `hve/reference/<id>/`, runs the check and reports the
file. The fake
sandbox (`Fake/ScriptedSandbox.cs`, a copy of the examples' scripted sandbox with a host mirror directory
that plays `/workspace`) hands the CLI launch to `Fake/FakeCopilotCli.cs`, which speaks the real BYOK wire
to the real bridge (`POST /v1/chat/completions`, bearer token, `stream: true`, the probe's request keys),
executes `bash`/`view`/`create`/`edit`/`skill` tool calls in the mirror, and prints the namespaced JSONL
the agent parses. Setup scripts, checks and the generic harness's `bash` tool run for real in the mirror;
a command's absolute `/workspace`, `/opt` and `/tmp` paths are rewritten to the mirror when the mirror holds
them, so `cat /opt/hve-core/.github/skills/.../SKILL.md` reads the provisioned plugin while a heredoc body
that merely mentions such a path is written out verbatim.

Live, against Foundry (`az login`, `AZUREAI_BASE_URL` set; Docker Desktop running):

```bash
# the default cell (copilot+hve), every sample, gpt-5.4-mini through the chat-completions wire
# (four containers at a time; --max-samples 8 for all at once)
dotnet run --project src/InspectAzureAI.HveDemo -- --task suite --model gpt-5.4-mini --log-dir logs

# the review tasks on a Claude deployment: the CLI speaks the Anthropic wire to the bridge
dotnet run --project src/InspectAzureAI.HveDemo -- --task review --model claude-sonnet-4-6 --route anthropic

# the three other cells: the plugin without the CLI, the CLI without the plugin, the baseline
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --harness generic --framework hve --model gpt-5.4-mini
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --harness copilot --framework none --model gpt-5.4-mini
dotnet run --project src/InspectAzureAI.HveDemo -- --task implement --harness generic --framework none --model gpt-4o

# the CLI's raw output kept in the store
dotnet run --project src/InspectAzureAI.HveDemo -- --task skill --debug --no-cleanup

# the real CLI in Docker driven by the scripted model (no Azure, but the tarball download and the image build)
dotnet run --project src/InspectAzureAI.HveDemo -- --task skill --fake --sandbox docker --limit 1
```

Exit codes: 0 the eval succeeded, 1 it did not, 2 a usage or prerequisite error, 3 cancelled or crashed.
The header prints one `harness` and one `framework` line, and the summary prints per-sample scores, every
metric (with the suite's per-kind groups) and a legend of which dataset, solver, scorers and task ran — the
legend names the cell (`solver   generic+hve: ...`) and drops the plugin and `hve_artefact_used` entries
under `--framework none`.

## Live results (2026-09-08, Docker Desktop on macOS arm64, Foundry)

Every run below used the real Copilot CLI 1.0.83 (`copilot-linux-arm64.tar.gz`, served from the host
cache) injected into a container built from `hve/sandbox/Dockerfile`, the vendored plugin at
`/opt/hve-core`, and the sandbox agent bridge on `host.docker.internal`. Time is wall clock for the
whole `dotnet run`, including the image build check and the container start; tokens are the bridged
model's totals (the CLI's ~32 KB system prompt is resent on every turn, prompt caching absorbed most of it).

The runs predate the two switches: they were spelled `--solver copilot` (= `copilot+hve`) and
`--solver basic` (= `generic+none`). Two things about the table have changed since. The baseline's
`hve_artefact_used` 0.00 is a cell that no longer exists: under `--framework none` the scorer is left out
of the task, because there is nothing to have used. And the `copilot+hve` figures are now lower bounds:
the evidence rules also credit a `view`/`bash` read of a `SKILL.md`, a `.prompt.md` or an `.agent.md`, and
a result that carries a skill's front matter, so for the same transcript `hve_artefact_used` can only go
up, never down; the offline suite's scores are unchanged. `generic+hve` and `copilot+none` have been run
offline only (`generic+hve` scores 1.0 on all eight samples with the fake model, which reads every file
the briefing points it at).

| Task / run | Model, wire | Samples | hve_check | artefact_reported | artefact_quality | hve_artefact_used | Tokens | Time |
|---|---|---|---|---|---|---|---|---|
| `--task implement --limit 1` | gpt-5.4-mini, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 172,854 | 59 s |
| `--task review --limit 1` | gpt-5.4-mini, openai | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 185,252 | 57 s |
| `--task skill --limit 1` | gpt-5.4-mini, openai | 1 | 0/1 | 1/1 | P | 1.00 | 94,036 | 39 s |
| `--task implement --limit 1 --route anthropic` | claude-sonnet-4-6, anthropic | 1 | 1/1 | 1/1 | 1/1 | 1.00 | 224,996 | 88 s |
| `--task suite` | gpt-5.4-mini, openai | 8 | 7/8 (implement 4/4, review 1/2, skill 2/2) | 8/8 | 6/8 (2 P) | 0.93 | 1,257,053 | 153 s |
| `--task suite --limit 2`, `generic+none` (then spelled `--solver basic`) | gpt-5.4-mini | 2 | 2/2 | 2/2 | 2/2 | 0.00 (the scorer is now left out of that cell) | 66,072 | 28 s |
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

### Live results (harness x framework)

One live run per cell, same sample, same model, same day: `--task implement --limit 1 --model
gpt-5.4-mini --sandbox docker` on 2026-09-09 (Foundry, Docker Desktop on macOS arm64). The sample is
`implement-slugify`, which names no custom agent, so every cell writes the same artefact and the rows
differ only in how the agent got there.

| Cell | hve_check | artefact_reported | artefact_quality | hve_artefact_used | Tokens | Time |
|---|---|---|---|---|---|---|
| `copilot+hve` | 1.000 | 1.000 | 1.000 | 1.00 | 219,686 | 52.5 s |
| `copilot+none` | 1.000 | 1.000 | 1.000 | (not in the task) | 233,200 | 58.5 s |
| `generic+hve` | 1.000 | 1.000 | 1.000 | 1.00 | 46,018 | 28.9 s |
| `generic+none` | 1.000 | 1.000 | 1.000 | (not in the task) | 17,254 | 19.2 s |

Every cell solves this sample; what separates them is cost. The Copilot harness spends roughly five to
thirteen times the tokens of the generic loop, because the CLI resubmits its own system prompt (and, under
`hve`, the plugin's skill listing) on every turn, so input tokens dominate. The framework axis is cheap on
the generic harness (17k to 46k) and, on this one sample, not measurable on the CLI (233k to 220k), where
the turn count matters more than the plugin.

A second run exercised the `<agent_instructions>` path that `implement-slugify` never reaches, using the
review sample that selects `hve-core:code-review-functional`:

| Cell | Sample | hve_check | artefact_reported | artefact_quality | hve_artefact_used | Tokens | Time |
|---|---|---|---|---|---|---|---|
| `generic+hve` | `review-functional-cart` | 0.000 | 1.000 | 1.000 | 1.00 | 24,829 | 24.3 s |

The agent body was embedded and scored (`agent/code-review-functional: the hve-core:code-review-functional
agent body was embedded in the system prompt`), and the review itself found all five planted defects, which
`artefact_quality` confirms. `hve_check` still failed, on the *format*: the findings file used
`severity: "medium"`, `line` and `end_line`, and no `title` or `verdict`, where the skill's
`references/output-formats.md` and `severity-taxonomy.md` pin `Medium`, a `lines` range, a title and a
top-level verdict. The agent read the skill's `SKILL.md`, which is why the evidence scorer credits it, but
not the reference files that `SKILL.md` points at. This is the difference the matrix exists to show: the
CLI's `skill` tool injects that content into the conversation, while the generic harness gets it only if
the model chooses to read it.

## Deviations and caveats

* `artefact_reported` is the built-in `includes` scorer, but with the artefact path as its target rather
  than the sample's descriptive target; the dataset has no literal-answer samples, so this is where the
  built-in string scorer earns its place. `artefact_quality` likewise wraps `model_graded_qa` to feed it
  the artefact's text, since the final message alone would not show the reviewer's findings.
* `hve_artefact_used` recognises a selected custom agent by finding the first body line of its
  `*.agent.md` (its H1) as a whole line of a bridged system prompt; the CLI embeds the whole body in an
  `<agent_instructions>` block, which both the generic briefing and the fake reproduce. Sub-agent dispatch
  (`subagent.started`) is recognised from the CLI's JSONL only; `session.skills_loaded`, although the CLI
  flags it `ephemeral`, is kept on the transcript as the weak evidence of a skill that was offered. Reads
  are recognised from either trail, which is why the rules exclude heredoc bodies, the CLI's bash
  `description` and failed calls: under the generic harness a write is a `bash` call like any read, and the
  plugin's `copilot-tracking` instructions ask the agent to cite its sources in the file it writes.
* The evidence scorer measures *use*, not benefit: the floors of `generic+hve` (0.5 and 0.57 on the two
  implement samples) come from the briefing having listed the components, and a cell with no framework has
  no such score at all, so the honest cross-cell comparison is `hve_check`, `artefact_reported` and
  `artefact_quality`.
* Under `generic+hve` the demo does the plugin runtime's job itself, because there is no CLI to do it: the
  briefing embeds the sample's agent body in `<agent_instructions>` and describes the plugin's layout, and
  "loading a skill" is a `bash` read of a `SKILL.md`. What that imitation does not reproduce is the rest of
  what the CLI's `--agent` and `--plugin-dir` do — the advertised tools are not cut down to the agent's
  front-matter `tools:` list (the loop always has `bash` and `submit`), there is no `skill` tool and so no
  injected `skill-context` message, and no sub-agent can be dispatched. So `generic+hve` measures the
  plugin's *content*, not a second implementation of its runtime.
* `--framework none` removes the framework, not every scaffold: the `.github` overlay stays in all four
  cells by design, and under the copilot harness the CLI still brings its own ~32 KB system prompt, its 17
  built-in tools and its table of the workspace's instruction files. The baseline of the matrix is
  `generic+none`, and `copilot+none` is what the CLI alone adds to it.
* The fake CLI supports the chat-completions wire only (`COPILOT_PROVIDER_TYPE=openai`); `--route
  anthropic` is ignored under `--fake`. Its `create` tool overwrites an existing file (the real one refuses).
* `--sandbox local` runs the host's `copilot` (or downloads a Linux tarball it cannot run on macOS); it
  points `--plugin-dir` at the host copy of the plugin and copies nothing to `/opt`. That host copy is also
  what `generic+hve` reads the agent bodies from; a `--plugin-dir` that exists only inside the sandbox
  leaves the briefing's fallback sentence (read your own agent file first) instead. It is a demo path,
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
