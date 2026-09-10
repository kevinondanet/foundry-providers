# Direct OpenAI and Anthropic providers

The shared `InspectAzureAI.Eval.Model.Models.Create/CreateApi` factory accepts Inspect model specifications. Evaluation, grading roles, retries, the example runner, and Sample generation commands use it. Custom registrations remain supported. Azure resource administration and deployment discovery remain Foundry-specific.

| Model specification | Destination |
|---|---|
| `openai/gpt-5.6-sol` | Direct OpenAI |
| `anthropic/claude-opus-5` | Direct Anthropic |
| `openai/azure/gpt-5.6-sol` | Foundry OpenAI Responses deployment |
| `anthropic/azure/claude-opus-5` | Foundry Anthropic Messages deployment |
| Bare deployment | Existing Foundry route selection |
| `azureai/<deployment>` | Foundry model-inference Chat Completions |

## Migration from the old prefixes

**Previously, `openai/` and `anthropic/` selected Foundry. They now select the vendor's direct service.** Each direct prefix emits a migration warning once per process. There is no credential-based destination fallback.

Change existing Foundry specifications to `openai/azure/<deployment>` or `anthropic/azure/<deployment>`. Bare deployments retain their existing behavior, including GPT-5.6 Sol on Foundry Responses and GPT-5.4 Mini on Foundry model-inference.

If relevant Azure endpoint settings exist, direct providers require an explicitly configured direct base URL. Set `OPENAI_BASE_URL=https://api.openai.com/v1` or `ANTHROPIC_BASE_URL=https://api.anthropic.com`, supply `baseUrl` in C#, or use the CLI's `--model-base-url` / `-M base_url=...`. Missing explicit destinations fail before HTTP and name both migration choices. An Azure URL or missing direct credentials also gives an actionable error naming the `/azure/` alternative. Configuring a direct endpoint authorizes routing there; the provider still requires the direct API key.

The final migration commit changes the prefixes, qualified recording names, migration guard, documentation and their assertions together. Reverting that commit restores the previous prefix meanings while keeping the provider implementations, shared factory and compatibility machinery.

## Credentials and C# usage

Direct OpenAI reads `OPENAI_API_KEY`, optionally `OPENAI_BASE_URL`, `OPENAI_ORG_ID`, `OPENAI_PROJECT_ID`, and `OPENAI_SAFETY_IDENTIFIER`. Direct Anthropic reads `ANTHROPIC_API_KEY` and optionally `ANTHROPIC_BASE_URL`. Explicit constructor settings override environment values. Foundry retains Entra ID authentication and existing Azure endpoint variables.

```csharp
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

var openai = Models.Create("openai/gpt-5.6-sol",
    baseUrl: "https://api.openai.com/v1");
var claude = Models.Create("anthropic/claude-opus-5",
    baseUrl: "https://api.anthropic.com");
var foundry = Models.Create("openai/azure/gpt-5.6-sol");
var output = await openai.GenerateAsync("Say hello.",
    config: new GenerateConfig { MaxTokens = 128 });
```

Provider-only callers can construct `OpenAIModelApi` or `AnthropicModelApi` with `apiKey`, `baseUrl`, `config`, `streaming`, `modelArgs`, and typed `DirectClientSettings`. These direct classes never construct Azure credentials or rewrite an endpoint into an Azure route. Use the `Model` wrapper for generation retries and concurrency. `DirectClientSettings` accepts an `HttpClient` or `HttpMessageHandler`, an API-key refresh callback, and a delay callback for deterministic tests. Factory-created direct providers consult enabled API-key override hooks again for each request; authentication failures participate in the wrapper's retry policy.

```sh
export OPENAI_BASE_URL=https://api.openai.com/v1
# Set OPENAI_API_KEY through your usual secret configuration.
dotnet run --project src/InspectAzureAI.Sample -- --model openai/gpt-5.6-sol chat "Say hello."
```

## Constructor options and logs

The following model arguments are consumed as provider options, never blindly appended to the request body:

| Provider | Options |
|---|---|
| Both | `api_key`, `base_url`, `streaming`, `timeout`, `client_timeout`, `max_retries`, `default_headers`, `extra_body` |
| OpenAI | `responses_api`, `responses_store`, `responses_phase`, `background`, `service_tier`, `organization`, `project`, `safety_identifier`, `prompt_cache_key`, `prompt_cache_retention` |
| Anthropic | `betas`, `anthropic_beta` |

Additional wire fields belong in `extra_body`. Unknown constructor arguments fail with guidance. `http_client` is accepted only as typed C# configuration, never through model arguments. OAuth, AWS credentials and custom authentication headers are unsupported; use `api_key` or the typed key callback. Credential fields and authentication headers are redacted from logged model arguments, role configuration and recorded generation configuration. Credential fields inside `extra_body` are rejected before HTTP. Existing Foundry adapters reject credential-bearing model arguments as well.

Logs record qualified model names and sanitized constructor arguments. Task identifiers remain version **3**. Resume first matches exact identifiers, then permits a legacy bare-name match only for an equivalent Foundry candidate, including its role identities. Other identity differences are preserved, ambiguous legacy matches fail, and direct providers cannot reuse legacy Foundry runs. Historical logs remain readable without migration rewrites. Foundry cache directories and endpoint-sensitive keys are preserved; direct caches include provider identity, endpoint and constructor-option inputs.

## API behavior

OpenAI GPT-5 and later, o-series, and Codex prefer Responses when `NumChoices` is null. Any explicit value, including `1`, selects Chat Completions unless `responses_api` or background requirements select Responses. Direct GPT-5.4 Mini uses Responses by default; this is independent of the Foundry deployment heuristic.

Both APIs support text/images, function tools, structured output, streaming and usage. Chat Completions uses new raw HTTP conversion and never attaches `api-version` or goes through Azure SDK types. It accumulates all streamed choices and tool-call fragments. Responses preserves encrypted reasoning and returned phase across turns; `responses_phase=true` optionally synthesizes commentary/final-answer phase for manually created histories. Responses defaults to stateless `store=false`; use `responses_store=true` to opt into storage.

**Reasoning summaries are opt-in.** Unlike Python's live organizational-verification probe, this port sends no summary request unless `ReasoningSummary` is explicitly set. Explicit requests retain the service's errors. Automatic streaming falls back to non-streaming only for HTTP 400 naming `stream`; explicit `streaming=true` remains an error.

Pro and deep-research models default to background submission, including `gpt-5.4-pro`. `ReasoningMode="pro"` also enables background by default, including when changed per generation. Polling retries transient failures without resubmitting the job and sends a bounded cancellation request when the caller cancels. `background` explicitly overrides the default. OpenAI permits `store=false` background requests with temporary retention for polling ([background-mode documentation](https://developers.openai.com/api/docs/guides/background)). Python at the pinned fixture revision automatically backgrounds named pro/deep-research models; the per-generation reasoning-mode default additionally implements the agreed C# behavior.

Claude settings are evaluated after each generation's configuration is merged:

- Claude 3/3.5 do not send thinking settings.
- Claude 3.7–4.5 use explicit reasoning tokens, otherwise effort-to-token translation.
- Claude 4.6 uses adaptive thinking for reasoning effort, including when both effort and a budget are given; an explicit budget alone uses extended thinking.
- Claude 4.7 and later, including Opus 5, reject any `ReasoningTokens` setting before HTTP and direct callers to `ReasoningEffort`.

Thinking requests include `display: "summarized"` unless the full-thinking beta is configured. Family rules govern sampling restrictions, disabled thinking, effort mapping, output-token defaults and caps. Explicit `MaxTokens` is preserved. Betas are rebuilt and deduplicated per request, including configured headers, structured output and output-limit requirements. Automatic streaming activates for callbacks, thinking or requests with at least 8192 output tokens; explicit settings override it.

## Validation and scope

`PythonReference.GenerateProviderFixtures` runs `scripts/generate-provider-goldens.py` against Python Inspect with in-memory HTTP transports. Committed fixtures cover all three APIs and record Python revision `76f1aa761aa7fa7785ca7ea878f00f3ef3704efb`, models, configuration, constructor arguments and scenario inputs. Tests compare normalized JSON without changing array order, relevant headers, images, multi-turn function tools and structured output. They regenerate fixtures when the reference Python environment is installed; ordinary fixture comparisons work offline without Python.

The suite additionally covers lifecycle cancellation/retries, streamed choices and usage, signed Claude thinking replay, family boundaries, endpoint separation, secret redaction and legacy partial/completed resume. `responses_store=true` is sent explicitly in C#, while Python omits it and uses the service default; the golden comparison normalizes this documented equivalence. Dated Claude 4 names are used for boundary fixtures because Python treats the unversioned `claude-opus-4` alias as unknown/current, while C# recognizes its explicit generation.

New hosted tools, batch processing, Anthropic native prompt-cache controls, server-side fallbacks, OAuth sign-in, Bedrock and Vertex are outside this change. Background processing is included. Model names are passed to the selected service; availability and permission are still determined by that account or Foundry deployment.
