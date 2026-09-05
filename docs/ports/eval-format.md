# Port: the `.eval` zip log format

Location: `src/InspectAzureAI.Eval/Log/EvalFormat/` (new), plus additive edits in `Log/EvalLogWriter.cs`
(format-agnostic `Read`/`Write`/`ReadHeader`), `Runner/EvalOptions.cs` (`LogFormat`, `INSPECT_LOG_DIR`) and
`Runner/Eval.cs` (the recorder lifecycle). Tests: `tests/InspectAzureAI.Eval.Tests/EvalFormatTests.cs` (32 cases);
fixtures under `tests/InspectAzureAI.Eval.Tests/fixtures/eval-logs/tiny/` (a Python-written log of `tiny_task.py`:
`inspect eval tiny_task.py --model mockllm/model --log-dir tiny`, and its `inspect log convert`-style chunked
variant under `tiny/chunked/`), `eval-logs/list_logs/` and `eval-logs/python/*.eval` (inspect_ai's own test logs),
and `fixtures/zstd/` (zstandard streams with a `manifest.json` of sizes and SHA-256s).

## What was ported, from where

| Python | C# |
|---|---|
| `log/_recorders/eval.py` `EvalRecorder`, `ZipLogFile`, `LogStart`, `_read_log`, `_read_header`, `_read_all_summaries`, `_read_config_updates`, `_replace_eval_header_in_place`, `LazyList` | `EvalRecorder`, `ZipLogFile` (internal), `LogStart`/`LogResults`, `EvalLogReading` (internal), `LazyList<T>` |
| `log/_recorders/recorder.py` `Recorder`, `create.py`, `file.py` `FileRecorder` naming | `ILogRecorder`, `LogRecorders`, `LogFileNaming` (`{created}_{task}_{id}`, `INSPECT_EVAL_LOG_FILE_PATTERN`) |
| `log/_recorders/json.py` `JSONRecorder` | `JsonRecorder` (whole-file atomic rewrite per flush, on `EvalLogWriter`'s JSON) |
| `log/_recorders/chunked/format.py`, `stats.py`, `design/large-samples.md` Part I | `ChunkedSampleFormat` (naming, chunk math, `ClassifySampleShape`, `EventStatsFor`), `ChunkedSampleReader` (internal) |
| `log/_file.py` `read_eval_log`, `write_eval_log`, `list_eval_logs`, `EvalLogInfo`, `read_eval_log_sample(s)`, `read_eval_log_sample_summaries`, `read_eval_log_headers`, `write_log_dir_manifest`, `log_files_from_ls`, `is_log_file`, `log_file_info` | `EvalLogFiles`, `EvalLogInfo`, `FileEntry` |
| `log/_resolve.py` `resolve_sample_events_data`, `event/_pool.py` `resolve_model_event_inputs/calls`, `_expand_refs` | `PoolRefs` (internal, on the sample JSON) |
| `_util/zipfile.py` (zstandard members, multi-frame), `_util/async_zip.py` | `ZipLogReader`, `ZipLogWriter`, `Crc32`, `ZstdDecoder` (RFC 8878 decoder: the BCL has no zstd and no packages are allowed) |
| `_util/constants.py` formats, `INSPECT_LOG_FORMAT` / `INSPECT_LOG_DIR` | `LogFormat`, `LogFormats` |

`log/_condense.py` and `_util/hash.py` were already ported (`LogAttachments`, `MurmurHash3`); this port uses them on
every sample written (`condense_sample`) and adds a direct equality test against the venv's `mm3_hash`.

## Public C# API

- `EvalRecorder(logDir)`: `LogInitAsync` → `LogStartAsync` → `LogSampleAsync` (buffered, or `writeThrough`) →
  `FlushAsync` → `LogFinishAsync`, plus `SampleSummariesAsync`, `BufferedSampleAsync`, `LogConfigUpdateAsync`,
  `DefaultLogBuffer`. Members appear exactly as Python writes them: `_journal/start.json`, `samples/{id}_epoch_{n}.json`
  per sample with `_journal/summaries/{k}.json` per flush (and `_journal/config_updates/{k}.json`), then
  `summaries.json`, `reductions.json`, `header.json`; a re-logged sample is a second member of the same name and the
  last one wins on read. Every flush copies the temp archive over the destination atomically. `LogFinishAsync`
  returns the header with `Samples` (and `Reductions`) as a `LazyList<T>` that loads from the file on first access.
- Static readers: `EvalRecorder.ReadLog(location, headerOnly, excludeFields)`, `ReadLogBytes`, `ReadLogSample(id/epoch
  or uuid)`, `ReadLogSampleSummaries`, `ReadLogSampleIds`, `ReadMemberNames`, `ReadMember`; `WriteLog(location, log,
  headerOnly)` (a header-only write replaces `header.json` in place).
- `EvalLogFiles`: `ReadEvalLog(path | EvalLogInfo | Stream, headerOnly, resolveAttachments, format, excludeFields)`,
  `WriteEvalLog(log, location, format, headerOnly)`, `ListEvalLogs(logDir, formats, filter, recursive, descending)`,
  `ReadEvalLogSample`, `ReadEvalLogSamples`, `ReadEvalLogSampleSummaries`, `ReadEvalLogHeaders`, `WriteLogDirManifest`.
- `EvalLogWriter.Read/ReadHeader/Write` now route `.eval` paths to this port, so existing callers read either format.
- `EvalOptions.LogFormat` (`null` → `INSPECT_LOG_FORMAT`/`INSPECT_EVAL_LOG_FORMAT`, else `eval`); `EvalOptions.LogDir`
  defaults to `INSPECT_LOG_DIR` (else `logs`). The runner records each sample as it completes (condensed) and flushes
  at `DefaultLogBuffer`, so the file on disk is a readable in-progress log throughout the run.
- `ChunkedSampleFormat` / `SampleShape`: a chunked sample (`samples/{id}_epoch_{n}/sample.json` + `messages/`,
  `events/`, `calls/`, `attachments/` chunks) is read back into the monolith shape — `message_refs` expanded,
  `input_refs`/`call_refs` resolved, `metadata.json` restored, attachments keyed by sequence index.

## Deviations from Python, and why

- Members this port writes are deflate-compressed (method 8); Python writes zstandard (method 93), which the BCL cannot
  produce. Python's `zipfile` reads deflate natively (the `INSPECT_PY` interop test proves it); this port reads both.
- `.eval` reads resolve `events_data` pools into the model events (`input`, `call.request`) as Python does on every
  read; the JSON reader (`EvalLogWriter.Read`, another agent's) still keeps the refs verbatim.
- Local files only: the S3 / fsspec branches (ETags, conditional writes, remote listings) are not ported; `write_eval_log`
  returns nothing rather than a `WriteEvalLogResult`.
- The runner keeps its existing `<local time>_<task>_<6 hex>` file name (with the format's extension); Python's
  `{created}_{task}_{task_id}` naming is available as `LogFileNaming.LogFilePath`/`EvalRecorder.LogFilePath`.
- The JSON recorder only writes at `FlushAsync`/`LogFinishAsync` (as Python), but the runner does not resolve
  `EvalSampleReductions`, so `reductions.json` is written only through `WriteEvalLog` of a log that carries them.
- Errors: a missing sample is `KeyNotFoundException` (Python `IndexError`), neither id nor uuid `ArgumentException`
  (`ValueError`), a non-list summaries member or a zip without `header.json`/`_journal/start.json`
  `InvalidDataException`, `read_eval_log_samples` preconditions `InvalidOperationException` (`RuntimeError`). Like
  Python, the `.eval` header read has no version gate (a newer version is kept as read); the JSON path rejects it.
- The sync `WriteEvalLog`/`WriteLog` wrappers block on the async recorder via the thread pool (the Python API is sync).
- An intermediate snapshot whose rename fails because a reader holds the file (Windows) is skipped with a
  `ProviderLogger` warning and retried at the next flush, as `write_local_snapshot` does; the final write always raises.
- `ZstdDecoder` skips (does not verify) the optional content checksum and rejects dictionary frames (Python never
  writes either); `ReadLogSampleIds` sorts ints zero-padded to 20 digits like Python.

## Not ported

- The chunked *writer* (`chunked/convert.py`): it depends on message/call pooling and `_skeleton.py`, which are not
  in this solution; `EventStatsFor` and the chunk math are provided for a future writer.
- The realtime sample buffer (`log/_recorders/buffer`, SQLite), `log_sample_streaming`, `LogOverview`/`to_overview`,
  `read_eval_log_async` fan-out limits, `SampleSerializationError` depth checks.
