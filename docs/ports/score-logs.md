# Port: scoring existing logs and recomputing results

Port of `inspect_ai`'s post-hoc scoring into `src/InspectAzureAI.Eval/Runner/Scoring`, sharing one results
computation with the runner (`Runner/EvalResultsBuilder.ComputeResults`).

## What was ported (Python source → C#)

| Python | C# |
|---|---|
| `_eval/score.py`: `score_async`, `_run_score_task`, `task_state_from_sample`, `_get_updated_scores`, `_get_updated_events`, `metrics_from_log_header`, `metric_from_log`, `reducers_from_log_header`, `ScoreAction` | `ScoreLogs.ScoreAsync(EvalLog, scorers, action, epochsReducer, options, ct)`, `ScoreMerging.UpdatedScores` / `UpdatedEvents` (internal), `LogHeader.MetricsFromLogHeader` / `MetricFromLog` / `ReducersFromLogHeader` / `ReducerLogNames` (internal), `ScoreAction` |
| `_cli/score.py`: `score()` (read, resolve action, score, write), `resolve_action` | `ScoreLogs.ScoreAsync(string path, ...)`, `ScoreLogs.ResolveAction`, `ScoreLogOptions.OutputPath` |
| `_eval/task/results.py`: `eval_results`, `compute_eval_scores_for_views`, `reduce_scores`, `EvalSampleReductions` output | `EvalResultsBuilder.ComputeResults(...)` → `ComputedResults(EvalResults Results, IReadOnlyList<EvalSampleReductions>? Reductions)`; `BuildScores` now delegates to it |
| `log/_metric.py`: `recompute_metrics` | `ScoreLogs.RecomputeMetrics(log, scorers, metrics)` and `ScoreLogs.ComputeResults(samples, scorers, reducers, metrics, headline, ...)` |
| `log/_headline.py`: `resolve_headline_metric`, `headline_metric`, `headline_metric_ref` | `HeadlineMetrics.Resolve` / `ForLog` / `Ref` (internal) |
| `event/_tree.py`: `event_tree`, `event_sequence`, `walk_node_spans`; `log/_score.py` `_find_scorers_span` | `EventTree.Build` / `Sequence` / `WalkSpans`, `ScoreMerging.FindScorersSpan` (internal) |
| `_eval/task/log.py` `resolve_eval_scorers`, `scorer/_scorer.py` `unique_scorer_name`, `util/_span.py` `SCORERS_SPAN_NAME` / `SCORER_SPAN_TYPE` | `LogHeader.ToEvalScorers`, `ScoreLogs.UniqueScorerName` (internal), `ScoreLogs.ScorersSpanName` / `ScorerSpanType` |

Additive edits outside the folder: `Eval.RunAsync` now builds its results through `ComputeResults`, so runner
logs carry `reductions`, `results.headline` and `eval.config.epochs_reducer` like Python's; `Transcript` gained a
constructor seeded with existing events (Python's `Transcript(events)`).

## Semantics kept

- Every sample is scored, errored ones included; `completed_samples` counts samples without an error and
  `total_samples` is the sample count of the log.
- Append keeps the existing scores (a reused scorer name becomes `name1`, a colliding sample score key `name-1`),
  re-parents the new scorer spans into the sample's last `scorers` span, appends the new `EvalScore`s,
  reductions and header scorers, and lets the new scorers keep their own metrics. Overwrite replaces all of it
  (the scorers span in place, keeping its position) and applies the header's task-level metrics when it has any.
- Scorers see the existing scores in `TaskState.Scores` on append and an empty dictionary on overwrite; a scorer
  that writes its own name into `Scores` fails the pass (`InvalidOperationException`, Python's `RuntimeError`).
- `ScoreEvent`s carry the unique scorer name and the sample's `model_usage` / `role_usage`; attachments are resolved
  (`core`) for the scorers while the written sample keeps its references.
- Reducers default to `eval.config.epochs_reducer`; an explicit list is recorded there. The headline is re-resolved
  against the merged results with the task's declaration; a stale declaration falls back to the first metric.
- Concurrency mirrors `tg_collect`: the first failure cancels the other samples and is rethrown; caller cancellation
  propagates. `ScoreLogOptions.MaxSamples` bounds the fan-out (Python has no bound).

## Tests

`tests/InspectAzureAI.Eval.Tests/ScoreLogsTests.cs`: ports of `test_get_updated_scores` / `test_get_updated_events`,
append vs. overwrite end to end, recomputation equal to the runner's results (JSON-identical results and
reductions for the same samples, including explicit `max`/`mean` reducer views), header/explicit reducers, errored
samples, failure and cancellation, model and model-role binding, headline resolution, header metrics, score events,
the file overload, and the Python-written fixture log re-scored in place. Two `PythonFact` tests cross-check with the
venv: Python reads the re-scored fixture back (scores, reductions, headline, scorer names, and `_find_scorers_span`
finds the written span) and `eval_results` over the same sample scores gives the same metrics, reductions and headline.

## Deviations from Python (and why)

- **No model registry.** Python rebuilds the active model and model roles from the log header; the port takes
  `ScoreLogOptions.Model` / `ModelRoles`. Without a model, scorers that generate fail with a `PrerequisiteError`
  naming the header model; header roles are not reconstructed.
- **Header metrics by table, not registry.** `metrics_from_log_header` re-creates the built-in metrics
  (`accuracy`, `mean`, `stderr`, `std`, `var`, `bootstrap_stderr`, `ci_wilson`, `perplexity_*`) with the options the
  table covers; anything else, and metric groups (dict-of-lists), throw `NotSupportedException` rather than scoring
  with different metrics. `ScorerDef` carries no instantiation params or metadata, so `eval.scorers[].options` and
  `metadata` are written as `{}` and `EvalScore.params` stays empty.
- **Scorers span type.** Python's `span()` defaults the type to the name, so its scorers span has type `scorers`;
  the port writes that too, and its finder also accepts the runner's `span` type so the port's own logs merge.
- **JSON only.** The path overload refuses the `.eval` zip format (the eval-format port's `EvalLogWriter` can now read and write it, so lifting the guard is a follow-up). There is no
  streaming mode, no interactive prompts (`ResolveAction` uses the prompt defaults), and no `-scored` file naming.
- **Headline fallback is silent** (Python warns once); an unnamed reducer passed explicitly is an `ArgumentException`
  (Python raises from the registry). The runner leaves `epochs_reducer` unset when a custom (unnamed) reducer is used.
- `ScoreLogs.ComputeResults` applies an explicit metric list to every scorer (Python's `score_async` does; its
  `recompute_metrics` uses it only for scores without a scorer, which is idempotent for Python-written headers).
- An explicit `epochsReducer` is not validated against the log's epoch count: Python's `score_async` does not call
  `validate_reducer` either (only `eval` does, which `Eval.RunAsync` mirrors).
- Timelines are not restored into the transcript (the port's `Transcript` has none); `scorer_args` on score events
  is null.
