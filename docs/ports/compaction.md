# Port: message trimming and conversation compaction

Location: `src/InspectAzureAI.Eval/Model/Compaction/` (namespace `InspectAzureAI.Eval.Model.Compaction`).
Tests: `tests/InspectAzureAI.Eval.Tests/CompactionTests.cs` (41 tests).

## What was ported, from where

| Python | C# |
|---|---|
| `model/_trim.py` (`trim_messages`, `partition_messages`, `strip_citations`) | `TrimMessages.Trim/Partition/StripCitations`, `PartitionedMessages` |
| `model/_compaction/types.py` (`CompactionStrategy`, `Compact`) | `ICompactionStrategy`, abstract `CompactionStrategy`, `CompactionThreshold`, `CompactionResult`, `ICompact` |
| `model/_compaction/memory.py` | `CompactionMemory` (`MemoryTool`, `ClearMemoryContent`, `HasMemoryCalls`, `MemoryWarningMessage`) |
| `model/_compaction/trim.py` | `CompactionTrim` |
| `model/_compaction/summary.py` (prompt templates verbatim, `_fit_summarization_input`, `_truncate_middle`) | `CompactionSummary` |
| `model/_compaction/edit.py` | `CompactionEdit` |
| `model/_compaction/native.py` | `CompactionNative` |
| `model/_compaction/auto.py` | `CompactionAuto` (native first, summary fallback) |
| `model/_compaction/_compaction.py` (`compaction()`, `_perform_compaction`, `_resolve_threshold`, `_redacted_reasoning_tokens_total`) | `Compaction.Create/Hook/TryRecoverOverflowAsync/ResolveThreshold/RedactedReasoningTokensTotal`, private `CompactionHandler` |
| `event/_compaction.py` | `Context.CompactionEvent` (log-schema's record; `metadata` is the base `TranscriptEvent.Metadata`) (+ `compaction` case in `Log/Json/TranscriptEventConverter.cs`) |
| `model/_tokens.py` | `TokenEstimator` |
| `Model.count_tokens/count_tool_tokens/compact`, `get_model_input_tokens`, `set_model_info` | `ModelCompactionExtensions`, `ICompactionModelApi`, `CompactionModelInfo` |
| `agent/_react.py` `_agent_compact`, `_model_generate`, `_handle_overflow` | `CompactionHook`, `Compaction.Hook`, `Compaction.TryRecoverOverflowAsync`; wired into `Solvers.BasicAgent(compaction:)` |

## Public C# API

- `TrimMessages.Trim(messages, preserve = 0.7)`: same partitioning and preservation rules as Python (system kept, input kept, `preserve` of the conversation from the end, orphan tool messages and orphan tool calls dropped, no trailing assistant). `preserve` outside `[0, 1]` is `ArgumentOutOfRangeException`.
- Strategies take `CompactionThreshold threshold = default` (0.9); an `int` converts to an absolute count, a `double` to a fraction unless it exceeds 1.0, exactly like the Python `int | float` union. `CompactionEdit.KeepThinkingTurns` is `int?` where `null` is Python's `"all"`; `CompactionAuto(memory: null)` is Python's `"auto"`.
- `ICompact` (`CompactInputAsync(messages, force)`, `RecordOutputAsync(input, output)`) from `Compaction.Create(strategy, prefix, tools, model)`; `model` defaults to the sample's active model.
- `CompactionEvent` with Python field names (`type`, `role`, `tokens_before`, `tokens_after`, `source`, `metadata`); metadata carries `strategy`, `messages_before`, `messages_after`, `trigger` (`threshold`/`forced`). Recorded on the ambient `SampleContext.Transcript`.

## The agent-loop seam (`CompactionHook`)

`public delegate ICompact CompactionHook(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools, Model model);`

An agent loop invokes the hook once with its starting messages, tools and model to obtain an `ICompact`; `Compaction.Hook(strategy)` builds one that derives the always-preserve prefix (system + sample input) by partitioning the messages, as `_agent_compact` does. The loop then, per iteration:

1. `var (input, message) = await compact.CompactInputAsync(state.Messages, ct)`; append `message` to the history if non-null; generate with `input`.
2. Append the output message; `await compact.RecordOutputAsync(input, output, ct)`.
3. On `StopReason.ModelLength`: `var recovered = await Compaction.TryRecoverOverflowAsync(compact, historyWithoutTheFailedTurn, ct)`; replace the history and continue if non-null, otherwise fall back to the loop's overflow policy (BasicAgent terminates, as before).

`Solvers.BasicAgent(..., compaction: Compaction.Hook(new CompactionEdit()))` is wired this way now; the react agent port should call the same three points (see `BasicAgent.cs` `LoopAsync`).

## Deviations from Python, and why

- **Token counting is a heuristic.** Python counts text with tiktoken `o200k_base` (+10%); no tokenizer package is allowed here, so `TokenEstimator.CountTextTokens` is `ceil(chars / 4) * 1.1` (min 1). Media, tool-call and message aggregation mirror `_tokens.py`. Providers can supply real counts by implementing `ICompactionModelApi.CountTokensAsync`, which `Model.CountTokensAsync` defers to (any other api gets the model-extras `TokenEstimation` estimate). Absolute thresholds therefore trigger at different points than in Python for the same text.
- **Context window resolution.** There is no model-info catalog in the .NET tree: `Model.ContextWindow()` checks the `CompactionModelInfo` registry (port of `set_model_info`), then `ICompactionModelApi.ContextWindow`, then 128,000 for `ScriptedModelApi` (Python's `mockllm`), else null, which makes a fractional threshold warn and assume 128,000 exactly as Python does.
- **Native compaction is unavailable.** Neither Azure provider has a compaction endpoint; `CompactionNative` (and `Model.CompactAsync`) throw `NotSupportedException` (Python `NotImplementedError`) with the same message text, token count and `CompactionAuto` suggestion. `CompactionAuto` falls back to summary. An api can opt in through `ICompactionModelApi.CompactAsync`.
- **Server-side tool uses (`ContentToolUse`) are not in the .NET content model**, so `CompactionEdit` clears only client-side tool results; `MCP_LIST_TOOLS_NAME` is kept as a constant. Likewise `ContentReasoning` has no `internal` dict, so Google replay anchors are not preserved (irrelevant to the Azure providers), and `ContentDocument` does not exist.
- **Citations**: `ContentText` has no `citations`, so `StripCitations` returns its input unchanged (kept so call sites mirror Python).
- **Consecutive-message collapse** uses the existing `Model.CollapseUserMessages` gated by `ModelApiHooks.CollapseUserMessages`; the assistant/system collapses Python also applies are not in the .NET `Model`.
- **Redacted reasoning accounting** applies Python's `"all"` mode; the .NET `GenerateConfig` has no `reasoning_history`, and `ApplyRedactedReasoningTokensToInput` is false unless an api opts in.
- `CompactionEvent.role` is always null (the .NET `Model` has no role). Base-event `uuid`/`working_start`/`pending`/`metadata` come from log-schema's `TranscriptEvent` base.
- `CompactionSummary.prompt` substitutes `{addendums}` by plain replacement rather than `str.format` (no `{{` escaping).
- Errors are `InvalidOperationException` where Python raises `RuntimeError` (insufficient compaction, summary overflow) and `NotSupportedException` for `NotImplementedError`.
- The handler's lock is a `SemaphoreSlim`; the ambient `Transcript` is the only event sink.

## Not ported

- Checkpointing of the handler state (`_CompactionState` / `Checkpointer`): no checkpoint subsystem exists in the .NET tree.
- The Anthropic `context_management` edit-compaction request shaping and `_compaction_from_message` helpers in the Python Anthropic provider (native compaction is not offered here).
- `reasoning_history` `"last"`/`"none"` modes of `_redacted_reasoning_tokens_total`.
