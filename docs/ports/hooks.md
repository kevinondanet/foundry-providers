# Port: lifecycle hooks (`inspect_ai.hooks`)

## What was ported

| Python | C# |
|---|---|
| `hooks/_hooks.py` — the 18 payload dataclasses, the `Hooks` base class (17 async callbacks, `enabled()`, `override_api_key()`), `@hooks(name, description)`, `get_all_hooks`, `has_api_key_override`, `override_api_key`, every `emit_*`, `_emit_to_all`, `start_sample_event_emitter` / `emit_sample_event` / `drain_sample_events` | `src/InspectAzureAI.Eval/Hooks/` — `HookEvents.cs` (payload records, Python names), `Hooks.cs` (abstract base, every callback a no-op virtual), `HookRegistry.cs` (`Register`/`Unregister`/`Lookup`/`Info`/`All`, `HasApiKeyOverride`, `OverrideApiKey`, `OverridesApiKey`), `HookEmitter.cs` (public `Emit*Async` entry points and `EmitToAllAsync`), `SampleHooks.cs` (per-attempt emissions and the background sample event emitter), `HookRun.cs` (run/task emissions and the run id), `HookContext.cs` (the ambient eval identity the model-level hooks read) |
| `hooks/_startup.py` — `init_hooks`, `_load_registry_hooks`, `_verify_all_required_hooks` (`INSPECT_REQUIRED_HOOKS`), the enabled-hooks banner | `HookStartup.cs` (`InitHooks`, `VerifyAllRequiredHooks`, `RequiredHooksVar`, `Reset`) |
| `model/_model.py` — `ModelAPI._apply_api_key_overrides`, the `has_api_key_override() and is_auth_failure(ex)` retry rule | `ApiKeyOverrides.cs` (`Apply`); `Model/ModelApiHooks.cs` gained `IsAuthFailure` |
| Emit sites: `_eval/eval.py` (`emit_run_start` / `emit_run_end`), `_eval/task/run.py` (`emit_task_start` / `emit_task_end`, `emit_sample_init` / `emit_sample_start` / `emit_sample_attempt_start` / `emit_sample_scoring` / `emit_sample_attempt_end` / `emit_sample_end`, the transcript event logger, the emitter start/drain), `model/_model.py` (`emit_before_model_generate`, `emit_model_cache_usage`, `emit_model_usage`, `log_model_retry` → `emit_model_retry`), `_eval/evalset.py` (`emit_eval_set_start` / `emit_eval_set_end`) | additive calls in `Runner/Eval.cs` (`HookRun` around the run, `InitHooks`, task start/end, one shared `EvalPlan`), `Runner/SampleRunner.cs` (`SampleHooks` per attempt), `Context/Transcript.cs` (`EventLogger`), `Model/Model.cs` (before-generate, cache usage, usage, retry, auth-failure retry); the eval set sites are the static `HookEmitter.EmitEvalSetStartAsync` / `EmitEvalSetEndAsync`, which `Runner/EvalSet/EvalSet.cs` calls around its passes, running each pass's tasks under one `HookRunGroup` (`Hooks/HookRunGroup.cs`: one run id, one run start naming every task, one run end) as Python's `eval_set` calling `eval()` once per pass does |

Tests: `tests/InspectAzureAI.Eval.Tests/HooksTests.cs` (28 cases). A recording hook asserts the exact lifecycle sequence
and payload contents of a run with an HTTP retry and a prompt-cache hit (ids, summaries, plan, retry cause and wait,
usage and retry counts, cache mode, the delivered event list equal to the transcript from the `solvers` span on and
all delivered before the attempt end); sample error retries, exhausted retries and no retries; epochs and multiple
samples; eval set id propagation; per-run hooks; hook failure semantics (logged; `LimitExceededException`
propagates, including "no attempt end without attempt start"; a hook's own `TaskCanceledException` is a hook failure
while run cancellation propagates into `RunEnd`); the required-hooks check and banner; the registry; the eval set
entry points; model hooks outside a run; pending events; and the api-key override family (`HasApiKeyOverride`,
`OverrideApiKey` ordering and failure logging, the three branches of `ApiKeyOverrides.Apply`, the 401 retry rule).

## Public C# API

- `abstract class Hooks` — `Enabled` (virtual, default true), `OnEvalSetStartAsync`, `OnEvalSetEndAsync`,
  `OnRunStartAsync`, `OnRunEndAsync`, `OnTaskStartAsync`, `OnTaskEndAsync`, `OnSampleInitAsync`,
  `OnSampleStartAsync`, `OnSampleEventAsync`, `OnSampleEndAsync`, `OnBeforeModelGenerateAsync`, `OnModelRetryAsync`,
  `OnSampleAttemptStartAsync`, `OnSampleAttemptEndAsync`, `OnModelUsageAsync`, `OnModelCacheUsageAsync`,
  `OnSampleScoringAsync` (all `(data, CancellationToken)` → `Task`), `OverrideApiKey(ApiKeyOverride)` → `string?`.
- Payload records: `EvalSetStart`, `EvalSetEnd`, `RunStart`, `RunEnd`, `TaskStart`, `TaskEnd`, `SampleInit`,
  `SampleStart`, `SampleEvent`, `SampleEnd`, `SampleAttemptStart`, `SampleAttemptEnd`, `ModelUsageData`,
  `ModelCacheUsageData`, `BeforeModelGenerate`, `ModelRetry`, `SampleScoring`, `ApiKeyOverride`; plus
  `RetryErrorInfo` (`_util/retry.py` `retry_error_type_status`) and `HookInfo` (the registry name/description).
- `HookRegistry.Register(hook, name, description)` (the `@hooks` decorator), `Unregister`, `Clear`, `All`, `Lookup`,
  `Info`, `HasApiKeyOverride`, `OverrideApiKey(envVarName, value)`, `OverridesApiKey(hook)`.
- `EvalOptions.Hooks` (per-run hooks, notified after the registry's) and `EvalOptions.EvalSetId`.
- `HookEmitter.Emit*Async(...)` for every event (an eval set or custom driver can emit the same events),
  `HookEmitter.EmitToAllAsync`, `HookEmitter.ActiveHooks`, `HookEmitter.HasApiKeyOverride`.
- `HookStartup.InitHooks(print)`, `VerifyAllRequiredHooks`, `RequiredHooksVar` (`INSPECT_REQUIRED_HOOKS`), `Reset`.
- `ApiKeyOverrides.Apply(apiKeyVars, apiKey)`.

## Semantics kept from Python

- Order and gating of every emission: run start → task start → per sample `init` (first attempt, before sandboxes) →
  `start` (first attempt) → `attempt start` (every attempt) → sample events (recorded from the `solvers` span until
  the end of scoring, queued to a background emitter, drained before the attempt end) → `scoring` (every attempt,
  error or not, not cancellable) → `attempt end` (only if the attempt started) → `sample end` (unless retried) →
  task end (after the log is written) → run end (once, with the exception that escaped the run). An eval set wraps
  its passes in eval set start/end and runs the tasks of a pass as one run (see `docs/ports/eval-set.md`).
- Model hooks: `before generate` on every provider attempt ahead of the cache lookup; `cache usage` on a hit (with
  usage); `usage` after a successful call with the attempt's call duration and the retry count; `retry` before each
  backoff with the wait, exception type and HTTP status. Eval ids come from the ambient sample (null outside one).
- `_emit_to_all`: enabled hooks in registration order; a throwing hook is logged
  (`Exception calling hook '<type>': <message>`) and the rest still run; `LimitExceededException` propagates;
  a limit raised from the sample event emitter is logged, not raised (Python's emit loop).
- `override_api_key` asks enabled hooks in order, logs a failing one; `has_api_key_override` counts disabled hooks
  too (Python's MRO check); an auth failure (401) becomes retryable when a hook overrides api keys.
- `init_hooks` runs once per process, verifies `INSPECT_REQUIRED_HOOKS` (Python's error text, set formatting
  sorted) and announces the enabled hooks; later calls are no-ops.

## Deviations from Python (and why)

1. **Registration is explicit.** Python instantiates and registers at import time and discovers entry points; the
   port has `HookRegistry.Register` (hosts call it before running) plus `EvalOptions.Hooks` for one run. The legacy
   `INSPECT_TELEMETRY` / `INSPECT_API_KEY_OVERRIDE` module hooks are not ported.
2. **`init_hooks` runs at `Eval.RunAsync`**, not in `get_model()` (there is no model registry); the banner goes to
   `EvalOptions.Reporter.Message`. A `PrerequisiteError` there still reaches `OnRunEndAsync` with the exception.
3. **`RunEnd.Logs` on an exception** holds the logs actually written (a cancelled run's log, since this port's
   `RunAsync` throws after writing it); Python passes an empty list with an exception and never throws for a cancel.
4. **`BeforeModelGenerate.SampleId` is the sample uuid** (stable across error retries, like every other payload);
   Python stamps the per-attempt `ActiveSample.id` there. Python documents that a hook may mutate the inputs, tools
   and config in place (reflected in the cache key and the call); the port's payload aliases the same lists but
   mutation is unsupported — its effect is undefined, so hooks must treat the payload as read-only.
5. **A hook's own `OperationCanceledException`** while the run is live is a logged hook failure; it propagates only
   once the run's token is cancelled (Python's `CancelledError` is a `BaseException` and always propagates).
6. **`ApiKeyOverride` is never consulted by the Foundry providers.** `AzureAIModelApi` and `AnthropicFoundryModelApi`
   authenticate with Entra ID only (`AzureHosting.ResolveAzureCredential`, `AzureAIClientSettings.TokenCredential`);
   there is no api-key environment variable to override, so `ApiKeyOverrides.Apply` is the entry point for a
   provider that does take one. Consequently the auth-failure retry cannot re-read a key: Python's `before_retry`
   closes and re-initialises the api (`initialize()`), which `IModelApi` has no equivalent of.
7. **`TaskStart.Plan` steps are named `setup` / `solver`** (the transcript span names): `Solver` delegates carry no
   registry name. The same instance is written to the log (`EvalLog.Plan`), as Python's recorder does.
8. **`Hooks.Enabled` is a property** (Python `enabled()` is a method); `HookInfo` replaces `RegistryInfo.metadata`.

## Not ported

- Entry-point discovery, `registry_find` and the `get_all_hooks` cache (`HookRegistry.All` is a snapshot array).
- The legacy telemetry / api-key-override module hooks (`hooks/_legacy.py`) and `send_telemetry_legacy`.
- `emit_launch_handoff` (`inspect ctl`), `SampleQueueHooks` (queue lifecycle) — not hook subscribers.
- Python's `on_sample_event` re-delivery of a completed pending event: this transcript records events once, complete.

Direct OpenAI and Anthropic factory instances consult enabled credential override hooks for every request, including authentication retries. Foundry remains Entra-only. See [direct providers](../direct-providers.md).
