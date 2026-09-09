# Model parameters on Foundry

What each chat deployment accepts beyond a plain prompt: the token-limit field, sampling controls
(temperature, top_p, top_k, seed, penalties), stop sequences, multiple choices, logprobs, JSON output
(free-form and strict schema), streaming usage, and the reasoning / thinking controls (effort level,
on/off toggle, token budget) together with what reasoning comes back.

Everything here was measured, not read from vendor documentation. The sample's `params` probe sends
one call per candidate parameter on top of a baseline call and classifies the result from the HTTP
status and the response body. The run behind this page:

| | |
|---|---|
| Probed | 2026-09-03 22:09 UTC |
| Resource | `myfoundry0406` (kind `AIServices`, eastus2) |
| Auth | `az login` (DefaultAzureCredential) |
| Raw data | `docs/dashboard/calls.json`; every exchange is viewable in `docs/dashboard/index.html` |

To refresh it:

```sh
dotnet run --project src/InspectAzureAI.Sample -- capture --include-failed --params all --out docs/dashboard/calls.json
scripts/build-dashboard.py            # docs/dashboard/index.html
scripts/build-readme-matrix.py        # the compact matrix in README.md
```

Verdict vocabulary used below:

| Verdict | Meaning |
|---|---|
| **yes** | HTTP 200 and the expected effect was observed (the count stopped before "4", two choices came back, logprobs present, a JSON object returned, usage appeared on the stream, reasoning switched on or off). |
| **accepted** | HTTP 200 but the probe has no way to see an effect (temperature, top_p, seed, penalties, top_k, verbosity, metadata) or the effect was already present without the parameter. |
| **ignored** | HTTP 200 and the expected effect did not happen. |
| **rejected** | HTTP 400; the service's message is quoted. |
| **not sent** | the provider derives no request field for this family, so nothing went on the wire. |
| **not probed** | the parameter is not part of that route's API and was not tried. |

## How a setting reaches the wire

Three routes exist. Most deployments are served on the model-inference chat-completions route
(`<endpoint>/models/chat/completions`, `AzureAIModelApi`). Claude deployments are served on the
Anthropic Messages route (`<endpoint>/anthropic/v1/messages`, `AnthropicFoundryModelApi`) because the
model-inference route answers `Requested API is currently not supported` for them. The gpt-5.6 family,
gpt-5.4-pro and o-series names default to the OpenAI Responses route (`<endpoint>/openai/v1/responses`,
`OpenAIResponsesModelApi`), because chat completions rejects function tools combined with
`reasoning_effort` on gpt-5.6 and gpt-5.4-pro is not a chat deployment at all; `--route models` puts
the gpt-5.6 family back on chat completions, which is where the verdicts below were measured.

### From `GenerateConfig`

| Setting | `GenerateConfig` field | Chat-completions route | Anthropic Messages route |
|---|---|---|---|
| Max output tokens | `MaxTokens` | `max_tokens`, or `max_completion_tokens` when the name is gpt-5 / o-series or the model arg `max_completion_tokens=true` is set. Default from `MaxTokens()`: 2048, or none (service default) when the name contains `mistral`. | `max_tokens`, mandatory on this API. Default from `MaxTokens()`: 32000, or 4096 for Claude 3 / 3.5. Raised to `budget_tokens + 2048` when a thinking budget would exceed it. |
| Temperature | `Temperature` | `temperature` | `temperature` |
| Top-p | `TopP` | `top_p` | `top_p` |
| Stop sequences | `StopSeqs` | `stop` (array) | `stop_sequences` (array) |
| Seed | `Seed` | `seed` | not sent (no such field on the Messages API) |
| Penalties | `FrequencyPenalty`, `PresencePenalty` | `frequency_penalty`, `presence_penalty` | not sent |
| Reasoning effort | `ReasoningEffort`: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` | Per family (`Util/ReasoningParams.cs`): `reasoning_effort: <level>` verbatim for OpenAI gpt-5.x, model-router, xAI, Microsoft and DeepSeek; `thinking: {type: enabled}` (or `disabled` for `none`) for MoonshotAI and Cohere; nothing for gpt-4o, Mistral and unknown families. | `thinking: {type: adaptive}` plus `output_config: {effort: <level>}`; `minimal` is sent as `low`; `none` sends no thinking field at all. |
| Reasoning budget | `ReasoningTokens` | Only Cohere has a budget field: `thinking.token_budget`. Every other family ignores it (nothing is sent). | `thinking: {type: enabled, budget_tokens: <n>}` (the deprecated Claude 4.6 form) instead of adaptive thinking. |
| Parallel tool calls | `ParallelToolCalls` | not sent from config; use the model arg `parallel_tool_calls=false` | `tool_choice.disable_parallel_tool_use: true` when `false` |
| Multiple choices, logprobs, top-k | `NumChoices`, `Logprobs`, `TopLogprobs`, `TopK` | carried on the record but never sent, exactly as Inspect's Python provider does; pass `n`, `logprobs`, `top_logprobs`, `top_k` as model args | not sent; `top_k` works as a model arg |
| Tool choice | `ToolChoice` argument | `auto`, `none`, `any` (sent as `required`), or a named function | `auto`, `any`, a named tool; `none` drops the tools from the request |
| Streaming | `streaming` model arg, else on when an `onStream` consumer is installed | `stream: true`; usage arrives only if the deployment reports it (see per-model) | SSE stream; usage always reported |

### The Responses route

`OpenAIResponsesModelApi` bypasses the per-family `ReasoningParams` table and always speaks the Responses
fields (`reasoning_effort` never appears in its requests):

| Setting | Responses route |
|---|---|
| Max output tokens | `max_output_tokens` from `MaxTokens`; no default, the service decides |
| Temperature, top-p | `temperature`, `top_p` only when reasoning is off (gpt-5.6 rejects them otherwise; dropped with a one-time warning) |
| Reasoning effort | `reasoning.effort` verbatim (`none` included); `max` becomes `xhigh` for models before gpt-5.6; `reasoning.summary` only with `--reasoning-summary auto` (Python defaults to `auto`; the port keeps it off because other Azure organisations can get HTTP 400; the test resource accepted it and returned summary text) |
| Reasoning budget | ignored |
| Structured output, verbosity | `text.format` (`json_schema`) from `ResponseSchema`; `text.verbosity` |
| Tool choice, parallel tool calls | `tool_choice` `none`, `required` or `{type: function, name}` (`auto` is not sent); `parallel_tool_calls` |
| Stop sequences, seed, penalties, `n`, logprobs, `logit_bias`, fallback models | ignored with a one-time warning |
| Storage | always `store: false` with `include: ["reasoning.encrypted_content"]` on reasoning models; the model arg `store=true` switches both off |
| Model args | applied last, as on the other routes |

### Model args

Any `-M key=value` (or `ModelArgs` entry from code) becomes a top-level body field on every route, applied
after the derived fields so it wins on a key clash. On the chat-completions route the request carries
`extra-parameters: pass-through` on streamed and non-streamed calls alike, otherwise the gateway rejects
unknown fields. Four keys are reserved and never reach the body:

| Model arg | Effect |
|---|---|
| `max_completion_tokens=true` | send `max_completion_tokens` instead of `max_tokens` (MAI-Thinking-1 needs it; `test-all` retries with it automatically) |
| `model_format=<vendor>` | name the family when the deployment name does not reveal it (`OpenAI`, `xAI`, `Microsoft`, `DeepSeek`, `MoonshotAI`, `Cohere`, `Mistral AI`, `Anthropic`) |
| `streaming=true` / `streaming=false` | force streaming on or off |
| `anthropic_beta=<list>` | sent as the `anthropic-beta` header on the Anthropic route |

### What comes back

- **Reasoning text** arrives as `ContentReasoning` items placed first on the assistant message; `Completion`
  and `Text` stay text-only. Streamed reasoning is a `StreamReasoningEvent`. On the Anthropic route thinking
  blocks carry a `signature` and are replayed unchanged on later turns; on the chat-completions route they are
  not replayed (DeepSeek rejects an echoed `reasoning_content`). On the Responses route encrypted `reasoning`
  items become redacted `ContentReasoning` (the item id as `signature`, plus a `summary` when one was asked
  for) and are replayed through `encrypted_content`.
- **Reasoning token counts** land in `ModelUsage.ReasoningTokens` from
  `usage.completion_tokens_details.reasoning_tokens`, or from the stream-only top-level `usage.reasoning_tokens`
  that Kimi, DeepSeek and model-router report, or from `output_tokens_details.reasoning_tokens` on the
  Responses route. The Anthropic route reports no separate count.
- **Multiple choices** (`n`) become `ModelOutput.Choices`.
- **Logprobs** are not parsed into `ModelOutput`; they are only visible in the recorded raw response on the
  `ModelCall`.
- **Cache reads** fill `ModelUsage.InputTokensCacheRead` from `prompt_tokens_details`.

## Per-model detail

Deployments with identical verdicts are grouped. "Reasoning output" is what the `reasoning` smoke check
saw with `--reasoning-effort medium`: `text` (reasoning text returned), `hidden` (a token count only),
`none` (a field was sent, nothing came back), `n/a` (no control exists for the family).

### gpt-5.6-sol, gpt-5.6-luna, gpt-5.6-luna-2, gpt-5.6-terra (OpenAI, version 2026-07-09)

The strictest deployments on the resource: they reason by default and refuse most sampling controls.
These are chat-completions verdicts (`--route models`); by default these names are now served on the
Responses route, because chat completions refuses function tools alongside `reasoning_effort`.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_completion_tokens` | `max_tokens` rejected: "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead." The provider picks the right field from the name. |
| temperature | rejected | "'temperature' does not support 0.2 with this model. Only the default (1) value is supported." Leave it unset or pass exactly 1. |
| top_p | rejected | "'top_p' is not supported with this model." |
| seed | accepted | |
| frequency_penalty, presence_penalty | rejected | "not supported with this model." |
| stop | rejected | "'stop' is not supported with this model." |
| n (multiple choices) | yes | two choices returned |
| logprobs | rejected | "'logprobs' is not supported with this model." |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | `{"answer":"ok"}` |
| response_format json_schema (strict) | yes | `{"answer":"ok"}` |
| Streaming usage | needs `stream_options` | no usage on a plain stream; `-M stream_options={"include_usage":true}` makes it appear |
| reasoning_effort | yes, all levels | `none` switches reasoning off (baseline 34 reasoning tokens went to 0); `low` 24, `high` 22, `xhigh` 45 tokens on the probe prompt |
| Thinking toggle / budget | not applicable | `reasoning_tokens` is not sent for this family |
| verbosity | accepted | `verbosity=low` as a model arg |
| Reasoning output | hidden | 30 to 53 reasoning tokens on the smoke check; the text is never returned |
| Tools | yes | native `tool_calls` |
| Tools + reasoning_effort | rejected | "Function tools with reasoning_effort are not supported for gpt-5.6-sol in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'." The Responses route takes both, which is why it is now the default for these names |

### gpt-5.4-mini (OpenAI, version 2026-03-17)

Same token-limit rule as the gpt-5.6 deployments, but it takes the sampling controls that gpt-5.6 rejects.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_completion_tokens` | `max_tokens` rejected with the same message as gpt-5.6 |
| temperature, top_p | accepted | 0.2 and 0.9 both HTTP 200 |
| seed, frequency_penalty, presence_penalty | accepted | |
| stop | rejected | "'stop' is not supported with this model." |
| n | yes | two choices |
| logprobs | yes | `choices[0].logprobs` present |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | needs `stream_options` | as gpt-5.6 |
| reasoning_effort | yes | the baseline does not reason (0 tokens); `low` produced 305, `high` 54, `xhigh` 64 reasoning tokens; `none` accepted |
| Thinking toggle / budget | not applicable | |
| verbosity | accepted | |
| Reasoning output | hidden | 37 tokens on the smoke check |
| Tools | yes | |

### gpt-4o (OpenAI, version 2024-11-20)

A chat model without reasoning. Everything classic works; every reasoning field is rejected.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | the gpt-5 rule does not apply; `max_completion_tokens` is also accepted as a model arg |
| temperature, top_p, seed, penalties | accepted | |
| stop | yes | count stopped at "1 2 3" |
| n | yes | |
| logprobs | yes | |
| top_k | rejected | "Unrecognized request argument supplied: top_k" |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | the ARM capability map advertises `jsonSchemaResponse` for this deployment |
| Streaming usage | needs `stream_options` | |
| reasoning_effort | rejected | "Unrecognized request argument supplied: reasoning_effort"; the provider therefore sends nothing for this family |
| thinking (enabled / disabled / budget) | rejected | "Unrecognized request argument supplied: thinking" |
| Reasoning output | n/a | |
| Tools | yes | |

### model-router (OpenAI, version 2025-11-18)

Routes each call to another model, so verdicts describe whatever model answered (a reasoning model with
visible text on this run).

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | `max_completion_tokens` also accepted |
| temperature, top_p, seed, penalties | accepted | |
| stop | ignored | the completion came back empty, so the probe could not judge |
| n | yes | |
| logprobs | yes | |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | rejected | "response_format value as json_schema is enabled only for api versions 2024-08-01-preview and later" |
| Streaming usage | yes by default | usage is in the stream without `stream_options` |
| reasoning_effort | partly | `low` and `high` accepted (reasoning text visible, as on the baseline); `xhigh` was routed to a hidden-reasoning model (1263 tokens, no text); `none` ignored |
| thinking (model arg) | enabled accepted, disabled ignored | reasoning stays on |
| Thinking budget | not applicable | |
| verbosity | accepted | |
| Reasoning output | text | streamed as `reasoning_content` from the routed model; the token count appears only in stream mode |
| Tools | yes | |

### DeepSeek-V4-Pro, DeepSeek-V4-Flash (version 2026-04-23), DeepSeek-V4-Flash-0731 (version 2026-07-31) (DeepSeek)

Thinking is off by default and is switched on by `reasoning_effort`, not by the `thinking` object.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | `max_completion_tokens` also accepted |
| temperature, top_p, seed, penalties | accepted | |
| stop | yes | "1 2 3" |
| n | yes | |
| logprobs | yes | |
| top_k | accepted | |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | |
| reasoning_effort | yes | baseline has no reasoning; `low` and `high` both bring back `reasoning_content` text; `none` accepted (already off) |
| thinking enabled (model arg) | ignored | no reasoning appeared |
| thinking disabled (model arg) | accepted | no effect to see |
| Thinking budget | not applicable | `reasoning_tokens` is not sent |
| Reasoning output | text | `reasoning_content` becomes `ContentReasoning`; the token count is stream-only |
| Tools | yes | |

### Mistral-Large-3 (Mistral AI, version 1)

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens`, no default | the provider sends none unless `--max-tokens` is given (Inspect's Mistral rule); `max_completion_tokens` rejected ("Service request failed.") |
| temperature, top_p, seed, penalties | accepted | |
| stop | yes | stopped after "1 2 3" |
| n | yes | |
| logprobs | rejected | "Service request failed." |
| top_k | rejected | "Service request failed." |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | |
| reasoning_effort, thinking (all forms) | rejected | "Service request failed." for every reasoning field; the provider sends nothing for this family |
| Reasoning output | n/a | |
| Tools | yes | user messages after a tool result are folded, as the Mistral naming rules require |

### Ministral-3B (Mistral AI, version 1)

Its name does not contain `mistral`, so the Mistral rules do not apply and a `max_tokens` of 2048 is sent
by default. The endpoint runs a strict schema and rejects any unknown field with `extra_forbidden`.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` (default 2048) | `max_completion_tokens` rejected: "Extra inputs are not permitted" |
| temperature, top_p | accepted | |
| seed | rejected | "Extra inputs are not permitted" |
| frequency_penalty, presence_penalty | accepted | |
| stop | yes | "1 2 3" |
| n | yes | |
| logprobs | rejected | "Logprobs are not enabled for this model" (code 3051) |
| top_k | rejected | "Extra inputs are not permitted" |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | `stream_options` itself is rejected ("Extra inputs are not permitted"), but usage arrives anyway |
| reasoning_effort, thinking (all forms) | rejected | "Extra inputs are not permitted" |
| Reasoning output | n/a | |
| Tools | yes | slow: about 60 s for the three smoke checks at capacity 1 |

### MAI-Thinking-1 (Microsoft, version 2026-06-01)

Always reasons, hides the text, and needs the newer token-limit field.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_completion_tokens` only | `max_tokens` rejected: "`max_tokens` is not supported; use `max_completion_tokens` instead." Pass `-M max_completion_tokens=true` (the name does not trigger the gpt-5 rule); `test-all` retries with it automatically |
| temperature, top_p, seed, penalties | accepted | |
| stop | ignored | the count ran to "1 2 3 4 5 6" |
| n | ignored | one choice returned |
| logprobs | ignored | none in the response |
| top_k | accepted | |
| parallel_tool_calls | accepted | |
| response_format json_object | rejected | "Structured `response_format` is not enabled for model 'MAI-Thinking-1'." |
| response_format json_schema (strict) | rejected | same message |
| Streaming usage | yes by default | |
| reasoning_effort | accepted, no clear effect | reasoning is on regardless: baseline 507 tokens, `low` 554, `high` 539, `none` 623 |
| thinking enabled / disabled (model arg) | no effect | 507 tokens either way |
| Thinking budget | not applicable | |
| Reasoning output | hidden | 889 tokens on the smoke check |
| Tools | yes | |

### Kimi-K2.7-Code (version 2026-06-12), Kimi-K2.6 (version 2026-04-20) (MoonshotAI)

Thinking is on by default with visible text and cannot be switched off on Foundry.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | `max_completion_tokens` also accepted |
| temperature, top_p, seed, penalties | accepted | |
| stop | ignored | empty completion, could not judge |
| n | yes | |
| logprobs | yes | |
| top_k | accepted | |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | |
| reasoning_effort (raw model arg) | accepted | text visible, as on the baseline; the provider maps config effort to `thinking` for this family |
| thinking enabled | accepted | already on |
| thinking disabled | ignored | reasoning text still returned |
| thinking budget (`budget_tokens`) | accepted | no observable change; `ReasoningTokens` from config sends `thinking: {type: enabled}` without a budget for this family |
| Reasoning output | text | `reasoning_content` in every mode, including plain chat and tool calls; token count stream-only |
| Tools | yes | |

### Cohere-command-a-plus-05-2026 (Cohere, version 1)

Same behaviour as Kimi, with a real budget field.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | `max_completion_tokens` also accepted |
| temperature, top_p, seed, penalties | accepted | |
| stop | ignored | empty completion, could not judge |
| n | yes | |
| logprobs | yes | |
| top_k | accepted | |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | |
| reasoning_effort (raw model arg) | accepted | text already visible on the baseline |
| thinking enabled | accepted | already on |
| thinking disabled | ignored | reasoning text still returned |
| thinking budget (`token_budget`) | accepted | `--reasoning-tokens N` becomes `thinking: {type: enabled, token_budget: N}` |
| Reasoning output | text | `reasoning_content` in every mode; Cohere's text markers are stripped from the completion |
| Tools | yes | |

### grok-4.6 (xAI, version 1)

Always reasons with hidden text; rejects several classic sampling fields.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens` | `max_completion_tokens` also accepted |
| temperature, top_p, seed | accepted | |
| frequency_penalty, presence_penalty | rejected | "Service request failed." |
| stop | rejected | "Service request failed." |
| n | rejected | "Service request failed." |
| logprobs | yes | |
| top_k | accepted | |
| parallel_tool_calls | accepted | |
| response_format json_object | yes | |
| response_format json_schema (strict) | yes | |
| Streaming usage | yes by default | |
| reasoning_effort | accepted | reasoning cannot be turned off; counts on the probe prompt were baseline 1093, `low` 1031, `high` 245 |
| thinking enabled / disabled (model arg) | no effect | 1095 and 1421 tokens |
| Thinking budget | not applicable | |
| Reasoning output | hidden | 98 to 227 tokens across the smoke checks; about 20 to 28 s per call |
| Tools | yes | |

### claude-sonnet-4-6 (Anthropic, version 1, Anthropic Messages route)

Served through `AnthropicFoundryModelApi` with the same Entra token. The Messages API has its own field
set, so `n`, `logprobs`, `seed`, penalties and `response_format` do not exist here and were not probed.

| Feature | Verdict | Detail |
|---|---|---|
| Token limit | `max_tokens`, mandatory | default 32000 (4096 for Claude 3 / 3.5); raised above a thinking budget automatically |
| temperature | accepted | |
| top_p | accepted | |
| stop_sequences | yes | "1 2 3" |
| top_k (model arg) | accepted | |
| metadata (model arg) | accepted | `{"user_id": ...}` |
| n, logprobs, seed, penalties, response_format | not probed | not part of the Messages API |
| parallel_tool_calls | via config | `ParallelToolCalls = false` becomes `tool_choice.disable_parallel_tool_use` |
| Streaming usage | yes | usage reported on the stream without any opt-in |
| thinking adaptive (model arg) | yes | thinking blocks returned |
| thinking enabled + budget_tokens (model arg) | yes | thinking blocks returned |
| thinking disabled (model arg) | accepted | no reasoning on the baseline either; the provider never sends this form because newer models reject it |
| output_config.effort=low (model arg) | accepted | |
| `--reasoning-effort high` | yes | sent as `thinking: {type: adaptive}` + `output_config: {effort: high}`; thinking text returned |
| `--reasoning-tokens 1024` | yes | sent as `thinking: {type: enabled, budget_tokens: 1024}`; thinking text returned |
| Reasoning output | text | `thinking` blocks with `signature`, streamed as `thinking_delta`; no separate token count |
| Tools | yes | `input_schema` tools, `tool_use` / `tool_result` blocks |
| Images | yes | base64 image sources; the `image` command described a test picture correctly |
| Beta headers | via model arg | `anthropic_beta=<list>` becomes the `anthropic-beta` header |

### Not chat-capable

| Deployment | Format | What happens |
|---|---|---|
| gpt-5.4-pro | OpenAI | Responses-API only (`chatCompletion: false` in ARM): HTTP 400 "The requested operation is unsupported." on chat completions. Served on the Responses route, now its default (`--route responses`, `openai/gpt-5.4-pro`); a call can take minutes, so that route sets no HTTP timeout and the model layer's attempt timeout governs |
| Cohere-parse-v5 | Cohere | document parsing model: HTTP 404 "Requested API is currently not supported" |
| FLUX.2-pro | Black Forest Labs | image generation: HTTP 404 "Service request failed." on chat completions; ARM still marks it chat-capable, so use `--only` to exclude it |

## Summary matrix

"Token field" is what the provider sends by default. Columns use the verdict vocabulary above with
`yes` shortened to ✔, `rejected` to ✘, `accepted` (no visible effect) to `ok`, `ignored` to `ign`,
and `–` for not probed / not sent.

| Deployment | Token field | temp | top_p | seed | penalties | stop | n | logprobs | top_k | JSON object | Strict schema | Stream usage | Effort | Thinking toggle | Budget | Reasoning out |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gpt-5.6-* | `max_completion_tokens` | ✘ (1 only) | ✘ | ok | ✘ | ✘ | ✔ | ✘ | – | ✔ | ✔ | needs opt-in | ✔ none/low/high/xhigh | – | – | hidden |
| gpt-5.4-mini | `max_completion_tokens` | ok | ok | ok | ok | ✘ | ✔ | ✔ | – | ✔ | ✔ | needs opt-in | ✔ none/low/high/xhigh | – | – | hidden |
| gpt-4o | `max_tokens` | ok | ok | ok | ok | ✔ | ✔ | ✔ | ✘ | ✔ | ✔ | needs opt-in | ✘ | ✘ | – | n/a |
| model-router | `max_tokens` | ok | ok | ok | ok | ign | ✔ | ✔ | – | ✔ | ✘ | default | ✔ low/high (none ign) | on only | – | text |
| DeepSeek-V4-* | `max_tokens` | ok | ok | ok | ok | ✔ | ✔ | ✔ | ok | ✔ | ✔ | default | ✔ low/high switch it on | ign | – | text |
| Mistral-Large-3 | `max_tokens` (none by default) | ok | ok | ok | ok | ✔ | ✔ | ✘ | ✘ | ✔ | ✔ | default | ✘ | ✘ | ✘ | n/a |
| Ministral-3B | `max_tokens` 2048 | ok | ok | ✘ | ok | ✔ | ✔ | ✘ | ✘ | ✔ | ✔ | default (opt-in ✘) | ✘ | ✘ | ✘ | n/a |
| MAI-Thinking-1 | `max_completion_tokens` (forced) | ok | ok | ok | ok | ign | ign | ign | ok | ✘ | ✘ | default | ok, always on | ign | – | hidden |
| Kimi-K2.* | `max_tokens` | ok | ok | ok | ok | ign | ✔ | ✔ | ok | ✔ | ✔ | default | ok | on only | ok (`budget_tokens`) | text |
| Cohere-command-a-plus | `max_tokens` | ok | ok | ok | ok | ign | ✔ | ✔ | ok | ✔ | ✔ | default | ok | on only | ok (`token_budget`) | text |
| grok-4.6 | `max_tokens` | ok | ok | ok | ✘ | ✘ | ✘ | ✔ | ok | ✔ | ✔ | default | ok, always on | ign | – | hidden |
| claude-sonnet-4-6 | `max_tokens` (required) | ok | ok | – | – | ✔ | – | – | ok | – | – | default | ✔ via `output_config.effort` | ✔ adaptive / enabled | ✔ `budget_tokens` | text |

Practical rules that fall out of the table:

- **gpt-5.x**: never send `temperature` other than 1, `top_p`, `stop`, penalties or `logprobs` to gpt-5.6; gpt-5.4-mini
  takes all of those except `stop`. Both need `stream_options` for usage on a stream and `reasoning_effort` is the only
  reasoning control. gpt-5.6 refuses function tools together with `reasoning_effort` on chat completions; the
  Responses route, now the default for those names, takes both.
- **Switching reasoning off** works only on gpt-5.x (`reasoning_effort=none`). DeepSeek is off unless an effort is
  given. Kimi, Cohere, grok, MAI and model-router reason regardless of what is sent.
- **Strict JSON schema** works everywhere on the chat-completions route except model-router and MAI-Thinking-1
  (which rejects both JSON modes). Claude has no `response_format`; use a tool with `tool_choice` instead.
- **Multiple choices** (`n`) fail on grok and are ignored by MAI-Thinking-1.
- **Token budgets** are honoured by Cohere (`token_budget`) and Claude (`budget_tokens`) only.
