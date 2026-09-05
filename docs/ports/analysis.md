# Port: tabular analysis of logs (evals, samples, messages and events tables)

Location: `src/InspectAzureAI.Eval/Analysis/` (new folder, no edits to existing files). Tests:
`tests/InspectAzureAI.Eval.Tests/AnalysisTests.cs` (42 cases, 2 gated on `INSPECT_PY`); fixtures under
`tests/InspectAzureAI.Eval.Tests/fixtures/eval-logs/analysis/` (inspect_ai's `tests/analysis/test_logs`, four
`.json` logs) and `analysis-choices/` (its `test_logs_choices`, two `.eval` logs).

## What was ported, from where

| Python | C# |
|---|---|
| `analysis/_dataframe/columns.py` `Column`, `ColumnType`, `ColumnError` | `Column` (abstract), `ColumnType` enum, `ColumnError` record, `ColumnImportException` (Python's `ValueError(str(error))`), `TableImport` (the `strict=False` tuple) |
| `evals/columns.py` `EvalColumn`, `EvalId`, `EvalLogPath`, `EvalInfo`, `EvalTask`, `EvalModel`, `EvalDataset`, `EvalConfiguration`, `EvalResults`, `EvalScores`, `EvalColumns`; `evals/extract.py` | `EvalColumn`; `EvalColumns.Id/LogPath/Info/Task/Model/Dataset/Configuration/Results/Scores/Default` and the `EvalLog*` extractors (headline columns via the existing `HeadlineMetrics`) |
| `samples/columns.py` `SampleColumn`, `SampleSummary`, `SampleMessages`, `SampleScores`; `samples/extract.py` | `SampleColumn` (`Full`, `PathRequiresFull`), `SampleColumns.Summary/Messages/Scores`, `SampleInputAsStr`, `SampleTotalTokens`, `SampleTotalFallbacks`, `SampleMessagesAsStr`, `AutoSampleId`, `AutoDetailId` |
| `messages/columns.py`, `messages/extract.py` | `MessageColumn`, `MessageColumns.Content/ToolCalls/Default`, `MessageText`, `MessageToolCalls` |
| `events/columns.py`, `events/extract.py`, `tool/_tool_call.py` `substitute_tool_call_content` | `EventColumn`, `EventColumns.Info/Timing/ModelEvent/ToolEvent`, `ModelEventInputAsStr`, `ToolChoiceAsStr`, `CompletionAsStr`, `ToolViewAsStr`, `SubstituteToolCallContent` |
| `extract.py` (`list_as_str`, `remove_namespace`, `score_values`, `score_value`, `score_details`, `auto_id`, `messages_as_str`, `message_as_str`) | `Extract` (public; `AutoId` is MD5 -> shortuuid base-57, bit-identical to Python) |
| `record.py` (`import_record`, `resolve_duplicate_columns`, `_resolve_value`, `_expand_fields`), `util.py` (`resolve_logs`, `resolve_columns`, `match_col_pattern`, `add_unreferenced_columns`, `normalize_records`) | `RecordImporter` (internal), `LogSource` (`resolve_logs`), `Table.FromRecords` |
| `evals/table.py` `evals_df`, `_read_evals_df`, `ensure_eval_data`, `reorder_evals_df_columns` | `EvalsTable.Read/ReadWithErrors/ReadAsync/ReadWithErrorsAsync` |
| `samples/table.py` `samples_df`, `_read_samples_df_serial`, `sample_messages_from_events`, `duplicate_assistant_message_reducer`, `reorder_samples_df_columns`; `messages/table.py` `messages_df`; `events/table.py` `events_df` | `SamplesTable`, `MessagesTable`, `EventsTable` (same four methods each; `MessagesDetail` / `EventsDetail` internal) |
| `_util/format.py` `format_function_call`, `format_value`; Python `str`/`repr`/`pprint.pformat` of JSON values | `PythonFormat` (internal) |
| `_prepare/operation.py`, `prepare.py`, `score_to_float.py`, `model_info.py`, `task_info.py`, `frontier.py` | `Operation` delegate, `Prepare.Apply/ScoreToFloat/ModelInfo/TaskInfo/Frontier` (`ModelInfo` reuses the cost port's `ModelInfoLookup`) |
| pandas `DataFrame` (the return type), `records_to_pandas`, `merge`, `drop_duplicates` | `Table` (ordered columns + rows; `Select`, `WithColumn`, `Where`, `DistinctBy`, `LeftMerge`, `FromRecords`, CSV and JSON Lines writers) |

## How extraction works

A record is serialised with the log format's own `EvalLogWriter.Options` (snake_case, `exclude_none`, Python's
number and datetime forms) and parsed back to a `JsonObject`; a column's JSONPath is evaluated against that, so
every default path (`eval.task`, `output.usage`, `error.message`, ...) sees exactly the JSON Python's
`model_dump(mode="json", exclude_none=True)` sees. Sample columns read the `EvalSampleSummary` JSON unless `Full`.
Extraction functions receive the typed record (`EvalLog`, `EvalSampleSummary` / `EvalSample`, `ChatMessage`,
`TranscriptEvent`) and return a `JsonNode`; `value` transforms and `type` coercion follow `record.py`. Cells are
null / bool / long / double / string / `DateTimeOffset` / `DateOnly` / `TimeOnly`; lists and dicts become
`json.dumps`-formatted strings (via `PythonJson.Dumps`), as in Python.

Verified against the venv (`INSPECT_PY`): `evals_df` column names and every value of the browser row, `samples_df`
column names and `sample_id`s (uuid or MD5/shortuuid auto id), the merged `EvalInfo + SampleSummary + metadata`
column order with `_eval` / `_sample` suffixes, `messages_df` / `events_df` row counts, every tool-event row of
`EventInfo + EventTiming + ToolEventColumns`, and `auto_id`.

## Deviations from Python, and why

- **Table, not DataFrame.** No pandas: `Table` keeps native cell types. `records_to_pandas`'s stringification of
  mixed-type columns (e.g. a score column holding both `"INVALID"` and `0.5`) is not reproduced; the CSV writer
  renders each cell (`True`/`False`, Python float `repr`, ISO 8601, NaN and null empty, `\n` line endings).
- **`strict` is two methods.** `Read` raises `ColumnImportException`; `ReadWithErrors` returns `TableImport`.
  `quiet`/progress and `parallel` (a `ProcessPoolExecutor`) have no counterpart; `ReadAsync` runs the sync read on
  the thread pool and checks the token between logs.
- **JSONPath subset.** Fields, `*`, `[n]`, `[-n]`, `[*]`; filters, slices, unions and `..` are a
  `NotSupportedException` (a `ColumnError`). Paths are not validated against a schema (`validate.py`, `jsonref`),
  so an unknown non-required path reads null instead of "Specified path is not valid".
- **Coercion subset.** `_resolve_value`'s YAML step covers booleans (`yes/no/on/off/...`), ints, floats,
  `.nan`/`.inf` and ISO dates; other YAML forms fall back to the constructor rules. Errors are `FormatException`.
- **Sample extractors are typed.** A `SampleColumn` built from a `Func<EvalSampleSummary, ...>` always receives a
  summary (built from the full sample when full samples were loaded), so `input` is the thinned summary input even
  on full reads, where Python hands the extractor the untruncated `EvalSample`. A `Func<EvalSample, ...>` column
  sets `Full`, as `SampleMessages` does in Python.
- **`pprint` wrapping.** `MessageToolCalls` renders arguments with sorted dict keys like `pformat`, but values
  longer than the 1000-character width are not line-wrapped.
- **Tool event `working_time`** goes through the existing `ToolEvent.Working` `TimeSpan`, so it carries tick
  precision with truncation (`4.317574` reads as `4.3175739`); Python keeps the float. Model events are exact.
- **`EvalMetric.Value` is a double**, so `score_*_*` and the headline value are always floats where Python may give
  an int.
- **In-memory log without a location**: the `log` column is the current directory, which is what Python's
  `native_path(None)` (fsspec) resolves to; kept for parity.
- **Message/event columns with no detail rows** fall back to the samples table (as Python does when
  `len(details_table) == 0`); the reorder step skips a missing `{name}_id` instead of raising.
- `ModelInfo` uses this solution's `ModelInfoLookup` (which adds the Foundry deployment overlay) rather than
  Python's `get_model_info`; dates are `DateOnly`. `Frontier` compares dates as instants when they parse, else as
  text, and skips rows whose task, date or score is missing (pandas drops NA group keys and the port drops NA scores
  before `idxmax`, as the Python fix does).

## Not ported

`log_viewer()` (`_prepare/log_viewer.py`), `parallel=` reads, progress display, `validate.py` schema validation,
`_convert_to_large_string`, `verify_prerequisites`, and the deprecated `inspect_ai.analysis.beta` aliases.
