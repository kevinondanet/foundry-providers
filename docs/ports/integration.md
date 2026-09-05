# Integration of the port branches

The thirteen area branches were merged into `port/inspect-full` in this order: log-schema, concurrency,
prompt-cache, metrics, cost, solvers, compaction, react-agents, sandbox-tools, builtin-tools, mcp-tools,
model-extras, runner-extras. Conflicts were resolved by keeping both sides' additions; where two areas defined
the same concept the definitions were unified as follows.

| Concept | Kept | Dropped / adapted |
|---|---|---|
| `connection_limit_history` element | `Log.ConnectionLimitChange` (log-schema) with concurrency's typed `LimitChangeReason` (Python literals on the wire) | concurrency's `LimitChangeRecord` (Python's own is a tuple alias); the controller's `History` is a list of the log record |
| `ModelEvent.cache` | prompt-cache's `CacheMode?` (`read`/`write`; any other value is a `JsonException`) | log-schema's `string?` |
| `CompactionEvent` | `Context.CompactionEvent` (log-schema); `metadata` is the base `TranscriptEvent.Metadata` | compaction's copy in `Model/Compaction` |
| `ModelFallback` | `Provider.Core.ModelFallback` (model-extras, positional with defaults) | log-schema's `Log.ModelFallback` |
| `Model.CountTokensAsync` | model-extras' instance method, which defers to `ICompactionModelApi.CountTokensAsync` when the api implements it (Python's provider override) and otherwise uses `TokenEstimation` | compaction's shadowed extension method; `TokenEstimator` remains the `ICompactionModelApi` default (compaction's tests pin it) |
| `adaptive_connections` | `Model.AdaptiveConnections` / `EvalOptions.AdaptiveConnections` (concurrency) with a fallback to the config-carried `GenerateConfig.AdaptiveConnections` (model-extras) through `AdaptiveConnections.FromConfigValue` (bool, int, `AdaptiveConcurrency`, CLI string; anything else is an `ArgumentException`) | nothing — the config value was previously carried but ignored |
| `SampleLimitEvent`, `EvalRetryError`, `EarlyStop`, `EarlyStoppingSummary` | log-schema's records (`Context/TranscriptEventTypes.cs`, `Log/EvalLogModels.cs`) | runner-extras' copies; `IEarlyStopping` stays in `Context` |
| `EvalConfig.fail_on_error` | runner-extras' `Runner.FailOnError` struct (bool or number, one converter), also on `EvalTask` / `EvalOptions` | log-schema's `bool? FailOnError` + `double? FailOnErrorThreshold` pair |
| Limit scopes | runner-extras' `Context.Limit` tree (`TokenLimit`, `MessageLimit`, `TurnLimit`, `TimeLimit`, `WorkingLimit`, `Context.LimitScope`) and `LimitExceededException.SourceLimit` | react-agents' scope is now `Agents.AgentLimitScope` (renamed from `LimitScope` to avoid the ambiguity with `Context.LimitScope`), an adapter that enters the set members of `AgentLimits` as nodes on the shared trees, so agent scopes and the sample's limits nest and are checked root-first as in Python; its `LimitSource` became `SourceLimit`; `Model.GenerateAsync` calls the tree statics |
| Sample cost limit | cost's flat `Context.Limits.CostLimit` enforcement (the sample runner still constructs `Limits { CostLimit }`) | not moved onto the scoped stack (runner-extras reserved a `SampleLimits.Cost` slot for a later `CostLimit : Limit`) |
| Sample loop in `Eval.RunAsync` | concurrency's `ISampleLimiter` lease and `EvalRunStats` reporting inside runner-extras' retry / early-stopping attempt loop | — |

Left as the areas reported them (not attempted here): the condensed-log pools, the `.eval` recorder, live sample
display fields, `EvalSample.model_fallbacks` / `role_usage` / `turn_count` / `token_limit_usage` population by the
runner, fork transcript events, MCP sampling, the Docker end-to-end sandbox-tools test, and every other item in the
individual `docs/ports/*.md` "not ported" lists.
