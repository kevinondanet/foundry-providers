# Port: built-in tools (think, web_search, read_file, list_files, grep, todo_write, update_plan)

## What was ported

| Python source (`src/inspect_ai/`) | .NET (`src/InspectAzureAI.Eval/Tools/Builtin/` unless noted) |
|---|---|
| `tool/_tools/_think.py` | `BuiltinTools.Think(description?, thoughtDescription?)` |
| `tool/_tools/_web_search/_web_search.py` | `BuiltinTools.WebSearch(params WebSearchProviderSpec[])`, `WebSearch(providers, HttpMessageHandler?)`, `WebSearchProviders`, `WebSearchProviderSpec`, `WebSearchProviderConfig` (`Normalize`, `HasExternalProvider`, `CreateExternalProvider`, `ExplicitProviders`) |
| `_web_search/_web_search_provider.py` | `SearchProvider` delegate |
| `_web_search/_base_http_provider.py` | `BaseHttpProvider` (concurrency, tenacity-style retry, `PrerequisiteError` on a missing key), `HttpStatusException`, `NamedConcurrency` |
| `_web_search/_tavily.py`, `_web_search/_exa.py` | `TavilySearchProvider`, `ExaSearchProvider` (+ `SearchOptionsValidator` standing in for the pydantic option models) |
| `model/_providers/perplexity.py` (request/result shape) | `PerplexitySearchProvider` (.NET-only HTTP fallback, see deviations) |
| `tool/_tools/_read_file.py`, `_list_files.py`, `_grep.py` | `BuiltinTools.ReadFile/ListFiles/Grep(timeout?, user?, sandbox?)` over `SampleContext.Sandbox()` |
| `tool/_tools/_todo_write.py`, `_update_plan.py` | `BuiltinTools.TodoWrite()`, `BuiltinTools.UpdatePlan(description?)` |
| `model/_call_tools.py` `validate_tool_input` | `ToolInputValidator` (jsonschema Draft 7 messages, same wording and order), `ToolArguments` (argument coercion) |
| `model/_providers/anthropic.py` web search + `_anthropic_citations.py` | `InspectAzureAI.Provider/Anthropic/AnthropicWebSearch.cs`, hooks in `AnthropicFoundryModelApi` |
| `_util/content.py` `ContentToolUse`, `_util/citation.py` | `Provider/Core/Content.cs` (`ContentToolUse`), `Provider/Core/Citation.cs`; JSON in `Eval/Log/Json/ContentConverters.cs` |

Every factory returns a `ToolDef` whose name, description, parameter schema, `options` and `parallel` flag are
asserted byte-for-byte against the Python `ToolDef(...)` dump (`tests/InspectAzureAI.Eval.Tests/fixtures/tools/tool_info.json`,
regenerated with the venv script in the scratchpad). `ToolExecutor` validates every call's arguments with
`ToolInputValidator` (after its required-parameter check, whose "Required parameter X not provided to tool call."
message is kept), as `call_tool` does for every tool; each built-in tool also validates in its own `Execute` so a
direct call is checked too.

## Behaviour notes

- `web_search` carries `{"__internal_tool_type__": "web_search", <provider>: {...}}` in `ToolInfo.Options` exactly
  as Python; the external provider is created lazily on the first call (so a missing API key is a
  `PrerequisiteError` at first use, not at construction). Tavily forces `include_answer`; Exa defaults `text: true`;
  `max_connections` is stripped and sizes the per-provider `NamedConcurrency` limit. HTTP 408/429/5xx and transport
  failures retry up to 5 attempts / 60 s with `min(2^(n-1) + U[0,1), 10)` s waits; Tavily's query-too-long 400 is
  a `ToolError`, any other non-success status an `HttpStatusException` (fatal, like `httpx.HTTPStatusError`).
- `read_file` / `list_files` / `grep` run the same argv Python does (`awk`, `find -- path -mindepth 1`, `grep -rn --`)
  and map failures identically (`File not found: …`, `No matches found.`, `grep failed`, count-mode `:0` filtering).
- Claude's server-side web search on Foundry: `platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry`
  lists only the *newer* `web_search`/`web_fetch` versions as unsupported when hosted on Azure, so the basic
  `web_search_20250305` is passed through. `AnthropicFoundryModelApi.BuildRequest` emits it (with
  `allowed_domains`, `blocked_domains`, `cache_control`, `max_uses`, `user_location`) for a `web_search` tool whose
  options include `anthropic`, on models `_supports_web_search`/Claude 5 accept; `ParseMessage` pairs
  `server_tool_use` + `web_search_tool_result` into `ContentToolUse` (arguments/result as JSON text, `error` =
  error_code) and reads text-block citations into `UrlCitation` (title capped at 255, `encrypted_index` in
  `Internal`); `AccumulateAsync` folds `server_tool_use` input deltas and `citations_delta`; later turns replay the
  two blocks and the citations. `ContentToolUse` serialises to the log as `{"type":"tool_use","tool_type",...}`.

## Deviations from Python (and why)

- **Perplexity is an HTTP fallback, not an internal provider.** Python's perplexity model provider forwards the
  options and reads `search_results`; no Azure route can do that. `PerplexitySearchProvider` makes the same
  chat-completions call itself (`PERPLEXITY_API_KEY`, `model` default `sonar`, other options forwarded verbatim,
  one `UrlCitation(url, title)` per search result) and is used only when the caller listed `"perplexity"`
  explicitly (`WebSearchProviderConfig.ExplicitProviders`), so the Python default configuration still fails with
  "No valid provider found." rather than silently spending Perplexity credit.
- **`google` provider not ported** (`NotSupportedException`): it needs an HTML parser plus a relevance model over
  fetched pages. The deprecated keyword form of `web_search()` (`provider=`, `num_results=`, …) is not ported.
- **Anthropic web search is the basic version only**: no `web_fetch` companion (beta header), no
  `web_search_20260209`, no server-tool span bookkeeping or compaction result clearing; replay is reconstructed from
  `ContentToolUse` (Python's own fallback path), which is sufficient because the encrypted payloads live in the
  result JSON. Citations that cannot be expressed on replay are dropped with a one-time warning where Python asserts.
  Unknown citation types become a `ContentCitation` carrying the raw payload in `Internal`.
- `think`'s tool-call viewer (markdown rendering) has no .NET counterpart; the other providers' internal search
  (openai, gemini, grok, mistral) cannot run on Azure and are only carried in `options`.
- Invalid provider options raise `ArgumentException` (pydantic `ValidationError`); malformed provider responses raise
  `InvalidDataException` with pydantic-style field messages.

## Tests

`tests/InspectAzureAI.Eval.Tests/BuiltinToolsTests.cs` (80 cases: fixture parity, validator message parity,
sandbox tools over `FakeSandboxEnvironment`, provider config, Tavily/Exa/Perplexity over a fake `HttpMessageHandler`
including retries, cancellation and error bodies, log round-trip) and `tests/InspectAzureAI.Tests/AnthropicWebSearchTests.cs`
(20 cases: model support table, server tool param, request/response/replay/streaming through `AnthropicFoundryModelApi`).
