# SWE showcase design: Inspect eval components and Inspect SWE agents in C#

This document is the specification for three new projects in `InspectAzureAI.sln`. It is written for
engineers (human or agent) implementing one slice each, so the shared contracts are spelled out as C#
declarations. Where a slice ports Python code, the Python source of truth is named so behaviour can be
checked line by line.

Reference material (read-only):

| What | Where |
|---|---|
| inspect_ai (Python) | `/Users/kevinburrowes/Documents/code/inspect_ai/src/inspect_ai` |
| inspect_ai core reference (signatures + file:line) | `<scratch>/py-core-reference.md` |
| inspect_swe checkout | `<scratch>/inspect_swe/src/inspect_swe` |
| Claude Code port spec (from inspect_swe) | `<scratch>/claude-code-port-spec.md` |
| mini-swe-agent upstream checkout | `<scratch>/mini-swe-agent/src/minisweagent` |
| This repo's Provider API + conventions | `<scratch>/cs-api-reference.md` |

`<scratch>` = `/private/tmp/claude-501/-Users-kevinburrowes-Documents-code-inspect-ai/a680ba8b-5463-4d31-b5bc-1801a274316c/scratchpad`.

## 1. Goal and scope

Add to the solution:

1. **`InspectAzureAI.Eval`** — a minimal, faithful port of Inspect's core eval components: Datasets and
   Samples, Tasks, Solvers and TaskState, Scorers and Metrics, Agents, Tools, Sandboxes (Docker and
   local), the `Model` wrapper (retry loop, limits, transcript), the sandbox **agent bridge**, an eval
   runner and a JSON eval log.
2. **`InspectAzureAI.Swe`** — ports of two Inspect SWE agents: **mini-swe-agent** (native C# port of the
   upstream v2 `DefaultAgent` tool-calling loop, executing bash in the sandbox) and **Claude Code**
   (the real CLI binary running inside the sandbox, with its Anthropic API calls proxied to the host
   bridge and served by our Azure providers).
3. **`InspectAzureAI.SweShowcase`** — a console app with built-in tasks that runs either agent against a
   Docker sandbox using the existing `AzureAIModelApi` / `AnthropicFoundryModelApi` providers on the
   user's Microsoft Foundry resource. It also runs fully offline (`--fake --sandbox local`).

Plus two xunit test projects: `tests/InspectAzureAI.Eval.Tests` and `tests/InspectAzureAI.Swe.Tests`.

Out of scope (document as such, do not stub): eval sets, approval policies, checkpointing,
centaur/human-cli mode, MCP servers and bridged host tools, skills, compaction strategies, the OpenAI
Responses and Google routes of the bridge, the Inspect log viewer format (`.eval` zip), model-info
registry/pricing, the `inspect` CLI.

## 2. Solution layout (already created; csproj files exist and build)

```
src/InspectAzureAI.Eval/              namespace InspectAzureAI.Eval.*
  Dataset/    Sample, SampleInput, IDataset, MemoryDataset, FieldSpec, JsonDataset, CsvDataset
  Tasks/      EvalTask, Epochs
  Solvers/    TaskState, Solver + Generate delegates, Solvers (chain, system_message, prompt_template,
              user_message, use_tools, generate, basic_agent)
  Scorers/    Score, ScoreValue, Target, Scorer delegates, Scorers (includes, match, exact, pattern,
              model_graded_qa, model_graded_fact), Metrics, ValueToFloat, Reducers, SampleScore
  Agents/     AgentState, Agent delegates, AgentDef, AgentAttempts, Agents.AsSolver
  Agents/Bridge/  AgentBridge (thread tracking), SandboxAgentBridge (HTTP proxy), AnthropicBridgeApi,
              CompletionsBridgeApi, SseWriter
  Tools/      ToolDef, ToolResult, tool errors, ToolExecutor, SandboxTools (bash, python)
  Sandbox/    ISandboxEnvironment, ExecResult, SandboxSpec, ISandboxProvider, SandboxEnvironments,
              SandboxRegistry, sandbox exceptions, Local/LocalSandboxProvider, Docker/DockerSandboxProvider
  Model/      Model, ModelRetryOptions, ModelEvent, IModelEventSink, ModelGenerateException, FoundryModels
  Context/    SampleContext (ambient per-sample services), Store, Limits, LimitExceededException, Transcript + events
  Runner/     Eval (RunAsync), EvalOptions, per-sample pipeline, console reporter
  Log/        EvalLog and friends, EvalLogWriter (JSON, snake_case)
  Testing/    ScriptedModelApi (IModelApi replaying canned turns), FakeSandboxEnvironment
src/InspectAzureAI.Swe/               namespace InspectAzureAI.Swe.*
  MiniSwe/    MiniSweAgent, MiniSweAgentOptions, MiniSweTemplates, TemplateRenderer
  ClaudeCode/ ClaudeCodeAgent, ClaudeCodeOptions, ClaudeCodeBinary (download/cache/install),
              ClaudeCodeEnv, ClaudeCodeCommand (argv), ClaudeCodeStream (JSONL framing), ClaudeCodeExit
  Util/       AgentPrompt (build_user_prompt), SandboxUtil (resolve_agent_cwd, detect platform, exec helper)
src/InspectAzureAI.SweShowcase/       namespace InspectAzureAI.SweShowcase
  Program.cs, Cli.cs, Tasks/*.cs, tasks/<name>/dataset.json(+files), sandbox/Dockerfile
tests/InspectAzureAI.Eval.Tests/, tests/InspectAzureAI.Swe.Tests/
```

Conventions (from `cs-api-reference.md` §1): net10.0, nullable, implicit usings, `TreatWarningsAsErrors`,
file-scoped namespaces, `sealed` types, records for data, `init`/`required`, `IReadOnly*` on public
surface, `.ConfigureAwait(false)` in library code, `///` summary on every public type naming the Python
source it ports, comments explain *why*. **No new NuGet packages** — BCL only (`System.Text.Json`,
`System.Net.HttpListener`, `System.Diagnostics.Process`). Tests: xunit 2.9.3, plain `Assert.*`, no mocking
libraries, `PascalCaseTests` classes, `snake_case_sentence` test methods, one `<summary>` per class.
Do not modify anything under `src/InspectAzureAI.Provider`, `src/InspectAzureAI.Sample`,
`tests/InspectAzureAI.Tests` or the root README (a later step updates the README).

## 3. Shared contracts (implement exactly; other slices code against these)

### 3.1 Sandbox (`InspectAzureAI.Eval.Sandbox`) — ports `util/_sandbox/environment.py`

```csharp
public sealed record ExecResult(bool Success, int ReturnCode, string Stdout, string Stderr);

public interface ISandboxEnvironment
{
    /// <summary>Hostname a process inside the sandbox uses to reach the host machine ("127.0.0.1" for the local sandbox, "host.docker.internal" for Docker).</summary>
    string HostAddress { get; }
    Task<ExecResult> ExecAsync(IReadOnlyList<string> cmd, string? input = null, string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null, string? user = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
    Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default);
    Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default);
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);
    Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class SandboxTimeoutException(string message, string truncatedOutput) : TimeoutException(message) { public string TruncatedOutput { get; } }
public sealed class SandboxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class OutputLimitExceededException(string limitDescription, string truncatedOutput) : Exception(...) { public string LimitDescription; public string TruncatedOutput; }
// ReadFileAsync: FileNotFoundException, UnauthorizedAccessException (permission), IOException("is a directory").

/// <summary>Ports SandboxEnvironmentSpec: Type is a registry key ("docker", "local"); Config is provider specific (docker: image name, a Dockerfile path, or a directory containing a Dockerfile; null = provider default).</summary>
public sealed record SandboxSpec(string Type, string? Config = null);

public sealed record SandboxEnvironments(IReadOnlyDictionary<string, ISandboxEnvironment> Environments, Func<bool, Task>? Cleanup = null);
// Ordering contract from Python: the FIRST entry is the default sandbox. Use an insertion-ordered dictionary.

public interface ISandboxProvider
{
    string Type { get; }
    Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default);      // e.g. build image once
    Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);
    Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default);
}

public static class SandboxRegistry { public static void Register(ISandboxProvider p); public static ISandboxProvider Get(string type); /* throws ArgumentException naming known types */ }
// "local" and "docker" are registered by a module initializer or static ctor in Eval.
```

Exec output is capped keep-the-tail at 10 MiB (`INSPECT_SANDBOX_MAX_EXEC_OUTPUT_SIZE` override), read
files at 100 MiB (`OutputLimitExceededException`). `ReadFileAsync` preserves newlines exactly.

**Local provider** (`Sandbox/Local/`): each sample gets a fresh temp directory (`Path.GetTempPath()/inspect-swe/<uuid>`)
used as the default cwd; `ExecAsync` runs the command on the host via `Process` with the temp dir as cwd;
relative paths in `WriteFileAsync`/`ReadFileAsync` resolve under it; `user` is ignored; `timeout` kills the
whole process tree (use `Process.Kill(entireProcessTree: true)`), raising `SandboxTimeoutException` with
the output captured so far. `HostAddress` = `"127.0.0.1"`. Cleanup deletes the directory.

**Docker provider** (`Sandbox/Docker/`): drives the `docker` CLI via `Process` (no Docker SDK).
- `TaskInitAsync`: resolve the image. `config` null → `python:3.12-slim-bookworm`; an existing directory
  or a path ending in `Dockerfile` → `docker build -t inspect-swe-sandbox:<sha256 of directory contents, first 12 hex> <dir>`
  (skip when `docker image inspect` succeeds); anything else → an image reference (pull is implicit on run).
- `SampleInitAsync`: `docker run -d --name inspect-swe-<12 hex> --add-host host.docker.internal:host-gateway [-w <WORKDIR from image>] <image> sleep infinity`
  (fall back to `tail -f /dev/null` if `sleep` is missing). Returns `{ "default": env }` and a cleanup that
  runs `docker rm -f` when `cleanup` is true (otherwise logs the container name so the user can inspect it).
- `ExecAsync`: `docker exec [-u user] [-w cwd] (-e K=V)* [-i] <name> <cmd...>`; stdin from `input`; when
  `timeout` is set wrap as `timeout -s KILL <secs> <cmd...>` inside the container and map exit code 137/124 to
  `SandboxTimeoutException`. Never build a shell string from `cmd` — pass argv through `ProcessStartInfo.ArgumentList`.
- `WriteFileAsync`: `docker exec -i <name> sh -c 'mkdir -p "$(dirname "$1")" && cat > "$1"' sh <path>` with the bytes on stdin.
  `ReadFileAsync`: `docker exec <name> cat <path>` (byte-exact); missing file → `FileNotFoundException`.
- `HostAddress` = `"host.docker.internal"`. Task cleanup: nothing (images are kept).
- Errors from the docker CLI (daemon down, image missing) → `SandboxUnavailableException` with the CLI stderr.

### 3.2 Context (`InspectAzureAI.Eval.Context`) — ambient per-sample services (Python `store()`, `sandbox()`, `transcript()`, `get_model()`, `score()`)

```csharp
public sealed class Store { object? Get(string key); T Get<T>(string key, T @default); void Set(string key, object? value); bool Contains(string key); IReadOnlyDictionary<string, object?> ToDictionary(); }

public sealed class SampleContext
{
    public static SampleContext? Current { get; }                                   // AsyncLocal
    public static IDisposable Begin(SampleContext context);                          // installs, restores previous on dispose
    public required Model ActiveModel { get; init; }
    public Store Store { get; } = new();
    public Transcript Transcript { get; } = new();
    public Limits Limits { get; init; } = new();
    public SandboxEnvironments? Sandboxes { get; init; }
    public Func<TaskState, Task<IReadOnlyList<Score>>>? Scorer { get; init; }        // Python score(state): runs the task scorers on the state
    public TaskState? SampleState { get; init; }                                     // Python sample_state(): set by the runner; score(AgentState) copies it via TaskState.WithMessages
    public ISandboxEnvironment Sandbox(string? name = null);                          // default = first entry; throws InvalidOperationException("No sandbox environment has been provided for the current sample.") when none
    public static SampleContext Require();                                            // Current ?? throw InvalidOperationException
}

public sealed class Limits { int? MessageLimit; int? TokenLimit; TimeSpan? TimeLimit; DateTimeOffset StartedAt; ModelUsage TotalUsage (accumulated); void AddUsage(ModelUsage u) /* then CheckTokenLimit */; void CheckMessageLimit(int count); void CheckTokenLimit(); }
public sealed class LimitExceededException(string type, string limitStr, double value) : Exception   // type: "message" | "token" | "time"

public sealed class Transcript { IReadOnlyList<TranscriptEvent> Events; void Add(TranscriptEvent e); void Info(string source, object? data = null); IDisposable Span(string name, string type = "span"); }
public abstract record TranscriptEvent { string Event { get; } DateTimeOffset Timestamp; string? SpanId; }
// ModelEvent, ToolEvent(Id, Function, JsonObject Arguments, string? Result, ToolCallError? Error, (int raw,int shown)? Truncated, TimeSpan? Working),
// SandboxEvent(Action "exec"|"write_file"|"read_file", JsonObject Input, JsonObject? Result), ScoreEvent(Score, Target, bool Intermediate),
// InfoEvent(Source, JsonNode? Data), ErrorEvent(Message, Traceback), SpanBeginEvent(Id, Name, Type, ParentId), SpanEndEvent(Id), StepEvent(Name, Type "solver"|"scorer", "begin"|"end")
```

### 3.3 Tools (`InspectAzureAI.Eval.Tools`) — ports `tool/_tool.py`, `_tool_def.py`, `model/_call_tools.py`, `tool/_tools/_execute.py`

```csharp
public delegate Task<ToolResult> ToolExecute(JsonObject arguments, CancellationToken cancellationToken);
public sealed record ToolResult { string? Text; IReadOnlyList<Content>? Contents; public static implicit operator ToolResult(string text); public static ToolResult FromContents(IEnumerable<Content>); }
public sealed record ToolDef(string Name, string Description, ToolParams Parameters, ToolExecute Execute) { public bool Parallel { get; init; } = true; public int? MaxOutput { get; init; }  public ToolInfo ToInfo(); }
public class ToolError(string message) : Exception; public sealed class ToolParsingError(string message) : ToolError;

public sealed record ExecuteToolsResult(IReadOnlyList<ChatMessage> Messages, ModelOutput? Output = null);
public static class ToolExecutor
{
    /// <summary>Ports execute_tools: acts only when messages[^1] is a ChatMessageAssistant with tool calls. Returns the ChatMessageTool messages (one per call, in call order).</summary>
    public static Task<ExecuteToolsResult> ExecuteToolsAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef> tools, int? maxOutput = null, CancellationToken cancellationToken = default);
}
```

`ExecuteToolsAsync` rules: a `ToolCall.ParseError` → `ToolCallError("parsing", message)` without running;
unknown function → `ToolCallError("unknown", $"Tool {name} not found")`; exception mapping per Python
(timeout→"timeout" with `"Command timed out before completing."`, `SandboxUnavailableException`→"sandbox_unavailable",
`UnauthorizedAccessException`→"permission", `FileNotFoundException`→"file_not_found" `$"File '{f}' was not found."`,
`OutputLimitExceededException`→"limit", `LimitExceededException`→"limit" `$"The tool exceeded its {type} limit of {limit}."`,
`ToolParsingError`→"parsing", `ToolError`→"unknown"; anything else propagates). Truncation: limit =
`tool.MaxOutput ?? maxOutput ?? 16 * 1024` bytes, keep-the-tail, wrapped exactly as
```
The output of your call to {tool_name} was too long to be displayed.
Here is a truncated version:
<START_TOOL_OUTPUT>
{truncated_output}
<END_TOOL_OUTPUT>
```
Parallel-safe calls run concurrently in stages; a `Parallel=false` call is a barrier. Each call produces
`new ChatMessageTool(content, toolCallId: call.Id, function: call.Function, error)` and a `ToolEvent` on the
current transcript (if any). Arguments are validated against `Parameters.Required` (missing → parsing error).

`SandboxTools.Bash(TimeSpan? timeout = null, string? user = null, string? sandbox = null)` — name `bash`,
description `Use this function to execute bash commands.`, param `cmd` (string, required); runs
`["bash", "--login", "-c", cmd]` in `SampleContext.Require().Sandbox(sandbox)`; result = `stdout + stderr`
(stderr appended after stdout, matching Python's `result.stdout + result.stderr`); timeout via the sandbox.
`SandboxTools.Python(...)` — name `python`, param `code`, runs `["python3"]` with `input = code`.

### 3.4 Model (`InspectAzureAI.Eval.Model`) — ports `model/_model.py` `Model.generate`

```csharp
public sealed class Model
{
    public Model(IModelApi api, GenerateConfig? config = null, ModelRetryOptions? retry = null);
    public string Name { get; }  public IModelApi Api { get; }  public GenerateConfig Config { get; }
    public Task<ModelOutput> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo>? tools = null, ToolChoice? toolChoice = null, GenerateConfig? config = null, StreamHandler? onStream = null, CancellationToken cancellationToken = default);
    public Task<ModelOutput> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolDef> tools, ToolChoice? toolChoice = null, GenerateConfig? config = null, StreamHandler? onStream = null, CancellationToken cancellationToken = default);
    public Task<ModelOutput> GenerateAsync(string input, ...);
}
public sealed record ModelRetryOptions(int MaxRetries = 5, TimeSpan? Timeout = null, double InitialBackoffSeconds = 3, double MaxBackoffSeconds = 60, Func<TimeSpan, CancellationToken, Task>? Delay = null);
public sealed class ModelGenerateException(string message, Exception? inner, ModelCall? call) : Exception;
public interface IModelEventSink { void OnModelEvent(ModelEvent e); }
public static class FoundryModels
{
    /// <summary>Route selection: names starting with "claude" (case-insensitive) or route "anthropic" → AnthropicFoundryModelApi, else AzureAIModelApi. Model name default: $INSPECT_AZUREAI_MODEL, else "gpt-5.4-mini".</summary>
    public static IModelApi CreateApi(string? model, string? route = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null, AzureAIClientSettings? settings = null);
    public static Model Create(string? model, GenerateConfig? config = null, string? route = null, object? streaming = null, IReadOnlyDictionary<string, object?>? modelArgs = null, AzureAIClientSettings? settings = null, ModelRetryOptions? retry = null);
}
```

`GenerateAsync` semantics (Python `_model.py:915-1300`, `_retry.py`): merge `config` over `Config`; if
`MaxTokens` is null use `Api.MaxTokens()`; collapse consecutive user messages when the api asks for it
(`AzureAIModelApi.CollapseUserMessages()`; Anthropic route: yes — the Messages API requires alternation; a
`ScriptedModelApi`: no) by concatenating content item lists; check `SampleContext.Current?.Limits`
message limit **before** the call (`CheckMessageLimit(input.Count)`) and token limit after; call
`Api.GenerateAsync` inside a retry loop: a **thrown** exception is retried when the api's `ShouldRetry`
says so (`ModelApiHooks.ShouldRetry(api, ex)` pattern-matches `AzureAIModelApi` / `AnthropicFoundryModelApi`,
else falls back to `HttpRetryUtil.IsRetryableHttpStatus(HttpRetryUtil.StatusCodeOf(ex) ?? 0)`), waiting
`RetryAfter` seconds when given, else exponential backoff with full jitter from `InitialBackoffSeconds`
capped at `MaxBackoffSeconds`, up to `MaxRetries` retries (N retries = N+1 attempts); `Delay` is injectable
so tests do not sleep. A returned `GenerateResult.Error` (terminal 400) is recorded and thrown as
`ModelGenerateException`. Every attempt produces a `ModelEvent` (input, tools, tool choice, config, output
or error, `ModelCall`, retry count, elapsed) delivered to `SampleContext.Current?.Transcript` and to any
sink installed via `Model.WithEventSink(sink)`/ambient `ModelEventSinks.Install(sink)`. Usage is added to
`Limits`. `output.Message.Source ??= "generate"`.

`Testing/ScriptedModelApi : IModelApi` — constructed with a list of `ScriptedTurn` (either a
`ModelOutput`, an `Exception` to throw, or a `Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolInfo>, ModelOutput>`);
records every request; `ModelName` configurable (default `scripted`); `MaxTokens()` = 2048; when turns are
exhausted it returns a final text output `"(scripted model has no more turns)"` unless `ThrowWhenExhausted`.
Helpers: `ScriptedTurn.Text(string)`, `ScriptedTurn.ToolCall(string function, object args, string? id = null, string? text = null)`,
`ScriptedTurn.Throw(Exception)`.

### 3.5 Solvers and TaskState (`InspectAzureAI.Eval.Solvers`) — ports `solver/_task_state.py`, `_solver.py`, `_chain.py`, `_prompt.py`, `_use_tools.py`, `_basic_agent.py`

```csharp
public sealed record SampleInput { string? Text; IReadOnlyList<ChatMessage>? Messages; implicit from string and from ChatMessage[]/List; IReadOnlyList<ChatMessage> ToMessages(); }
public sealed class TaskState
{
    public TaskState(string model, object sampleId, int epoch, SampleInput input, IEnumerable<ChatMessage> messages, Target? target = null, IReadOnlyList<string>? choices = null, ModelOutput? output = null, int? messageLimit = null, int? tokenLimit = null, IDictionary<string, object?>? metadata = null, Store? store = null, string? sampleUuid = null);
    string Model; object SampleId; int Epoch; SampleInput Input; string InputText /* last user message text; throws if none */; ChatMessageUser UserPrompt;
    Dictionary<string, object?> Metadata; List<ChatMessage> Messages { get; set; } ModelOutput Output { get; set; } Store Store; List<ToolDef> Tools { get; set; } ToolChoice? ToolChoice { get; set; }
    int? MessageLimit; int? TokenLimit; int TokenUsage /* from SampleContext.Current?.Limits.TotalUsage.TotalTokens */; bool Completed { get; set; } Target Target; Dictionary<string, Score>? Scores; string Uuid; IReadOnlyList<string> Choices;
    public TaskState WithMessages(IEnumerable<ChatMessage> messages, ModelOutput output);   // the copy score(AgentState) makes of sample_state()
}
public enum ToolCallsMode { Loop, Single, None }
public delegate Task<TaskState> Generate(TaskState state, ToolCallsMode toolCalls = ToolCallsMode.Loop, GenerateConfig? config = null, CancellationToken cancellationToken = default);
public delegate Task<TaskState> Solver(TaskState state, Generate generate, CancellationToken cancellationToken);
public static class Solvers
{
    public static Solver Chain(params Solver[] solvers);
    public static Solver SystemMessage(string template, IReadOnlyDictionary<string, object?>? parameters = null); // inserts after existing system messages; template vars from parameters + state.Metadata + state.Store
    public static Solver PromptTemplate(string template, ...);   // rewrites the user prompt; {prompt} = current user prompt text
    public static Solver UserMessage(string template, ...);
    public static Solver UseTools(params ToolDef[] tools); public static Solver UseTools(IReadOnlyList<ToolDef> tools, ToolChoice? toolChoice = null);
    public static Solver Generate(ToolCallsMode toolCalls = ToolCallsMode.Loop, GenerateConfig? config = null);
    public static Solver BasicAgent(Solver? init = null, IReadOnlyList<ToolDef>? tools = null, int? messageLimit = null, int? tokenLimit = null, int maxAttempts = 1, string submitName = "submit", string submitDescription = "Submit an answer for evaluation.", string? incorrectMessage = null, string? continueMessage = null, Func<ScoreValue, double>? scoreValue = null);
}
```

The runner supplies `Generate`: it calls `Model.GenerateAsync(state.Messages, state.Tools, state.ToolChoice, config)`,
appends the assistant message, sets `state.Output`, and for `Loop` executes tools via `ToolExecutor` and
repeats while the output has tool calls (`Single`: one tool round; `None`: no tool execution). Message
limit and token limit are enforced by `Limits` (a `LimitExceededException` ends the sample gracefully in
the runner — the sample is scored with whatever state it has, like Python).

`BasicAgent` semantics follow `_basic_agent.py`: default system message
`"You are a helpful assistant attempting to submit the correct answer. You have several functions available to help with finding the answer. Each message may perform one function call. You will see the result of the function right after sending the message. If you need to perform multiple actions, you can always send more messages with subsequent function calls. Do some reasoning before your actions, describing what function calls you are going to use and how they fit into your plan.\n\nWhen you have completed the task and have an answer, call the submit() function to report it."`,
submit tool with `answer` param, `continue_message` default
`"Please proceed to the next step using your best judgement. If you believe you have completed the task, please call the `submit()` tool with your final answer."`,
`incorrect_message` default `"Your submission was incorrect. Please proceed and attempt to find the correct answer."`;
on submit, `state.Output.Completion = answer` (rebuild the ModelOutput with the text) and score via
`SampleContext.Current.Scorer` when `maxAttempts > 1`.

### 3.6 Scorers (`InspectAzureAI.Eval.Scorers`) — ports `scorer/_metric.py`, `_scorer.py`, `_match.py`, `_pattern.py`, `_model.py`, `_metrics/*`, `_reducer/*`

```csharp
public abstract record ScoreValue { record Str(string Value); record Num(double Value); record Bool(bool Value); record List(IReadOnlyList<ScoreValue> Items); record Dict(IReadOnlyDictionary<string, ScoreValue?> Items);
    implicit from string, double, int, bool; JsonNode ToJson(); static ScoreValue FromJson(JsonNode?); string Text; }
public static class ScoreConstants { const string Correct = "C", Incorrect = "I", Partial = "P", NoAnswer = "N"; }
public sealed record Score(ScoreValue Value) { string? Answer; string? Explanation; string? Reason; IReadOnlyDictionary<string, object?>? Metadata; static Score Unscored(string? reason = null, ...) /* Value = Num(NaN) */; double AsFloat(); string AsStr(); }
public sealed record Target(IReadOnlyList<string> Values) { string Text /* "\n".Join */; implicit from string; }
public delegate Task<Score> Scorer(TaskState state, Target target, CancellationToken cancellationToken);
public sealed record ScorerDef(string Name, Scorer Score, IReadOnlyList<MetricDef> Metrics);
public sealed record SampleScore(Score Score, object? SampleId = null, IReadOnlyDictionary<string, object?>? SampleMetadata = null, string? Scorer = null);
public delegate ScoreValue Metric(IReadOnlyList<SampleScore> scores);
public sealed record MetricDef(string Name, Metric Compute);
public static class Metrics { MetricDef Accuracy(Func<ScoreValue,double>? toFloat = null); MetricDef Mean(); MetricDef Stderr(bool clusterByMetadata = false /*ignore*/); MetricDef Std(); }
public static class ValueToFloat { Func<ScoreValue, double> Default { get; } ; Func<ScoreValue,double> Create(ScoreValue correct, ScoreValue incorrect, ScoreValue partial, ScoreValue noanswer); }
public delegate Score ScoreReducer(IReadOnlyList<Score> scores);
public static class Reducers { ScoreReducer Mean(); Max(); Median(); Mode(); AtLeast(int k, double value = 1.0); PassAt(int k); string? NameOf(ScoreReducer r) /* reducer_log_name: "mean", "at_least_2", ...; null for a custom delegate */; }
public static class Scorers
{
    ScorerDef Includes(bool ignoreCase = true);             // target text appears in output completion
    ScorerDef Match(string location = "end", bool ignoreCase = true, bool numeric = false);
    ScorerDef ExactMatch(bool ignoreCase = true);
    ScorerDef Pattern(string pattern, bool ignoreCase = true, bool matchAll = false);
    ScorerDef ModelGradedQa(string? template = null, string? instructions = null, string? gradePattern = null, bool includeHistory = false, bool partialCredit = false, Model? model = null);
    ScorerDef ModelGradedFact(...same...);
    ScorerDef Custom(string name, Scorer scorer, params MetricDef[] metrics);
}
```
Default metrics: `Includes/Match/ExactMatch/Pattern` → `[Accuracy, Stderr]`; `ModelGraded*` → `[Accuracy, Stderr]`.
Port the model-graded templates and grade regex verbatim from `scorer/_model.py` (see
`py-core-reference.md` §4.9). `Stderr` = sample std / sqrt(n) (n < 2 → 0). `Accuracy` = mean of
`toFloat(value)`, skipping NaN (unscored). Score reducers for epochs: default `Mean`.

### 3.7 Agents (`InspectAzureAI.Eval.Agents`) — ports `agent/_agent.py`, `_as_solver.py`, `_types.py`

```csharp
public sealed class AgentState { public AgentState(IEnumerable<ChatMessage> messages); public List<ChatMessage> Messages { get; set; } public ModelOutput Output { get; set; } /* default new ModelOutput() */ }
public delegate Task<AgentState> Agent(AgentState state, CancellationToken cancellationToken);
public sealed record AgentDef(string Name, string Description, Agent Execute);
public sealed record AgentAttempts(int Attempts = 1, string IncorrectMessage = "Your submission was incorrect. Please proceed and attempt to find the correct answer.", Func<ScoreValue, double>? ScoreValue = null);
public static class Agents { public static Solver AsSolver(AgentDef agent); }   // state.Messages → AgentState → run → copy Messages/Output back; sets state.Completed if agent completed? (no — mirrors as_solver: just copies)
```

### 3.8 Agent bridge (`InspectAzureAI.Eval.Agents.Bridge`) — ports `agent/_bridge/types.py` (thread tracking), `util.py` (model resolution), `anthropic_api_impl.py`, `completions.py` and the SSE synthesis of `inspect_sandbox_tools/_agent_bridge/proxy.py`

```csharp
public sealed class AgentBridge
{
    public AgentBridge(AgentState state, Model model, IReadOnlyDictionary<string, Model>? modelAliases = null, int? retryRefusals = null, bool forwardGenerationConfig = false, IModelEventSink? modelEventSink = null);
    public AgentState State { get; }
    /// <summary>Resolve the requested model name (alias → Model; "inspect" or "inspect/<x>" or unknown → the default model), apply config precedence, generate (retrying refusals up to retryRefusals), then track state.</summary>
    public Task<ModelOutput> GenerateAsync(string requestedModel, IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig requestConfig, CancellationToken cancellationToken = default);
}

public sealed class SandboxAgentBridge : IAsyncDisposable
{
    /// <summary>Starts an HTTP server the sandboxed agent reaches at BaseUrl. port 0 = pick a free port. Binds 127.0.0.1 when sandbox.HostAddress is loopback, else 0.0.0.0. Every request must carry the bearer/x-api-key token (401 otherwise).</summary>
    public static Task<SandboxAgentBridge> StartAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port = 0, CancellationToken cancellationToken = default);
    public int Port { get; }  public string HostAddress { get; }  public string BaseUrl => $"http://{HostAddress}:{Port}";  public string AuthToken { get; }  // random 32-hex per instance
    public AgentState State => bridge.State;  public IReadOnlyList<Exception> Errors { get; }
    public ValueTask DisposeAsync();
}
```

Routes: `POST /v1/messages` (Anthropic Messages; `stream` true/false), `POST /v1/messages/count_tokens`
(returns `{"input_tokens": ceil(totalChars/4)}`), `POST /v1/chat/completions` (OpenAI chat completions,
`stream` true/false with `data: [DONE]`), anything else → 404 with an Anthropic-shaped error body. Bodies up
to 50 MiB. Use `System.Net.HttpListener`; streaming responses use chunked transfer and flush after every
event. A provider exception becomes an error response in the client's dialect (Anthropic:
`{"type":"error","error":{"type":"api_error","message":...}}` with status 500, or 400 for
`ModelGenerateException`; for a stream that already started: `event: error`). Exceptions are also
appended to `Errors`.

Anthropic translation (port `anthropic_api_impl.py`): `system` (string or text blocks) → one
`ChatMessageSystem` per block; `messages[]` with `content` string or blocks: `text` → `ContentText`,
`image` (`source.type == "base64"` → data URI; `url` → as-is) → `ContentImage`, `thinking` →
`ContentReasoning(thinking, signature)`, `redacted_thinking` → `ContentReasoning("", data, Redacted: true)`,
`tool_use` → `ToolCall(id, name, input)` on the assistant message, `tool_result` → its own
`ChatMessageTool(content, toolCallId: tool_use_id, error: is_error ? ToolCallError("unknown", text) : null)`
(a user message that contains tool_result blocks becomes ChatMessageTool messages, plus a ChatMessageUser for
any remaining non-tool blocks). Tools `{name, description, input_schema}` → `ToolInfo` with `ToolParams`
parsed from the schema (server tools with a `type` such as `web_search_20250305` are dropped);
`tool_choice` `auto|any|tool(name)|none`. `GenerateConfig` from the request when
`forwardGenerationConfig` (max_tokens, temperature, top_p, top_k, stop_sequences, thinking.budget_tokens →
ReasoningTokens); otherwise cleared. Response: `{"id":"msg_<uuid>","type":"message","role":"assistant","model":<requested name>,"content":[...],"stop_reason":...,"stop_sequence":null,"usage":{"input_tokens","output_tokens","cache_creation_input_tokens","cache_read_input_tokens"}}`
with `ContentText` → `text`, `ContentReasoning` → `thinking` (with `signature`) or `redacted_thinking`,
tool calls → `tool_use` blocks; stop reasons `Stop→end_turn`, `MaxTokens|ModelLength→max_tokens`,
`ToolCalls→tool_use`, `ContentFilter→refusal`, `Unknown→end_turn`. Streaming: `message_start` (message
with empty content and input usage), per block `content_block_start` / `content_block_delta`
(`text_delta`, `thinking_delta` + `signature_delta`, `input_json_delta` carrying the whole JSON in one
delta) / `content_block_stop`, then `message_delta` (`stop_reason`, output usage) and `message_stop`;
every event as `event: <type>\ndata: <json>\n\n`.

Thread tracking (`AgentBridge._track_state`, `types.py:264-575`): port faithfully — message fingerprints
(role + text), `_extends` prefix test, candidate promotion, descent grading against the initial non-system
messages (verbatim anchor / containment with the 20-char minimum), displacement and legacy length rules.
`State.Messages = input + [output.Message]`, `State.Output = output` on adoption.

### 3.9 Datasets and Tasks (`InspectAzureAI.Eval.Dataset`, `.Tasks`) — ports `dataset/_dataset.py`, `_sources/json.py`, `_sources/csv.py`, `_eval/task/task.py`, `epochs.py`

```csharp
public sealed record Sample(SampleInput Input) { object? Id; Target Target /* default "" */; IReadOnlyList<string>? Choices; IReadOnlyDictionary<string, object?>? Metadata; SandboxSpec? Sandbox; IReadOnlyDictionary<string, string>? Files; string? Setup; }
public interface IDataset : IReadOnlyList<Sample> { string? Name; string? Location; bool Shuffled; IDataset Filter(Func<Sample,bool> predicate, string? name = null); void Shuffle(int? seed = null); void Sort(...); }
public sealed class MemoryDataset : IDataset { MemoryDataset(IEnumerable<Sample> samples, string? name = null, string? location = null, bool shuffled = false); }
public sealed record FieldSpec(string Input = "input", string Target = "target", string Choices = "choices", string Id = "id", IReadOnlyList<string>? Metadata = null, string Sandbox = "sandbox", string Files = "files", string Setup = "setup");
public delegate IEnumerable<Sample> RecordToSample(JsonObject record);
public static class Datasets { IDataset Json(string path, FieldSpec? fields = null, RecordToSample? recordToSample = null, string? name = null, bool shuffle = false, int? seed = null, int? limit = null); /* .json array or .jsonl */  IDataset Csv(string path, ...); }
```
Record→Sample mapping per `dataset/_util.py` (`py-core-reference.md` §1.5): `input` string or message list
(`{"role","content"}` objects), `target` string or list, `files` dict, `sandbox` string or `[type, config]`,
`metadata` = named fields or all remaining fields when `Metadata` is null. `files` values: a path relative
to the dataset file, or inline text.

```csharp
public sealed record Epochs(int Count, IReadOnlyList<ScoreReducer>? Reducers = null);
public sealed record EvalTask
{
    public required string Name { get; init; }
    public required IDataset Dataset { get; init; }
    public Solver? Setup { get; init; }
    public Solver Solver { get; init; } = Solvers.Generate();
    public IReadOnlyList<ScorerDef> Scorers { get; init; } = [];
    public IReadOnlyList<MetricDef>? Metrics { get; init; }      // overrides scorer metrics
    public GenerateConfig Config { get; init; } = new();
    public SandboxSpec? Sandbox { get; init; }
    public Epochs? Epochs { get; init; }
    public bool FailOnError { get; init; } = true;
    public int? MessageLimit { get; init; }  public int? TokenLimit { get; init; }  public TimeSpan? TimeLimit { get; init; }
    public string Version { get; init; } = "0";  public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
```

### 3.10 Runner and log (`InspectAzureAI.Eval.Runner`, `.Log`) — ports `_eval/eval.py`, `_eval/task/run.py`, `log/_log.py`

```csharp
public sealed record EvalOptions { public required Model Model { get; init; } int? Limit; IReadOnlyList<object>? SampleIds; int? Epochs; int MaxSamples = 4; bool? FailOnError; string LogDir = "logs"; bool Cleanup = true; int? MessageLimit; int? TokenLimit; TimeSpan? TimeLimit; IEvalReporter? Reporter; }
public interface IEvalReporter { void SampleStarted(object id, int epoch); void SampleCompleted(EvalSample sample); void Message(string text); }
public static class Eval { public static Task<EvalLog> RunAsync(EvalTask task, EvalOptions options, CancellationToken cancellationToken = default); }
// The class shares its name with the `InspectAzureAI.Eval` namespace: consumers outside `InspectAzureAI.Eval.Runner` reference it
// through `using Eval = InspectAzureAI.Eval.Runner.Eval;` (placed after their namespace declaration, like the `Model` alias).
```
Per-sample pipeline (`run.py`): sandbox provider `TaskInitAsync` once → for each (sample, epoch) with
`MaxSamples` concurrency: `SampleInitAsync` → copy `Files` (relative paths land in the sandbox default cwd) →
run `Setup` script (`bash -c` in default sandbox; non-zero exit fails the sample) → build `TaskState`
(`Messages` = system messages? no: `Input.ToMessages()`; `Target`; limits) → `SampleContext.Begin(...)`
→ task `Setup` solver then `Solver` (a `LimitExceededException` is recorded and the sample proceeds to
scoring) → each `ScorerDef` → `EvalSample` (messages, output, scores, transcript events, usage, timing,
error) → sandbox cleanup (`Cleanup` option) in `finally`. Errors: with `FailOnError` the first failing
sample aborts the eval (status `error`); otherwise the sample is recorded with `Error`. Epoch scores are
reduced (default `Mean`) to per-sample scores; metrics run over the reduced scores. Write
`<LogDir>/<yyyy-MM-ddTHH-mm-ss>_<task>_<6hex>.json` via `EvalLogWriter` (snake_case JSON, `WriteIndented`).
`EvalLog` (subset of Python, same field names): `version=1`, `status`, `eval` (`task`, `task_version`,
`task_id`, `run_id`, `created`, `model`, `dataset{name,location,samples,sample_ids,shuffled}`,
`sandbox`, `config{limit,epochs,max_samples,message_limit,token_limit,time_limit,fail_on_error}`),
`results` (`total_samples`, `completed_samples`, `scores[]{name,scorer,metrics{name:{name,value}}}`),
`stats` (`started_at`, `completed_at`, `model_usage{model: usage}`), `error`, `samples[]`; plus a JSON-ignored
`Location` (Python `EvalLog.location`) set by the runner and by `EvalLogWriter.Read`.
`EvalSample`: `id, epoch, input, target, sandbox, files, setup, messages, output, scores, metadata,
store, events, model_usage, total_time, working_time, uuid, error, limit`.

## 4. Inspect SWE agents (`InspectAzureAI.Swe`)

### 4.1 mini-swe-agent — native port of upstream `minisweagent/agents/default.py` + `models/litellm_model.py` + `models/utils/actions_toolcall.py` + `environments/local.py`, configured by `config/mini.yaml`

inspect_swe runs the real Python package inside the sandbox and bridges its litellm calls. This port
runs the same loop natively in C# (the agent is ~100 lines) against `Model` and the sandbox, which keeps
the sandbox image free of Python packaging and exercises the Eval components directly. Record this as a
documented deviation.

```csharp
public sealed record MiniSweAgentOptions
{
    string Name = "mini-swe-agent"; string Description = "Minimal AI agent that solves software engineering tasks using bash commands.";
    string? SystemPrompt;                  // appended to the system template (after any task system messages), like inspect_swe
    AgentAttempts Attempts = new();  Model? Model;   // null → SampleContext.ActiveModel
    string? Cwd; IReadOnlyDictionary<string,string>? Env; string? User; string? Sandbox;
    int StepLimit = 0; int WallTimeLimitSeconds = 0; int MaxConsecutiveFormatErrors = 3; TimeSpan CommandTimeout = 30s;
    string SystemTemplate = MiniSweTemplates.System; string InstanceTemplate = MiniSweTemplates.Instance;
}
public static class MiniSwe { public static AgentDef Agent(MiniSweAgentOptions? options = null); }
```
Behaviour: templates are the `mini.yaml` texts verbatim (`{{task}}`, `{{system}} {{release}} {{version}} {{machine}}`
from `uname -s`, `-r`, `-v`, `-m` in the sandbox; the `{% if system == "Darwin" %}` block is omitted —
sandboxes are Linux). The task prompt = `AgentPrompt.BuildUserPrompt(state.Messages)` with system messages
prefixed as `System instructions:\n...\n\nTask:\n...` (inspect_swe). Messages sent to the model: the
mini-swe system message, the instance message, then the loop; the single tool is `bash` with param
`command` (description `Execute a bash command`). Each step: generate with `tools=[bash]`,
`toolChoice=Auto`; no tool calls → format error user message (`format_error_template`, including the
`finish_reason` length branch); unknown tool / bad args → format error; each `bash` action → exec
`["bash","-c","exec 2>&1\n" + command]` with cwd/env (`PAGER=cat MANPAGER=cat LESS=-R PIP_PROGRESS_BAR=off TQDM_DISABLE=1` + Env)
and `CommandTimeout`; timeout → `returncode -1` and `exception_info`; observation = the `mini.yaml`
JSON observation (`returncode`, `output` or `output_head`/`output_tail`/`elided_chars`/`warning` beyond
10000 chars) as a `ChatMessageTool` for the call id; submission when the first non-blank output line is
exactly `COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT` and returncode 0 → stop, `state.Output` = the last
assistant output with `Completion` replaced by the submission text (rest of the output). Limits:
`StepLimit`, `WallTimeLimitSeconds`, `MaxConsecutiveFormatErrors` (append an exit note and stop).
Attempts: after a submission, if `Attempts.Attempts > attempt` call `SampleContext.Scorer`, stop on
`ScoreValue(score) == 1.0`, else append `IncorrectMessage` (plus the resume reminder
`"When you are done, submit by running: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`"`) as a user message
and continue. The full trajectory (all messages, including the mini-swe system/instance messages) is the
returned `AgentState.Messages`. Store `mini_swe_agent_exit_status` / `_submission` in the sample store.

### 4.2 Claude Code — port of inspect_swe `claude_code()` per `claude-code-port-spec.md`

```csharp
public sealed record ClaudeCodeOptions
{
    string Name = "Claude Code"; string Description = "Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.";
    string? SystemPrompt; string? ReplaceSystemPrompt; IReadOnlyList<string>? DisallowedTools; AgentAttempts Attempts = new();
    Model? Model; string? ModelConfig /* presented name */; string? Effort; IReadOnlyDictionary<string, Model>? ModelAliases;
    string? PermissionMode; int? RetryRefusals = 3; int? RetryUncaughtErrors = 3; string? Cwd; IReadOnlyDictionary<string,string>? Env; string? User; string? Sandbox;
    string Version = "auto"; bool Debug; string? CacheDir; string? DownloadBaseUrl; HttpMessageHandler? HttpHandler; int Port = 0;
}
public static class ClaudeCode { public static AgentDef Agent(ClaudeCodeOptions? options = null); }
```
Implement spec §1.1 validation (same messages), §2 binary acquisition (`ClaudeCodeBinary`: base URL from
`https://claude.ai/install.sh` regexes with fallback `https://downloads.claude.ai/claude-code-releases`
when the fetch fails, version validation, `stable`/`latest` pointers, manifest + SHA-256, download with
retries `1,2,4s` on 5xx/transport errors, host cache `~/.cache/inspect-azureai/claude-code-downloads/claude-<version>-<platform>`
pruned to the 3 most recently accessed, offline fallback, sandbox install to
`/var/tmp/.5c95f967ca830048/claude-<version>-<platform>` + `chmod +x` as root, `auto`/`sandbox` → `which claude`),
§2.5 platform detection, §3.1 env (all variables; `ANTHROPIC_BASE_URL` = the bridge `BaseUrl`, i.e.
`http://host.docker.internal:<port>` for Docker; `ANTHROPIC_AUTH_TOKEN` = bridge `AuthToken`), §3.2 the
`$HOME/.claude/settings.json` `apiKeyHelper` write (quote safely), §4 argv (`--session-id`/`--resume`,
permission flags, `--model <presented>`, `--print --output-format stream-json --verbose`, `--debug`,
`--disallowed-tools`, `--system-prompt`/`--append-system-prompt` rules, `-- <prompt>`), the
`bash -c 'exec 0</dev/null; "$@"' bash <argv>` launch with cwd/env/user, §4.7 JSONL framing (lines to the
transcript as `InfoEvent("claude_code", line)`; store stdout/stderr under `claude_code_debug` when Debug),
§4.8 exit classification (refusal exit = success; retry uncaught errors; else `InvalidOperationException($"Error executing claude code agent {code}: {stderr}")`),
§6 attempts loop, §8 model resolution (presented/opus/sonnet/haiku/subagent all = presented unless aliases;
`Effort` → `GenerateConfig.ReasoningEffort` on the served model). The served model is
`options.Model ?? SampleContext.ActiveModel`; the bridge is `new AgentBridge(state, served, aliases, RetryRefusals)`
served through `SandboxAgentBridge.StartAsync(bridge, sandbox, Port)`. Return `sandboxBridge.State`.
Port allocation: ephemeral (`Port = 0`) instead of the Python store counter. Not ported: MCP, bridged
tools, skills, centaur, checkpointing, `transparent_proxy`, web-search grant (no server tools are ever
forwarded).

### 4.3 Shared (`InspectAzureAI.Swe.Util`)
`AgentPrompt.BuildUserPrompt(IReadOnlyList<ChatMessage>) → (string Prompt, bool HasAssistantResponse)`
(port `_util/messages.py`, incl. the `ValueError` when the last message is an assistant message);
`SandboxUtil.ResolveAgentCwdAsync(sandbox, user, cwd)`, `DetectPlatformAsync(sandbox)`, `ExecAsync(sandbox, cmd, user, cwd)`
(port `_util/sandbox.py`, same error strings).

## 5. Showcase app (`InspectAzureAI.SweShowcase`)

Commands (same flag parsing style and exit codes as `InspectAzureAI.Sample`: 0 ok, 1 eval had errors /
no samples correct?, 2 usage or prerequisite, 3 Azure/sign-in/runtime failure):

```
swe-showcase list
swe-showcase run --task <name> --agent mini-swe|claude-code|basic [--model <deployment>] [--route models|anthropic]
    [--limit N] [--sample-id ID]* [--epochs N] [--max-samples N] [--attempts N] [--sandbox docker|local]
    [--log-dir DIR] [--no-cleanup] [--max-tokens N|none] [--reasoning-effort LEVEL] [--model-arg k=v]* [--fake] [--debug]
swe-showcase show <log.json>          # print scores + per-sample summary from a log
```
Model creation via `FoundryModels.Create` (Entra ID through `az login`, env vars as the Sample). `--fake`
uses `ScriptedModelApi` with a per-task script that solves sample 1 (so an offline run reports 1 correct)
and `--sandbox local` (the fake mode refuses docker unless asked). Progress: one line per sample start and
completion (id, epoch, score, tokens, time), then a metrics table and the log path.

Built-in tasks (`Tasks/*.cs`, data under `tasks/<name>/` copied to output via `<None Include=... CopyToOutputDirectory=PreserveNewest>`):

1. `hello-swe` — 3 samples, sandbox = the showcase Dockerfile. Each sample's `metadata.check` is a bash
   command whose exit code decides correctness (scorer `ExecCheck`: runs `check` in the sandbox, `C`/`I`,
   explanation = output). Samples: create `hello.py` printing a given string; add a `--reverse` flag to a
   provided `words.py` (files); fix an off-by-one in a provided `stats.py` so `python3 stats.py` prints the expected number.
2. `pytest-fix` — 2 samples with a tiny Python package + failing pytest in `files`; scorer runs
   `python3 -m pytest -q` (exit 0 → `C`).
3. `system-explorer` — the inspect_swe docs example: 2 questions about the container ("Which Python
   version is installed?", "How many CPU cores does this machine report?"), `Scorers.ModelGradedQa()` with
   the task model as grader.

`sandbox/Dockerfile`: `FROM python:3.12-slim-bookworm`; `apt-get install -y --no-install-recommends bash curl ca-certificates git procps coreutils`; `pip install pytest`; `WORKDIR /workspace`; `ENV PYTHONUNBUFFERED=1 PIP_DISABLE_PIP_VERSION_CHECK=1`. Claude Code's binary is copied in at run time (no Node needed).

## 6. Testing requirements

Every slice ships tests in the matching test project; no test may need Docker or network unless it is
marked with the `[DockerFact]`/`[NetworkFact]` attributes (in `tests/InspectAzureAI.Eval.Tests/TestSupport.cs`:
skip when `docker version` fails or `INSPECT_SWE_SKIP_DOCKER=1`; network when `INSPECT_SWE_NETWORK_TESTS` is unset).
Use `ScriptedModelApi` + the local sandbox for behavioural tests. Test parallelization is disabled
per assembly (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because `SampleContext`
and the model-event sink are ambient.

## 7. Python → C# mapping and fidelity notes

Each slice appends its rows to `docs/swe-showcase.md` (created by the docs step) — a mapping table
(Python symbol → C# type) and numbered fidelity notes for every deliberate deviation. Known deviations so
far: (1) the bridge proxy runs on the host and is reached through `host.docker.internal`, not inside the
sandbox over file RPC; (2) mini-swe-agent is a native C# loop, not the Python package; (3) no
model-info registry; (4) `Model` retry defaults are bounded (5 retries, 60 s cap) rather than unbounded;
(5) the default Docker image is `python:3.12-slim-bookworm`; (6) the bridge requires a per-instance
random token; (7) ephemeral bridge ports; (8) eval logs are plain JSON, not `.eval` archives.
