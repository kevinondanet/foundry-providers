# Port: model extras (structured output, JSON schema, reflection tools, token estimation, stall scopes, fallbacks, roles)

Branch `port/model-extras`. Python sources: `model/_generate_config.py`, `util/_json.py`, `tool/_tool_info.py`,
`tool/_tool_params.py`, `tool/_tool_with.py`, `model/_tokens.py`, `model/_stream.py` (+ `design/stream-idle-timeout.md`),
`model/_model.py` (attempt/stall scopes, fallback rollup, roles), `model/_model_role.py`, `model/_util.py`
(`resolve_model_roles`), `_eval/loader.py` (`_merge_model_roles`), `model/_model_output.py` (`ModelFallback`).

## What was ported

| Area | C# (new unless noted) | Python |
|---|---|---|
| Every missing `GenerateConfig` field | `Provider/Core/GenerateConfig.cs` (edited): `AttemptTimeout`, `AdaptiveConnections`, `LogitBias`, `PromptLogprobs` (validated 1-20), `InternalTools`, `MaxToolOutput`, `CachePrompt`, `FallbackModels`, `Verbosity`, `Effort`, `ReasoningMode`, `ReasoningSummary`, `ReasoningHistory`, `ResponseSchema`, `ExtraHeaders`, `ExtraBody`, `Modalities`, `Cache`, `Batch`; `Merge` extended. `Provider/Core/GenerateConfigTypes.cs`: `ResponseSchema`, `BatchConfig`, `ImageOutput`, `OutputModality`, `GenerateConfigUtil` (`HasImageOutput`, `ImageOutputConfig`, `NormalizedBatchConfig`). A test checks `GenerateConfig.model_fields` against the C# properties. | `_generate_config.py` |
| Structured output on the wire | `Provider/Tools/ResponseFormat.cs`; `AzureAIModelApi.CompletionParams` adds `response_format: {type: json_schema, json_schema: {name, schema, description?, strict?}}` (extended fields stripped); `AnthropicFoundryModelApi.BuildRequest` adds `output_format` with `additionalProperties: false` recursively plus the `structured-outputs-2025-11-13` beta via `BetaHeader(config)`. | `_openai.py`, `_providers/anthropic.py` |
| `JsonSchema` record | `Provider/Core/JsonSchema.cs`: same fields as `JSONSchema`; `ToJson` (`exclude_none` dump), `FromJson`, conversions to/from `ToolParam`/`ToolParams`, `SetAdditionalPropertiesFalse`, an STJ converter so logs carry Python's shape. | `util/_json.py` |
| `JsonSchemaOf<T>` | `Provider/Core/JsonSchemaGenerator.cs`: records/classes/structs, enums, `Nullable<T>` and nullable reference annotations (`anyOf [T, null]`), arrays/enumerables/tuples, dictionaries, dates, `JsonNode`, `Description` / `JsonPropertyName` / `required` / constructor defaults; `ClsJsonSchema`, `PythonTypeToJsonType`. Cross-checked with the venv `json_schema()` for primitives, collections, enums and a pydantic model. | `json_schema`, `cls_json_schema` |
| Reflection tools | `Eval/Tools/ToolDef.Reflection.cs` (`ToolDef` is now `partial`): `ToolDef.FromMethod(Delegate|MethodInfo, ...)`, `ToolDef.ParseToolInfo`, `ToolDef.ToolWith`; argument binding with Python's `ToolParsingError` messages (strict: exact property names, no number-from-string coercion; schema validation happens in `ToolExecutor`). Cross-checked with `parse_tool_info` dumps. | `_tool_info.py`, `_tool_with.py`, `_call_tools.py` |
| Token estimation | `Eval/Model/TokenEstimation.cs` (all constants verbatim: fallback image/audio/video/document tokens, bytes-per-second/page, tokens-per-second/page), `Model.CountTokensAsync` (defers to `ICompactionModelApi` when the api implements it); `ContentDocument` added to `Provider/Core/Content.cs` (+ log converter case). | `_tokens.py` |
| Stall scopes | `Provider/Core/StallScope.cs` (`STALL_DEADLINE_BUMP_INTERVAL` = 1 s, capped at timeout/10); `ModelStreamObserver.ArmStallScope`/`Stall`, bumps on every report, `ModelStreamRequested()` true for an armed scope, nested `Install` inherits the outer scope; `Eval/Model/GenerateAttempt.cs` (internal) arms the scope and the `attempt_timeout` cancel scope per attempt; `Eval/Model/ModelTimeouts.cs`: `StreamIdleTimeoutException`, `AttemptTimeoutException` (Python's messages), always retried as transient. | `_stream.py`, `_model.py` |
| Fallbacks | `Provider/Core/ModelFallback.cs`, `ModelOutput.Fallback` (+ log converter); `Eval/Model/SampleModelAccumulators.cs` (fallback rollup by (model, fallback) pair, role usage); `Eval/Model/FallbackModelApi.cs` (port-only, see deviations); Anthropic route ignores `FallbackModels` with Python's one-time warning. | `_model_output.py`, `_model.py`, `anthropic.py` |
| Roles | `Eval/Model/ModelRoles.cs`: `ModelRole`, `ModelRoles.Resolve` (Python's error messages, per-role copies, single-model list collapse), `Merge`, ambient `Begin`/`Current`, `GetModel(role, default, required, config)`; `Model.Role`/`WithRole`/`WithConfig`; `ModelEvent.Role` (logged as `role`); `EvalOptions.ModelRoles`, `EvalTask.ModelRoles`, `Eval.RunAsync` installs the merged roles (eval-level wins). | `_model.py`, `_model_role.py`, `_util.py`, `loader.py` |

## Public C# API (summary)

`GenerateConfig.*` (above); `ResponseSchema(name, JsonSchema) { Description, Strict }`; `JsonSchema` / `JsonSchemaGenerator.JsonSchemaOf<T>()`;
`ResponseFormat.JsonSchemaResponseFormat` / `AnthropicOutputFormat`; `StallScope`; `ToolDef.FromMethod` / `ParseToolInfo` / `ToolWith`;
`TokenEstimation.*`, `Model.CountTokensAsync`; `ModelFallback`, `FallbackModelApi`, `SampleModelAccumulators.Begin/RecordFallback/RecordRoleUsage/SampleModelFallbacks/SampleRoleUsage`;
`ModelRoles.Resolve/Merge/Begin/Current/GetModel`, `Model.WithRole`, `AttemptTimeoutException`, `StreamIdleTimeoutException`.

## Deviations from Python (and why)

- **`response_format` on the azureai route is port-only.** Python's `azureai` provider ignores `response_schema`; the Foundry chat-completions gateway accepts the OpenAI shape, so it is forwarded as the `openai` provider does. It travels as a pass-through extra because the SDK's typed json-schema response-format types are internal in `Azure.AI.Inference 1.0.0-beta.5`. Unset `description`/`strict` are omitted rather than sent as `null`.
- **Client-side fallback is port-only.** Python's `fallback_models` is a server-side Anthropic first-party feature (unsupported on Azure, warned and ignored — ported as such). `FallbackModelApi` adds a client-side switch after N consecutive failures and stamps the same `ModelFallback` record, so the sample rollup and log see fallbacks the Python way.
- **Text token counting has no tiktoken.** No NuGet packages were allowed, so `CountTextTokens` uses a character heuristic (one token per four ASCII characters, one per other character) with Python's 10% buffer; a tokenizer delegate can be supplied. Media estimates are exact ports.
- **Schema generation follows pydantic for nested objects.** `additionalProperties: false` is set on every object `json_schema()` returns (including through collections/unions), but not on objects nested inside another object's properties (pydantic's `$defs` carry none). Constructor defaults and `Description` attributes are included (the pydantic path; dataclasses carry neither). Recursive types yield `{}` at the cycle. Port-only mappings: `Guid`→`uuid`, `Uri`→`uri`, `TimeSpan`→`duration`, `byte[]`→`byte` formats.
- **`ToolWith` returns a copy** (records are immutable; Python mutates in place). Tool results stringify with `str()` semantics (`True`/`False`, invariant numbers) and other objects as JSON.
- **Timeout scopes are cancellation tokens.** Python mutates an anyio cancel-scope deadline; here `StallScope` reschedules a `CancellationTokenSource` timer and the wrapper links it with the attempt timeout and the caller's token. `stream_idle_timeout` must be positive (Python would arm 0 and fire at the first chunk). The wrapper installs its observer only when chunks were requested (an `on_stream` handler or an idle timeout), so callers that never asked see no change.
- **Roles resolve names through `FoundryModels.Create`** (Python: `get_model(memoize=False)`); a factory can be supplied. `GetModel` without a role binding, default or active model falls back to `INSPECT_EVAL_MODEL` / `INSPECT_AZUREAI_MODEL`, else throws.
- **Union-typed config fields** (`cache_prompt`, `cache`, `batch`, `adaptive_connections`) are `object?` with documented accepted values, matching the existing `ToolParam.AdditionalProperties` convention.
- **Reflection tools bind strictly.** Argument binding uses exact (case-sensitive) property names and never reads a number from a string, and `ToolExecutor` validates every call against the tool's schema before running it (Python's `validate_tool_input`), so `{"a": "5"}` for an `int a` is reported with the jsonschema message rather than coerced (a wrong-case nested member is likewise rejected by the schema's `additionalProperties: false` and `required`).

## Not ported

`reasoning_history` bool migration and the unknown-field rejection of old logs (deserialisation concerns of the log reader); eval-level `role_usage` /
`model_roles` in `EvalSpec` and `model_fallbacks` / `role_usage` on `EvalSample` (log-schema area — the per-sample values are available from
`SampleModelAccumulators` inside the runner's scope); the live `inspect ctl` override of `stream_idle_timeout`; partial-output snapshots on the pending
event; `NoStreamDataError`; `count_tool_tokens` and server-side `tokenize`.
