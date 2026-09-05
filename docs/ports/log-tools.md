# Port: log editing, recovery, bundling, conversion and the `inspect log` helpers

Location: `src/InspectAzureAI.Eval/Log/Tools/` (all new files; nothing outside the folder was edited). Tests:
`tests/InspectAzureAI.Eval.Tests/LogToolsTests.cs` (27 cases, 4 of them `PythonFact` cross-checks against the venv).
Builds on the log-schema port (`EvalLogEdits.cs`: `ProvenanceData`, `TagsEdit`/`MetadataEdit`, `EvalLogEditing`,
`ScoreEdit`/`Edited<T>`, `ScoreEditEvent`) and the eval-format port (`EvalRecorder`, `ZipLogReader`, `EvalLogFiles`).

## What was ported, from where

| Python | C# |
|---|---|
| `log/_score.py` `edit_score`, `_find_scorers_span`, `_drop_legacy_unscored_reason` | `EvalLogEdits.EditScore` (`ScoreMerging.FindScorersSpan` reused) |
| `log/_edit.py` `invalidate_samples`, `uninvalidate_samples`, `_prepare_samples`, `_update_sample_invalidation` (`edit_eval_log` re-exported) | `EvalLogEdits.InvalidateSamples` / `InvalidateAllSamples` / `UninvalidateSamples` / `UninvalidateAllSamples`, `EditEvalLog`, `RecomputeTagsAndMetadata` |
| `_eval/score.py` `resolve_scorers_info`; `_eval/task/results.py` `eval_results` over collected scores | `HeaderScorers.FromLog` / `ComputeResults` (internal) |
| `log/_recover/_read.py` `CrashedEvalLog`, `read_crashed_eval_log`, `read_flushed_sample`; `_write.py` `write_recovered_eval_log`, `default_output_path`, `_StatsAccumulator`, `RecoveryStats`; `_api.py` `recover_eval_log(_async)`, `recoverable_eval_logs`, `RecoverableEvalLog`, `RecoveryNotAvailable`; `design/recover.md` | `EvalLogRecovery` (`ReadCrashedEvalLogAsync`, `WriteRecoveredEvalLogAsync`, `RecoverEvalLogAsync`, `RecoverableEvalLogsAsync`, `DefaultOutputPath`), `CrashedEvalLog`, `RecoveryResult`, `RecoverableEvalLog`, `RecoveryNotAvailableException` |
| `log/_convert.py` `convert_eval_logs`, `_stream_convert_file` | `LogConversion.ConvertEvalLogsAsync` |
| `log/_bundle.py` `bundle_log_dir`, `embed_log_dir`, `_prepare_viewer`, `inject_configuration`, `write_robots_txt`, `copy_log_files`, `move_output`, `is_hf_target`; `log/_file.py` `write_log_listing`, `to_overview`, `LogOverview`; `_view/_dist.py` | `LogBundle` (`BundleLogDirAsync`, `EmbedLogDirAsync`, `WriteLogListingAsync`, `ToOverview`, `InjectConfiguration`, `WriteRobotsTxt`, `CopyLogFiles`, `IsHfTarget`), `LogOverview`, `ViewerAssets` |
| `_cli/log.py` `log_list`, `dump`, `headers`, `schema`, `recover --list --json` (the click layer aside) | `LogCommands.ListLogs` / `ListLogsText` / `ListLogsJson`, `Dump`, `HeadersJson`, `SchemaJson`, `RecoverableLogsJsonAsync` |

## Semantics kept

- `EditScore`: a new score needs a value; an existing score gets its pre-edit state prepended to `History` once,
  the set fields applied (metadata replaces), and an explicit reason drops the legacy `unscored_reason` key; the
  `ScoreEditEvent` carries the last scorers span's id and sits just before that span's end. Metrics are recomputed
  from the header's scorers (score names no scorer accounts for get accuracy + stderr, as `ScorerInfo.from_name`).
  The `PythonFact` test edits the same log with `edit_score` and compares scores, events, results and reductions.
- Invalidation: unknown uuids are rejected before anything changes; an already invalidated sample keeps its
  provenance; `Invalidated` follows whether any invalidated sample remains.
- Recovery: a log is recoverable when it has `_journal/start.json` and no `header.json`; flushed samples are
  streamed and condensed into a clean new file, flushed every 10; stats sum model/role usage with the earliest
  start; results come from the header's scorers/reducers/metrics (a failure is a `ProviderLogger` warning and no
  results); status `error` with a "recovered" `EvalError`; journaled config updates are carried; a successful log of
  the same task in the output directory refuses recovery; `overwrite` writes to the sibling path then moves it over
  the original. The tests build the crashed file with the recorder (flush, buffer one more sample, dispose without
  finishing) — exactly the state a crash between flushes leaves.
- Conversion: file or directory (relative paths kept), `.eval`/`.json` both ways, overwrite guard, `.eval` inputs
  streamed sample by sample with bounded concurrency and periodic flushes; the round-trip `json → eval → json` is
  JSON-identical to the source.
- Bundle: viewer assets copied, `index.html` gets the `log_dir_context` script (`json.dumps` form, verified byte for
  byte against Python), `robots.txt`, logs and their `eval-set.json` under `logs/`, `logs/listing.json` of
  `LogOverview`s (verified against the venv's `write_log_listing`), assembled in a temp dir and moved into place;
  Python's validations (no output dir, output inside the log dir, existing output, empty log dir) are all errors.
- `HeadersJson` is byte for byte what `json.dumps(to_jsonable_python(...), indent=2)` produces for the same log JSON
  (verified against the venv), `ListLogsJson` has Python's field order with `mtime` in milliseconds.

## Deviations from Python, and why

- **No sample buffer database.** Python recovers unflushed samples from its SQLite/filestore buffer and raises
  `RecoveryNotAvailable` when none exists; this solution's recorder has no buffer, so recovery is journal-only,
  samples buffered since the last flush are lost, `RecoverableEvalLog.CompletedSamples`/`InProgressSamples` are 0
  with `Source = "journal"`, and the recovered `EvalError.Traceback` says "recovered from the .eval journal".
  `WriteRecoveredEvalLogAsync` takes an `extraSamples` sequence for a caller with its own source.
- Only monolith sample members (`samples/{id}_epoch_{n}.json`) are recovered; Python's name filter would also pick
  up chunked members, which this recorder never writes.
- Metric recomputation during recovery catches `NotSupportedException`/`ArgumentException`/`InvalidOperationException`
  (Python catches everything); other failures propagate.
- The streaming conversion always starts the output clean (Python's `log_init` would seed it with a stale file's
  summaries) and keeps the log's `error` (Python's streaming path drops it).
- Viewer assets are not shipped: `ViewerAssets.ResolveDistDir` takes a directory or `INSPECT_VIEW_DIST_DIR`
  (the Python package's `_view/dist`, e.g. `<checkout>/src/inspect_ai/_view/dist`); the schema likewise via
  `INSPECT_VIEW_SCHEMA_PATH` or the dist's sibling `inspect-openapi.json`. Hugging Face (`hf/`) targets are a
  `NotSupportedException`; there is no progress display; local filesystems only.
- `ListLogs` names are the plain absolute paths of the eval-format port (Python strips a `file://` scheme first);
  `EvalLogInfo.Mtime` is seconds there, so `ListLogsJson` multiplies by 1000 to match Python's milliseconds.
- Errors: Python's `ValueError`s are `ArgumentException`; `FileExistsError` is `IOException`; the recovery reader
  throws `RecoveryNotAvailableException` directly (Python's API maps the reader's `ValueError` to it).
- The C# `EvalSpec` writer omits `solver_args_passed`/`task_args_passed` when unset and STJ writes astral characters
  as `💡` (log-schema port behaviour, observed while cross-checking `HeadersJson`; `EnsureAscii` lower-cases
  such escapes so the CLI output matches Python).

## Not ported

- The buffer-backed paths of `_recover` (`_buffer.py`, `_reconstruct.py`, `_stream.py`, `_attachments.py`), the
  `eval_retry`/`eval_set` opportunistic recovery hooks and the interactive TUI.
- `convert-chunked` (no chunked writer in this solution), `export-config`, `types`, the click option plumbing, and
  the Hugging Face upload of `bundle_log_dir`.
