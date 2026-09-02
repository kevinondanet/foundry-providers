# InspectAzureAI — a .NET 10 port of the Inspect AI `azureai` provider

This repository is a standalone C# sample that faithfully ports the
[Inspect AI](https://inspect.aisi.org.uk) `azureai` model provider — the adapter for
[Azure AI Foundry](https://ai.azure.com/) model-inference endpoints — and demonstrates every piece of
its behaviour: credential and endpoint resolution, model-name handling, request assembly, native and
emulated (Llama 3.1 `<tool_call>` prompt format) tool calling, streaming accumulation with `on_stream`
events, content-filter stop details, `ModelCall` capture with media redaction, and retry
classification.

The Python source of truth is `src/inspect_ai/model/_providers/azureai.py` and its helpers in the
Inspect repository; every C# type cites the Python location it ports in its `///` summary.

The port targets **net10.0** and is built on the official
[`Azure.AI.Inference`](https://www.nuget.org/packages/Azure.AI.Inference) (`ChatCompletionsClient`) and
[`Azure.Identity`](https://www.nuget.org/packages/Azure.Identity) packages.

## Solution layout

```
InspectAzureAI.sln
├── src/InspectAzureAI.Provider     class library — the port
│   ├── Core/                       minimal Inspect types (messages, content, tools, config, output, ModelCall, stream events)
│   ├── Util/                       ports of util/util.py, azure_hosting.py, _util/http.py, _openai.py helpers, images.py
│   ├── Tools/                      ChatAPIHandler + Llama31Handler, parse_tool_call, tool/message conversion
│   ├── Testing/                    CannedTransport — an offline HttpPipelineTransport
│   ├── AzureAIModelApi.cs          port of AzureAIAPI
│   ├── AzureAIStreamAccumulator.cs port of azureai_completion_from_stream
│   ├── AzureChatCompletions.cs     dict-backed response view (raw JSON)
│   └── SseParser.cs                server-sent-events reader
├── src/InspectAzureAI.Sample       console app with one subcommand per feature
└── tests/InspectAzureAI.Tests      xunit suite (offline, canned transport)
```

## Architecture

```mermaid
flowchart LR
    subgraph Caller
        S[Sample / your code]
    end
    subgraph Provider["InspectAzureAI.Provider"]
        API[AzureAIModelApi<br/>port of AzureAIAPI]
        H[Llama31Handler<br/>tool emulation]
        MC[AzureMessageConversion<br/>AzureToolConversion]
        ACC[AzureAIStreamAccumulator<br/>+ SseParser]
        RAW[AzureChatCompletions<br/>raw JSON view]
        OBS[ModelStreamObserver<br/>on_stream events]
        CALL[ModelCall<br/>request/response capture]
        RET[ShouldRetry / IsAuthFailure<br/>HttpRetryUtil]
    end
    subgraph SDK["Azure.AI.Inference / Azure.Core"]
        CLI[ChatCompletionsClient]
        T[HttpPipelineTransport<br/>(injectable)]
    end
    subgraph Azure
        EP[/models/chat/completions<br/>api-version=2024-05-01-preview]
    end
    S -->|ChatMessage, ToolInfo, ToolChoice, GenerateConfig| API
    API -->|emulate_tools| H
    API --> MC
    MC -->|ChatCompletionsOptions| CLI
    CLI --> T --> EP
    EP -->|JSON / SSE| T --> CLI
    CLI -->|raw response| RAW
    CLI -->|raw SSE stream| ACC --> RAW
    ACC -->|Text / ToolCall deltas| OBS --> S
    RAW -->|ModelOutput + StopDetails| API
    API --> CALL
    API -.->|thrown RequestFailedException| RET
    API -->|GenerateResult| S
```

## Python → C# mapping

| Python (inspect_ai) | C# |
|---|---|
| `model/_providers/azureai.py` `AzureAIAPI.__init__` | `AzureAIModelApi` constructor |
| `AzureAIAPI.generate` | `AzureAIModelApi.GenerateAsync` |
| `AzureAIAPI.completion_params` | `AzureAIModelApi.CompletionParams` |
| `AzureAIAPI.resolve_streaming` | `AzureAIModelApi.ResolveStreaming` |
| `AzureAIAPI.max_tokens` / `should_retry` / `is_auth_failure` / `collapse_user_messages` / `connection_key` | same-named methods on `AzureAIModelApi` |
| `AzureAIAPI.service_model_name` / `canonical_name` / `is_llama` / `is_mistral` / `is_openai_model` | same-named methods |
| `AzureAIAPI.handle_azure_error` | `AzureAIModelApi.HandleAzureError` |
| `except AzureError` in `AzureAIAPI.generate` (azure-core's `HttpResponseError` / `ServiceRequestError` / `ServiceResponseError`) | `AzureAIModelApi.AsAzureError` (normalises what Azure.Core throws, see fidelity note 3) |
| `_is_llama_model` / `_is_llama3_model` / `_is_mistral_model` / `_is_openai_model` | `AzureAIModelApi.IsLlamaModel` / `IsLlama3Model` / `IsMistralModel` / `IsOpenAIModelName` |
| `_StreamChoice` / `azureai_completion_from_stream` | `StreamChoice` / `AzureAIStreamAccumulator.CompletionFromStreamAsync` |
| `chat_request_messages` / `chat_request_message` / `chat_content_item` | `Tools/AzureMessageConversion` |
| `mistral_message_reducer` / `fold_user_message_into_tool_message` | `AzureMessageConversion.MistralMessageReducer` / `FoldUserMessageIntoToolMessage` |
| `chat_tools` / `chat_tool_definition` / `chat_tool_choice` / `chat_tool_call` | `Tools/AzureToolConversion` |
| `chat_completion_choices` / `chat_complection_choice` / `chat_completion_assistant_message` / `chat_completion_stop_reason` | `AzureAIModelApi.ChatCompletionChoices` / `ChatCompletionChoice` / `ChatCompletionAssistantMessage` / `ChatCompletionStopReason` |
| `azure.ai.inference.models.ChatCompletions` (dict-backed `as_dict()`) | `AzureChatCompletions` / `AzureChatChoice` / `AzureChatResponseMessage` (raw `JsonObject`) |
| `util/chatapi.py` `ChatAPIHandler`, `ChatAPIMessage` | `Tools/ChatApiHandler.cs` |
| `util/llama31.py` `Llama31Handler`, `parse_tool_call_content`, `filter_assistant_header` | `Tools/Llama31Handler.cs` |
| `_call_tools.py` `parse_tool_call`, `tool_parse_error_message`, `_object_with_trailing_quotes` | `Tools/ToolCallParsing.cs` |
| `yaml.safe_load` (non-JSON tool arguments) | `Tools/YamlScalar.cs` (approximation, see fidelity notes) |
| `util/_json.py` `json_schema_dump`, `JSON_SCHEMA_EXTENDED_FIELDS`, `JSONSchema` | `Tools/JsonSchemaDump.cs`, `Core/Tools.cs` (`ToolParam.ToJson`) |
| `util/util.py` `normalize_stream_arg`, `model_base_url`, `environment_prerequisite_error` | `Util/ProviderUtil.cs` |
| `util/azure_hosting.py` `resolve_azure_token_provider`, `DEFAULT_AZURE_AUDIENCE` | `Util/AzureHosting.cs` |
| `_util/http.py` `is_retryable_http_status`, `parse_retry_after(_from_exception)`, `status_code_of` | `Util/HttpRetryUtil.cs` |
| `_openai.py` `needs_max_completion_tokens`, `openai_stop_details`, `openai_media_filter` | `Util/OpenAIUtil.cs` |
| `_model_output.py` `collect_stop_details` | `Util/ModelOutputUtil.cs` |
| `_util/images.py` `inline_media_data_uri`, `_util/url.py` data-URI helpers | `Util/InlineMedia.cs` |
| `_util/logger.py` `warn_once` | `Util/ProviderLogger.WarnOnce` |
| `hooks._hooks.override_api_key` / `has_api_key_override`, `ModelAPI._apply_api_key_overrides` | `Util/ModelApiHooks.cs`, `AzureAIModelApi.ApplyApiKeyOverrides` |
| `_model.py` `RetryDecision` | `Core/RetryDecision.cs` |
| `_model_output.py` `ModelOutput`, `ChatCompletionChoice`, `ModelUsage`, `StopReason`, `StopDetails`, `StopCategory` | `Core/ModelOutput.cs` |
| `_model_call.py` `ModelCall`, `ModelCallFilter`, `_walk_json_value` | `Core/ModelCall.cs` |
| `_chat_message.py` `ChatMessage*` | `Core/ChatMessage.cs`, `Core/MessageContent.cs` |
| `_util/content.py` `ContentText/Image/Audio/Video` | `Core/Content.cs` |
| `tool/_tool_info.py`, `_tool_params.py`, `_tool_call.py`, `_tool_choice.py` | `Core/Tools.cs` |
| `_generate_config.py` `GenerateConfig` (subset) | `Core/GenerateConfig.cs` |
| `_stream.py` `Stream*Event`, `StreamHandler`, `ModelStreamObserver`, `model_stream_requested`, `report_model_stream_*` | `Core/Streaming.cs` |
| `_util/error.py` `PrerequisiteError`; `azure.core.exceptions.ServiceResponseError` | `Core/Errors.cs` (`PrerequisiteError`, `ServiceResponseException`) |
| `textwrap.dedent`, `json.dumps` / `json.loads`, `shortuuid.uuid`, Python truthiness | `Util/TextWrap.cs`, `Util/PythonJson.cs` (`Dumps` / `Loads`), `Util/ShortUuid.cs`, `Util/PythonSemantics.cs` |
| `tests/model/providers/test_azureai.py`, `tests/model/test_canonical_names.py::TestAzureAICanonicalName`, `tests/model/test_parse_tool_call.py` | `tests/InspectAzureAI.Tests` (`StreamingTests`, `EnvPrecedenceTests`, `NamingTests`, `ParseToolCallTests`, ...) — tests keep the Python test names |

## Environment variables

Same names and precedence as the Python provider:

| Variable | Meaning |
|---|---|
| `AZURE_API_KEY` | API key (legacy name, **preferred**: checked first) |
| `AZUREAI_API_KEY` | API key (fallback when `AZURE_API_KEY` is unset — a set-but-empty `AZURE_API_KEY` is taken as-is, see fidelity note 15) |
| `AZURE_ENDPOINT_URL`, `AZUREAI_ENDPOINT_URL`, `AZUREAI_BASE_URL` | endpoint, consulted in that order (e.g. `https://your-url.azure.com/models`) |
| `INSPECT_EVAL_MODEL_BASE_URL` | last-resort endpoint fallback |
| `AZUREAI_AUDIENCE` | Entra ID token scope for managed identity (default `https://cognitiveservices.azure.com/.default`) |

Resolution order for the key: explicit constructor argument → api-key override hook → `AZURE_API_KEY` →
`AZUREAI_API_KEY` → `DefaultAzureCredential` (Entra ID). The resulting key or token is sent both as
`Authorization: Bearer …` and `api-key: …`, exactly as the Python SDK does. Missing prerequisites raise
`PrerequisiteError` with the Python messages (Rich markup included).

The sample additionally reads `INSPECT_AZUREAI_MODEL` for the default model name.

## Building and running

```bash
dotnet build InspectAzureAI.sln -warnaserror
dotnet test  InspectAzureAI.sln
```

The sample app has one subcommand per feature. Every command accepts `--model <name>`,
`--streaming auto|true|false`, `--emulate-tools true|false` and `--fake` (answer from an in-memory canned
endpoint — no network, no keys — handy for seeing the request/response shapes).

```bash
export AZUREAI_API_KEY=...            # or rely on DefaultAzureCredential
export AZUREAI_BASE_URL=https://your-url.azure.com/models
export INSPECT_AZUREAI_MODEL=Llama-3.3-70B-Instruct

S="dotnet run --project src/InspectAzureAI.Sample --"
$S --help                          # list everything
$S config                          # resolved endpoint / auth mode / names, no call made
$S naming gpt-4o moonshotai/kimi-k2.5 custom-org/llama-3-70b
$S chat "What are you?"            # non-streaming completion
$S stream "Tell me a joke"         # streaming: deltas printed as they arrive
$S tools                           # native function calling loop (get_weather)
$S emulate-tools                   # Llama <tool_call> prompt-format loop, same tool
$S image ./photo.png "What is in this picture?"
$S retry-demo                      # ShouldRetry / IsAuthFailure / HandleAzureError decisions
$S chat --fake                     # any command, offline
```

Each generating command prints the captured `ModelCall` request and response JSON (with base64 image
payloads redacted to `<base64-data-removed>`), the output, tool calls, stop details and token usage.

### Using the provider from code

```csharp
var api = new AzureAIModelApi("Llama-3.3-70B-Instruct");          // env vars resolve key + endpoint
var result = await api.GenerateAsync(
    input: [new ChatMessageUser("What is the weather in Paris?")],
    tools: [weatherTool],
    toolChoice: ToolChoice.Auto,
    config: new GenerateConfig { MaxTokens = api.MaxTokens(), Temperature = 0 },
    onStream: e => { if (e is StreamTextEvent t) Console.Write(t.Text); return Task.CompletedTask; });
var output = result.OutputOrThrow();        // ModelOutput; result.Call is the ModelCall record
```

`GenerateAsync` mirrors the Python return contract: a `GenerateResult` carrying either the
`ModelOutput` or, for a terminal HTTP 400, the exception (which Inspect wraps without retrying); every
other Azure failure is recorded on the `ModelCall` and **thrown** — already normalised to
`RequestFailedException` / `ServiceResponseException` (fidelity note 3) — so the caller can consult
`ShouldRetry(ex)` / `IsAuthFailure(ex)`; the sample's `retry-demo` shows the classification table.
Non-Azure failures (an empty stream, a malformed SSE chunk, caller cancellation) propagate unrecorded
and unretried, exactly as non-`AzureError` exceptions do in Python.

## Intentionally out of scope

The port covers the provider and the framework contract it directly touches. These Inspect pieces are
**not** ported:

- the eval loop, `Model.generate` wrapper and its input transforms (collapsing consecutive user
  messages, moving tool-result images into user messages, reasoning-history filtering, `max_tokens`
  defaulting — the sample passes `api.MaxTokens()` explicitly);
- the tenacity retry loop, adaptive concurrency and connection pooling (`ShouldRetry` /
  `ConnectionKey` / `MaxConnections` are exposed for a host to drive);
- transcript / `ModelEvent` recording (a `ModelCall` is returned instead of registered on an event);
- sample limits, token/time limits, caching, the model-info registry (`model_family()` therefore
  always equals `service_model_name()`), tool execution and approval, and `stream_idle_timeout`.

## Fidelity notes

Places where the port deliberately deviates from the Python implementation, and why:

1. **Raw JSON instead of the SDK's typed response models.** The .NET `ChatCompletions` /
   `StreamingChatCompletionsUpdate` types drop undeclared fields, notably the per-choice
   `content_filter_results` and the tool-call `index` on streamed fragments. The provider therefore
   reads `Response.Content` for non-streamed calls and parses the raw SSE body
   (`StreamingResponse.GetRawResponse().ContentStream`, via `SseParser`) for streamed calls, exposing a
   dict-backed `AzureChatCompletions` view — the same shape the Python SDK's mapping-backed models give.
   The SDK still builds and sends the request (auth headers, `api-version`, `extra-parameters`).
2. **`max_completion_tokens` is actually sent.** The Python provider passes it through `**request` into
   the SDK's `**kwargs`, which azure-ai-inference forwards to the HTTP pipeline rather than the body —
   so for gpt-5 / o-series families Python effectively sends no limit. The port sends it as a
   pass-through extra (`AdditionalProperties`), which is the evident intent; this adds the
   `extra-parameters: pass-through` header on those requests.
3. **Azure error types.** Python catches the whole `AzureError` family; the .NET SDK throws a wider
   mix, which `AzureAIModelApi.AsAzureError` normalises on both the streaming and the non-streaming
   path before the `ModelCall` is error-marked and `HandleAzureError` runs:
   - `azure.core.HttpResponseError` ↔ `RequestFailedException` with `Status > 0`;
   - `ServiceRequestError` (connection failure, not retried) ↔ `RequestFailedException` with
     `Status == 0` (Azure.Core wraps `HttpRequestException` this way);
   - `ServiceResponseError` (response could not be read, retried as transient) ↔ the provider's
     `ServiceResponseException`, into which the port wraps the raw `IOException` and the
     `TaskCanceledException` Azure.Core's network timeout (`ClientOptions.Retry.NetworkTimeout`, 100 s)
     raises — Azure.Core does not wrap either — while an `OperationCanceledException` caused by the
     caller's own token propagates untouched;
   - anything else (`JsonException` from a malformed SSE chunk ↔ the SDK's `json.JSONDecodeError`,
     the empty-stream `InvalidOperationException`) is not an Azure error and propagates unrecorded and
     unretried, like Python.
   `str(ex.message)` ↔ the leading line of `RequestFailedException.Message` (Azure.Core appends a
   status/content/header dump).
4. **SDK-level retries stay on.** Both SDK pipelines retry underneath Inspect's `should_retry` loop; the
   defaults differ (azure-core: 10 attempts, Azure.Core: 3). When Azure.Core exhausts its attempts on
   thrown failures it raises an `AggregateException` ("Retry failed after N tries"); `AsAzureError`
   unwraps it to the *last* attempt's exception (azure-core re-raises the last `AzureError`) and maps
   that by the rules in note 3. `AzureAIClientSettings.ConfigureClientOptions` can change or disable the
   SDK retries (the tests run with 0, and with 2 for the exhaustion case).
5. **JSON parse-error text.** `ToolCall.ParseError` embeds `System.Text.Json` messages rather than
   Python's `json` messages (e.g. `Expecting value: line 1 column 1 (char 0)`); the surrounding
   `Error parsing the following tool call arguments: … Error details: …` framing, middle-truncation at
   16 KiB (decoding split multibyte sequences with `errors="ignore"` semantics), trailing-quote recovery
   and the depth-100 limit are identical. Tool-call JSON is parsed with `PythonJson.Loads`, which keeps
   `json.loads`' last-wins handling of duplicated object keys (`JsonNode.Parse` would throw). The one
   acceptance difference: the non-standard `NaN` / `Infinity` / `-Infinity` tokens `json.loads` accepts
   are reported as a parse error (a `JsonNode` cannot hold them; models essentially never emit them).
6. **YAML fallback is an approximation.** `yaml.safe_load` for non-JSON tool arguments is replaced by
   `YamlScalar`: YAML 1.1 scalars (including `yes/no/on/off`, `1_000`, `0x10`), quoted strings, flow
   collections and a single-line `key: value` mapping. Block collections, anchors/aliases and
   multi-document input fall back to the raw string (Python would parse them).
7. **Empty stream** raises `InvalidOperationException` (Python: a plain `RuntimeError`), with the same
   message; like Python it is not retried.
8. **Streaming observer.** `ModelStreamObserver` ports `on_stream` delivery, handler detachment on
   exception, usage/heartbeat progress and the "choice 0 only" gating; stall scopes
   (`stream_idle_timeout`) and partial-output flushing are not ported, so `ModelStreamRequested()` is true
   only when a handler is installed.
9. **Null assistant content under emulation** is treated as `""` (Python would raise `TypeError` from
   `re.findall(None)`).
10. **API-key override hook.** Inspect's hook registry is replaced by `ModelApiHooks` with a separate
    `HasApiKeyOverride` flag, because in Python `has_api_key_override()` reflects registered hook
    classes while `override_api_key` is a module function (the Python test patches only the latter).
11. **Numeric precision.** The SDK stores `temperature`, `top_p` and the penalties as `float`
    (single precision) and `seed` as `long`; `0.0` therefore serialises as `0`.
12. **`streaming` argument.** `null` is accepted as a synonym for `"auto"` (C# cannot default an
    `object` parameter to a string).
13. **`model_family()`** has no model-info registry to consult and always returns `service_model_name()`.
14. **Managed identity** uses `DefaultAzureCredential` from Azure.Identity, a hard dependency, so the
    Python `ImportError` → `PrerequisiteError` branch cannot occur; credential construction failures
    surface with the same message.
15. **Empty `AZURE_API_KEY`.** Like `os.environ.get(AZURE_API_KEY, os.environ.get(AZUREAI_API_KEY))`,
    a set-but-empty `AZURE_API_KEY` is taken as-is (`ApiKey == ""`, `AZUREAI_API_KEY` never consulted)
    and the constructor falls through to managed identity. Python's `generate` then still builds
    `AzureKeyCredential("")` (its check is `is not None`) and sends an empty key; .NET's
    `AzureKeyCredential` rejects an empty key, so the port uses the resolved token provider instead —
    the evident intent of the fall-through.
16. **Refusal text is not a stop detail.** `openai_stop_details` reads `message.refusal` with
    `getattr`, and azure.ai.inference's dict-backed `ChatResponseMessage` exposes no such attribute, so
    for this provider Python never produces a `refusal` explanation; the port reproduces that by not
    reading the raw `refusal` key (a refusal with no filtered category yields no stop details).
