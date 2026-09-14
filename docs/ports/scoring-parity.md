# Scoring and grading parity: inspect_ai (Python) vs InspectAzureAI (C#)

- Audited 2026-09-10.
- Python: `inspect_ai` at `76f1aa761`
- C#: this repo at `58b8585`
- Method: seven audit slices. Each slice had a first pass and a verification pass that re-read both repos at every cited line. Nothing was built or run. Python-side claims about SymPy, latex2sympy2_extended 1.11.0 and runtime Unicode behaviour come from reading those sources and documentation, not from probes.

## Headline answer

**Mostly, but not fully.** The C# port implements nearly every public scoring and grading symbol. The metric maths (accuracy, mean, std/var/stderr including clustering, ci, ci_wilson, aggregate, krippendorff) and the epoch reducers (mode, median, max, at_least, pass_at, pass_k, majority, collect) are faithful to the bit or near it on a live run. Most metric rows marked divergent below are options that never reach the log header, so they change numbers when a log is rescored or recomputed. The gaps sit in scorers and the plumbing around them. Several of them **silently change scores**:

1. `model_graded_qa` / `model_graded_fact` never look up the `grader` model role, so the evaluated model grades itself even when a grader is bound.
2. The `math` scorer has no SymPy. Symbolic answers become text, and the last-number fallback then produces false positives (`\boxed{42x}` vs `42`, `x=2` vs `y=2`). There are false negatives too (bare `1, 2` tuples, `atan`, `inf`, `\frac12`, unit words, degrees).
3. The CLI name `exact` (explicit or read from a log header) builds `ExactMatch` (exact string match, accuracy) instead of the ported `Scorers.Exact` (normalized tokens, mean).
4. Scores a solver writes into `state.scores` are dropped from samples and results.
5. Metric options (`stderr(cluster=)`, `ci(...)`, `bootstrap_stderr(num_samples=)`, `krippendorff_alpha(level=)`) and scorer `-S` args are not written to the log header, so rescoring from the header silently uses defaults.

Across 206 audited rows (about 199 distinct symbols; 7 appear in two slices):

| Status | Rows | Share |
|---|---:|---:|
| faithful | 67 | 33% |
| divergent | 73 | 35% |
| partial | 21 | 10% |
| missing | 5 | 2% |
| deferred (documented) | 20 | 10% |
| C#-only | 20 | 10% |

Most "divergent" rows are edge cases: non-ASCII case folding, .NET vs Python regex dialects, number formatting, and log-fidelity fields. The ranked list in [Score-affecting divergences](#score-affecting-divergences) separates those from the ones that move real benchmark numbers.

### Counts by slice

| Slice | faithful | divergent | partial | missing | deferred | C#-only | total |
|---|---:|---:|---:|---:|---:|---:|---:|
| Core types, scorer protocol, in-run scoring | 10 | 7 | 8 | 0 | 2 | 3 | 30 |
| Text-matching scorers and normalization | 4 | 14 | 0 | 0 | 0 | 1 | 19 |
| math scorer | 8 | 12 | 1 | 0 | 2 | 2 | 25 |
| Model-graded and composite scorers | 8 | 5 | 0 | 0 | 2 | 2 | 17 |
| Metrics | 4 | 12 | 0 | 1 | 0 | 3 | 20 |
| Epoch reducers and results aggregation | 22 | 9 | 4 | 3 | 1 | 2 | 41 |
| Post-hoc scoring, score editing, control and human scoring | 11 | 14 | 8 | 1 | 13 | 7 | 54 |
| **Total** | **67** | **73** | **21** | **5** | **20** | **20** | **206** |

Rows that appear in two slices: `UNCHANGED`, `ScoreEdit`, `recompute_metrics` (core and post-hoc); `collect_score`, `unique_scorer_name` (core and reducers); `value_to_float` (core and metrics); `ExactMatch` (core and text).

## Reference conventions

File references use the bare file name plus line. Directories:

| File(s) | Directory |
|---|---|
| Score.cs, ScoreValue.cs, ScoreReason.cs, ScorerDelegates.cs, SampleScore.cs, Target.cs, ValueToFloat.cs, Scorers.cs, MetricDict.cs, Metrics.cs, Reducers.cs, ExtraReducers.cs, MatchScorers.cs, ChoiceScorers.cs, ClassificationScorers.cs, PythonText.cs, MathScorer.cs, ModelGraded.cs, CascadeScorer.cs, MultiScorer.cs, PrecomputedScores.cs | `src/InspectAzureAI.Eval/Scorers/` |
| StdMetrics.cs, AggregateMetric.cs, CategoricalMetrics.cs, CategoricalEnumMetrics.cs, GroupedMetric.cs, KrippendorffMetric.cs, PerplexityMetrics.cs, Distributions.cs | `src/InspectAzureAI.Eval/Scorers/Metrics/` |
| SampleRunner.cs, EvalResultsBuilder.cs, MetricDictResults.cs, Eval.cs, EvalOptions.cs | `src/InspectAzureAI.Eval/Runner/` |
| ScoreLogs.cs, ScoreLogOptions.cs, ScoreMerging.cs, EventTree.cs, LogHeader.cs, HeadlineMetrics.cs, ScoreAction.cs, UnavailableModelApi.cs | `src/InspectAzureAI.Eval/Runner/Scoring/` |
| EvalSetLogs.cs | `src/InspectAzureAI.Eval/Runner/EvalSet/` |
| TranscriptEvents.cs, TranscriptEventTypes.cs, SampleContext.cs, Transcript.cs | `src/InspectAzureAI.Eval/Context/` |
| EvalLogConverters.cs, PythonJsonFormat.cs, ScoreConverters.cs, ScalarConverters.cs, TranscriptEventConverter.cs, PythonScalarConverters.cs, PlainJson.cs | `src/InspectAzureAI.Eval/Log/Json/` |
| EvalLog.cs, EvalLogModels.cs, `Log/EvalLogEdits.cs` (edit_eval_log, LogUpdate) | `src/InspectAzureAI.Eval/Log/` |
| `Tools/EvalLogEdits.cs` (EditScore, HeaderScorers, invalidate) | `src/InspectAzureAI.Eval/Log/Tools/` |
| EvalLogFiles.cs | `src/InspectAzureAI.Eval/Log/EvalFormat/` |
| EvalTask.cs, Epochs.cs | `src/InspectAzureAI.Eval/Tasks/` |
| TaskState.cs, BasicAgent.cs, Choices.cs | `src/InspectAzureAI.Eval/Solvers/` |
| Run.cs; `Human/Commands/ScoreCommand.cs`; HumanAgentText.cs | `src/InspectAzureAI.Eval/Agents/`, `.../Agents/Human/` |
| Prepare.cs | `src/InspectAzureAI.Eval/Analysis/` |
| ModelRoles.cs, ModelEvent.cs | `src/InspectAzureAI.Eval/Model/` |
| Catalog.cs, ParameterBinder.cs | `src/InspectAzureAI.Cli/Registry/` |
| `Cli/Commands/ScoreCommand.cs`, ResultsPrinter.cs | `src/InspectAzureAI.Cli/Commands/` |
| PythonJson.cs | `src/InspectAzureAI.Provider/Util/` |
| C# tests (MetricTests.cs, ScorerTests.cs, ...) | `tests/InspectAzureAI.Eval.Tests/` unless noted (Cli.Tests, Examples.Tests) |
| Python refs | relative to `src/inspect_ai/`; Python tests relative to `tests/` |

## Per-slice tables

Status values: **faithful**, **divergent** (behaviour differs), **partial** (subset implemented), **missing**, **deferred** (not ported and documented in `docs/ports`), **C#-only**.

### 1. Core types, scorer protocol and in-run scorer invocation

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `CORRECT/INCORRECT/PARTIAL/NOANSWER` `scorer/_metric.py:36-46` | `ScoreConstants` `ScorerDelegates.cs:6-15` | faithful | — | MetricTests.cs:48 |
| `UNCHANGED` `_metric.py:78` | `ScoreConstants.Unchanged`, `Edited<T>` `ScorerDelegates.cs:17`, `TranscriptEventTypes.cs:202-211`, `EvalLogConverters.cs:403-451` | faithful | — | LogSchemaTests, LogToolsTests |
| `ScoreReason` `_metric.py:48-56` | `ScoreReason` `ScoreReason.cs:9-27` | faithful | — | LogSchemaTests, MetricPortTests |
| `Value` `_metric.py:67-71` | `ScoreValue` `ScoreValue.cs:10-125` | divergent | int and float are both `Num(double)`, so int 1 is written as `1.0` (PythonJsonFormat.cs:39,107-111); `Text` renders 1.0 as "1" and uses "1E+16" (ScoreValue.cs:104-124; documented metrics.md:64-67); `FromJson` maps the strings "nan"/"infinity" to numbers, so a precomputed "nan" becomes unscored NaN instead of 0.0 (ScoreValue.cs:90-98, PrecomputedScores.cs:114); nested containers accepted that Python rejects (ScoreValue.cs:18,25) | LogSchemaTests.cs:212-213,480-590; no int-vs-float test |
| `Score` `_metric.py:112-220` | `Score` `Score.cs:7-52`, `ScoreConverters.cs:99-159` | divergent | legacy `metadata.unscored_reason` not lifted into `reason` (ScoreConverters.cs:112); `AsBool(NaN)` is false (Score.cs:48); `AsInt` truncates "3.5" and saturates NaN/inf (Score.cs:42); `AsFloat` throws on "inf", "-inf", "1_000" (Score.cs:37); no `as_list`/`as_dict` | no accessor tests (ScorerTests.cs:193 calls AsFloat only) |
| `ScoreEdit` `_metric.py:82-109` | `ScoreEdit` + converter `TranscriptEventTypes.cs:214-227`, `EvalLogConverters.cs:403-451` | faithful | — (metrics.md:79 still says not ported) | LogSchemaTests, LogToolsTests |
| `Reference` `_metric.py:223-242` | — | deferred | documented metrics.md:79 | none |
| `SampleScore` `_metric.py:245-272` | `SampleScore` `SampleScore.cs:4-8` | partial | no `sample_metadata_as` (documented metrics.md:79-80) | indirect (MetricTests, MetricDictTests) |
| `ValueToFloat` type `_metric.py:275` | `Func<ScoreValue,double>` `ValueToFloat.cs:11-14` | faithful | — | MetricTests.cs:37-115 |
| `value_to_float` `_metric.py:279-340` | `ValueToFloat.Create/Default` `ValueToFloat.cs:17-101` | divergent | numeric strings parsed with `double.TryParse`: "1_000" and non-ASCII digits give 0.0 plus a warning, Python gives 1000.0 (ValueToFloat.cs:78-79) | MetricTests.cs:37-115, :286 |
| `Scorer` protocol `_scorer.py:34-62` | `delegate Scorer` `ScorerDelegates.cs:21` | divergent | non-nullable return: a scorer cannot decline with None, every result is recorded (documented metrics.md:50-52) | ScorerTests.cs:185 |
| `@scorer` `_scorer.py:133-204` | `ScorerDef`, `Scorers.Custom` `ScorerDelegates.cs:27-38`, `Scorers.cs:78-84`, `MetricDict.cs:72-79` | partial | no params/metadata, so header `options`/`metadata` are `{}` and `EvalScore.params` empty (LogHeader.cs:113,115; documented score-logs.md:57-58); no registry or `scorer_create` for user scorers (Catalog.cs:23-24) | ScorerTests.cs:176,185 |
| `scorer(metrics=list/dict)`, `Task(metrics=)` `_scorer.py:134-135`, `_eval/task/results.py:258-299,468-483`, `_eval/task/task.py:232,639-651` | `ScorerDef.MetricsByKey`, `EvalTask.Metrics/MetricsByKey` `MetricDict.cs:14-62`, `EvalTask.cs:31-43`, `EvalResultsBuilder.cs:119-124`, `Eval.cs:299-308` | partial | only one `MetricDict` per scorer (Python lists may hold several); `metrics=[{...}]` drops the scorer-level EvalScore (EvalResultsBuilder.cs:124; noted only in a code comment); task metrics applied at results time to every scorer, including scorers added later (EvalResultsBuilder.cs:120-122) | MetricDictTests.cs:249,264,408; MetricsByKeyOverrideTests.cs:26-68 |
| `MetricProtocol` `_metric.py:348-368` | `delegate Metric` `ScorerDelegates.cs:41` | faithful | — | MetricTests, MetricPortTests |
| `Metric` `_metric.py:343-378` | `Metric`/`MetricDef` `ScorerDelegates.cs:41-55` | partial | deprecated `list[Score]` signature not accepted | MetricTests |
| `@metric` `_metric.py:494-568` | `MetricDef` `ScorerDelegates.cs:44-67` | partial | no metric registry: header replay uses a fixed table (LogHeader.cs:57-76; documented score-logs.md:54-56); names written unqualified, "mean" not "inspect_ai/mean" (MetricDictResults.cs:295); options recorded only when a factory sets `MetricDef.Options` (MetricDictResults.cs:296-298) | MetricsByKeyOverrideTests.cs:68,90; MetricDictTests.cs:278 |
| `score()` `scorer/_score.py:14-89` | `Agents.ScoreAsync`, `SampleRunner.ScoreIntermediateAsync` `Run.cs:74-84`, `SampleContext.cs:47`, `SampleRunner.cs:367-382` | divergent | intermediate ScoreEvents emitted after all scorers, not after each (SampleRunner.cs:371-379); they lack `scorer`, `scorer_args`, `model_usage`, `role_usage` (SampleRunner.cs:378); single target written as a bare string (ScalarConverters.cs:103-106); a non-sample TaskState only contributes messages/output (SampleRunner.cs:369); no None skip | ReactAgentTests.cs:457; EvalRunnerTests.cs:507,547; LogSchemaTests.cs:156 |
| `collect_score` `_reducer/reducer.py:304-329` | `Reducers.Collect` `ExtraReducers.cs:68-90` | faithful | — | MetricPortTests.cs:703; ScorerPortTests.cs:200 |
| `unique_scorer_name` `_scorer.py:262-269`, `_eval/task/run.py:925-931` | `EvalResultsBuilder.UniqueScorerNames` `EvalResultsBuilder.cs:17-34`, `ScoreLogs.cs:254-255` | faithful | — | EvalRunnerTests.cs:114 |
| `ScoreEvent` `event/_score.py:10-58` | `ScoreEvent` `TranscriptEvents.cs:145-160`, `TranscriptEventConverter.cs:85-95`, `ScoreConverters.cs:23-30` | divergent | live final ScoreEvent leaves `scorer`, `scorer_args`, `model_usage`, `role_usage` unset (SampleRunner.cs:323; the rescoring path sets scorer and usage at ScoreLogs.cs:379); no ScoreEvent for solver-set scores; a one-element list target is written as a string (ScalarConverters.cs:103-106) | LogSchemaTests.cs:150-156,634-635; EvalRunnerTests.cs:95,547 |
| `task_run_sample` scoring block `_eval/task/run.py:2715-2848` | `SampleRunner.RunAsync` `SampleRunner.cs:201-227,316-330` | divergent | **solver-set `state.scores` dropped** from samples and results (SampleRunner.cs:215-221,279); no "Scorer {name} has modified state.scores" check (SampleRunner.cs:219-220); `score_on_error` not implemented (SampleRunner.cs:236-249; documented runner-extras.md:44); timeout error text differs (SampleRunner.cs:224-227); extra StepEvents around each scorer (SampleRunner.cs:318,328); scorers span type "span" not "scorers" (SampleRunner.cs:214; documented score-logs.md:59-60) | EvalRunnerTests.cs:93-95 (locks in StepEvents), :114; no solver-score or timeout test |
| `Target` `scorer/_target.py:4-31` | `Target` `Target.cs:4-25` | faithful | — | LogSchemaTests.cs:240,635 |
| `Scorers` alias `scorer/_scorers.py:11-14` | `EvalTask.Scorers` `EvalTask.cs:29` | partial | `Scanner[Transcript]` not accepted (documented deferred.md:97) | none |
| `recompute_metrics` `log/_metric.py:9-61` | `ScoreLogs.RecomputeMetrics` `ScoreLogs.cs:234-252` | partial | caller must pass scorers; header path cannot rebuild metric groups or non-built-in metrics (LogHeader.cs:37,47,75; documented score-logs.md:54-56) | ScoreLogsTests.cs:283,452 |
| (none) | `Scorers.ExactMatch` + CLI alias `exact`→`ExactMatch` `Scorers.cs:40-42`, `Catalog.cs:20,102-106` | C#-only | **alias shadows the ported `Scorers.Exact`**: `--scorer exact` builds "exact_match" with accuracy instead of "exact" with mean (Catalog.cs:104) | ScorerTests.cs:117,176 |
| (none) | `Score.IsUnscored`, `ScoreValue.IsNaN`, `ScoreReason.All`, `Target.Empty`, `ValueToFloat.Default`, `MetricDict.ForAllKeys` | C#-only | — | MetricTests, MetricDictTests |
| (none) | `ScoreValue.FromJson/ToJson` `ScoreValue.cs:58-102` | C#-only | turns "nan"/"infinity" strings into numbers; a precomputed file cannot carry a bare NaN because `PythonJson.Loads` rejects it (PythonJson.cs:25) | ScorerPortTests.cs:244-311 |
| `eval(score=False)`, `--no-score` `_eval/task/run.py:922-923`, `_eval/eval.py:173`, `_cli/eval.py:717` | — (`EvalOptions.cs` has no flag) | deferred | CLI flag documented cli.md:68-71; programmatic form not mentioned | none |
| `ScorerSpec`/`MetricSpec`/`resolve_metrics` `_scorer.py:65-83,207-249`, `_metric.py:384-392,443-473` | `LogHeader.ToEvalScorers`, `MetricDictResults.HeaderMetrics` `LogHeader.cs:108-117`, `MetricDictResults.cs:274-299` | partial | options and metadata always `{}` (LogHeader.cs:113,115; documented); unqualified metric names (MetricDictResults.cs:295); `[{...}]` recorded as a bare dict (MetricDictResults.cs:284-287) | MetricDictTests.cs:408; MetricsByKeyOverrideTests.cs:68 |
| `MetricScores` / `@metric(scores=)` `_metric.py:476-491`, `results.py:190-225` | `MetricScores` + `MetricDef.Scores` `ScorerDelegates.cs:47,62-67`, `EvalResultsBuilder.cs:127-165` | faithful | — | MetricDictTests.cs:278,325 |

### 2. Text-matching scorers (match, includes, pattern, answer, exact, f1) and normalization

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `match` `scorer/_match.py:8` | `Scorers.Match` `Scorers.cs:28` | divergent | `ignore_case` uses `ToLowerInvariant`, not `casefold` (MatchScorers.cs:72-73): "Straße" vs "strasse" and "ΟΔΟΣ" vs "οδος" are CORRECT only in Python; an unknown `location` throws at build (Scorers.cs:30-33) where Python scores it as "any"; `numeric=True` inherits the Unihan and astral-digit gaps | ScorerTests.cs:21-97 (not mirrored: "2½", "三", "１２３", punctuation variants such as "42!" and "(42).", operator prefixes at scorer level) |
| `includes` `_match.py:45` | `Scorers.Includes` `Scorers.cs:15` | divergent | `ToLowerInvariant` (Scorers.cs:20-21) changes both the match and the recorded answer | ScorerTests.cs:99-114 (C#-authored) |
| `pattern` `scorer/_pattern.py:55` | `Scorers.Pattern` `MatchScorers.cs:177` | divergent | .NET regex dialect: `(?P<name>...)` throws, `(?<name>...)` and `\p{L}` compile where Python raises (MatchScorers.cs:179); `\Z` also matches before a trailing newline; an invalid regex fails at build, not per sample; `\w`/`\b` classes differ ("²" vs U+0301); inherits `match_target` lowercasing | ScorerTests.cs:128-173 (all 17 Python cases; no dialect or non-ASCII cases) |
| `match_target`/`match_first`/`match_all_groups` `_pattern.py:12` | `MatchScorers.MatchTarget` etc. `MatchScorers.cs:238` | divergent | `ToLowerInvariant` vs `str.lower()` final-sigma rule (MatchScorers.cs:245-246) | ScorerTests.cs:128-173 (indirect) |
| `answer` `scorer/_answer.py:35` | `Scorers.Answer` `ChoiceScorers.cs:43` | divergent | inherits the `\w` difference ("ANSWER: A²" vs "A" CORRECT only in C#) and the lowercasing difference | MultipleChoiceTests.cs:600-651 |
| `AnswerPattern` `_answer.py:15`, `_util/pattern.py:5-9` | `AnswerPattern` `ChoiceScorers.cs:10-22` | divergent | LETTER's `[^\w]` runs on .NET `\w` (ChoiceScorers.cs:13); `\Z`→`\z` is a correct translation | MultipleChoiceTests.cs:600-651; Examples.Tests Scorer/ScorerTests.cs:228-231 |
| `f1` `scorer/_classification.py:14` | `Scorers.F1` `ClassificationScorers.cs:14` | divergent | `Math.Round(x, 2, ToEven)` rounds x·100, Python rounds the exact binary value: 0.175→0.17 in Python, 0.18 in C# (ClassificationScorers.cs:59; needs ≥80 unique tokens); inherits `_normalize`; 1.0 renders "1" (documented) | ScorerPortTests.cs:49-81 (no midpoint case) |
| `exact` `_classification.py:43` | `Scorers.Exact` `ClassificationScorers.cs:27` | divergent | **shadowed by the CLI alias** for `--scorer exact` and for header rescoring (Catalog.cs:20,64-67,104; Cli ScoreCommand.cs:90-91); reachable only as `--scorer Exact`; inherits `_normalize` | ScorerPortTests.cs:83-102; Cli.Tests TaskRegistryTests.cs:135 and EvalEndToEndTests.cs:271-276 pin the alias |
| `max_f1_score` `_classification.py:61` | `Classification.MaxF1Score` `ClassificationScorers.cs:48` | divergent | same rounding difference (ClassificationScorers.cs:59) | ScorerPortTests.cs:59-61 |
| `max_exact_score` `_classification.py:73` | `Classification.MaxExactScore` `ClassificationScorers.cs:63` | faithful | — | ScorerPortTests.cs:83-102 |
| `compute_f1` `_classification.py:85` | `Classification.ComputeF1` `ClassificationScorers.cs:79` | faithful | — | ScorerPortTests.cs:80 |
| `_normalize` and helpers `_classification.py:117` | `Classification.Normalize` etc. `ClassificationScorers.cs:94` | divergent | `ToLowerInvariant` not `casefold` (ClassificationScorers.cs:113; documented for f1 only, metrics.md:66); non-ASCII digits not canonicalised, so `exact("１２３", "123")` is CORRECT only in Python (PythonText.cs:96-106); loose underscore check, "1_.5"→"1.5" (PythonText.cs:99); article `\b` uses .NET classes (ClassificationScorers.cs:143) | ScorerPortTests.cs:49-102 (indirect; no non-ASCII) |
| `str_match_scorer` `scorer/_common.py:17` | `MatchScorers.StrMatchScorer` `MatchScorers.cs:37` | faithful | — | ScorerTests.cs:58-69,99-114 |
| `match_str` `_common.py:40` | `MatchScorers.MatchStr` `MatchScorers.cs:58` | divergent | `ToLowerInvariant` (MatchScorers.cs:72-73); numeric branch inherits astral/Unihan gaps | ScorerTests.cs:21-89 |
| `normalize_number` and helpers `_common.py:105` | `MatchScorers.NormalizeNumber` etc. `MatchScorers.cs:143` | divergent | first parse pass is ASCII-only, so astral digits such as "𝟐" are not numbers (MatchScorers.cs:143-152, ValueToFloat.cs:79) | ScorerTests.cs:198-224 (G5 vs `.5g` binary ties unverified) |
| `strip_numeric_punctuation`/`strip_punctuation` `_util/text.py:32` | `MatchScorers.StripNumericPunctuation` `MatchScorers.cs:132` | faithful | — | ScorerTests.cs:217-224 |
| `unicode_number_to_float` `scorer/_unicode.py:7` | `UnicodeNumber.TryParse` `MatchScorers.cs:279` | divergent | lone 万/萬 (10000), 亿/億 (1e8), 兆 (1e12) parse as 0 in C# (MatchScorers.cs:374,743-748), so `match(numeric=True)` on "万" matches "10000" only in Python and "0" only in C#; astral characters handled as UTF-16 units (MatchScorers.cs:452-458,554-603) | ScorerTests.cs:226-262 (no Unihan single-character or astral cases) |
| (none) | `Scorers.ExactMatch` ("exact_match") `Scorers.cs:40` | C#-only | see `exact` for the alias shadowing | ScorerTests.cs:116-126,175-182; Cli.Tests |
| `is_finite_number` `_util/text.py:18` | `PythonText.TryParseFiniteFloat`, `ValueToFloat.TryParseFiniteNumber` `PythonText.cs:96` | divergent | ASCII digits only (ValueToFloat.cs:78-79); accepts misplaced underscores "1_.5", "-_1" (PythonText.cs:99-105) | none direct |

### 3. math scorer

All C# refs are `MathScorer.cs` unless noted; Python refs are `scorer/_math.py`. Python side read against latex2sympy2_extended 1.11.0 and SymPy 1.14.

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `math` factory `_math.py:1180` | `Scorers.Math()` `:20` | divergent | no `timeout` parameter (documented metrics.md:47-49,75-76); symbolic answers compared as normalized text, not SymPy equivalence (documented metrics.md:41-47, deferred.md:100); **undocumented false positives** (last-number fallback behind symbolic answers, cross-variable assignments, near-equal irrationals; see rows below); C#-only sample errors from arithmetic limits hit in the fallback scan (`:229-238`, `:835-837`, `:1013-1016`) and from `(1,2)\%` (`:757`, `:995-998`) | ScorerPortTests.cs:326-501 (name/metrics check at :411-412 is C#-authored) |
| `timeout` worker budget (`_MATH_WORKERS`, `target_timeout`/`answer_timeout`) `_math.py:47-54,163-189,1198-1239` | — | deferred | a slow parse cannot time out; those statuses never occur (documented metrics.md:47-49,75-76) | none |
| `_check_dependency` / antlr version checks `_math.py:1134-1173` | — | deferred | no external parser to check; covered implicitly by metrics.md:41, deferred.md:100 | none |
| score closure result mapping `_math.py:1072-1131,1218-1264` | `MathAnswer.ScoreCompletion/ParseTargets/StatusMetadata` `:197-275` | faithful | — | ScorerPortTests.cs:397-436 |
| `_MathParseError/_MathLimitError/_MathUnsafeError` `_math.py:131-140` | `MathParseException` etc. `:30-36` | faithful | — | ScorerPortTests.cs:416-436,479 |
| pre-parse limits, `_validate_candidate`, `_contains_nested_latex_power` `_math.py:30-37,369-430` | `ValidateCandidate`, `ContainsNestedLatexPower` `:134-141,522-634` | faithful | — | ScorerPortTests.cs:426-432 (SVD, double-bar norm and `\xRightarrow` inputs not mirrored) |
| post-parse limits, `expensive` flag `_math.py:38-45,787-867` | `Power`/`Factorial`/`Binomial` limits `:142-144,1003-1028,1089-1138` | divergent | exponent >10000 throws: target `2^{20000}` is Unscored in C#, a CORRECT self-match in Python (`:1013-1016`); a power whose exponent is a power is not marked expensive: `2^2^3` vs `256` CORRECT only in C#; `MaxResultBits` rejects `(10^{100})^{9999}` (`:144`, `:1019-1023`); factorials 101-10000 evaluated exactly: `101!` vs `101\cdot 100!` CORRECT only in C# (`:1096-1108`); binomial n>10000 limit (`:1118-1121`); no node/depth/arg/symbol/int-bit limits: a 70-term `1+...+1` vs `70` is CORRECT only in C#, a 129-element tuple target Unscored only in Python | none for the limit differences |
| regex constants `_math.py:56-94` | static `Regex` fields `:146-161` | faithful | — | indirect |
| `_replace_unicode` `_math.py:192-220` | `ReplaceUnicode` `:170-194,280-289` | faithful | — | ScorerPortTests.cs:353,363 |
| `_balanced_content`/`_boxed_candidates` `_math.py:223-255` | `BalancedContent`/`BoxedCandidates` `:292-344` | faithful | — | ScorerPortTests.cs:376-384 |
| `_last_delimited_math`/`_last_single_dollar_math` `_math.py:258-290` | `LastDelimitedMath`/`LastSingleDollarMath` `:347-405` | divergent | `LastIndexOf` also finds a `$$` overlapping the closer: `$$3$$$ then 4` vs `3` CORRECT in Python, INCORRECT in C# (`:391-395`) | indirect (ScorerPortTests.cs:333,473) |
| `_strip_delimiters` `_math.py:293-311` | `StripDelimiters` `:408-429` | divergent | length guard keeps `$$$`: target `$$$` Unscored in Python, scored in C#; completion status answer_parse_error vs incorrect (`:418-422`) | ScorerPortTests.cs:476-478 |
| `_append_candidate`/`_answer_candidates`/`_target_candidates` `_math.py:314-366` | `AppendCandidate` etc. `:431-517` | faithful | — | ScorerPortTests.cs:469-480 |
| `_looks_like_prose`/`_is_short_composite_answer` `_math.py:433-449` | `LooksLikeProse`/`IsShortCompositeAnswer` `:650-680` | faithful | — | ScorerPortTests.cs:342-343 |
| `_normalize_text` `_math.py:452-457` | `NormalizeText` `:683-690` | divergent | `ToLowerInvariant` not `casefold` for multi-word prose ("Die Straße" vs "DIE STRASSE") (`:689`) | none |
| `_split_plain_equation` `_math.py:460-478` | `SplitPlainEquation` `:693-727` | divergent | splits on a leading `=`: target `=5` parses as 5 in C#, Unscored in Python (`:710-722`, `:1174-1180`) | none |
| `_percentage_base` `_math.py:780-784,877-885` | `PercentageBase`, `ParseCandidate` percent branch `:730-740,751-759` | divergent | tuple/set percent throws an uncaught `NotNumericException`, so the sample errors (`:754-758`, `:995-998`); Python scores `\boxed{(1,2)\%}` CORRECT. Reverse: `x = 5\%` errors in Python (`TypeError` from `sympy.Mul`), C# scores 0.05 | ScorerPortTests.cs:332,357,494 |
| `_PlainExpressionBuilder`, `_parse_plain_expression`, `_PLAIN_FUNCTIONS` `_math.py:97-121,481-681` | `MathExpressionParser` `:1145-1692` | partial | no free symbols (documented metrics.md:45-47); missing `acos/asin/atan/cosh/sinh/tanh` and inverse hyperbolics: `atan(1)` vs `0.785398163397448` INCORRECT in C# (`:1159`); constants only `pi` and `e`: `inf` vs `\infty` INCORRECT (`:1631-1636`); bare comma lists are not tuples: `1, 2` vs `(1, 2)` INCORRECT (`:1209-1223`); no `%` (Mod) or complex literals: `7 % 3` vs `1` INCORRECT (`:1372-1379`); relations compared as text (documented) | ScorerPortTests.cs:338-339,483-497 |
| `_parse_latex_expression` (normalize_latex + latex2sympy) `_math.py:684-760` | `NormalizeExpression`, `ParseCommand/ParsePrimary/ParseGroup` `:888-915,1524-1626` | divergent | only `\frac`, `\sqrt`, `\binom`, `\pi` evaluate; `\ln 2`, `\log_2 8`, `\sin`, `\infty`, matrices, intervals, complex become text (`:1576-1600`; partly documented metrics.md:75); `\frac12` falls to text and `\sqrt23` evaluates as √23, not 3√2 (`:1293-1300`, `:1614-1618`); any `\frac` after an integer is a mixed fraction: `2\frac{\sqrt{3}}{2}` is 2+√3/2, Python √3 (`:1532-1536`); unit handling: `10 cm` vs `10` INCORRECT in C#, `5\text{ cm}^2` gives 25 vs 5 (`:167`, `:902-906`); only a trailing degree is stripped: `30^\circ + 45^\circ` vs `75` INCORRECT (`:168`, `:901`) | ScorerPortTests.cs:331-364,491-497 |
| `_parse_candidate`/`_parse_first`/`_parse_expression` `_math.py:763-777,870-927` | `ParseCandidate`/`ParseFirst`/`Evaluate` `:745-823,861-885` | divergent | text fallback lacks the 2+-letter word check: target `3+` scored as text in C#, Unscored in Python (`:789-795`); equation right-side retry rethrows limit errors (`:779-782`; reachable only via C#-only limits); C#-only limits propagate from `Evaluate` (`:866-884`) | indirect |
| `_matching_expression_candidate` (prose fallback) `_math.py:930-953,1116-1125` | `MatchingExpressionCandidate` + fallback `:229-238,826-855` | divergent | **fallback runs for every symbolic answer**: `\boxed{42x}` or `\boxed{2x+42}` vs `42` CORRECT in C#, INCORRECT in Python (`:230-237`, `:789-793`); completion `x\n2^{20000}` errors the sample in C# (`:835-842`) | ScorerPortTests.cs:439-452 (no test pins the false positive) |
| `_expression_equivalent` `_math.py:999-1069` | `Equivalent`/`ValuesEquivalent`, `ParseTop` equations `:920-948,1174-1180` | divergent | **equations reduced to numbers**: `x=2` vs `y=2` CORRECT in C# (`:1174-1180`); `4 = x^2` vs `4` CORRECT in C# (`:1177-1179`); no `.equals`: `x^2 - x^2` vs `0` INCORRECT (documented); no element-wise matrix equivalence (documented metrics.md:75) | ScorerPortTests.cs:350,386-394,454-466 |
| `_numeric_equivalent`/`_is_exact_number` `_math.py:956-996` | `NumericEquivalent`, `Power`, `Root`, `IntegerRoot` `:930-961,1003-1087` | divergent | 1e-10 tolerance applies to irrationals: `\sqrt{10^{12}+1}` vs `10^6` CORRECT in C# (`:1030-1040`, `:946`); double cancellation: `\sqrt{10^{20}+1}-10^{10}` vs `0` CORRECT in C# (`:1000`, `:1040`); `\sqrt{-3}` and `(-8)^{1/3}` are NaN so even a self-match fails (`:953-956`); `1.5 \times 10^{400}` overflows and fails a self-match (`:1000`); `\sqrt{10^{400}}` falls to text (`:1077`, `:881-884`) | ScorerPortTests.cs:354,358,363,483-497 |
| (none) | `Rational`, `MathValue`, `MathParsedValue`, `MathExpressionParser` `:39-125,1145-1692` | C#-only | exact big-rational evaluator with double fallback standing in for SymPy; effects listed above | ScorerPortTests.cs:483-497 |
| (none) | `MaxResultBits`, oversized-exponent and binomial limits `:144,1013-1023,1118-1121` | C#-only | C#-only explanations ("expression contains an oversized exponent", "expression is too expensive to evaluate exactly", "binomial argument is too large") give answer_limit/target_parse_error where Python scores normally | none |

### 4. Model-graded and composite scorers

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `model_graded_qa` `scorer/_model.py:114` | `Scorers.ModelGradedQa`, `ModelGraded.Create` `Scorers.cs:52`, `ModelGraded.cs:199` | divergent | **no `model_role`; the `grader` role is never resolved**: `model ?? ActiveModel` (ModelGraded.cs:214), so the evaluated model grades itself even when a grader is bound (`ModelRoles.GetModel` exists, ModelRoles.cs:124) and grader ModelEvents lack `role="grader"`; no list of grader models or `reducer` panel (Scorers.cs:52-58); no model-name string; no callable `include_history` (ModelGraded.cs:218); template not loaded via `resource()`, so a path is used as literal text (Scorers.cs:61); empty `grade_pattern` becomes `Regex("")` and scores "" (ModelGraded.cs:208); a group 1 that did not participate gives "" instead of unscored (ModelGraded.cs:224); .NET regex syntax; a grader with no choices yields a score where Python errors (ModelGraded.cs:241) | ModelGradedTests.cs:42-295 (not mirrored: role tests, file template, panel majority/mode, warn-once, callable include_history, nested-dict metadata, capture case) |
| `model_graded_fact` `_model.py:34` | `Scorers.ModelGradedFact` `Scorers.cs:65` | divergent | shares every `model_graded_qa` gap (same `ModelGraded.Create`) | ModelGradedTests.cs:152,164 |
| `DEFAULT_MODEL_GRADED_QA_TEMPLATE` `_model.py:367` | `ModelGraded.DefaultQaTemplate` `ModelGraded.cs:22` | faithful | — | ModelGradedTests.cs:286 |
| `DEFAULT_MODEL_GRADED_FACT_TEMPLATE` `_model.py:385` | `ModelGraded.DefaultFactTemplate` `ModelGraded.cs:43` | faithful | — | ModelGradedTests.cs:286,152 |
| `default_instructions` `_model.py:406` | `ModelGraded.DefaultInstructions` `ModelGraded.cs:92` | faithful | — | ModelGradedTests.cs:286,89 |
| `DEFAULT_GRADE_PATTERN` `_model.py:433` | `DefaultGradePattern`, `PermissiveGradePattern` `ModelGraded.cs:72,78` | faithful | — | ModelGradedTests.cs:295,114 |
| `chat_history` `_model.py:464` | `ModelGraded.ChatHistory` `ModelGraded.cs:115` | divergent | tool errors render as `type='timeout' message='...'` instead of the message only (ModelGraded.cs:147; ModelGradedTests.cs:206 pins the C# form); container tool-call arguments rendered as JSON, not Python repr (ModelGraded.cs:275). Both change the grader's prompt text | ModelGradedTests.cs:164,187,209,213 |
| `model_scoring_prompt` `_model.py:539` | `ModelGraded.ModelScoringPrompt`/`FormatTemplate` `ModelGraded.cs:161,291` | divergent | only `{name}` and `{{ }}` supported: `{meta[key]}` and `{score:.2f}` throw `KeyNotFoundException` (ModelGraded.cs:313-316); repr emulation differs: 1.0→"1", 1e-07→"1E-07", nested strings always single-quoted and unescaped (ModelGraded.cs:349,353); typed collections (`int[]`, `List<int>`, `Dictionary<string,string>`) render a CLR type name and skip delimiter neutralization (ModelGraded.cs:282-283,351-352) | ModelGradedTests.cs:242,256,265 |
| `neutralize_structural_delimiters` `_model.py:514` | `ModelGraded.NeutralizeStructuralDelimiters` `ModelGraded.cs:105` | faithful | — | ModelGradedTests.cs:242-252 |
| `choice` `scorer/_choice.py:60` | `Scorers.Choice` `ChoiceScorers.cs:37` | faithful | — | MultipleChoiceTests.cs:453-581 |
| `cascade` `scorer/_cascade.py:12` | `Scorers.Cascade` `CascadeScorer.cs:20` | faithful | — (threshold and stage params not in header options, as for every ScorerDef) | ScorerPortTests.cs:107-181 (no end-to-end task test) |
| `multi_scorer` `scorer/_multi.py:19` | `Scorers.MultiScorer` `MultiScorer.cs:16,33` | faithful | — | ScorerPortTests.cs:186,213,504 (test_multi_scorer_aggregation.py not mirrored) |
| `precomputed_scores` `scorer/_precomputed.py:15` | `Scorers.PrecomputedScores`, `PrecomputedScoreFile` `PrecomputedScores.cs:21,50` | divergent | missing record gives Unscored instead of no score (PrecomputedScores.cs:41; metric values equal; documented metrics.md:50-52); local and `file://` only (PrecomputedScores.cs:153-158; documented metrics.md:57); no dict metrics (PrecomputedScores.cs:21; documented metrics.md:58-59); `scores`/`on_missing`/`metrics` params not in header, so the log is not rescorable (LogHeader.cs:113); epoch accepts 1.0 and rejects `true`, the reverse of Python (PrecomputedScores.cs:105-106); non-object `metadata` silently becomes null (PrecomputedScores.cs:118) | ScorerPortTests.cs:244-322 (rescorable, dict-metrics and custom-metrics tests not mirrored) |
| `perplexity` `scorer/_perplexity.py:26` | — | deferred | deferred.md:100-101, metrics.md:77-78; `ModelOutput.cs` has no logprob fields | none |
| `target_perplexity` `scorer/_target_perplexity.py:42` | — | deferred | same deferral; also needs a provider tokenize API | none |
| (none) | `Scorers.Cascade(IReadOnlyList<ScorerDef>, threshold)` `CascadeScorer.cs:74` | C#-only | names stages by `ScorerDef.Name`; duplicate names allowed | ScorerPortTests.cs:178 |
| (none) | `Scorers.Cascade(params (string, Scorer)[])` default-threshold overload `CascadeScorer.cs:11` | C#-only | spelling of Python's default `threshold=1.0` | ScorerPortTests.cs:107 |

### 5. Metrics

Python refs are under `scorer/_metrics/`. C# drops unscored (NaN) scores before calling any metric (EvalResultsBuilder.cs:232, MetricDictResults.cs:94-98), as `results.py:405-410` does, so pipeline numbers match even though a direct C# call skips NaN inside the metric.

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `accuracy` `accuracy.py:14` | `Metrics.Accuracy` `Metrics.cs:11` | faithful | — | MetricTests.cs `accuracy_and_mean_skip_unscored_samples` |
| `mean` `mean.py:11` | `Metrics.Mean` `Metrics.cs:15` | faithful | sequential sum vs numpy pairwise sum: last-bit only | MetricTests.cs `accuracy_and_mean_skip_unscored_samples` (C/I labels only; no custom to_float for Mean) |
| `std` `std.py:539` | `Metrics.Std` `Metrics.cs:36` | faithful | — | MetricPortTests.cs `var_std_and_stderr_match_python`; MetricTests.cs `stderr_and_std_use_the_sample_deviation` |
| `var` `std.py:574` | `Metrics.Var` `StdMetrics.cs:12` | faithful | — | MetricPortTests.cs `var_std_and_stderr_match_python`, `var_returns_zero_below_two_scores_and_skips_unscored` |
| `stderr` `std.py:122` | `Metrics.Stderr` `Metrics.cs:23` | divergent | **`cluster` not written to header options** (Metrics.cs:23-33, MetricDictResults.cs:293-299), so rescoring from the header computes an unclustered stderr (LogHeader.cs:67); list- or dict-valued cluster ids all collapse into one cluster instead of raising (PythonText.cs:144) | MetricPortTests.cs `clustered_stderr_*`, `cluster_partition_uses_python_equality_for_ids` (no header or list-id test) |
| `bootstrap_stderr` `std.py:18` | `Metrics.BootstrapStderr` `StdMetrics.cs:24` | divergent | `num_samples` not in header, so replay uses 1000 (LogHeader.cs:70); negative `num_samples` throws at build (StdMetrics.cs:26); RNG differs (documented metrics.md:60-61) | MetricPortTests.cs `bootstrap_stderr_approximates_the_standard_error_and_handles_degenerate_input` |
| `ci` `std.py:172` | `Metrics.Ci` `StdMetrics.cs:46` | divergent | `level`/`method`/`num_samples`/`cluster` not in header; `ci` absent from the C# replay table (LogHeader.cs:65-75; documented score-logs.md:54-57); `num_samples` validated even with method "t" (StdMetrics.cs:64) | MetricPortTests.cs `ci_*`, `t_inverse_cdf_*` (no end-to-end test) |
| `ci_wilson` `std.py:262` | `Metrics.CiWilson` `StdMetrics.cs:108` | divergent | `level`/`cluster` not in header; replay builds `ci_wilson(0.95)` with no cluster (LogHeader.cs:71) | MetricPortTests.cs `ci_wilson_*` (no end-to-end test) |
| `aggregate` `aggregate.py:18` | `Metrics.Aggregate` `AggregateMetric.cs:14` | divergent | null or empty key rejected at build; Python aggregates a dict's "" entry (AggregateMetric.cs:16) | MetricPortTests.cs `aggregate_*` |
| `frequency` `categorical.py:68` | `Metrics.Frequency`, `Frequency<TEnum>` `CategoricalMetrics.cs:13` | divergent | category keys from `ScoreValue.Text`: 1 and 1.0 are counted together (documented metrics.md:64-65); undocumented: exponent keys "1E-05", "2E+20", "1E+15" vs Python "1e-05", "2e+20", "1000000000000000.0" (CategoricalMetrics.cs:38, ScoreValue.cs:122-124) | MetricPortTests.cs `frequency_*`; MetricsByKeyOverrideTests.cs:109-112; Examples.Tests CategoricalDemoTests.cs:115-121 |
| `categorical` `categorical.py:99` | `Metrics.Categorical`, `Categorical<TEnum>` `CategoricalMetrics.cs:59` | divergent | inherits frequency key formatting | MetricPortTests.cs `frequency_is_unreduced_and_rejects_containers_and_categorical_wraps_it` (no recompute round trip) |
| `grouped` `grouped.py:15` | `Metrics.Grouped` `GroupedMetric.cs:29` | divergent | `name_template` filled by literal `Replace`, not `str.format` (escapes, format specs, unknown placeholders) (GroupedMetric.cs:68); **list- or dict-valued group metadata all named "System.Collections.Generic.List`1[System.Object]"**, so every list group merges into one (GroupedMetric.cs:55, PythonText.cs:24); empty `group_key` rejected (GroupedMetric.cs:38) | MetricPortTests.cs `grouped_*` (no format-escape or container-metadata test) |
| `krippendorff_alpha` `krippendorff.py:22` | `Metrics.KrippendorffAlpha` `KrippendorffMetric.cs:17` | divergent | `level` not in header: a Python rescore of a C# log computes nominal alpha; C# cannot replay it at all (MetricDictResults.cs:293-299, LogHeader.cs:65-75) | MetricPortTests.cs `krippendorff_*` (all 21 Python cases have counterparts) |
| `perplexity_per_token` `perplexity.py:65` | `Metrics.PerplexityPerToken` `PerplexityMetrics.cs:16` | divergent | `num_tokens` via `(long)Convert.ToDouble`: "12.5" truncates to 12 and NaN/inf are cast, where Python `int()` raises (PerplexityMetrics.cs:67) | MetricPortTests.cs `perplexity_metrics_*` (no string or non-finite case) |
| `perplexity_per_seq` `perplexity.py:94` | `Metrics.PerplexityPerSeq` `PerplexityMetrics.cs:36` | divergent | same `num_tokens` coercion (PerplexityMetrics.cs:67) | same |
| `value_to_float` `scorer/_metric.py:278` | `ValueToFloat.Default/Create` `ValueToFloat.cs:17` | divergent | "1_000" and "３" give 0.0 plus a warning, Python 1000.0 / 3.0 (ValueToFloat.cs:78-79) | MetricTests.cs `value_to_float_*` (no underscore or Unicode-digit case) |
| `bootstrap_std` (deprecated alias) `scorer/__init__.py:128-133` | — | missing | not in `__all__`; low priority | none |
| (none) | `Distributions` (`TInvCdf`, `RegularizedIncompleteBeta`, `LogGamma`, `NormalInvCdf`) `Distributions.cs:8` | C#-only | Lanczos `LogGamma` and `Math.Log(1-x)`: sub-1e-15 noise only | MetricPortTests.cs `t_inverse_cdf_*`, `normal_inverse_cdf_matches_cpython` |
| (none) | `random` parameter on `BootstrapStderr`/`Ci` `StdMetrics.cs:24` | C#-only | seedable `System.Random` | MetricPortTests.cs (seeded) |
| (none) | `Metrics.CategoryName<TEnum>`/`CategoryNames<TEnum>` `CategoricalEnumMetrics.cs:12` | C#-only | enum label default is the lower-cased member name ("notsure", not "not_sure") | Examples.Tests CategoricalDemoTests.cs:115-121 |

### 6. Epoch reducers and eval results aggregation

Reducer refs are `scorer/_reducer/`; results refs are `_eval/task/results.py`.

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `mode_score` `reducer.py:12` | `Reducers.Mode` `Reducers.cs:37` | faithful | — | MetricTests.cs:148,189,235,357 |
| `majority_score` `reducer.py:41` | `Reducers.Majority` `ExtraReducers.cs:24` | faithful | `panel.votes` stores int votes as doubles (log only) | MetricPortTests.cs:629,650,678 |
| `mean_score` `reducer.py:85` | `Reducers.Mean` `Reducers.cs:24` | divergent | plain double average vs `statistics.mean` exact fractions: `[0.1,0.2,0.3]` gives 0.20000000000000004 in C#, 0.2 in Python (Reducers.cs:25) | MetricTests.cs:148,345 (integer inputs only) |
| `median_score` `reducer.py:107` | `Reducers.Median` `Reducers.cs:28` | faithful | — | MetricTests.cs |
| `at_least` `reducer.py:129` | `Reducers.AtLeast` `Reducers.cs:62` | faithful | — | MetricTests.cs |
| `pass_at` `reducer.py:163` | `Reducers.PassAt` `Reducers.cs:73` | faithful | — | MetricTests.cs:148,181 |
| `pass_k` `reducer.py:208` | `Reducers.PassK` `ExtraReducers.cs:51` | faithful | one-ulp difference only once a binomial exceeds 2^53; k<0 gives NaN where Python raises | MetricPortTests.cs:590,614 |
| `max_score` `reducer.py:247` | `Reducers.Max` `Reducers.cs:40` | faithful | — | MetricTests.cs:235,262 (NaN-order-independence test not mirrored) |
| `collect_score` `reducer.py:304` | `Reducers.Collect` `ExtraReducers.cs:68` | faithful | — | MetricPortTests.cs:703,734 |
| `_reduced_score`/`_nan_score`/partitioning/`_is_reducible` `reducer.py:332` | `Reducers.ReduceScalars/ReduceDict/ReduceList` etc. `Reducers.cs:181` | faithful | — | MetricTests.cs:169,207,271,286,300,328 |
| `score_reducer` / `reducer_register` `registry.py:35` | (private `Reducers.Named`) `Reducers.cs:97` | deferred | custom reducers cannot be registered or created by name (ExtraReducers.cs:112-124, Catalog.cs:38-54); an unnamed custom reducer logs `reducer=null` (EvalResultsBuilder.cs:153); deferral only indirect (cli.md:57-59, score-logs.md:63-64) | none |
| `create_reducers` `registry.py:133` | `Reducers.Create`, `Catalog.CreateReducers`, `LogHeader.ReducersFromLogHeader` `ExtraReducers.cs:97` | divergent | a `_k` suffix on a reducer without `k` ("mean_2", "max_3") is silently stripped (ExtraReducers.cs:102-119; also header replay LogHeader.cs:83) where Python raises; only a single string, no instance or mixed list (list form only in Catalog.cs:57) | MetricPortTests.cs:725-742; Cli.Tests TaskRegistryTests.cs:153-160 |
| `reducer_log_name` `registry.py:115` | `Reducers.NameOf` `Reducers.cs:17` | partial | names exist only for `Reducers` factory output | MetricPortTests.cs:721,729-737 |
| `reducer_log_names` `registry.py:104` | `EvalResultsBuilder.EpochsReducerNames`, `LogHeader.ReducerLogNames` `EvalResultsBuilder.cs:92` | divergent | **`Epochs(n, [])` recorded as null and omitted** (EvalResultsBuilder.cs:94-97, PythonScalarConverters.cs:205-210); Python writes `[]`. A later recompute then applies mean (LogHeader.cs:80-84), and eval-set reuse treats `[]` as the default (EvalSetLogs.cs:248-257); one unnamed reducer nulls the whole list (EvalResultsBuilder.cs:99-100) | EvalRunnerTests.cs:639; ScoreLogsTests.cs:345,380; EvalSetTests.cs:512-515 (no `[]` test) |
| `validate_reducer` `registry.py:176` | `Reducers.Validate` `ExtraReducers.cs:131` | faithful | — | MetricPortTests.cs:745; EvalRunnerTests.cs:617 |
| `set_reducer_name` `registry.py:164` | — | missing | not exported or called in Python; no behaviour lost | none |
| `ScoreReducer`/`ScoreReducers` `_reducer/types.py:6` | `delegate ScoreReducer` `ScorerDelegates.cs:70` | partial | no string-or-list union; names go through `Reducers.Create` (Epochs.cs:11) | none |
| `Epochs` `_eval/task/epochs.py:4` | `Tasks.Epochs` `Epochs.cs:9` | partial | `reducer` cannot be a string, list of strings, or single reducer (Epochs.cs:11) | TaskTests.cs:64; EvalRunnerTests.cs:639 |
| `eval_results` `results.py:90` | `EvalResultsBuilder.ComputeResults` `EvalResultsBuilder.cs:55` | divergent | recompute naming: Python names scorers against the EvalScore names built so far (results.py:156-162), so a dict key equal to a later scorer's name renames that scorer; C# always uses `UniqueScorerNames` (ScoreLogs.cs:201, Tools/EvalLogEdits.cs:336); **solver-written scores are never aggregated** (SampleRunner.cs:108,215-222,279) | ScoreLogsTests.cs:283,662; EvalRunnerTests.cs:114 |
| `ScorerInfo.from_scorer/from_name` `results.py:54` | `ScorerDef` + orphan fallback `ScoreLogs.cs:203` | partial | `from_name` never loads the named scorer's metrics, so an orphan "f1" or "exact" score gets accuracy+stderr instead of mean+stderr (ScoreLogs.cs:209-210, Tools/EvalLogEdits.cs:344); `EvalScore.params` `{}` and `metadata` null (EvalResultsBuilder.cs:264-270; params documented score-logs.md:57-58) | ScoreLogsTests.cs (no from_name metric test) |
| `compute_eval_scores_for_views` + helpers `results.py:190` | `ComputeViews`, `MetricDictResults.Filter/Flatten`, `HasRepeatedSampleIds` `EvalResultsBuilder.cs:106` | divergent | unnamed reducer gives view name null (EvalResultsBuilder.cs:153); repeated-id check keeps int 1 and "1" apart (Eval.cs:534, EvalResultsBuilder.cs:196) where Python merges them | MetricPortTests.cs:765,785,808; MetricDictTests.cs:278,312,325; EvalRunnerTests.cs:230 |
| `compute_eval_scores`/`split_metrics` `results.py:249` | `ComputeEvalScores`, `ScorerDef.MetricsByKey`, `HeaderScorers.MetricsOf` `EvalResultsBuilder.cs:176` | divergent | `metrics=[{...}]` drops the scorer-level EvalScore, also when recomputing Python headers (EvalResultsBuilder.cs:124, Tools/EvalLogEdits.cs:369-393); several dicts merged per key on header replay instead of separate groups (Tools/EvalLogEdits.cs:379-385) | MetricDictTests.cs:83,249,264,297; MetricsByKeyOverrideTests.cs |
| `scorer_for_metrics` `results.py:394` | `ScoreForMetrics`, `MetricValue` `EvalResultsBuilder.cs:230` | divergent | a metric returning a non-numeric string or null is logged as NaN where Python raises (EvalResultsBuilder.cs:297-303); `EvalMetric.params` `{}` (documented metrics.md:62-63) | MetricTests.cs:118; MetricPortTests.cs:501 |
| `scorers_from_metric_dict` `results.py:486` | `MetricDictResults.ScorersFromMetricDict` `MetricDictResults.cs:54` | divergent | **a None entry for a key is counted as unscored and excluded** (MetricDictResults.cs:88-92); Python counts it scored and it becomes 0.0 (results.py:527-535), so `scored_samples`, `unscored_samples` and the metric value all change; None/non-numeric metric results logged as NaN instead of failing validation (MetricDictResults.cs:110,118,123) | MetricDictTests.cs (18 tests; :151 asserts the C# behaviour) |
| `resolve_glob_metric_keys` `results.py:641` | `ResolveGlobMetricKeys`, `GlobRegex` `MetricDictResults.cs:145` | faithful | reversed range `[z-a]` throws in .NET (invalid input only) | MetricDictTests.cs:111,216,229 |
| `reduce_scores` `results.py:678` | `EvalResultsBuilder.ReduceScores` `EvalResultsBuilder.cs:200` | divergent | groups by typed `SampleIdKey`, so int 1 and "1" stay separate (Eval.cs:534) | EvalRunnerTests.cs:205 (indirect) |
| `metrics_unique_key`/`with_suffix` `results.py:702` | `UniqueMetricKey` `EvalResultsBuilder.cs:274` | faithful | — | none |
| `call_metric`/`is_metric_deprecated` `results.py:599` | — | missing | shim for deprecated `list[Score]` metrics; nothing to port | none |
| `resolve_headline_metric` `log/_headline.py:26` | `HeadlineMetrics.Resolve` `HeadlineMetrics.cs:16` | faithful | fallback is silent where Python warns once (documented score-logs.md:63) | ScoreLogsTests.cs:509 |
| `headline_metric` `_headline.py:106` | `HeadlineMetrics.ForLog` `HeadlineMetrics.cs:32` | faithful | — | AnalysisTests.cs:183 |
| `headline_metric_ref` `_headline.py:126` | `HeadlineMetrics.Ref` `HeadlineMetrics.cs:39` | faithful | — | ScoreLogsTests.cs:514,517,662 |
| `ResolvedHeadlineMetric` `_headline.py:13` | `ResolvedHeadlineMetric` `HeadlineMetrics.cs:6` | faithful | — | indirect |
| `HeadlineMetric` `log/_log.py:736` | `Log.HeadlineMetric` `EvalLogModels.cs:39` | faithful | — | ScoreLogsTests; LogSchemaTests |
| `Task(headline_metric=)` / `resolve_headline_metric_spec` `_eval/task/task.py:537` | — | missing | `EvalTask` has no headline declaration; the runner neither sets `EvalSpec.HeadlineMetric` (Eval.cs:129-170) nor passes one to results (Eval.cs:299-308), so live runs always headline the first metric of the first score; no `"<scorer>.<score>"` shorthand; undocumented | none |
| `EvalResults` `log/_log.py:841` | `Log.EvalResults` `EvalLog.cs:252` | faithful | — | LogSchemaTests; ScoreLogsTests |
| `EvalScore` `_log.py:791` | `Log.EvalScore` `EvalLog.cs:269` | faithful | params/metadata never filled (see ScorerInfo) | MetricDictTests; ScoreLogsTests |
| `EvalMetric` `_log.py:769` | `Log.EvalMetric` `EvalLog.cs:286` | faithful | value is double (documented analysis.md:62) | MetricTests; LogSchemaTests |
| `EvalSampleReductions`/`EvalSampleScore` `_log.py:819` | `EvalSampleReductions`/`EvalSampleScore` `EvalLogModels.cs:51` | faithful | — | ScoreLogsTests.cs:662; LogSchemaTests |
| `_unique_scorer_names` `results.py:304` | `EvalResultsBuilder.UniqueScorerNames` `EvalResultsBuilder.cs:17` | faithful | — | EvalRunnerTests.cs:114 |
| (none) | `Reducers.NameOf` `Reducers.cs:17` | C#-only | `ConditionalWeakTable` stand-in for the registry | MetricPortTests.cs:721-737 |
| (none) | `EvalResultsBuilder.BuildScores` `EvalResultsBuilder.cs:36` | C#-only | legacy wrapper over `ComputeViews` | MetricPortTests; MetricDictTests |

### 7. Post-hoc scoring, score editing, live control scoring and human scoring

Python `score.py` refs are `_eval/score.py`. Note: `recompute_metrics` is rated partial in slice 1 and divergent here; this slice examined it in more depth, so treat it as divergent.

| Python symbol | C# symbol | Status | Divergences | Tests |
|---|---|---|---|---|
| `score` (sync wrapper) `score.py:81-152` | `ScoreLogs.ScoreAsync(EvalLog, ...)` `ScoreLogs.cs:37-127` | divergent | async-only entry point, so every `score_async` divergence applies | ScoreLogsTests.cs |
| `score_async` `score.py:213-411` | `ScoreLogs.ScoreAsync` `ScoreLogs.cs:37-127` | divergent | no `samples` streaming (ScoreLogs.cs:47,67; documented score-logs.md:61-62); **no model rebuilt from the header**, so model-graded scorers fail with PrerequisiteError (ScoreLogs.cs:64, UnavailableModelApi.cs:15-24; documented score-logs.md:51-53); header `model_roles` not merged (ScoreLogs.cs:65; documented); timelines not restored (ScoreLogs.cs:356; documented score-logs.md:69); `scorer_args` null (ScoreLogs.cs:379; documented); None scores cannot be skipped (ScoreLogs.cs:371-380); `Metrics = []` applied to every scorer instead of falling back (ScoreLogs.cs:72-73); empty samples or scorers rejected (ScoreLogs.cs:47-55); header metric groups and custom metrics throw on overwrite (LogHeader.cs:37,47,75; documented score-logs.md:54-58); header options/metadata `{}` (LogHeader.cs:108-116; documented); span type "span" accepted as the scorers span (ScoreMerging.cs:101-102; documented score-logs.md:59-60); per-key metrics survive a metrics override in the header (ScoreLogs.cs:73, LogHeader.cs:114); appended scorer-span `parent_id` rewritten (see below) | ScoreLogsTests.cs:104,127,169-242,494,536-554 (timeline restore and attachment resolution not mirrored) |
| `_get_updated_scores` `score.py:155-172` | `ScoreMerging.UpdatedScores` `ScoreMerging.cs:14-48` | faithful | — | ScoreLogsTests.cs:104 |
| `_get_updated_events` `score.py:175-210` | `ScoreMerging.UpdatedEvents`, `EventTree.Sequence` `ScoreMerging.cs:55-94`, `EventTree.cs:83-105` | divergent | append: C# rewrites each new scorer span's begin `parent_id` to the existing scorers span (EventTree.cs:90); Python writes the original begin events (`event/_tree.py:103-110`); overwrite: new span's parent rewritten when the old span was nested (ScoreMerging.cs:90) | ScoreLogsTests.cs:127 (:162 asserts the C#-only behaviour) |
| `_find_scorers_span` `log/_score.py:150-155` | `ScoreMerging.FindScorersSpan` `ScoreMerging.cs:96-102` | divergent | accepts type "scorers" or "span" (documented score-logs.md:59-60) | ScoreLogsTests.cs:624; LogToolsTests.cs:45,75 |
| `ScoreAction` `score.py:78` | `ScoreAction` `ScoreAction.cs:4-11` | faithful | — | ScoreLogsTests.cs:127,169-242 |
| `SampleTaskState` `score.py:414-420` | — | missing | built privately in ScoreLogs.cs:336-357; not listed in deferred.md | none |
| `task_state_from_sample` `score.py:423-488` | `ScoreLogs.ScoreSampleAsync` (private) `ScoreLogs.cs:334-357` | partial | not public; timelines not re-added (ScoreLogs.cs:356); roles bound from options only (ScoreLogs.cs:65) | ScoreLogsTests.cs:242 |
| `metrics_from_log_header` `score.py:559-584` | `LogHeader.MetricsFromLogHeader` `LogHeader.cs:21-49` | partial | dict metric groups throw (LogHeader.cs:37,47); fixed metric table only | ScoreLogsTests.cs:521,544 |
| `metric_from_log` `score.py:587-588` | `LogHeader.MetricFromLog` `LogHeader.cs:57-77` | partial | 10 built-ins only (accuracy, mean, stderr, std, var, bootstrap_stderr, ci_wilson, perplexity_per_token, perplexity_per_seq, frequency); anything else throws (LogHeader.cs:63-76,119-130); documented score-logs.md:54-58, which omits frequency | ScoreLogsTests.cs:544 |
| `reducers_from_log_header` `score.py:591-592` | `LogHeader.ReducersFromLogHeader`, `Reducers.Create` `LogHeader.cs:80-84`, `ExtraReducers.cs:97-110` | partial | custom reducers cannot be re-created | ScoreLogsTests.cs:345,362,380 |
| `resolve_scorers` `score.py:595-642` | `Catalog.CreateScorer`, `Catalog.ScorersFromLog` `Catalog.cs:23-24,64-68,102-106` | divergent | built-in factories only; no registry, `file.py@name` or task_file (documented cli.md:57-59); null `eval.scorers` returns `[]` instead of rebuilding from `results.scores`, so `inspect score` fails (Catalog.cs:67); **`exact` rebuilt as `ExactMatch`** (Catalog.cs:20,104) | Cli.Tests EvalEndToEndTests.cs:271-291 (asserts exact_match at :276-277) |
| `resolve_scorers_info` `score.py:645-700` | `HeaderScorers.FromLog` `Tools/EvalLogEdits.cs:309-317,359-399` | divergent | header options/metadata dropped, so a recompute empties `results.scores[].params`/`metadata` (Tools/EvalLogEdits.cs:312-316); custom metrics throw (LogHeader.cs:75) where Python loads the task file | LogToolsTests.cs:156; MetricsByKeyOverrideTests.cs |
| `score_command` flags `_cli/score.py:41-154` | Cli `ScoreCommand` `Cli/Commands/ScoreCommand.cs:19-59` | divergent | `--stream` refused unless 0/false (ScoreCommand.cs:64-68; documented cli.md:75); built-in `--scorer`/`--metric` only (ScoreCommand.cs:89-97; documented cli.md:57-59); **`-S` args bound but not recorded** (LogHeader.cs:113); no prompts (ScoreCommand.cs:98-101,115-137; documented cli.md:74); `--scorer exact` gives exact_match with accuracy (Catalog.cs:20,104) | Cli.Tests CommandParsingTests.cs:265-282, EvalEndToEndTests.cs:139,271-299 |
| `score` CLI pipeline `_cli/score.py:157-271` | `ScoreCommand.RunAsync` `Cli/Commands/ScoreCommand.cs:61-108` | divergent | written in the output path's format, not the input log's recorder: `in.eval --output-file out.json` gives JSON in C#, a zip in Python (ScoreCommand.cs:105, EvalLogFiles.cs:197-201); local files only (ScoreCommand.cs:71); header model not rebuilt without `--model` (ScoreLogs.cs:64; documented cli.md:76) | Cli.Tests EvalEndToEndTests.cs:271-299 |
| `_resolve_output_file` `_cli/score.py:317-354` | `ScoreCommand.ResolveOutputFile` `Cli/Commands/ScoreCommand.cs:110-137` | partial | no prompts, always `{stem}-scored.{ext}` (documented cli.md:74-75); local `File.Exists` only | Cli.Tests EvalEndToEndTests.cs:279-284 |
| `print_results` `_cli/score.py:274-314` | `ResultsPrinter.Print` `ResultsPrinter.cs:13-48` | partial | no sample coverage messages (early-stop note, error percentage) (ResultsPrinter.cs:33-35); display only | none |
| `resolve_metrics` (CLI) `_cli/score.py:357-363` | inline `Catalog.CreateMetric` `Cli/Commands/ScoreCommand.cs:97`, `Catalog.cs:31-32` | partial | built-in metrics only | Cli.Tests EvalEndToEndTests.cs:279 |
| `resolve_action` `_cli/score.py:366-378` | `ScoreLogs.ResolveAction` `ScoreLogs.cs:160-164` | partial | no prompt; defaults to append (documented score-logs.md:62, cli.md:74) | ScoreLogsTests.cs:464 |
| `ProvenanceData` `log/_edit.py:13-26` | `ProvenanceData` `Log/EvalLogEdits.cs:8-18` | faithful | — | LogToolsTests.cs:228-289 |
| `LogEdit` `_edit.py:29-30,57` | `LogEdit` `Log/EvalLogEdits.cs:21-25` | faithful | — | LogToolsTests.cs:228 |
| `TagsEdit` `_edit.py:33-42` | `TagsEdit` `Log/EvalLogEdits.cs:28-35` | faithful | — | LogToolsTests.cs:228 |
| `MetadataEdit` `_edit.py:45-54` | `MetadataEdit` `Log/EvalLogEdits.cs:38-45` | faithful | — | LogToolsTests.cs:228-239 |
| `LogUpdate` `_edit.py:60-67` | `LogUpdate` `Log/EvalLogEdits.cs:48-52` | faithful | — | LogToolsTests.cs:228-256 |
| `edit_eval_log` `_edit.py:70-149` | `EvalLogEditing.EditEvalLog` `Log/EvalLogEdits.cs:78-162` | divergent | no-op filter never equates bool with number (PlainJson.cs:49-52): 1→true is applied in C#, dropped as a no-op in Python (Log/EvalLogEdits.cs:131-133) | LogToolsTests.cs:228 |
| `invalidate_samples` `_edit.py:152-226` | `InvalidateSamples`/`InvalidateAllSamples` `Tools/EvalLogEdits.cs:155-171,254-293` | divergent | duplicate uuids: first index wins in C#, last in Python; "all" also touches uuid-less samples (Tools/EvalLogEdits.cs:262,275) | LogToolsTests.cs:260 |
| `uninvalidate_samples` `_edit.py:229-252` | `UninvalidateSamples`/`UninvalidateAllSamples` `Tools/EvalLogEdits.cs:178-192,295-296` | divergent | same uuid edge (Tools/EvalLogEdits.cs:275) | LogToolsTests.cs:260 |
| `edit_score` `log/_score.py:13-147` | `EvalLogEdits.EditScore` `Tools/EvalLogEdits.cs:47-147` | divergent | recompute: custom/task-file metrics throw (LogHeader.cs:75); header task metrics applied to every scorer (EvalResultsBuilder.cs:119-122; documented score-logs.md:65-66); params/metadata emptied (Tools/EvalLogEdits.cs:312-316); orphan score names get accuracy+stderr (ScoreLogs.cs:210); task metric groups throw (LogHeader.cs:37,47); "span"-typed scorers span accepted (ScoreMerging.cs:101-102) | LogToolsTests.cs:45-181 (custom-reducer recompute, results-metadata preservation, task-file metric loading not mirrored) |
| `recompute_metrics` `log/_metric.py:9-61` | `ScoreLogs.RecomputeMetrics` `ScoreLogs.cs:234-252` | divergent | public API needs caller-supplied scorers (`HeaderScorers` is internal, Tools/EvalLogEdits.cs:305); task metrics override every scorer (EvalResultsBuilder.cs:119-122; documented); orphan fallback accuracy+stderr (ScoreLogs.cs:210); `EvalScore.params`/`metadata` unset (EvalResultsBuilder.cs:264-270); groups and non-built-in metrics throw (LogHeader.cs:37,47,75) | ScoreLogsTests.cs:283,325; LogToolsTests.cs:156 |
| `ScoreEdit` `scorer/_metric.py:82-111` | `ScoreEdit`, `Edited<T>`, `ScoreEditConverter` `TranscriptEventTypes.cs:202-227`, `EvalLogConverters.cs:400-450` | faithful | — | LogSchemaTests.cs:157,210,425-429; LogToolsTests.cs:85,94 |
| `UNCHANGED` `_metric.py:78` | `ScoreConstants.Unchanged`, `Edited<T>.Unchanged` `ScorerDelegates.cs:17`, `TranscriptEventTypes.cs:205` | faithful | — | LogSchemaTests.cs:425-429 |
| `ScoreEditEvent` `event/_score_edit.py:9-21` | `ScoreEditEvent` `TranscriptEventTypes.cs:230-233` | faithful | — | LogSchemaTests.cs:157,411,636; LogToolsTests.cs:45-82 (non-finite list value untested) |
| `score_to_float` `analysis/_prepare/score_to_float.py:7-46` | `Prepare.ScoreToFloat` `Prepare.cs:29-67,216-225` | faithful | — | AnalysisTests.cs:722-743 |
| Human `ScoreCommand` (`task score`) `agent/_human/commands/score.py:17-75` | Human `ScoreCommand` `Human/Commands/ScoreCommand.cs:13-67` | divergent | no ANSI bold around "Answer:"/"Score:" (ScoreCommand.cs:62); non-string values printed as compact JSON with 1.0→"1" instead of indent=2 JSON and "1.0" (HumanAgentText.cs:34-35); intermediate ScoreEvents lack scorer/usage and are appended after all scorers run (SampleRunner.cs:367-381) | HumanAgentTests.cs:393 |
| `TaskScoring` `_control/scoring.py:123-161` | — | deferred | control server deferred at deferred.md:38-53 (line 42 names interim scoring) | none |
| `ScorePass` `scoring.py:164-201` | — | deferred | deferred.md:38-53 | none |
| `start_score_pass` `scoring.py:270-311` | — | deferred | deferred.md:38-53 | none |
| `get_score_pass` `scoring.py:396` | — | deferred | deferred.md:38-53 | none |
| `start_sample_score_pass` `scoring.py:442-493` | — | deferred | deferred.md:38-53 | none |
| `get_sample_score_pass` `scoring.py:526` | — | deferred | deferred.md:38-53 | none |
| `cancel_score_pass` `scoring.py:220` | — | deferred | deferred.md:38-53 | none |
| `reset_score_passes` `scoring.py:208` | — | deferred | deferred.md:38-53 | none |
| `run_score_pass` `scoring.py:788-895` | — | deferred | deferred.md:38-53 | none |
| `inspect ctl sample score` `_cli/ctl/_sample.py:1421-1705` | — | deferred | deferred.md:38-44 | none |
| `inspect ctl sample cancel --action score` `_cli/ctl/_sample.py:549-605,949-1030` | — | deferred | deferred.md:38-44 | none |
| `inspect ctl task score` `_cli/ctl/_task.py:173-240` | — | deferred | deferred.md:38-44 | none |
| `inspect ctl task cancel --action score` `_cli/ctl/_task.py:124-130` | — | deferred | deferred.md:38-44 | none |
| (none) | `ScoreLogOptions.MaxSamples` `ScoreLogOptions.cs:27`, `ScoreLogs.cs:58-61,276-326` | C#-only | scoring concurrency bound | ScoreLogsTests.cs:409,452 |
| (none) | `ScoreLogs.ScoreAsync(string path, ...)` `ScoreLogs.cs:134-154` | C#-only | JSON only; refuses `.eval` (ScoreLogs.cs:143-146); not used by the CLI | ScoreLogsTests.cs:579,600,624 |
| (none) | `ScoreLogs.ComputeResults(...)` `ScoreLogs.cs:174-227` | C#-only | public `eval_results` over scored samples | ScoreLogsTests.cs:325,662 |
| (none) | `UnavailableModelApi` `UnavailableModelApi.cs:9-25` | C#-only | placeholder model; throws PrerequisiteError on generate | ScoreLogsTests.cs:476 |
| (none) | `EvalLogEdits.EditScore(scorers:)` `Tools/EvalLogEdits.cs:54,146` | C#-only | explicit scorers for the recompute | LogToolsTests.cs:173-174 |
| (none) | `HeaderScorers.ComputeResults` `Tools/EvalLogEdits.cs:324-351` | C#-only | used by log recovery | LogToolsTests.cs:343,358 |
| (none) | Human `ScoreCommand.Activity` `Human/Commands/ScoreCommand.cs:65-66` | C#-only | "Intermediate score. {text}" activity line | HumanAgentTests.cs:433 |

## Score-affecting divergences

Ranked by user impact: silently different numbers on common workloads first, then input-specific edge cases, then log-only differences.

### A. Silently different scores or metrics on common workloads

| # | Divergence | Effect | C# location |
|---|---|---|---|
| 1 | `model_graded_qa`/`model_graded_fact` never resolve the `grader` role | the evaluated model grades itself even when `grader` is bound; grader ModelEvents carry no role; no grader panel | ModelGraded.cs:214; Scorers.cs:52-58 |
| 2 | math: last-number fallback runs behind every symbolic answer | `\boxed{42x}` or `\boxed{2x+42}` vs `42` scores CORRECT | MathScorer.cs:229-238, :789-793 |
| 3 | math: equations reduced to a number at parse time | `x=2` vs `y=2` and `4 = x^2` vs `4` score CORRECT | MathScorer.cs:1174-1180 |
| 4 | math: 1e-10 tolerance and double arithmetic on irrationals | `\sqrt{10^{12}+1}` vs `10^6` and `\sqrt{10^{20}+1}-10^{10}` vs `0` score CORRECT | MathScorer.cs:946, :1000, :1030-1040 |
| 5 | math: missing parser coverage (false negatives) | bare `1, 2` vs `(1, 2)`, `atan(1)`, `inf` vs `\infty`, `7 % 3`, `\frac12`, `\ln`/`\log`/`\sin`, `10 cm` vs `10`, `30^\circ + 45^\circ` vs `75` all INCORRECT; `2^{20000}` and `\binom{1000000}{500000}` targets Unscored | MathScorer.cs:1159, :1209-1223, :1293-1300, :1372-1379, :1576-1600, :1631-1636, :901-906, :1013-1016, :1118-1121 |
| 6 | CLI alias `exact`→`ExactMatch` | `--scorer exact`, and rescoring any log header that lists `exact` (Python logs included), runs exact string match with accuracy under key `exact_match` instead of normalized-token `exact` with mean | Catalog.cs:20, :64-67, :104 |
| 7 | solver-written `state.scores` dropped | no sample score, no ScoreEvent, no results entry; `Task(metrics=[mean()])` with no scorer gives no results | SampleRunner.cs:215-221, :279 |
| 8 | metric and scorer options not written to the header | rescoring or recomputing from the header silently uses defaults: clustered `stderr` becomes unclustered, `ci`/`ci_wilson` lose level and cluster, `krippendorff_alpha` reverts to nominal, `bootstrap_stderr` to 1000 samples, `-S stop_words` and other scorer args vanish | MetricDictResults.cs:293-299; Metrics.cs:23-33; LogHeader.cs:67-75, :113, :115 |
| 9 | a None entry in a dict-valued score is excluded as unscored | Python counts it as scored with value 0.0, so C# reports a higher mean and different `scored_samples`/`unscored_samples` | MetricDictResults.cs:88-92 |
| 10 | `Task(metrics=)` applied to every scorer at results time | a scorer appended later (`task with { Scorers = ... }`) loses its own metrics; on recompute and `edit_score`, header task metrics override every scorer (documented) | EvalResultsBuilder.cs:119-122 |
| 11 | `Epochs(n, [])` recorded as null | a later recompute reduces with mean instead of not reducing; eval-set reuse matches logs recorded with the default reducer | EvalResultsBuilder.cs:94-97; LogHeader.cs:80-84; EvalSetLogs.cs:248-257 |
| 12 | container metadata stringified with .NET `ToString()` | `grouped()` merges every list-valued group into one "System.Collections.Generic.List`1[System.Object]" group; list cluster ids collapse into one cluster, so clustered `stderr` returns 0 where Python raises | GroupedMetric.cs:55; PythonText.cs:24, :144 |

### B. Input-specific edge cases that change a score

| # | Divergence | Example | C# location |
|---|---|---|---|
| 13 | `ToLowerInvariant` instead of `casefold`/`str.lower()` | "Straße" vs "strasse", "ΟΔΟΣ" vs "οδος" differ in match, includes, pattern, answer, f1, exact, math prose | MatchScorers.cs:72-73, :245-246; Scorers.cs:20-21; ClassificationScorers.cs:113; MathScorer.cs:689 |
| 14 | .NET regex engine for user patterns | `(?P<name>...)` throws; `\w`/`\b` differ on "²" and combining marks; `\Z` matches before a trailing newline; empty `grade_pattern` scores ""; a non-participating group 1 scores "" instead of unscored | MatchScorers.cs:179; ChoiceScorers.cs:13; ModelGraded.cs:208, :224 |
| 15 | Unicode numbers | lone 万/億/兆 parse as 0; astral digits "𝟐" and full-width "１２３" not numbers; `value_to_float("1_000")` gives 0.0 | MatchScorers.cs:143-152, :374, :743-748; PythonText.cs:96-106; ValueToFloat.cs:78-79 |
| 16 | grader prompt text differs | tool errors shown as `type=... message=...`; tool args as JSON; metadata floats "1" and "1E-07"; typed collections render CLR type names; `{meta[key]}` and format specs throw | ModelGraded.cs:147, :275, :282-283, :313-316, :349-353 |
| 17 | f1 and `max_f1_score` rounding | double nearest 0.175 gives 0.18 in C#, 0.17 in Python (needs ≥80 unique tokens) | ClassificationScorers.cs:59 |
| 18 | math delimiter and parse edges | `$$3$$$ then 4` vs `3`; target `$$$`; target `=5`; target `3+`; `(1,2)\%` errors the sample; `2^2^3` vs `256` and `101!` vs `101\cdot 100!` CORRECT only in C#; 70-term sum not limited | MathScorer.cs:391-395, :418-422, :710-722, :789-795, :757, :1003-1028, :1096-1108 |
| 19 | precomputed scores | string "nan" becomes unscored instead of 0.0; epoch `1.0` accepted and `true` rejected; list metadata silently dropped | ScoreValue.cs:90-98; PrecomputedScores.cs:105-106, :114, :118 |
| 20 | `frequency`/`categorical` keys | 1 and 1.0 merge; "1E-05" instead of "1e-05" | CategoricalMetrics.cs:38; ScoreValue.cs:122-124 |
| 21 | typed sample ids | int 1 and "1" reduced as separate samples | Eval.cs:534; EvalResultsBuilder.cs:196 |
| 22 | metric returns a non-numeric string or null | logged as NaN where Python raises | EvalResultsBuilder.cs:297-303; MetricDictResults.cs:110-123 |
| 23 | orphan score names on recompute | always accuracy+stderr, never the named scorer's own metrics | ScoreLogs.cs:209-210 |
| 24 | `Reducers.Create("mean_2")` | silently builds mean where Python raises | ExtraReducers.cs:102-119 |
| 25 | perplexity `num_tokens` | "12.5" truncates, NaN/inf cast, where Python raises | PerplexityMetrics.cs:67 |
| 26 | mean epoch reducer | last-bit difference on non-integer values | Reducers.cs:25 |

### C. Log fidelity and display only (no score change)

- Final and intermediate ScoreEvents lack `scorer`, `scorer_args`, `model_usage`, `role_usage`, so scorer model usage is not attributed in live logs (SampleRunner.cs:323, :378). Intermediate events are appended after all scorers (SampleRunner.cs:371-379).
- Extra StepEvents around scorers and a scorers span of type "span" (SampleRunner.cs:214, :318, :328); appended scorer spans get a rewritten `parent_id` (EventTree.cs:90).
- Integer scores written as `1.0`; nested containers Python cannot validate (PythonJsonFormat.cs:39,107-111; ScoreValue.cs:18,25).
- `inspect score` writes the output path's format, not the input's (EvalLogFiles.cs:197-201).
- Unqualified metric names ("mean") are accepted by Python's registry lookup, so they are harmless.
- `edit_eval_log` bool-vs-number no-op filter, invalidate uuid edges, human `task score` formatting, CLI coverage messages.

## Missing and partial items

Ranked by impact.

| # | Item | Status / doc | C# location |
|---|---|---|---|
| 1 | Grader model role, grader panels with a reducer, model-name strings, callable `include_history`, `resource()` templates in `model_graded_*` | divergent; not in docs/ports (only swe-showcase.md:503-505, inspect-components.md:519, ARCHITECTURE.md:1255) | ModelGraded.cs:214, :218; Scorers.cs:52-75 |
| 2 | Model rebuilt from the log header in post-hoc scoring; header model roles merged | divergent; documented score-logs.md:51-53, cli.md:76 | ScoreLogs.cs:64-65 |
| 3 | `Task(headline_metric=)` | missing; undocumented | Eval.cs:129-170, :299-308 |
| 4 | `score_on_error` | not ported; documented runner-extras.md:44 | SampleRunner.cs:236-249 |
| 5 | Scorer, metric and reducer registries (`scorer_create`, `metric_create`, custom `@score_reducer`, `file.py@name`) | partial/deferred; indirect docs cli.md:57-59, score-logs.md:54-58,63-64 | Catalog.cs:23-32; LogHeader.cs:57-76; ExtraReducers.cs:112-124 |
| 6 | Scorer params and metadata in the header | partial; documented score-logs.md:57-58 | LogHeader.cs:113, :115 |
| 7 | SymPy equivalence and parse timeouts in `math` | deferred; documented metrics.md:41-49,75-76 | MathScorer.cs:20 |
| 8 | Streaming (`samples` callback, `--stream`) for `inspect score` | divergent; documented score-logs.md:61-62, cli.md:75 | Cli/Commands/ScoreCommand.cs:64-68 |
| 9 | `eval(score=False)` / `--no-score` | deferred; CLI flag documented cli.md:68-71 | EvalOptions.cs |
| 10 | Several metric dicts per scorer; `metrics=[{...}]` scorer-level score | partial; code comment only | ScorerDelegates.cs:34-35; EvalResultsBuilder.cs:124 |
| 11 | `Epochs(n, "at_least_2")` and string/list reducer unions | partial | Epochs.cs:11 |
| 12 | `perplexity`, `target_perplexity` scorers | deferred (no logprobs); deferred.md:100-101 | Provider ModelOutput.cs |
| 13 | `precomputed_scores` remote paths, dict metrics | divergent; documented metrics.md:57-59 | PrecomputedScores.cs:21, :153-158 |
| 14 | Live control-server scoring (`TaskScoring`, score passes, `inspect ctl ... score`) | 13 rows deferred; deferred.md:38-53 | — |
| 15 | Rarely used: `Score.as_list/as_dict`, `SampleScore.sample_metadata_as`, `Reference`, `SampleTaskState` and public `task_state_from_sample`, `bootstrap_std` alias, deprecated `list[Score]` metrics, scanners as scorers, CLI coverage messages | missing/partial; `SampleTaskState` and `bootstrap_std` undocumented | Score.cs; SampleScore.cs; ScoreLogs.cs:334-357; ResultsPrinter.cs:33-35 |

## C#-only extras

- `Scorers.ExactMatch` and the CLI alias `exact`→`ExactMatch` (Scorers.cs:40-42, Catalog.cs:20). This is the one extra that harms parity, because it shadows `Scorers.Exact`.
- `ScoreValue.FromJson/ToJson` (ScoreValue.cs:58-102): maps "nan"/"infinity" strings to numbers.
- Math evaluator types (`Rational`, `MathValue`, `MathExpressionParser`) and extra limits with C#-only explanations (MathScorer.cs:39-125, :144, :1013-1023, :1118-1121).
- `Scorers.Cascade` overloads (CascadeScorer.cs:11, :74).
- Public `Distributions` (Distributions.cs:8), seedable `random` on `BootstrapStderr`/`Ci` (StdMetrics.cs:24), `CategoryName<TEnum>` helpers (CategoricalEnumMetrics.cs:12).
- `Reducers.NameOf` (Reducers.cs:17), `EvalResultsBuilder.BuildScores` (EvalResultsBuilder.cs:36).
- `ScoreLogOptions.MaxSamples`, JSON-only `ScoreLogs.ScoreAsync(string path)`, public `ScoreLogs.ComputeResults`, `UnavailableModelApi`, `EditScore(scorers:)`, `HeaderScorers.ComputeResults`, human `ScoreCommand.Activity`.
- Convenience members: `Score.IsUnscored`, `ScoreValue.IsNaN`, `ScoreReason.All`, `Target.Empty`, `ValueToFloat.Default`, `MetricDict.ForAllKeys`.

## Test-coverage gaps worth closing

1. `model_graded` role resolution and grader panels (Python `tests/scorer/test_model_graded.py:155-349`, `:465-506`), file templates, warn-once.
2. Math false-positive pins: `\boxed{42x}` vs `42`, `x=2` vs `y=2`, `\sqrt{10^{12}+1}` vs `10^6`. None exist; ScorerPortTests.cs:454-466 pins only the documented false negatives.
3. Runner: solver-set scores (`test_solver_simple_score`, `test_solver_dict_score`, `test_solver_score_event_scorer_name`), the modified-`state.scores` guard, the scoring timeout, `test_added_scores`.
4. Header round trip of metric options (`stderr(cluster=)`, `ci`, `krippendorff_alpha(level=)`) and scorer `-S` params (`tests/_cli/test_score.py:130-131`); `precomputed_scores` rescorable tests.
5. `Epochs(n, [])` header (`test_no_reducer`, `test_score_no_reducer`); `test_recompute_duplicate_scorer_names`; custom-reducer recompute.
6. Non-ASCII inputs: casefold (ß, final sigma), full-width and astral digits, Unihan units, regex-dialect cases, f1 rounding midpoints, mean reducer with non-integer values, max reducer NaN order independence.
7. `test_headline_metric.py`, `test_multi_scorer_aggregation.py`, end-to-end `cascade`, `ci`/`ci_wilson` end-to-end.
8. Post-hoc: timeline restore, attachment resolution during scoring, `--stream`, CLI coverage messages, task-file metric loading, non-finite `ScoreEditEvent` values.
9. Tests that lock in non-Python behaviour and will need changing with the fixes: MetricDictTests.cs:151 (None entry), Cli.Tests TaskRegistryTests.cs:135 and EvalEndToEndTests.cs:271-277 (`exact` alias), EvalRunnerTests.cs:93-95 (StepEvents), ScoreLogsTests.cs:162 (span `parent_id`), ModelGradedTests.cs:206 (tool-error format).

## Stale docs and comments

**docs/ports/metrics.md**
- :18 lists `Reducers.Create` without the `_k` caveat; :35 says the tests replicate `test_reducers.py` (only partly).
- :36 math cases are inline copies, and three of the five omitted cases are not symbolic; :41-42 post-parse limits and the `expensive` flag were not "ported verbatim"; :43 unit and degree handling is only partial; :45-47 describes false negatives only, not the false positives.
- :50-51 says `multi_scorer` treats unscored NaN as None; only `cascade` does (same wrong claim in docs/inspect-components.md:519).
- :58-59 says metrics are a flat list everywhere; `MetricDict` and `ScorerDef.MetricsByKey` exist.
- :63 says `MetricDef` has no registry params; `Options` exists. No doc mentions the metric-options header gap.
- :77-78 lists `answer()`/`choice()` as not ported; both are in ChoiceScorers.cs.
- :79 lists `ScoreEdit` and `metric_create` replay as not ported; `ScoreEditConverter` and `LogHeader.MetricFromLog` exist.
- :81 lists StrEnum categories as not ported; `Frequency<TEnum>` exists.

**docs/ports/score-logs.md**
- :11 maps the CLI `score()` to the path overload; the CLI port is Cli/Commands/ScoreCommand.cs.
- :18-19 says runner logs carry `results.headline` and `epochs_reducer` "like Python's" (no task headline; `[]` becomes null).
- :54-58 metric replay list omits `frequency`.
- :61-62 "JSON only" and "no -scored naming" apply only to the library path overload.

**Other docs**
- docs/ports/cli.md:58-59 documents the `exact`→`exact_match` alias as intended; it now shadows `Scorers.Exact`.
- docs/ports/runner-extras.md:44 lists `TerminateSampleError` as not ported; SampleRunner.cs:175-180 handles it.
- docs/ports/deferred.md:65-72 still defers the human agent, which is ported.
- docs/ports/log-tools.md:24-25 misstates `ScorerInfo.from_name` (it tries the registered scorer's metrics first).
- docs/swe-showcase.md:505-506 says `exact()` is not ported.
- Not in docs/ports at all: the model_graded single-model/no-role limitation, the casefold/Unicode/regex-dialect notes for `_match`/`_pattern`/`_common`/`_unicode` (only swe-showcase.md:508-515), `@score_reducer` deferral, `SampleTaskState`, programmatic `eval(score=False)`, `Task(headline_metric=)`.

**Code comments**
- MetricDictResults.cs:47-48 (null entry is unscored), EvalLog.cs:268 ("Name and Scorer are both the scorer name"), EvalResultsBuilder.cs:89-90 (null when no reducers hides `[]`), MathScorer.cs:1603 (claims `\frac12` is supported).

## Recommended fixes, ordered by impact

1. **Grader role.** In `ModelGraded.Create`, resolve `model ?? ModelRoles.GetModel("grader") ?? ActiveModel` and stamp `role="grader"`; add `modelRole`, a model list and a `reducer` (reuse `MultiScorer` with "majority") (ModelGraded.cs:214, Scorers.cs:52-75). Small change, removes self-grading.
2. **`exact` alias.** Drop `exact`→`ExactMatch` so `exact` reaches `Scorers.Exact`; keep `exact_match` under its own name; update Cli.Tests and cli.md:58-59 (Catalog.cs:20, :104).
3. **Math false positives.** Skip the last-number fallback when the primary answer parsed as a symbolic expression (MathScorer.cs:229-238, :789-793); keep equations whose sides are symbolic as text and require the same variable (MathScorer.cs:1174-1180); do not apply numeric tolerance when both sides are exact algebraic values (MathScorer.cs:946, :1030-1040). Add a pinning test for each and correct metrics.md:36-47.
4. **Math coverage.** Bare comma tuples, the missing plain functions and constants, `\frac12`/`\sqrt23` normalization, unit words with superscripts, degrees anywhere, and the `expensive` flag for power towers and factorials (MathScorer.cs:1159, :1209-1223, :1293-1300, :1631-1636, :901-906, :1003-1028, :1096-1108).
5. **Solver-set scores.** Snapshot solver score names before scorers run, emit ScoreEvent and SampleScore entries for them, and add the "has modified state.scores" guard (SampleRunner.cs:215-221, :279).
6. **Header options.** Set `MetricDef.Options` in the `Stderr`, `BootstrapStderr`, `Ci`, `CiWilson` and `KrippendorffAlpha` factories; add `ci` and `krippendorff_alpha` to `LogHeader.MetricFromLog`; carry scorer params and metadata through `ScorerDef` into `eval.scorers[]` and `EvalScore.params` (Metrics.cs:23-33, StdMetrics.cs, KrippendorffMetric.cs, LogHeader.cs:65-75, :113, :115).
7. **Dict-score None entries.** Count them as scored and convert with `value_to_float` (MetricDictResults.cs:88-92); flip MetricDictTests.cs:151.
8. **Task metrics timing.** Apply `EvalTask.Metrics` when the task is built, not in `EvalResultsBuilder` (EvalResultsBuilder.cs:120-122); on recompute use task metrics only for fallback names.
9. **`Epochs(n, [])`.** Write `[]` (EvalResultsBuilder.cs:94-97) and compare canonical reducer lists in eval-set reuse (EvalSetLogs.cs:248-257).
10. **Python `str()` for containers.** Render lists/dicts as Python repr in `PythonText.Str` and raise on unhashable cluster ids (PythonText.cs:24, :144).
11. **Headline declaration.** Add `EvalTask.HeadlineMetric` with the `"<scorer>.<score>"` shorthand and pass it to `EvalSpec` and `ComputeResults` (Eval.cs:129-170, :299-308).
12. **Unicode helpers.** A shared `PythonText.CaseFold` (full case folding, including ß and final sigma) and Python `str.lower()` for `match_target`; Unicode decimal-digit parsing in `ValueToFloat.TryParseFiniteNumber` and `PythonText.TryParseFiniteFloat`; Unihan values for 万/億/兆 (MatchScorers.cs:72-73, :245-246, :374; Scorers.cs:20-21; ClassificationScorers.cs:113; ValueToFloat.cs:78-79; PythonText.cs:96-106).
13. **Numeric exactness.** Round f1 on the exact binary value as CPython's `round` does, not on x·100 (ClassificationScorers.cs:59); compute the mean reducer with exact rationals, reusing the math scorer's `Rational` (Reducers.cs:25).
14. **model_graded parse edges.** Treat an empty `grade_pattern` as the default, return unscored when group 1 did not participate, error when the grader returns no choices, render tool errors as the message only (ModelGraded.cs:147, :208, :224, :241).
15. **Log fidelity.** Fill `scorer`, `model_usage` and `role_usage` on live ScoreEvents and emit each intermediate event right after its scorer; drop the extra StepEvents and use span type "scorers" (SampleRunner.cs:214, :318, :323, :328, :371-379).
16. **Strictness.** Reject `_k` suffixes on reducers without `k` (ExtraReducers.cs:102-119) and raise on non-numeric metric results (EvalResultsBuilder.cs:297-303).
17. **Docs.** Apply the stale-doc corrections above, and add a docs/ports page for the text-matching scorers covering the remaining casefold, regex and Unicode deviations.
