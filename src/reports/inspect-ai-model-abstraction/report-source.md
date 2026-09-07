# How Inspect AI abstracts models: Microsoft Foundry, Claude, and agent interoperability

## Technical summary

**Inspect AI uses a common conversation and generation model, implemented by provider-specific adapters. It does not convert every model to a single universal wire protocol.** An evaluation or agent supplies typed messages, tool definitions, and generation settings. The selected adapter constructs the vendor request, invokes the appropriate SDK or endpoint, and converts the result into Inspect’s common output and transcript types. [Inspect model orchestration](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_model.py#L409)

For Microsoft Foundry, the essential routing distinction is:

- **Claude on Foundry:** `anthropic/azure/<deployment>` → Anthropic Messages through `AsyncAnthropicFoundry`.
- **OpenAI on Azure:** `openai/azure/<deployment>` → Azure OpenAI through `AsyncAzureOpenAI`, with Chat Completions or Responses selected by the adapter.
- **Azure model inference:** `azureai/<deployment>` → Azure AI Inference `ChatCompletionsClient`.

These routes have different feature coverage. At the reviewed revision, the upstream Python `azureai` adapter does not map `response_schema`, `reasoning_effort`, or `reasoning_tokens` in its normal generation-parameter translation. The Anthropic adapter has extensive model-specific handling for these features. Your local .NET provider adds functionality beyond Python’s Azure inference adapter. [Azure inference provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/azureai.py#L361) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L989)

**Portability has a boundary.** Inspect can preserve thinking state and normalize schemas, but a bridge, provider, SDK, endpoint, or specific model can still reject, alter, omit, or incompletely represent a feature. A valid comparison must record all of these choices. “The same prompt and `high` effort” does not establish equal compute or equivalent behavior.

**Evidence date:** September 7, 2026. Upstream source is pinned to commit `3fea5104189022519d8e432950bc73f37103848e`. This report is a source-level architecture review, with current Microsoft and Anthropic documentation checked separately. No live model requests or deployment benchmarks were performed.

## A shared semantic layer sits between agents and provider protocols

The translation pipeline below identifies the responsibility of each layer. The return path reverses the message translation while retaining usage and provider evidence where implemented.

```text
Evaluation / solver / agent
          |
          | native Inspect call, or framework call through a bridge
          v
Model.generate(messages, tools, tool_choice, config)
          |
          | configuration, history, limits, retries, caching, logging
          v
Selected ModelAPI provider adapter
          |
          | roles + content + schemas + model-specific settings
          v
Vendor SDK -> authenticated endpoint -> model
          |
          | response / streaming events
          v
ModelOutput + typed assistant content + usage + ModelCall
          |
          v
Agent executes client tools, appends results, and calls again
```

**`Model` and `ModelAPI` have different jobs.** `Model` is the common orchestration wrapper. `ModelAPI` is the extension contract: its core `generate` method receives canonical input, tools, tool choice, and `GenerateConfig`, returning `ModelOutput` or output/error together with `ModelCall`. Provider hooks also supply retry classification, connection grouping, defaults, token-counting behavior, and history requirements. This keeps the evaluation loop reusable without pretending each backend has the same semantics. [Inspect model orchestration](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_model.py#L275)

**The abstraction is richer than a text prompt.** Messages distinguish system, user, assistant, and tool roles. Assistant messages can carry tool calls separately from their content. Content types represent text, media, reasoning, and provider-executed tool activity. Output includes choices, stop reasons, usage, and metadata; the human-readable completion is only one view of that result. Flattening everything to the final text discards much of what makes agents reproducible. [Canonical chat messages](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_chat_message.py) [Typed content and reasoning](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/_util/content.py) [Model output and usage](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_model_output.py)

Inspect normalizes API-level inputs. For a hosted model, the server generally remains responsible for the tokenizer and proprietary chat template. A local model provider may apply its own model template. “Different model syntax” therefore includes both the client JSON schema, which Inspect translates, and the model’s token-level prompt encoding, which is commonly outside the hosted adapter.

## Scope and terms: model, host, protocol, and agent are separate

This report covers the model-facing integration architecture across provider families and agent integration routes, with detailed treatment of Foundry and Claude. It does not claim tested compatibility with every agent product or every feature in every model catalog.

**Provider** means the Inspect adapter selected by the model string. **Host** means the service serving the deployment. **Protocol** means the request/response contract, such as Anthropic Messages, OpenAI Responses, or Bedrock Converse. **Agent framework** means the component that manages the loop, tools, and state. A hosted agent service is an additional orchestration boundary; it is not simply a model endpoint.

**Thinking tokens** can refer to requested reasoning budget, reported reasoning usage, visible reasoning summary, or opaque state needed for continuation. Those are different objects. **Strict formatting** can mean prompt instructions, valid JSON, schema-constrained output, or validated tool arguments; these also require separate checks.

The unit of compatibility is the full path: **agent interface → bridge → Inspect adapter → SDK → endpoint/API version → deployment/model version**. Capability changes at any point can alter the result. This is the basis for the route and validation tables below.

## Foundry requires choosing the right adapter and endpoint

The model string selects the adapter before the request is sent. The host name alone cannot select the correct message format. The table describes source-observed routes, not a promise that every deployed model accepts every setting. [Provider registrations](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/providers.py) [OpenAI and Azure OpenAI provider](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai.py#L290) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L430) [Azure inference provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/azureai.py#L283)

| Target | Inspect selector | Client | Protocol | Boundary |
| --- | --- | --- | --- | --- |
| Claude on Microsoft Foundry | anthropic/azure/<deployment> | AsyncAnthropicFoundry | Anthropic Messages | Use the Anthropic endpoint; native Claude semantics and model-specific gates. |
| OpenAI on Azure | openai/azure/<deployment> | AsyncAzureOpenAI | Chat Completions or Responses | Endpoint/API version and deployment must support the selected API. |
| Azure model inference | azureai/<deployment> | Azure AI Inference ChatCompletionsClient | Azure inference chat completions | Upstream mapped configuration is narrower than the shared config type. |
| Claude direct | anthropic/<model> | AsyncAnthropic | Anthropic Messages | Direct-only features are explicitly gated in the adapter. |
| Claude on AWS / Google | anthropic/bedrock/<model> or anthropic/vertex/<model> | AsyncAnthropicBedrock / AsyncAnthropicVertex | SDK adapts Messages to hosting service | Hosting-specific authentication and feature availability. |
| Bedrock generic | bedrock/<model> | AWS Bedrock runtime | Converse / ConverseStream | Separate adapter from anthropic/bedrock; do not equate capabilities. |
| Google models | google/<model> | Google GenAI client | GenerateContent | Contents/parts and thinking config differ from OpenAI and Claude. |
| Compatible / local servers | openai-api/<model>, vllm/<model>, other registered providers | Provider-specific client, often OpenAI-compatible | Advertised server API or local inference | Compatibility does not imply native reasoning, tools, or schema enforcement. |

## Foundry hosting does not erase Anthropic’s contract

Microsoft documents Claude’s resource endpoint as `https://<resource>.services.ai.azure.com/anthropic/v1/messages`. The base URL for an SDK that appends `/v1/messages` is normally the `/anthropic` prefix. A Foundry project URL, an Azure `/models` inference URL, and this Anthropic URL are distinct interfaces. Follow the endpoint supplied for the deployment rather than mechanically appending paths to a project URL. [Microsoft deployment guide](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-claude).

**The current upstream Python auth path has a meaningful limit.** Its Azure branch resolves `AZUREAI_ANTHROPIC_BASE_URL` or the legacy alternative, requires an API key, and constructs `AsyncAnthropicFoundry`. Although the platform supports Entra ID, this branch still enforces an API-key prerequisite. Do not infer Python Inspect managed-identity support merely from Foundry’s authentication capabilities. Your local .NET implementation instead explicitly uses Entra ID. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L430)

For Azure OpenAI, the adapter resolves Azure-specific base URL and API version and supports an Azure token provider. Its API selection is model-aware; Responses is preferred for certain reasoning and coding model families, and can be controlled through `responses_api`. Model names that hide the real family can therefore affect more than display labels. [OpenAI and Azure OpenAI provider](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai.py#L193)

Foundry also distinguishes Azure-hosted and Anthropic-hosted Claude offerings. Features and lifecycle status are deployment-specific. Current Microsoft documentation describes Messages, streaming, thinking, schemas, and tools; this is platform capability evidence, not proof that each feature is surfaced by Inspect. [Microsoft Claude capabilities](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models).

**Practical rule:** choose the Anthropic route for Claude, the OpenAI route for Azure OpenAI features, and the Azure inference route only when that is the target deployment contract. Then check adapter support independently.

## Messages and tool calls are translated structurally

A single logical tool cycle has different native shapes. The sketches below show payload structure, not captured traffic; IDs and values are illustrative.

### OpenAI Chat Completions

```json
{"role":"assistant","tool_calls":[
  {"id":"call_1","type":"function","function":
    {"name":"lookup","arguments":"{\"key\":\"alpha\"}"}}
]}
{"role":"tool","tool_call_id":"call_1","content":"found"}
```

### Anthropic Messages

```json
{"role":"assistant","content":[
  {"type":"tool_use","id":"call_1","name":"lookup",
    "input":{"key":"alpha"}}
]}
{"role":"user","content":[
  {"type":"tool_result","tool_use_id":"call_1","content":"found"}
]}
```

Inspect’s common representation uses a structured `ToolCall` and a `ChatMessageTool` tied to the call ID. The provider translates JSON argument objects versus JSON-encoded strings, role placement, error indicators, and content blocks. Claude tool results are user content blocks, not OpenAI-style `role: tool` messages. Standard Anthropic system instructions are separated into the top-level `system` field; its adapter also contains newer model-specific message handling. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L2294) [OpenAI message and configuration utilities](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_openai.py) 

**Responses is another contract.** It represents function calls and outputs as typed items, with reasoning items and other outputs distinct from text messages. Google uses contents and parts; Bedrock Converse uses its own content blocks and tool-use/result shapes. They converge on Inspect objects, not on identical raw JSON. [OpenAI Responses translation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai_responses.py) [Google provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/google.py) [Bedrock Converse provider](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/bedrock.py)

**Multimodal data needs conversion too.** Image URLs/data URIs, base64 payloads, documents, citations, and tool-returned media require provider-specific conversion and may be restricted by the endpoint. The Anthropic adapter includes image and document conversion; the generic Azure inference message path is substantially smaller. A common content class expresses intent, not universal backend acceptance.

**Tool execution has two owners.** Inspect or the external agent normally executes custom client tools. Vendor server tools execute at the service and can return tool activity and citations inside model output. Treating server-tool results as locally executed function results would misrepresent the agent’s behavior.

## Claude thinking requires control, preservation, and accounting

### 1. Control is translated by model family

`GenerateConfig.reasoning_effort` is an abstract effort setting. `reasoning_tokens` is an explicit budget where supported. In the inspected Claude adapter, older budget-based models can receive `thinking: {"type":"enabled","budget_tokens":...}`. For eligible adaptive models, effort becomes `thinking: {"type":"adaptive",...}` together with `output_config.effort`. The implementation rejects explicit reasoning-token budgets for its Claude 4.7-and-later detection path and applies model-specific disabled-thinking rules. It also removes incompatible sampling settings with warnings. These are explicit branches, not a protocol-wide automatic negotiation. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L989)

For budget-based translation, the shared mapping is `minimal→2048`, `low→4096`, `medium→10000`, `high→16000`, and `xhigh/max→32000`. These are Inspect policy values. They are not vendor promises of equal computation across models. An explicit supported budget takes precedence on the older Claude path. [Reasoning conversion utilities](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_reasoning.py#L25)

### 2. Returned thinking is not necessarily the underlying reasoning text

`ContentReasoning` has reasoning, summary, signature, and redaction fields. Crucially, **field meaning must be read with the provider implementation**: current upstream Anthropic conversion stores a thinking block’s readable text in `summary`, its signature in `reasoning`, and sets `redacted=True`. A redacted block stores opaque data without a readable summary. The local .NET code uses a different arrangement: readable text in `Reasoning` and signature in `Signature`. Sharing class names is insufficient for serialization parity. [Typed content and reasoning](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/_util/content.py#L32) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L4005)

### 3. Continuation requires the original state

The Anthropic adapter forces reasoning history retention and records original thinking blocks internally for replay, including through simpler bridge formats. It reconstructs native blocks using their stored signatures/data. Tool loops must preserve block order, IDs, and opaque state; retaining a visible summary alone is insufficient. A transcript export that keeps only display text can therefore lose replay fidelity even when it remains readable. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L1563) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L4005)

Cross-model reasoning text can sometimes be rendered as ordinary context, but that is a compatibility transformation, not conversion into a valid signed Claude reasoning block. Do not manufacture signatures or assume a block from another provider can serve as native continuation state.

### 4. Usage is separate from displayed reasoning

The current adapter prefers `usage.output_tokens_details.thinking_tokens` when returned. Its fallback counts nonempty visible thinking text through token counting and can undercount the hidden computation represented by a summary. Reasoning is already included in Anthropic’s output-token total; adding it again double-counts tokens. Report missing or estimated reasoning usage explicitly. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L3474)

The engineering implication is to store three separate things: requested effort/budget, provider-reported usage, and the full continuation payload. A chart of visible thinking length cannot substitute for reasoning cost.

## Strict response formatting and strict tool inputs are different

**Prompt-only formatting** asks the model to produce a shape. **JSON mode** requests parseable JSON. **Schema-constrained output** asks the backend to enforce a schema on response text. **Strict tool use** constrains tool names and argument schemas. An agent can need any combination of these.

Inspect exposes `ResponseSchema(name, json_schema, description, strict)` through `GenerateConfig.response_schema`. The providers translate it differently: OpenAI Chat Completions uses `response_format`; Responses uses `text.format`; the current Anthropic adapter writes `output_format` into `extra_body` and adds the structured-output beta header. It also changes additional-properties behavior in the schema. [Generation configuration](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_generate_config.py#L16) [OpenAI message and configuration utilities](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_openai.py#L400) [OpenAI Responses translation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai_responses.py#L537) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L1102)

Anthropic’s current public contract uses `output_config.format`; its documentation states that the older field remains accepted during a transition. The upstream Inspect code reviewed here still emits that legacy form. This is a concrete migration dependency to test, not evidence that structured output is absent. [Anthropic structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).

**The ordinary Anthropic tool conversion does not automatically emit `strict: true`.** It builds `ToolParam` with name, description, and input schema. Setting a response schema does not change this into strict tool validation. If strict tool arguments are required, verify the actual outbound tools array and use a supported provider extension or adapter change; do not assume arbitrary `ToolInfo.options` are copied through. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L1727)

**The bridge can weaken the requested schema.** In the inspected Anthropic bridge, `output_config.format` is converted into Inspect’s `ResponseSchema`. Its source explicitly notes that unsupported schema keywords such as `allOf`, `const`, `$ref`/`$defs`, and `minItems` can be dropped with a warning. The result may constrain less than the client asked for. This is an observed conversion limit at this revision, not a conjecture. [Anthropic bridge implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/agent/_bridge/anthropic_api_impl.py#L266)

Thinking and constrained final JSON are compatible concepts: the constraint applies to the answer segment, not every intermediate reasoning or tool block. Still validate the final application object and distinguish incomplete output, refusal, and truncation from a successful structured answer. [Anthropic structured-output scope](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).

**For this workspace, schema fidelity should be tested in both directions:** client schema → common schema → wire schema, then raw model response → extracted final text → application validation. Validating only the final text cannot reveal a silently weakened generation constraint.

## Shared settings do not imply identical provider support

This matrix separates observed automatic translation from provider capability. “No mapping” means the reviewed normal Python adapter path lacks it; a custom body or an updated adapter may change the result. Such a workaround still needs response decoding and continuation support. [Azure inference provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/azureai.py#L361) [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L989) [OpenAI Responses translation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai_responses.py#L510)

| Capability | Anthropic | OpenAI / Azure OpenAI | Azure inference |
| --- | --- | --- | --- |
| Reasoning controls | Budget/adaptive mappings with model-specific guards | Reasoning settings mapped by selected API/model | No normal reasoning_effort/reasoning_tokens mapping |
| Structured response schema | Legacy output_format + beta; schema transformed | response_format or text.format | No normal response_schema mapping |
| Thinking output | Typed summaries, signatures/data, replay state | API-dependent reasoning/summary representation | Normal output path does not expose equivalent rich reasoning |
| Custom tool calls | Native tool_use/tool_result translation | Function calls and outputs translated | Native tools or model-specific tool emulation |
| Usage accounting | Output/reasoning/cache detail where available | Provider-reported details by API | Prompt/completion/total; streaming usage can be absent |
| Provider escape hatches | Model args, extra_body, headers, betas; platform gates | Provider options and API-specific fields | Remaining constructor args via model_extras |

### A bounded source audit illustrates the coverage gap

For the three inspected fields—`response_schema`, `reasoning_effort`, and `max_tokens`—Anthropic and OpenAI Responses each contain explicit mappings for all three; Azure inference maps only `max_tokens`. The chart counts code mappings at the pinned revision. It is not a completeness score, a performance comparison, or evidence that every model accepts each field. The matrix above provides the more important semantic detail.

Configuration mapping audit: Anthropic 3/3; OpenAI Responses 3/3; Azure inference 1/3. See rendered chart in report.html.

## Wire protocols and streaming retain provider-specific behavior

On hosted routes, the selected SDK sends authenticated network requests using the service’s native HTTP contract. Non-streaming responses are complete JSON objects. Streaming paths receive provider events and accumulate them into a complete output while optionally emitting progress. Anthropic uses SSE content-block events, including text, thinking, signatures, and partial tool JSON; OpenAI Responses uses typed response events. Bedrock streaming uses its AWS runtime event interface rather than assuming Anthropic SSE. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L4300) [OpenAI Responses translation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai_responses.py) [Bedrock Converse provider](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/bedrock.py)

**Partial tool JSON is not yet a runnable tool call.** The stream accumulator must assemble arguments, preserve ordering, and wait for completion. A signature can arrive separately from thinking text. Usage and finish information may arrive late. Providers also differ in how they signal refusals and content filtering.

The Azure inference implementation accumulates chunks and warns when a stream contains no usage. Its retry classification recognizes both HTTP failures and failures occurring after HTTP 200. The OpenAI Azure Chat Completions path avoids automatic streaming in cases where accumulation loses filter details; an explicitly selected streaming path can have different evidence fidelity. [Azure inference provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/azureai.py#L303) [Azure inference provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/azureai.py#L383) [OpenAI and Azure OpenAI provider](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/openai.py#L626)

`ModelCall` exposes provider request/response evidence alongside normalized output. This is invaluable for debugging a field that was omitted or transformed. However, an SDK-level logged object is not automatically a byte-for-byte network capture: SDK defaults, headers, media filtering, and server-side processing can sit outside the representation. Use raw transport fixtures when exact wire parity matters.

**Live streaming versus simulated streaming matters.** An agent-facing interface may generate its stream updates from an already-complete response. That can preserve eventual content while changing first-token latency, cancellation, and incremental tool behavior. The local .NET `InspectChatClient` does exactly this. Do not benchmark it as provider-native token streaming.

## Agent interoperability is implemented at API boundaries

Inspect documents two main bridges. `agent_bridge()` intercepts supported Python SDK calls within the process. `sandbox_agent_bridge()` provides an HTTP interface for agents running in a sandbox, allowing other implementation languages. The supported client-facing families include OpenAI Chat Completions, OpenAI Responses, Anthropic, and Google. `model="inspect"` routes a client call to the configured Inspect model. [Agent bridge documentation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/docs/agent-bridge.qmd)

This supports framework integrations such as OpenAI Agents SDK, LangChain, and Pydantic AI through their model clients, and sandboxed CLI agents such as Claude Code or Codex through compatible endpoint configuration. The framework still owns its agent behavior; Inspect mediates model calls and evaluation state. A framework that bypasses the bridge or uses an unimplemented endpoint is outside that path.

**Client format need not equal target format.** An OpenAI-shaped client can reach Claude through Inspect, but the call passes through two translations: client format → Inspect → Anthropic. Claude-specific semantics must survive both. Prefer a native Anthropic-facing bridge when Claude-native state and tools are central, while still testing the actual mappings.

**Generation forwarding is a policy choice.** By default, upstream bridge documentation says ordinary client generation settings—including reasoning settings—are not forwarded. Inspect’s resolved model configuration and provider defaults govern generation. Structural intent, including tools, tool choice, response format, stop sequences, and seed, is forwarded. Set `forward_generation_config=True` only when the client’s settings should govern. An apparent “lost thinking budget” can therefore be deliberate policy rather than a parser defect. [Agent bridge documentation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/docs/agent-bridge.qmd#L204)

**Hosted Foundry Agent Service is a separate case.** Calling a model through `anthropic/azure` does not by itself run or evaluate an existing hosted Foundry agent. Evaluating that system requires a solver/agent wrapper that invokes the hosted service and imports its behavior or traces as appropriate. If its internal model calls never pass through Inspect, Inspect cannot claim the same per-call control and accounting as a bridged agent. This is an architectural implication of the boundary, not a claim of a universal built-in Foundry Agent Service connector.

**MCP is a tool protocol, not the model message protocol.** Tools exposed by an MCP server can be adapted into the tool layer, and some vendor endpoints can call remote MCP servers themselves. Those are different execution paths, with different visibility and policy. The Anthropic provider explicitly partitions MCP server definitions from ordinary tool definitions. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L1713)

## Model-specific features use explicit extensions and capability gates

Inspect avoids a lowest-common-denominator design by combining shared fields with provider-specific configuration and typed output. Examples in the Anthropic adapter include prompt caching and cache TTL, beta headers, web search, computer use, code execution, document citations, context management, and platform-gated options. Some vendor-defined tools are still client-executed; “native tool schema” does not always mean “server execution.” [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L1755)

Three extension mechanisms are worth separating:

1. **Common setting translated by the provider.** Examples: response schema, reasoning effort, parallel-tool preference.
2. **Provider argument or body/header override.** Examples: constructor options, `extra_body`, and beta headers. Constructor arguments are not universally request-body fields: Anthropic passes remaining arguments to its SDK client, while Azure inference uses remaining arguments as `model_extras`.
3. **A custom registered `ModelAPI`.** This provides full control when the existing adapter cannot represent the endpoint or its return values. It must implement request conversion, response decoding, errors, and relevant continuation semantics—not just construct a URL.

A request-side escape hatch does not automatically teach the response converter a new block type. Likewise, a bridge may forward only selected extension fields. For example, the Anthropic bridge’s general native extra-field list contains `metadata` and `service_tier`; other behavior is handled through explicit mappings. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L4952) [Anthropic bridge implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/agent/_bridge/anthropic_api_impl.py#L315)

Model-family detection supplies another capability layer. The reviewed common model code consults model information and its `family`, then falls back to the service model name. Friendly deployment aliases should therefore retain explicit family metadata or be tested against the expected mapping. Otherwise, the adapter may choose the wrong reasoning mode, default limit, or token-limit field. [Inspect model orchestration](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_model.py#L391)

## A concrete Claude-on-Foundry configuration

This minimal Python example uses the upstream Anthropic route. It is illustrative and was not run against a deployment. Use an existing deployment that supports adaptive thinking and structured output; the deployment name should allow correct model-family resolution.

```bash
export AZUREAI_ANTHROPIC_BASE_URL="https://<resource>.services.ai.azure.com/anthropic"
export AZUREAI_ANTHROPIC_API_KEY="<provided securely>"
```

```python
from inspect_ai.model import GenerateConfig, ResponseSchema, get_model
from inspect_ai.util import JSONSchema

schema = JSONSchema(
    type="object",
    properties={"answer": JSONSchema(type="string")},
    required=["answer"],
    additionalProperties=False,
)
model = get_model(
    "anthropic/azure/<claude-deployment>",
    config=GenerateConfig(
        max_tokens=8192,
        reasoning_effort="high",
        response_schema=ResponseSchema(
            name="answer", json_schema=schema
        ),
    ),
)
# Within an async evaluation or agent:
output = await model.generate("Explain why preserving tool-call IDs matters.")
```

At the reviewed revision, an eligible adaptive Claude request is conceptually assembled as follows. This omits credentials and other optional fields:

```json
{
  "model":"<claude-deployment>",
  "max_tokens":8192,
  "messages":[{"role":"user","content":"..."}],
  "thinking":{"type":"adaptive","display":"summarized"},
  "output_config":{"effort":"high"},
  "output_format":{"type":"json_schema","schema":{
    "type":"object",
    "properties":{"answer":{"type":"string"}},
    "required":["answer"],"additionalProperties":false
  }}
}
```

Inspect also adds the applicable structured-output beta header. Current native Anthropic examples instead put the schema under `output_config.format`; the sketch intentionally reflects the inspected adapter’s legacy request shape. A different model family can change the thinking section. [Anthropic provider implementation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/src/inspect_ai/model/_providers/anthropic.py#L989)

Before accepting the result, inspect the stop reason, parse the final answer as JSON, validate it against the intended schema, and preserve the full assistant message for a subsequent tool turn. Do not serialize only `output.completion` as the agent’s entire state.

## Your .NET implementation is related to upstream, but not identical

The following findings come from the supplied working tree, not from the upstream Python package. They describe code paths, not proven live-service behavior.

**`AzureAIModelApi` extends upstream Azure inference.** It adds family-sensitive reasoning mapping, response-format passthrough, and reasoning-content extraction. Its `model_format` argument supplies a vendor/family hint for opaque deployment names. The source explicitly labels these as port-only behavior. These additions address real upstream coverage gaps but should have their own compatibility contract.

**`AnthropicFoundryModelApi` is a dedicated Messages implementation.** It builds `/anthropic/v1/messages`, authenticates with Entra ID, sends HTTP JSON directly, and implements streamed and non-streamed responses. It supports thinking/signature replay, response schemas through legacy `output_format`, and web-search result/citation handling. Its documentation excludes prompt caching, input document citations, batch mode, and other server tools from the port’s implemented scope.

**Thinking policy is less model-specific than current upstream.** `ThinkingParams` uses a positive explicit budget whenever supplied, otherwise adaptive thinking, without the upstream generation-specific guards. For `reasoning_effort="none"`, it emits no thinking field rather than consulting model defaults and disable support. This creates a credible compatibility risk for models that reject budgets or default to thinking. This is a source-derived risk, not a demonstrated failed request.

**Reasoning serialization differs.** The local implementation stores readable thinking and signatures separately. Upstream’s current Anthropic converter uses summary plus opaque reasoning/redaction state. A direct field-name-based interchange would need explicit translation.

**The Microsoft Agent Framework integration is in process.** `InspectChatClient` implements `Microsoft.Extensions.AI.IChatClient`, converts framework messages through `MafConversion`, and calls the Inspect bridge. `TextReasoningContent.ProtectedData` carries the signature/opaque data. It does not route through Python’s HTTP bridge.

**The MAF generation converter has a narrower explicit field list.** `ToGenerateConfig` maps sampling parameters, limits, tool parallelism, and schema response formats, but does not map a general reasoning setting or arbitrary additional properties. Configure the Inspect-side model deliberately and check forwarding policy. Schema-less JSON mode is explicitly rejected.

**MAF streaming is synthesized.** `GetStreamingResponseAsync` first awaits the complete response and then converts it to updates. Final-message fidelity and live streaming fidelity need separate tests.

Reviewed local files: `InspectAzureAI.Provider/AzureAIModelApi.cs`; `InspectAzureAI.Provider/Anthropic/AnthropicFoundryModelApi.cs`; `InspectAzureAI.Maf/MafConversion.cs`; `InspectAzureAI.Maf/InspectChatClient.cs`. Source paths and hashes are retained in the companion review notes. No implementation changes were made.

## Evidence, limitations, and robustness

**Method.** The review traced the common model contract, content and output types, provider registrations, the Azure/OpenAI/Anthropic adapters, representative Google and Bedrock paths, the Anthropic bridge, and the local .NET conversions. Upstream references are pinned to one commit. Microsoft and Anthropic documentation were checked for endpoint, hosting, authentication, thinking, and schema-contract changes.

**Source precedence.** Executable adapter code establishes what Inspect sends; platform documentation establishes what a service advertises. They are intentionally not treated as interchangeable. For example, upstream provider documentation contains an Azure Anthropic `/models` URL example, while Microsoft’s deployment guide specifies the Anthropic endpoint. This report uses the dedicated endpoint and records the discrepancy rather than relying on the example blindly. [Provider configuration documentation](https://github.com/UKGovernmentBEIS/inspect_ai/blob/3fea5104189022519d8e432950bc73f37103848e/docs/providers.qmd#L315)

**No runtime certification.** No live auth, billing, quota, model availability, refusal, streaming, or schema-enforcement tests were run. No throughput, latency, or accuracy measurements are claimed. Upstream `main` is not necessarily the user’s installed release. The .NET observations reflect the current working files, which may contain uncommitted changes.

**Main sources of uncertainty.** SDK versions can change accepted parameters; service deployments can enable features at different times; model aliases can hide family identity; schema normalization can weaken constraints; stream accumulation can lose metadata; and hidden reasoning usage may be estimated or unavailable. Cross-platform parity should be evaluated against these specific failure modes rather than a single success response.

**Robustness criterion.** A feature should be called supported only when configuration survives conversion, the target accepts it, the response is decoded correctly, and required state survives a subsequent turn. The validation plan below is designed around that criterion.

## Recommended validation before claiming Foundry parity

**Prioritize multi-turn and schema fidelity before expanding the provider list.** Basic text completion verifies only a small fraction of the contract. The following checks are proposed; they were not executed in this review.

1. **Route and identity:** test deployment alias, family metadata, endpoint path, API version, and selected auth method. Include an opaque deployment name.
2. **Tool round trip:** assistant tool call → tool result → assistant continuation, including multiple calls, error results, and correct ID matching.
3. **Reasoning replay:** signed thinking, redacted thinking, empty summaries, and thinking before/after tool calls. Assert that original state survives both framework and provider conversions.
4. **Reasoning controls:** budget-based and adaptive models; unsupported explicit budget; absent effort versus `none`; max-token limits; incompatible temperature settings. Check warnings and actual serialized fields.
5. **Schema fidelity:** simple object, nested schema, enums, required fields, additional properties, and keywords such as `$ref`/`$defs` and `minItems`. Compare the requested and transmitted schemas structurally.
6. **Strict tool inputs:** assert `strict` is actually transmitted when required; verify response schemas do not accidentally stand in for tool constraints.
7. **Bridge policy:** run with and without generation forwarding. Confirm which settings come from the evaluation and which come from the external agent.
8. **Streaming:** fragmented tool JSON, late signatures, final usage, content-filter information, disconnect after HTTP 200, and cancellation. Test genuine incremental streaming separately from synthesized updates.
9. **Accounting:** reconcile raw usage against normalized prompt/output/reasoning/cache totals; identify estimates and absent usage without converting them to zero.
10. **Provider-specific tools and media:** verify the supported web-search/citation and image/document paths; explicitly reject or record unsupported blocks.

Start with canned transport fixtures for exact payload conversion, then use a small live deployment suite for endpoint acceptance and continuation. Preserve the request, response, normalized message, SDK version, model version, and test outcome together. This separates adapter regressions from service behavior changes.

## Decisions and open questions for the implementation

The recommended design is a rich common model with explicit provider adapters and a capability record per deployment. Preserve opaque provider state, normalize only semantics that can be represented faithfully, and reject or warn on unsupported requested guarantees.

The remaining implementation decisions are:

- Is the target behavior parity with a specific Python Inspect release, or a deliberately broader Foundry integration? The current .NET code already implements the latter in several areas.
- Must Entra ID work end to end for Claude in the Python path as well as .NET?
- Which deployments and SDK/API versions form the supported contract, including Claude model families and Foundry hosting type?
- Does “strict” require only constrained final answers, or also strict tool arguments and full JSON Schema keyword preservation?
- Are you evaluating models through a common harness, third-party agents through a bridge, or existing hosted Foundry agents? These require different integration boundaries.
- Is true incremental streaming required, or is final-result streaming acceptable for the MAF consumer?

Resolving these points turns a broad “supports Foundry and Claude” claim into a testable interface specification.
