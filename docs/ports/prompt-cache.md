# Port: prompt cache (on-disk cache for model generate calls)

## What was ported

| Python | C# |
|---|---|
| `model/_cache.py` — `CachePolicy`, `_parse_expiry`, `CacheEntry`, `_cache_key`, `cache_store`, `cache_fetch`, `cache_path`, `cache_clear`, `cache_size`, `cache_list_expired`, `cache_prune` | `src/InspectAzureAI.Eval/Model/Cache/` — `CachePolicy.cs`, `CacheEntry.cs` (`CacheEntry` + `CacheKey`), `PromptCache.cs` (store/fetch), `CacheOps.cs` (path/clear/size/list-expired/prune), `CacheFile.cs` (internal on-disk shape) |
| `model/_model.py` — the `cache` argument of `generate`, the lookup before the provider call, the store after it, `ModelEvent.cache` | additive edits in `Model/Model.cs`, `Model/ModelEvent.cs`, `Model/ModelApiHooks.cs` (`BaseUrl`), `Log/Json/TranscriptEventConverter.cs` (`cache` field) |
| `event/_model.py` — `cache: Literal["read", "write"]` | `CacheMode.cs` (`CacheMode` enum + wire names) |
| `_util/appdirs.py` (over `platformdirs`) | `AppDirs.cs` |
| `_cli/cache.py` — list / clear / prune semantics, `_readable_size` | `CacheOps.CacheSize/CacheClear/CacheListExpired/CachePrune/ReadableSize` (no CLI; see "Not ported") |

Tests: `tests/InspectAzureAI.Eval.Tests/PromptCacheTests.cs` (41 cases): hit and miss through `Model.GenerateAsync`,
expiry grammar against values computed with the Python `_parse_expiry`, expiry honoured (an expired entry is a miss
and is deleted), per-epoch keys (both `CacheEntry` and the epoch flowing from `SampleContext.SampleState`), scopes,
key insensitivity to message ids and connection fields, key component order, prune/list-expired, corrupt entries,
content-filter outputs, clear (including path escapes), size grouping, cache path resolution, cancellation, store
failures, retry attempts, and the `ModelEvent.cache` round trip through the log JSON.

## Public C# API

- `CachePolicy { Expiry = "1W", PerEpoch = true, Scopes = {} }` (record), `CachePolicy.Default`, `FromString(expiry)`,
  `ParseExpiry(period)` / `TryParseExpiry`, `ExpirySeconds`, and `implicit operator CachePolicy?(bool)`
  (`true` → `Default`, `false` → null) so `cache: true` reads like Python.
- `Model.GenerateAsync(input, tools, toolChoice, config, cache, onStream, cancellationToken)` — all three overloads
  gained an optional `CachePolicy? cache = null` after `config` (Python's parameter order).
- `ModelEvent.Cache` (`CacheMode?`): `Read` for a hit (no provider call, `Call` null), `Write` for every attempt run
  under a policy (success, terminal error, retried exception), null without a policy. Serialised as `"cache": "read" | "write"`.
- `CacheEntry(baseUrl, config, input, model, policy, toolChoice, tools, epoch)` with `Key`; `CacheKey.Compute`,
  `CacheKey.Components` (the components in Python's order, for diagnosing why two calls key differently), `CacheKey.CanonicalJson`.
- `PromptCache.StoreAsync(entry, output, ct)` → `bool`, `PromptCache.FetchAsync(entry, ct)` → `ModelOutput?`,
  `PromptCache.EntryPath`, `CacheExpiry`, `IsExpired`.
- `CacheOps.CachePath(model = "")`, `IsInCache`, `CacheClear(model = "")`, `CacheSize(subdirs, files)` →
  `IReadOnlyList<ModelCacheSize>`, `CacheListExpired(filterBy)`, `CachePrune(files)`, `ReadableSize(bytes)`;
  `CacheOps.CacheDirVar` = `INSPECT_CACHE_DIR`.
- `AppDirs.UserCachePath(app)`, `AppDirs.InspectCacheDir(subdir)`.

## Semantics kept from Python

- Layout: `$INSPECT_CACHE_DIR/generate/<model>/<key>` or `<user cache dir>/inspect_ai/generate/<model>/<key>`
  (`~/Library/Caches/inspect_ai` on macOS, `$XDG_CACHE_HOME`/`~/.cache` on Linux, `%LOCALAPPDATA%\inspect_ai\inspect_ai\Cache`
  on Windows — the `platformdirs` locations). The model name is the directory, not a key component.
- Key components, in order: config minus `max_connections`, `adaptive_connections`, `max_retries`, `timeout`,
  `stream_idle_timeout`, `cache`, `batch`; the messages minus `id`; base url; tool choice; tools; expiry in seconds
  (so `7D` and `1W` key identically); scopes; and, when `PerEpoch`, the epoch. MD5 hex digest.
- The entry used for the lookup is built from the messages after the config system message is prepended and
  user messages are collapsed, and from the config after `max_tokens` is defaulted — exactly what the provider sees.
- The lookup runs on every attempt of the retry loop (a concurrent identical call may have cached between attempts).
- A hit is not counted against the sample's token limits; a store happens after the usage limit check, so an output
  that breaches the token limit is not cached.
- `content_filter` outputs are never stored (a cached refusal would be replayed on every refusal retry).
- An expired entry is deleted on fetch. `CacheClear` / `CacheListExpired` refuse model names that resolve outside
  the cache (`../..`). `CacheListExpired(filterBy)` lists only entries directly inside the named model directories.
  `CacheSize` ignores directories without files, filters by substring, groups explicit files by parent directory,
  and sorts by name. `CachePrune` re-reads each file and deletes only if it is really expired.

## Deviations from Python (and why)

1. **Key bytes differ.** Python hashes `str()` of pydantic dumps; the port hashes canonical JSON (keys sorted at
   every level, `json.dumps` separators and ASCII escaping via `PythonJson`). The components are the same in content,
   so the invariants hold, but a Python process and a .NET process never share entries. A consequence of sorting:
   the insertion order of `Scopes` does not matter here (in Python it does).
2. **Entry format is JSON, not pickle**: `{"expiry": ISO-8601 | null, "output": <ModelOutput as in the eval log>}`,
   written with the log serializer. Written to a temp file and renamed into place (Python writes in place);
   `*.tmp` files are ignored by the maintenance walks.
3. **Invalid expiry fails at policy construction** (`ArgumentException`), not at the first generate (`ValueError`).
   `ParseExpiry` keeps Python's `int()` leniency (sign, surrounding whitespace; a negative period expires at once).
4. **Epoch** is an explicit `CacheEntry` argument that `Model` reads from `SampleContext.Current?.SampleState?.Epoch`
   instead of a context variable; outside a sample it is null (Python: `None` when the variable was never set).
5. **No `GenerateConfig.Cache` field.** Python falls back to `config.cache`; here the policy is passed to
   `GenerateAsync` only (no edit to the Provider's `GenerateConfig`). Solver-level plumbing (`generate(cache=...)`)
   is outside this port.
6. **Unreadable entries are logged.** Python traces at debug level and silently misses; the port emits a
   `ProviderLogger.Warning` naming the file (fetch, list-expired, prune), leaves the file in place (the next
   successful generate overwrites it) and treats it as a miss. Store and delete failures warn and return false;
   `CacheClear` failures warn (Python: `logger.error`) and return false.
7. **Base url** comes from `ModelApiHooks.BaseUrl`: `AzureAIModelApi.EndpointUrl` (the resolved endpoint, which is
   what Python's `base_url` holds), `AnthropicFoundryModelApi.BaseUrl`, null for any other `IModelApi`.
8. **The hit event** carries `Completed` and `WorkingTime` (the fetch duration) because the port's event recorder
   always stamps them; Python's hit event is created without a completion.
9. `CacheSize`'s substring filter and model names use forward slashes on every platform, so `openai/gpt-4` filters
   the same way on Windows.

## Not ported

- The `inspect cache` CLI (rich tables, `--all`/`--model` flags, `ModelName` normalisation of `--model`);
  `ReadableSize` is provided for a future command.
- `trace_message` debug traces, the `emit_model_cache_usage` hook, and the `_request_was_cache_hit` marker for
  the adaptive concurrency controller (neither hooks nor adaptive connections exist in the .NET port).
- `examples/cache.py` and the `generate(cache=...)` solver argument.
