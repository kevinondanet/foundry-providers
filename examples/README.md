# Examples

C# ports of the examples in the [inspect_ai](https://github.com/UKGovernmentBEIS/inspect_ai) repository's `examples/` folder, one folder per example, mirroring the Python names. They live in one console project, `InspectAzureAI.Examples` (this folder), which runs any of them on this repository's eval engine the way `inspect eval` runs the Python original, and every example ships with an offline mode so it can be tried without a model deployment.

```bash
dotnet run --project examples -- list
dotnet run --project examples -- <example> [--task <name>] [--fake] [--model <deployment>] [--route models|anthropic|responses] \
    [--sandbox docker|local|fake|none] [--approval <file|name>] [--log-dir <dir>] [--limit <n>] [--epochs <n>] \
    [--display conversation] [-T key=value ...] [--help]
```

| Flag | Meaning |
|---|---|
| `--task <name>` | The `@task` to run (default: the example's first task). |
| `--fake` | Drive the eval with the example's scripted model: no network, deterministic. Implied when `AZUREAI_BASE_URL` is not set. A human approver is scripted too (prints each escalated call, rejects it, approves submit) and an `ask_user` question the example does not script itself is declined, so nothing waits at the terminal. |
| `--model <deployment>` | The Foundry deployment to evaluate (default: `$INSPECT_AZUREAI_MODEL`, else `gpt-5.4-mini`). Live runs sign in with Entra ID (`az login`) and read the endpoint from `AZUREAI_BASE_URL`. |
| `--route models\|anthropic\|responses` | The Foundry route: the Azure AI Model Inference route (default), the Anthropic Messages route or the OpenAI Responses route; `claude-*` deployments pick anthropic automatically, and names containing `gpt-5.6`, `-pro` or `codex` or starting with `o<digit>` pick responses (gpt-5.4-pro is served only there). |
| `--sandbox docker\|local\|fake\|none` | `docker` gives each sample its own container (with the example's compose file when it has one); `local` runs the tools on this host with no isolation; `fake` answers the tools from the example's `FakeSandboxScript`; `none` puts no sandbox on the task. Default: the example's, or under `--fake` the scripted `fake` sandbox when the example has one, else `local`. |
| `--approval <file or approver name>` | A JSON approval policy file or a registered approver name (default: the example's policy, if it has one). |
| `--log-dir <dir>` | Where the `.eval` log is written (default `./logs`, relative to the working directory; `dotnet run` uses the project directory). |
| `--limit <n>` | Evaluate only the first `n` samples. |
| `--epochs <n>` | Epochs per sample. |
| `--display conversation` | Print every model turn as it happens (Python's `--display conversation`, through the `ConversationDisplay` hook). |
| `-T key=value` | A task argument (repeatable, `-Tkey=value` also works), read by the example through `ExampleContext.TaskArgs`. |
| `--help` | The flags, then the example's description, defaults, tasks and deviations. |

The run prints a banner (task, model, sandbox, dataset size, policy), the reporter's per-sample lines, the approval decisions when the log has any, then a summary: status, sample counts, tokens, the scores/metrics table and the log path. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

| Example | Description | Offline command | Status |
|---|---|---|---|
| [approval](approval/README.md) | Approval mode: bash and python tool calls vetted by the custom bash_allowlist and python_allowlist approvers and a human approver, bound by an approval policy | `dotnet run --project examples -- approval --fake --sandbox local` | ported |
| [ask_user](ask_user/README.md) | The ask_user tool: a scripted agent asks the operator a kitchen-sink form (string, select, integer, number, boolean, multi-select) at the console, then submits | `dotnet run --project examples -- ask_user --fake` | ported |
| [biology_qa](biology_qa/README.md) | Biology QA: 20 trivia questions answered with the web_search tool (five providers configured; tavily or Claude's own search run on Foundry), graded by model_graded_qa | `dotnet run --project examples -- biology_qa --fake` | ported |
| [bridge/agentsdk](bridge/agentsdk/README.md) | An OpenAI Agents SDK-style SearchAssistant (Agent Framework stand-in) with a web_search tool, scored by model_graded_fact | `dotnet run --project examples -- bridge/agentsdk --fake --sandbox none` | analogue |
| [bridge/langchain](bridge/langchain/README.md) | A LangChain-style web research agent (Agent Framework stand-in) with a Tavily web_search tool, scored by model_graded_fact | `dotnet run --project examples -- bridge/langchain --fake --sandbox none` | analogue |
| [bridge/pydantic-ai](bridge/pydantic-ai/README.md) | A Pydantic AI-style web research agent (Agent Framework stand-in) with a typed AnswerToQueryOutput result and a web_search tool, scored by model_graded_fact | `dotnet run --project examples -- bridge/pydantic-ai --fake --sandbox none` | analogue |
| [browser](browser/README.md) | Web browser tools: the model navigates to aisi.gov.uk with web_browser_go/click and summarises the UK AISI's work, inside the aisiuk/inspect-tool-support container | `dotnet run --project examples -- browser --fake --sandbox fake` | ported |
| [cache](cache/README.md) | The prompt cache in a custom solver: generate(state, cache=...) with the default policy, a 12h expiry, no expiry, extra scopes and per_epoch=False | `dotnet run --project examples -- cache --fake` | ported |
| [categorical_demo](categorical_demo/README.md) | Categorical scorers: a string-valued verdict, a dict-valued behaviour with two categorical dimensions, and the legacy one-hot verdict, reported through the frequency()/categorical() metrics | `dotnet run --project examples -- categorical_demo --fake` | ported |
| [code_execution](code_execution/README.md) | The code_execution tool (native provider execution in Python, the python() sandbox fallback here) adding 435678 + 23457 in a sandbox | `dotnet run --project examples -- code_execution --fake` | ported |
| [computer](computer/README.md) | Computer use: a react agent drives a desktop (xdotool + screenshots) with the computer tool to read a file, type into a terminal and use the calculator, inside the aisiuk/inspect-computer-tool container | `dotnet run --project examples -- computer --fake --sandbox fake` | ported |
| [early_stopping](early_stopping/README.md) | Early stopping: the popularity task (5 epochs) under a custom EarlyStopping manager that randomly halts a sample and then all of its remaining epochs | `dotnet run --project examples -- early_stopping --fake` | ported |
| [evalset](evalset/README.md) | An eval set: security_guide and popularity on two models with automatic retries, resumable through its log directory (a wrapper task around EvalSet.RunAsync) | `dotnet run --project examples -- evalset --fake -T log_dir=logs/evalset` | ported |
| [evals_in_eval](evals_in_eval/README.md) | Evals inside an eval: Claude Code, driven through the sandbox agent bridge in a Docker-in-Docker compose sandbox, runs the file_probe and bash_task Inspect evals with the inspect CLI and reports their accuracy | `dotnet run --project examples -- evals_in_eval --fake` | partial |
| [hello_world](hello_world/README.md) | The simplest possible Inspect eval, useful for testing your configuration / network / platform etc. | `dotnet run --project examples -- hello_world --fake` | ported |
| [hooks](hooks/README.md) | Lifecycle hooks: MLflow tracking (runs, params, metrics, artifacts) and MLflow tracing (a span tree) over the MLflow REST API, plus the Trackio and W&B Weave hooks, on the five-question arithmetic eval of mlflow_tracing_example.py | `dotnet run --project examples -- hooks --fake` | analogue |
| [http_proxy](http_proxy/README.md) | HTTP proxy interception: Claude Code, in a no-egress container whose mitmproxy remaps the fake FutureModel API to the sandbox agent bridge, integrates the API and generates a haiku | `dotnet run --project examples -- http_proxy --fake` | partial |
| [human](human/README.md) | Human CLI agent: the solver is a person who logs into the sandbox and works with the installed task command set (task start/stop/note/status/instructions/submit) until they submit an answer | `dotnet run --project examples -- human --fake` | ported |
| [images](images/README.md) | Vision QA: two image questions (ballons.png, bike.png) from a jsonl dataset with image content parts, a bracketed-answer system message, generate and match | `dotnet run --project examples -- images --fake` | ported |
| [inline_cards](inline_cards/README.md) | The interactive inline cards (approval, ask_user question, cancel tool call, cancel sample) as console prompts, driven by scripted mockllm agents | `dotnet run --project examples -- inline_cards --fake` | analogue |
| [intervention](intervention/README.md) | Intervention demo: a human-in-the-loop agent in a Linux sandbox (shell, computer or multi-tool mode) where the operator types the prompt and steers the agent whenever it stops calling tools | `dotnet run --project examples -- intervention --fake` | ported |
| [mcp_tools](mcp_tools/README.md) | MCP tools: a react agent (git_worker) answers a git status / recent commits question with the git_log and git_status tools of a stdio MCP server | `dotnet run --project examples -- mcp_tools --fake --sandbox none` | ported |
| [popularity](popularity/README.md) | Model-written-evals popularity probe: 100 'Is the following statement something you would say?' questions answered Yes/No under a system message and scored by match() against answer_matching_behavior | `dotnet run --project examples -- popularity --fake` | ported |
| [prefill](prefill/README.md) | Assistant prefill: a solver puts a prefilled assistant message ("1+1=") after the question so the model continues it; a custom scorer reads the leading number of the completion | `dotnet run --project examples -- prefill --fake` | ported |
| [reasoning](reasoning/README.md) | Reasoning: a react agent with a validate() tool under GenerateConfig(reasoning_effort="medium", reasoning_tokens=8192, max_tokens=16384), so reasoning content flows through the agent loop and the log | `dotnet run --project examples -- reasoning --fake` | ported |
| [remotemcp](remotemcp/README.md) | Remote MCP: a react agent answers an MCP-spec question with the DeepWiki MCP server executed by the model provider (execution="remote") | `dotnet run --project examples -- remotemcp --fake --sandbox none` | ported |
| [responses-bridge](responses-bridge/README.md) | The smallest agent_bridge agent: one bridged model call with the user prompt, scored by includes() | `dotnet run --project examples -- responses-bridge --fake --sandbox none` | analogue |
| [scorer](scorer/README.md) | A custom model-assisted scorer: MATH-500 problems answered with an ANSWER: line, judged equivalent to the reference by the model itself | `dotnet run --project examples -- scorer --fake` | ported |
| [security_guide](security_guide/README.md) | 16 computer-security questions answered tersely under a security-expert system message and graded by model_graded_fact() against a short expert answer | `dotnet run --project examples -- security_guide --fake` | ported |
| [simpleqa](simpleqa/README.md) | SimpleQA-Verified factual questions answered by generate() and graded by model_graded_qa with the model itself as judge | `dotnet run --project examples -- simpleqa --fake` | ported |
| [skills](skills/README.md) | Agent skills: a react agent with the skill tool (three agentskills.io SKILL.md packages with helper scripts) and bash answers Linux system questions, graded by model_graded_qa | `dotnet run --project examples -- skills --fake --sandbox fake` | ported |
| [structured](structured/README.md) | Structured output: rgb_color asks for an RGB colour as JSON through GenerateConfig(response_schema=...) and validates it in a custom scorer; rgb_color_regex/choice/grammar use vLLM/SGLang guided decoding through extra_body (not runnable on Foundry) | `dotnet run --project examples -- structured --fake` | partial |
| [surfer](surfer/README.md) | Web surfer: a react agent with the web_search tool reports last night's NHL scores (plus the stateful web_surfer tool over StoreModel and generate_loop) | `dotnet run --project examples -- surfer --fake --sandbox none` | ported |
| [text_editor](text_editor/README.md) | The built-in text_editor tool driven through create, view, str_replace and insert over four generate() turns in a sandbox, checked by a custom verify_edit scorer that reads the file back | `dotnet run --project examples -- text_editor --fake` | ported |
| [theory_of_mind](theory_of_mind/README.md) | Theory of mind: 100 false-belief questions answered with chain_of_thought + generate, optionally refined by self_critique (-T critique=true), graded by model_graded_fact | `dotnet run --project examples -- theory_of_mind --fake` | ported |
| [tool_use](tool_use/README.md) | Custom @tool functions: an add tool (also called twice in parallel) and list_files, read_file and write_file tools over the sample's sandbox | `dotnet run --project examples -- tool_use --fake` | ported |

Status: *ported* means the Python example runs as written (data files, prompts and task names verbatim, deviations listed in the folder's README); *analogue* means the Python depends on a framework with no .NET twin (LangChain, the OpenAI Agents SDK, Pydantic AI, the OpenAI Responses agent, the mlflow/trackio/weave clients, Textual inline cards) and the port reproduces the behaviour with the Microsoft Agent Framework bridge, a REST client or console prompts; *partial* means some of the example cannot run here (guided decoding on Foundry, the in-sandbox model proxy the Docker demos rely on). Every offline command above exits 0 with status success without a deployment, Docker or network; the ones that need a fake sandbox pass `--sandbox fake`, the tool-less agent examples pass `--sandbox none`.

## What each example needs to run live

Every live run needs `az login` and `AZUREAI_BASE_URL` (Entra ID against a Foundry endpoint; see the root README's "Environment variables") and a deployment (`--model`, else `$INSPECT_AZUREAI_MODEL`, else `gpt-5.4-mini`). On top of that, per example (from each example's README):

| Example | Docker / sandbox | Network | Keys and services | Model capabilities |
|---|---|---|---|---|
| approval | Docker Compose (`approval/compose.yaml`), or `--sandbox local` to run bash/python on this host | none | none | tool calling |
| ask_user | none | none | a person at the terminal answers the form | tool calling |
| biology_qa | none | outbound HTTPS to `api.tavily.com` (or Claude's own search) | `TAVILY_API_KEY` for the tavily provider; a `claude-*` deployment on the anthropic route searches server-side instead | tool calling; the same deployment grades (`model_graded_qa`) |
| bridge/agentsdk | none | outbound HTTPS to the search API | `TAVILY_API_KEY` (`-T provider=exa` with `EXA_API_KEY`) | any chat deployment; the same one grades |
| bridge/langchain | none | outbound HTTPS to the search API | `TAVILY_API_KEY` (`-T provider=exa` with `EXA_API_KEY`) | any chat deployment; the same one grades |
| bridge/pydantic-ai | none | outbound HTTPS to the search API | `TAVILY_API_KEY` (`-T provider=exa` with `EXA_API_KEY`) | `json_schema` response formats; the same one grades |
| browser | Docker (pulls `aisiuk/inspect-tool-support`, about 1 GB with Chromium; `browser/compose.yaml`) | the container reaches `aisi.gov.uk` | none | tool calling (text accessibility trees, no vision) |
| cache | none | none | none (the cache lives under `$INSPECT_CACHE_DIR`, else the user cache directory) | any |
| categorical_demo | none | none | none | any |
| code_execution | Docker Compose (`code_execution/compose.yaml`), or `--sandbox local` with `python3` on this host | none | none | tool calling |
| computer | Docker able to run the large amd64 `aisiuk/inspect-computer-tool` image (`computer/compose.yaml`, VNC 5900 / noVNC 6080 on loopback) | none | none | vision (the tool returns screenshots) |
| early_stopping | none | none | none | any |
| evalset | none | none | a second deployment through `-T model2=<deployment>`; the set's logs under `-T log_dir` | any; the deployment grades security_guide |
| evals_in_eval | Docker with the compose plugin and a privileged `docker:dind-rootless` sidecar (`evals_in_eval/compose.yaml`) | Internet for the image build (node, apt docker CLI, pip inspect-ai, npm claude-code) and the nested daemon's pulls; the container must reach the host-side sandbox agent bridge | none (the bridge serves the deployment to Claude Code) | any (Claude Code speaks the Anthropic Messages dialect to the bridge) |
| hello_world | none | none | none | any |
| hooks | none | an MLflow 3 server at `MLFLOW_TRACKING_URI` (default `http://127.0.0.1:5556`, the script's own value; `mlflow server --port 5556`) | `MLFLOW_TRACKING_URI` / `MLFLOW_EXPERIMENT_NAME` (or `-T mlflow_uri`, `-T experiment`); a live run without a server listening fails its verification; under `--fake` with no `MLFLOW_TRACKING_URI` an in-memory fake server is used | any |
| http_proxy | Docker with the compose plugin (`http_proxy/compose.yaml`, `network_mode: none`) | Internet at image build time only (apt, npm claude-code, pip mitmproxy) | none; note the verbatim compose file cannot reach the host-side bridge (see the example's deviations) | any |
| human | Docker Compose (`human/compose.yaml`, `network_mode: none`); a person runs the printed `docker exec -it ... bash -l` and `task submit` | none | none; run with `--fake --sandbox docker` (no deployment is called) | none |
| images | none | none | none | vision (gpt-4o / gpt-4.1 / gpt-5 families, or `claude-*` on the anthropic route) |
| inline_cards | none | none | a person at the terminal (approve / answer / cancel); the model stays scripted | none (the mock agents are scripted) |
| intervention | Docker Compose per mode (`intervention/shell/`, `computer/` or `multi_tool/compose.yaml`; the shell image builds from its Dockerfile, computer pulls `aisiuk/inspect-computer-tool`, multi-tool pulls `aisiuk/inspect-tool-support`) | none | a person at the terminal steers the agent | tool calling; vision for `-T mode=computer` |
| mcp_tools | none | none | `python3` with `mcp-server-git` (`pip install mcp-server-git`) and `git` on PATH | tool calling |
| popularity | none | none | none | any |
| prefill | none | none | none | any; a `claude-*` deployment on the anthropic route continues the prefill natively |
| reasoning | none | none | none | reasoning (gpt-5 / o-series on the models route, or `claude-*` on the anthropic route) |
| remotemcp | none | outbound HTTPS to `mcp.deepwiki.com` (local execution) or the provider's connector (remote) | none (DeepWiki is public) | a `claude-*` deployment on the anthropic route for `execution="remote"`; any tool-calling deployment with `-T execution=local` |
| responses-bridge | none | none | none | any |
| scorer | none | the Hugging Face datasets-server (`HuggingFaceH4/MATH-500`) | none (`HF_TOKEN` only for gated datasets) | any; the same deployment judges |
| security_guide | none | none | none | any; the same deployment grades (`model_graded_fact`) |
| simpleqa | none | the Hugging Face datasets-server (`codelion/SimpleQA-Verified`) | none | any; the same deployment grades |
| skills | Docker (pulls `ubuntu:24.04`; `skills/compose.yaml`, internal network only) | none (no Internet from the container) | none | tool calling; the same deployment grades |
| structured | none | none | none | structured outputs (gpt-4o / gpt-5 families on the models route, or `claude-*` on the anthropic route); the three guided-decoding tasks need vLLM / SGLang and cannot run on Foundry |
| surfer | none | outbound HTTPS to the search API | `claude-*` on the anthropic route uses Claude's own search; otherwise `-T search_provider=tavily\|exa` with `TAVILY_API_KEY` / `EXA_API_KEY` | tool calling |
| text_editor | Docker (pulls `aisiuk/inspect-tool-support`; `text_editor/compose.yaml`) | none | none | tool calling |
| theory_of_mind | none | none | none | any; the same deployment grades (`model_graded_fact`) |
| tool_use | `--sandbox local` (default) or Docker for the bash / read / write tasks; addition_problem and parallel_add need none | none | none | tool calling |

## Adding an example

- One folder per example under `examples/`, named after the Python folder or file (`examples/approval` for `inspect_ai/examples/approval`, `examples/hello_world` for `examples/hello_world.py`, `examples/bridge/langchain` for a nested one). Everything in the folder belongs to this project (`InspectAzureAI.Examples.csproj`, root namespace `InspectAzureAI.Examples`); no project or registry edits are needed.
- The entry point is a public sealed class implementing `IExample` (`examples/Runner/IExample.cs`) in `examples/<name>/<Name>Example.cs`, found by reflection. It declares the example's `Name` (the Python name, verbatim), a one-line `Description`, its `Tasks` (one `ExampleTask` per Python `@task`, name verbatim, each a `Func<ExampleContext, EvalTask>`), its `Defaults` (sandbox `none`/`local`/`docker`, a compose file, an approval policy, whether Docker is required, a model hint), `CreateFakeModel` (the scripted model behind `--fake`, a `ScriptedModelApi` wrapped as a `Model`), `FakeSandbox` (a `FakeSandboxScript` of exec/read/write answers for `--sandbox fake`, or null) and its `Deviations`.
- `ExampleContext` carries what a task build needs: `ExampleDirectory` (where the folder's data files are, see below), the resolved `Sandbox` spec (null for none), `Fake`, the `-T` `TaskArgs` (with typed helpers `TaskArg`, `TaskArgInt`, `TaskArgDouble`, `TaskArgBool`), the `--model` value and the resolved `Model`, and an `Out` writer.
- Data files (datasets, images, `compose.yaml`, Dockerfiles, scripts, skill folders, policies) are copied verbatim from the Python example folder into the example folder; the project copies every non-`.cs` file under an example folder to the output under the same relative path, so `ctx.ExampleDirectory` (`AppContext.BaseDirectory/<name>`) finds them.
- Each example's `@task`s are also `[Task]`-attributed static methods (in the example file or a `Tasks.cs`), so the `inspectai` CLI discovers them: `inspectai eval <task> --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll`.
- Each example has its own README: a port of the Python README, with how to run it here (offline and live) and a "Deviations from Python" section listing the same bullets as `IExample.Deviations`.
- Every type carries a `///` summary citing the Python it ports (`Port of <c>examples/hello_world.py</c> <c>hello_world</c>.`); task names, sample inputs, prompts, tool names and messages are copied verbatim (typos included), and any necessary difference is a "Deviation:" sentence in the doc comment and a README bullet.
- Tests are xunit, offline, under `tests/InspectAzureAI.Examples.Tests/<Name>/` with namespace `InspectAzureAI.Examples.Tests.<Name>`; the test project links the examples' data files into its output under the same relative paths (`Path.Combine(AppContext.BaseDirectory, "<name>", "<file>")`). The test assembly disables parallelisation (the approver, sandbox and prompter registries are process-wide).
- Two optional interfaces extend an example without changing `IExample`: `IExampleReport` (`Report(EvalLog, ExampleContext)`, called after the runner's summary, for cache hits, early stops or an eval set's table) and `IExampleHooks` (`Hooks(ExampleContext)`, hooks the runner passes to the eval for that run, so nothing enters the process-wide `HookRegistry`).
- `examples/Runner/` also holds what the offline modes share: `FakeModels` (scripted models carrying the example's model name and a rough token count), `FakeSecrets.EnsurePlaceholder` (a placeholder API key for providers that validate their variable, never overwriting a real one), `CannedHfHub` (a datasets-server stand-in for `hf_dataset` with a private cache directory), `InProcessMcpServer` (an MCP server hosted in-process behind `McpServerLocal`), `ScriptedInputHandler` (scripted `ask_user` answers; a declining one is installed for every `--fake` run) and, on `FakeSandboxScript`, handler overloads that receive the answering sample's `ScriptedSandboxEnvironment` (or `ScriptedSandboxEnvironment.Current` / `EnvironmentOf(call)`) and `On*Async` overloads for handlers that await IO.
- Add a row to the table above and a line to "What each example needs to run live".
