# Port: connection concurrency, adaptive concurrency, throughput, sample scheduling

Source files: `util/_concurrency.py`, the connection-concurrency parts of `model/_model.py`
(`_connection_concurrency`, `ConnectionSlot`, `model_concurrency_key`, the success/retry
notification, `record_and_check_model_usage`'s throughput feed), `_util/retry.py`
(`report_http_retry`), `model/_retry.py` (the before-sleep retry-wait feed),
`model/_throughput.py`, `_eval/task/run.py` (`create_sample_semaphore`), and the design notes
`design/adaptive-concurrency.md` and `design/model-throughput.md`.

## What was ported (`src/InspectAzureAI.Eval/Concurrency/`)

| C# | Python |
|---|---|
| `Concurrency` (static): `GetOrCreateSemaphore`, `AcquireAsync` (an `IAsyncDisposable` `ConcurrencyLease`), `StatusDisplay`, `Semaphores`, `AdaptiveControllers`, `AddControllerCreatedObserver`, `TaskSampleSemaphore` / `RegisterTaskSampleSemaphore`, `Init`, `AdaptiveActive`, `ReportHttpRetry`, `HttpRetriesCount`, `BeginRequest` / `ActiveController` / `ActiveRequest` | `concurrency()`, `get_or_create_semaphore`, `concurrency_status_display`, `concurrency_semaphores`, `adaptive_controllers`, `add_controller_created_observer`, task sample semaphore registry, `init_concurrency`, `adaptive_active`, `report_http_retry`, `http_retries_count`, the `_active_controller` / `_request_had_retry` / `_request_was_cache_hit` ContextVars |
| `ResizableLimiter`, `ResizableSemaphore`, `IConcurrencySemaphore`, `ConcurrencyLease` | `ResizableLimiter`, `ResizableSemaphore`, the `ConcurrencySemaphore` protocol, `async with` exit |
| `AdaptiveConcurrency` (`Create`, `Parse`, `Validate`), `AdaptiveConnections` (`Disabled` / `Default` / `WithMax` / `From` / `Parse` / `Resolve`) | `AdaptiveConcurrency` incl. the shorthand and struct-form clamping, the `adaptive_connections` value forms and `resolve_adaptive` / `_parse_adaptive_connections_cli` |
| `AdaptiveConcurrencyController` (slow start, AIMD, saturation gate, cooldown debounce, `SetMax`, observers, bounded `History` of `LimitChangeRecord` / `LimitChangeReason`) | `AdaptiveConcurrencyController`, `_SaturationTrackingLimiter`, `LimitChangeRecord`, `_ceil_to_nice` / `_floor_to_nice` |
| `DynamicSampleLimiter`, `ISampleLimiter`, `SampleScheduler.CreateSampleSemaphore`, `ModelConcurrency.Key` | `DynamicSampleLimiter`, `create_sample_semaphore`, `model_concurrency_key` / `_connection_pool_key`, `DEFAULT_MAX_CONNECTIONS(_BATCH)` |
| `ConnectionSlot` | `ConnectionSlot` / `_held_connection_slot` |
| `Throughput` (static), `TokenBuckets`, `BackoffInterval`, `ModelThroughput`, `ModelThroughputView`, `EvalRunStats` | `_throughput.py` (`record_generate`, `record_retry`, `record_retry_wait`, `throughput_snapshot`, `throughput_view`, `throughput_report`, `throughput_footer_rate`, `init_model_throughput`) and the display footer counters |

Wiring into existing code (all additive): `IModelApi.MaxConnections()` / `ConnectionKey()` default
interface members; `ModelApiHooks.ConnectionKey` (endpoint-scoped keys for the two Foundry routes);
`Model.AdaptiveConnections`, `Model.ConcurrencyKey`, the connection slot held around the whole retry
loop, `ReportHttpRetry` on every retry decision, `Throughput.RecordRetryWait` before each backoff,
`Throughput.RecordGenerate` and the clean-success notification after each success;
`EvalOptions.MaxSamples` (now nullable) and `EvalOptions.AdaptiveConnections`; the runner's sample
semaphore from `SampleScheduler`, `IEvalReporter.Stats(EvalRunStats)` (default no-op, implemented by
`ConsoleEvalReporter` as the footer line) and `EvalStats.ConnectionLimitHistory`
(`connection_limit_history` in the log, Python's `ConnectionLimitChange` shape);
`ScriptedModelApi.ConnectionLimit` / `ConnectionScope` / `Gate` / `PeakConcurrentCalls`.

Adaptive connections are default-on exactly as in Python: a `Model` with no `MaxConnections` gates
its generates with a controller (10 / 20 / 100) keyed by `Model{ApiType}:{ConnectionKey}`, an explicit
`MaxConnections` wins silently, `AdaptiveConnections.Disabled` falls back to `IModelApi.MaxConnections()`.
The slot is held across retries and their backoff sleeps, as Python holds it; `ConnectionSlot` keeps
the release/reacquire seam Python's hard-pause gate uses.

## Deviations from Python, and why

- Every registry, limiter and controller is lock-protected: Python relies on its single event-loop
  thread, .NET generates complete on the thread pool. `ResizableLimiter` is a FIFO counting limiter
  written for this port (no `CapacityLimiter`); cancelling a queued acquire removes it without taking a
  slot, and an already-cancelled token never acquires.
- The registries are process-global but `Eval.RunAsync` does not reset them: `MatrixRunner` runs several
  evals concurrently in one process, so Python's per-run `init_concurrency()` would break sibling runs.
  Hosts and tests call `Concurrency.Init()` / `Throughput.Init()`; `HttpRetriesCount` is never reset (parity).
- `adaptive_connections` lives on `Model` / `EvalOptions` (`AdaptiveConnections`), not on `GenerateConfig`,
  because the Provider project cannot reference Eval types; `GenerateConfig.MaxConnections` remains the
  static setting. Batch mode has no .NET counterpart — `AdaptiveActive` and `CreateSampleSemaphore` keep a
  `batch` flag for the precedence rule only.
- `MaxConnections = 0` and a static limit below 1 throw `ArgumentOutOfRangeException` (Python treats a
  falsy 0 as unset and would build a semaphore nothing can enter). `AdaptiveConcurrency` validates in
  `Create` / `Parse` and at the first consumer rather than in a constructor (records with initializers).
- `EvalOptions.MaxSamples` changed from `int = 4` to `int?`: null derives the value as Python does
  (controller + 5 under adaptive, else `max_connections`) and is logged as unset. The only non-additive
  public change.
- Throughput is keyed by `Model.Name` (the same key the usage bookkeeping uses; there is no
  provider-qualified name in this port). `retry_waits_active` comes from the registry's own per-sample
  marks (set before a backoff, cleared when the retried call resolves) instead of a scan of active
  samples, without the 1 s memo. The `Retry-After` delay the .NET retry loop already honoured is kept
  (Python ignores it for backoff; both ignore it for the cooldown).
- The Foundry providers have no api key, so `ConnectionKey` is `{endpoint}:{model}` (Python: `{api_key}:{model}`).
- `ConnectionRequest.WasCacheHit` is carried for parity but never set: `Model` has no cache.
- The runner does not register its sample limiter under the task id (no in-run retries in this
  runner, and a never-reset registry would grow per run); the registry API is there for hosts.

## Not ported

The control channel (`ctl` endpoints and CLI, `set_max` retunes arrive only via the C# API), the
hard-pause gate, sandbox / subprocess limiters, `ensure_model_controller`'s eager creation, the
`count_tokens` pool, chatapi / SDK-hook retry feeds, the periodic `[Throughput]` trace line and the
retry-line enrichment (no trace subsystem), and sample waiting/working-time accounting (`Limits` has no
working time). Tests: `tests/InspectAzureAI.Eval.Tests/ConcurrencyTests.cs` (101 cases mirroring
`tests/util/test_concurrency.py`, `test_adaptive_concurrency.py`, `tests/model/test_model_throughput.py`
and `test_adaptive_connections.py`).
