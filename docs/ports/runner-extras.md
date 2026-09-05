# Port: runner extras (error policy, retries, early stopping, sample ids, scoped limits, working time)

## What was ported

| Python source | .NET |
|---|---|
| `_eval/task/error.py` (`SampleErrorHandler`, `_should_eval_fail`) | `Runner/FailOnError.cs` (the `bool | float` policy value with its JSON form), `Runner/SampleErrorHandler.cs` |
| `_eval/task/run.py` — `SampleAttempt`, the attempt loop of `task_run_sample`, `retry_on_error`, `_eval_retry_error`, the early-stopping calls, `monitor_working_limit`, `create_eval_sample` timing | `Runner/SampleRunner.cs` (rewritten: one attempt per call, `SampleAttempt`, `SampleResult.Retry`), `Runner/Eval.cs` (attempt loop, `SampleErrorHandler`, early stopping, end-of-run status) |
| `_eval/task/util.py` (`sample_id_filter`, `resolve_task_sample_ids`, `slice_dataset`), `dataset/_util.py` `normalise_sample_id` | `Runner/SampleIdFilter.cs` |
| `util/_early_stopping.py` | `Context/EarlyStopping.cs` (`IEarlyStopping`; `EarlyStop` and `EarlyStoppingSummary` are the `Log` records), `EvalResults.EarlyStopping`, `EvalTask.EarlyStopping` |
| `util/_limit.py` — `Limit`, `_Tree`, `apply_limits`/`LimitScope`, token/message/turn/time/working limits, the module functions, `sample_limits()` and its snapshot | `Context/Limit.cs`, `LimitTree.cs`, `LimitScope.cs`, `TokenLimit.cs`, `MessageLimit.cs`, `TurnLimit.cs`, `TimeLimit.cs`, `WorkingLimit.cs`, `SampleLimits.cs` |
| `event/_sample_limit.py` | `SampleLimitEvent` in `Context/TranscriptEventTypes.cs` (+ `sample_limit` in `TranscriptEventConverter`) |
| `_util/working.py` (`report_sample_waiting_time`, `sample_waiting_time`) | `WorkingLimit.ReportSampleWaitingTime`, `Limits.WaitingTime` |
| `log/_log.py` fields | `EvalConfig.FailOnError/ContinueOnFail/RetryOnError/TurnLimit/WorkingLimit`, `EvalSample.ErrorRetries`, `EvalRetryError` |

## Public API

- `FailOnError` (readonly record struct): `Always`, `Never`, `Threshold(double)`, `Fraction(double)`, `Count(int)`, `ShouldFail(errorCount, totalSamples)`; implicit from `bool`/`int`/`double`; serialises as `true`/`false`/number. Used by `EvalTask.FailOnError` (default `Always`), `EvalOptions.FailOnError` (null defers to the task) and `EvalConfig.FailOnError`. `ContinueOnFail` on task/options/config as in Python: never abort mid-run, apply the policy to the final count.
- `EvalOptions.RetryOnError` / `EvalTask.RetryOnError`: a sample that errors is re-run from scratch (fresh state, store, transcript and sandbox; same uuid) up to N times, back of the queue each time; the retried errors land in `EvalSample.ErrorRetries` as `EvalRetryError(Message, Traceback, TracebackAnsi, Events)` with the events from the attempt's last `ModelEvent` onward. Retried attempts are neither logged nor counted against `fail_on_error`.
- `IEarlyStopping` (`StartTaskAsync`, `ScheduleSampleAsync`, `CompleteSampleAsync`, `CompleteTaskAsync`) on `EvalTask.EarlyStopping`; a halted sample is never logged, counts in `TotalSamples` but not `CompletedSamples`, and `EvalResults.EarlyStopping` carries the `EarlyStoppingSummary`.
- `EvalOptions.SampleIds`: fnmatch glob patterns over normalised ids (digit strings are ints, ints are zero-padded to 20 places), `task:id` scoping, a reporter warning per unmatched pattern, `PrerequisiteError` when nothing matches.
- Scoped limits: `new TokenLimit(n [, "all"|"output"])`, `MessageLimit(n)`, `TurnLimit(n)`, `TimeLimit(TimeSpan?)`, `WorkingLimit(TimeSpan?)`, each entered with `.Enter()` inside a `using` and popped on dispose; `Limit.Apply(params Limit[])` / `Limit.ApplyAsync(limits, body, catchErrors, ct)` / `LimitScope.RunAsync` port `apply_limits`. The module functions live as statics on the concrete classes: `TokenLimit.RecordModelUsage/CheckTokenLimit/SuspendTokenLimit/TokenLimitUsage`, `MessageLimit.CheckMessageLimit`, `TurnLimit.RecordTurn/CheckTurnLimit/SuspendTurnLimit/TurnCount`, `WorkingLimit.RecordActiveWaitingTime/CheckWorkingLimit/WorkingLimitExceeded/ReportSampleWaitingTime`, `SampleLimits.Current()`. `LimitExceededException` gained `Limit`, `SourceLimit` and `ValueStr`.
- `Model.GenerateAsync` hooks: `MessageLimit.CheckMessageLimit(count, raiseForEqual: true)` before the call; `TokenLimit.RecordModelUsage` + `CheckTokenLimit` after a successful one and `TurnLimit.RecordTurn()` on every successful return — prompt-cache hits included, which also advance the conversation by one assistant message (Python records the turn in the outer frame of `generate`); retry waits reported as waiting time. `TaskState.MessageLimit`/`TokenLimit` write through to the sample-level scopes once the runner attaches them (`state.MessageLimit = 50` retunes the live limit, as in Python).
- `SampleLimitEvent(Type, Message, Limit)` is recorded on every trip — scoped limits, the flat `Limits` checks (message, token, time and cost), the runner's time and working deadlines — and round-trips through the JSON log as `sample_limit`.
- Timing: `EvalSample.StartedAt` is the sample start, `TotalTime` runs from after sandbox init (Python's `start_time`), `WorkingTime = TotalTime - Limits.WaitingTime`; both are null when init failed.

## How a new limit kind slots in

Derive from `Limit`, keep a `LimitTree<T>`, push/pop in `EnterCore`/`ExitCore`, record on `this` and ancestors, check root-first and raise `LimitExceededException(type, value, limit, message, source: this)` after `EmitLimitEvent`. Add a static record/check pair, a slot in `SampleLimits` (`Cost` is already reserved), a node in `SampleRunner`'s `Limit.Apply(...)`, and a case in `SampleRunner.SampleLimit`. The cost limit currently lives on the flat `Limits` class; the integrator can move it onto this base.

## Deviations from Python

- Names: C# forbids `Limit.Limit`, so the base exposes `LimitValue` (concrete classes keep a typed `Limit` property); `Exception.Source` exists, so the raising limit is `SourceLimit`. Module functions are statics on the concrete classes rather than free functions.
- `TimeLimit` is a `CancellationToken` deadline, not a cancel scope: the body observes an `OperationCanceledException`, and `LimitScope.RunAsync`/`ApplyAsync` (or the runner) convert it with `ThrowIfExceeded`. A limit above ~49 days is rejected by `CancellationTokenSource`.
- The working-limit monitor sleeps until the earliest possible trip instead of polling every second, and does not skip checks during an in-flight model call (retry waits are reported as they happen, so the usage is already accurate).
- Token limits meter `"all"` or `"output"`; formulas and the `500k`/`1m` string syntax are not ported. `FailOnError.Threshold` rejects negative/NaN values that Python would accept.
- The flat `Context/Limits` class stays for per-sample usage accounting (and still enforces limits when constructed with them, now emitting events); the runner no longer sets its message/token/time limits — enforcement is the scoped stack. `Limits.Suspend()` is kept for compatibility.
- A mid-run fail-on-error abort still computes results and scores (Python finishes with `results=None`); an end-of-run threshold failure marks the status `error` with no eval-level error, as Python does. `EvalTask.FailOnError` is non-nullable (default `Always`) where Python's task field can be `None`.
- Early stopping: `CompleteTaskAsync` runs only when the run neither failed nor was cancelled (as in Python) but its summary is recorded even when Python would have no results; a hook exception fails the eval with that error.
- `EvalRetryError.TracebackAnsi` is empty (no ANSI rendering); only model retry waits are reported as waiting time (no `concurrency()` / semaphore waits exist in this port).

## Not ported

`score_on_error`, operator interrupts and `TerminateSampleError` ("operator"/"custom" limits), sample sources and task-level retry/requeue history seeding, `limit` ranges `(start, stop)`, the `turn_count`/`token_limit_usage`/`time_limit` fields on `EvalSample` (available from `TurnLimit.TurnCount()` / `TokenLimit.TokenLimitUsage()`), `sample_limit_override_scope` live retuning from the control channel, `LimitExceededError.with_state`.
