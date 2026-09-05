# Port: model pricing data, cost computation, cost limit

## What was ported, from where

| Python | C# | Notes |
|---|---|---|
| `model/_model_data/model_data.py` (`ModelCost`, `ModelInfo`, `read_model_info`, `create_model_info`, `model_key`) | `Model/Cost/ModelCost.cs`, `ModelInfo.cs`, `ModelData.cs` | The database is loaded from JSON resources embedded in `InspectAzureAI.Eval` (`Model/Cost/ModelData/*.json` + `manifest.json`). |
| `model/_model_data/*.yml` | `scripts/convert-model-data.py` | Run with the inspect_ai venv python (needs pyyaml); converts each YAML file one-to-one (dates to `YYYY-MM-DD`), pins the file order in `manifest.json` and records the source commit (`76f1aa761`). |
| `model/_model_info.py` (`get_model_info`, `set_model_info`, `set_model_cost`, `clear_model_info_cache`, normalisation, org detection, fuzzy match) | `ModelInfoLookup.cs` | Same three-stage lookup (exact, case-insensitive index, fuzzy with the 60 threshold) over the same 781 keys; process-wide state under one lock. |
| `model/_model.py` (`compute_model_cost`, `CACHE_WRITE_1H_MULTIPLIER`, `sample_total_cost`, the cost part of `record_and_check_model_usage`) | `ModelCosts.cs`, hook in `Model/Model.cs` | Same arithmetic in the same order; results match the venv to the last bit. |
| `_eval/eval.py` `model_cost_config`, `model/_util.py` `resolve_model_costs` | `ModelCostConfig.cs`, `EvalOptions.ModelCostConfig`, `Eval.ResolveModelCosts` | JSON price file; a `cost_limit` without cost data is a `PrerequisiteError` before any sample runs. |
| `util/_limit.py` `cost_limit`, `record_model_cost`, `check_cost_limit`, `_CostLimit` | `Context/Limits.cs` (`CostLimit`, `CostUsage`, `RecordModelCost`, `CheckCostLimit`) | Trips with `LimitExceededException("cost")` and the Python message (`Cost limit exceeded. value: $0.0070; limit: $0.0050`), strict `>`, negative limits rejected, `Suspend()` stops enforcement like the other limits. |
| `log/_log.py` `EvalConfig.cost_limit`; `EvalSampleLimit` type `"cost"` | `EvalLog.EvalConfig.CostLimit`; `SampleRunner` maps the exception to `EvalSampleLimit("cost", limit, message)` | Written as `cost_limit`. |
| `_eval/task/task.py` `Task.cost_limit`, `eval(cost_limit=)` | `EvalTask.CostLimit`, `EvalOptions.CostLimit` | Option wins over task, like the other limits. |
| Port-only | `FoundryModelOverlay.cs` | Maps Foundry deployment names to database keys through `ReasoningParams.FamilyOf` (ARM `Format` when known, else the name) and `AzureAIModelApi.IsOpenAIModelName` / `IsMistralModel`. |

## Public C# API (namespace `InspectAzureAI.Eval.Model.Cost`)

- `record ModelCost(double Input, double Output, double InputCacheWrite, double InputCacheRead)` — $/million tokens.
- `record ModelInfo { Organization, Model, Snapshot, ReleaseDate, KnowledgeCutoffDate, ContextLength, OutputTokens, Reasoning, ReasoningEffortDefault, Family, Cost, InputTokensOverride, InputTokens }`.
- `ModelInfoLookup.GetModelInfo(string | Model)`, `SetModelInfo`, `SetModelCost` (throws `ArgumentException` for an unknown model, like Python's `ValueError`), `ClearModelInfoCache`.
- `ModelCosts.ComputeModelCost(cost, usage, cacheTtl = null)`, `ComputeCostForModel(model, usage)` (null when unknown or unpriced, never zero), `PriceOutput`, `SampleTotalCost`, `CacheWrite1hMultiplier`.
- `ModelCostConfig.EnvironmentVariable` (`INSPECT_AZUREAI_MODEL_COST_CONFIG`), `Apply(path)`, `Apply(dictionary)`, `Parse(json)`.
- `FoundryModelOverlay.BaseModelKey(name, format = null)`, `BaseModelKey(FoundryDeployment)`, `OrganizationKey(family)`, `RegisterDeployments(...)`.
- `Limits.CostLimit`, `Limits.CostUsage`, `Limits.RecordModelCost`, `Limits.CheckCostLimit`; `EvalTask.CostLimit`; `EvalOptions.CostLimit`, `EvalOptions.ModelCostConfig`; `EvalConfig.CostLimit`.

## Where cost is recorded

Exactly where Python records it. `record_and_check_model_usage` sets `usage.total_cost` on the output's usage object, which the returned output and the `ModelEvent` share; here `Model.GenerateAsync` prices the output (`ModelCosts.PriceOutput`) before the event is recorded, so `ModelEvent.Output.Usage.TotalCost`, the returned `ModelOutput.Usage.TotalCost`, `EvalSample.ModelUsage[model].TotalCost` (via `Limits.AddUsage`, which sums `total_cost` with the `ModelUsage` operator) and `EvalStats.ModelUsage[model].TotalCost` (the runner's aggregate) all carry it, serialised as `total_cost`. Python's `ModelEvent`, `EvalSample` and `EvalStats` have no separate cost field, so none was added. The cost limit is recorded after the token check, as in Python, so a token-limit trip leaves `Limits.CostUsage` unrecorded for that call while the usage dictionaries still carry the cost.

## Pricing data: what the embedded resources contain

The Python YAML files carry model metadata (context length, output tokens, dates, reasoning, aliases, versions) and **no prices**: in inspect_ai, prices come only from `set_model_cost()` or `--model-cost-config`. The embedded JSON is therefore a faithful conversion with `cost` unset for every entry (the test cross-checks all 781 entries against a dump of the Python database, `tests/.../fixtures/model-info/python-model-db.json`). Prices are supplied by:

1. The override file named by `INSPECT_AZUREAI_MODEL_COST_CONFIG`, applied on the first lookup (a missing or invalid file throws on every lookup until fixed — nothing is silently left unpriced). Same shape as Python's `--model-cost-config`, JSON only: `{ "gpt-5.4-mini": { "input": 0.25, "output": 2.0, "input_cache_write": 0, "input_cache_read": 0.025 } }`. All four prices are required. Keys are the names looked up: the bare Foundry deployment name (`Model.Name`), `azureai/<name>` (also applies to the bare name) or an Inspect string. An entry for a model the database does not know registers cost-only metadata; an entry for a known model keeps its metadata, so the file wins over the embedded data.
2. `EvalOptions.ModelCostConfig` (a path, applied at eval start after the environment file, so it wins) or `ModelInfoLookup.SetModelCost` / `SetModelInfo` in code.

## Foundry overlay

`Model.Name` for this solution's providers is the deployment name. `GetModelInfo` treats a bare name, or one prefixed `azureai/`, as a Foundry deployment: `FoundryModelOverlay.BaseModelKey` picks the organization from the vendor family (OpenAI, xAI → `grok`, DeepSeek, MoonshotAI, Mistral, Anthropic) and the name is looked up as `org/name`; otherwise the name falls back to Python's own `azureai/<name>` path. The overlay reproduces what Python's provider-resolving `get_model_info` returns (e.g. `azureai/grok-4.6` → xAI / Grok 4.6, which Python's direct lookup misses). `RegisterDeployments` aliases ARM deployments (custom deployment names) to their base model, never clobbering an existing override.

## Deviations from Python

- No prices are embedded, because the Python source has none (see above). The override file is JSON only (Python also accepts YAML through `resolve_args`); no YAML parser is available without a package.
- The provider-instantiation fallback of `get_model_info` (canonicalising a name via `get_model()`) is not ported; the Foundry overlay covers this solution's providers instead.
- Override-file entries for unknown models register cost-only metadata instead of raising (Python's `set_model_cost` raises); the `SetModelCost` API itself keeps Python's behaviour.
- `cache_ttl`: no provider in this solution exposes a prompt-cache TTL, so calls are priced at the default (5-minute) cache-write rate; `ComputeModelCost` still accepts `"1h"` and applies the 2/1.25 multiplier.
- `resolve_model_costs` checks the eval model only (this runner has no `model_roles`).
- The YAML file order Python gets from `Path.glob` is filesystem-dependent; `manifest.json` pins the order observed on the reference machine so the case-insensitive index resolves the same winners.

## Not ported

`TaskState.cost_limit` and the `set_active_sample_total_cost` / `cost_limit` display fields of the live sample view; `suspend_cost_limit` (Python has none either — only token and turn limits suspend; here `Limits.Suspend()` already covers cost); the `--cost-limit` / `--model-cost-config` CLI flags (no CLI in this solution); `model_roles` cost checks.

## Tests

`tests/InspectAzureAI.Eval.Tests/CostTests.cs`: cost arithmetic against nine venv-computed values (bit-exact, including 1h cache writes); the 781-entry database cross-check; lookup hits and misses against venv `_get_model_info_direct` results (Inspect names and bare deployment names); overlay mapping and ARM deployment registration; unknown model → null cost (not zero); override file wins over embedded data and its error paths; `SetModelCost`; `Limits` cost accumulation, strict trip, validation, suspension and the token-before-cost ordering; runner-level cost limit (sample limit type, scoring still runs, `cost_limit` / `total_cost` in the JSON log and round trip), the prerequisite error and `EvalOptions.ModelCostConfig`.
