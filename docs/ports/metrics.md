# Port: metrics, extra scorers and reducers

Port of the remaining `inspect_ai.scorer` metrics, reducers and scorers into
`src/InspectAzureAI.Eval/Scorers` (metrics under `Scorers/Metrics/`). Everything is additive to the
existing `Metrics`, `Reducers` and `Scorers` classes, which became `partial`.

## What was ported (Python source → C#)

| Python | C# |
|---|---|
| `scorer/_metrics/std.py`: `var`, `stderr(cluster=)`, `bootstrap_stderr`, `ci`, `ci_wilson`, `_cluster_partition`, `_clustered_stderr`, `_t_inv_cdf`, `_reg_inc_beta` | `Metrics.Var`, `Metrics.Stderr(toFloat, cluster)`, `Metrics.BootstrapStderr`, `Metrics.Ci`, `Metrics.CiWilson` (`Metrics/StdMetrics.cs`); `Distributions.TInvCdf`, `RegularizedIncompleteBeta`, `LogGamma`, `NormalInvCdf` (AS241, ported from CPython `statistics`) |
| `_metrics/grouped.py` | `Metrics.Grouped(metric, groupKey, GroupedAll all, allLabel, valueToFloat, nameTemplate)` |
| `_metrics/categorical.py` | `Metrics.Frequency(categories, normalize)` (declared `MetricScores.Unreduced`), `Metrics.Categorical` |
| `_metrics/aggregate.py` | `Metrics.Aggregate(key, agg, toFloat, onMissing)` |
| `_metrics/krippendorff.py` | `Metrics.KrippendorffAlpha(level, toFloat)` (nominal, ordinal, interval) |
| `_metrics/perplexity.py` | `Metrics.PerplexityPerToken`, `Metrics.PerplexityPerSeq` |
| `_metric.py`: `UNCHANGED`, `ScoreReason`, `MetricScores` | `ScoreConstants.Unchanged`, `ScoreReason` (constants + `All`), `MetricScores` enum and `MetricDef.Scores` |
| `_reducer/reducer.py`: `majority_score`, `pass_k`, `collect_score`; `registry.py`: `create_reducers`, `validate_reducer` | `Reducers.Majority`, `Reducers.PassK`, `Reducers.Collect`, `Reducers.Create(name)`, `Reducers.Validate(epochs, reducer)` (`ExtraReducers.cs`) |
| `_classification.py`: `f1`, `exact` and helpers | `Scorers.F1(answerFn, stopWords)`, `Scorers.Exact()`, internal `Classification.MaxF1Score/MaxExactScore/ComputeF1/Normalize` |
| `_cascade.py` | `Scorers.Cascade(threshold, params (name, Scorer)[])`, `Scorers.Cascade(IReadOnlyList<ScorerDef>, threshold)` |
| `_multi.py` | `Scorers.MultiScorer(scorers, ScoreReducer)` / `(scorers, string reducerName)` |
| `_precomputed.py` | `Scorers.PrecomputedScores(path, onMissing, metrics)` |
| `_math.py` | `Scorers.Math()` over the numeric-equivalence subset; internal `MathAnswer` (extraction, validation, comparison), `MathExpressionParser`, `MathValue`, `Rational` |
| `_eval/task/results.py`: `compute_eval_scores_for_views` | `EvalResultsBuilder.BuildScores` now gives `Unreduced` metrics their own reducer-less `EvalScore` over every epoch, names the implicit mean view `mean` when views are mixed, and rejects `Reduced` metrics when reduction is disabled over repeated sample ids |

Python `str()`/`repr(float)` and `Counter` equality semantics used by the above live in `PythonText`
(group names, cluster ids, `1 == 1.0 == True` keys).

## Tests

`tests/InspectAzureAI.Eval.Tests/MetricPortTests.cs` and `ScorerPortTests.cs`. Reference values for var,
std, stderr, clustered stderr, ci, ci_wilson, t/normal quantiles, krippendorff, grouped, frequency,
aggregate, perplexity, pass_at/pass_k and f1 were computed with the venv Python on the same inputs and
are asserted within 1e-9 (1e-12 for the normal quantile). Bootstrap metrics take a `Random` and are
asserted within tolerance plus for degenerate inputs. Reducer tests replicate `tests/scorer/test_reducers.py`;
the math cases replicate `tests/scorer/math_cases.json` minus the symbolic ones, and the extra numeric rules
were cross-checked against the Python scorer (sympy is installed in the venv).

## Deviations from Python (and why)

- **Math scorer has no SymPy.** Answer/target extraction, the complexity limits and the status metadata
  are ported verbatim; parsing evaluates the numeric subset exactly (integers, decimals, `\frac`, `a/b`,
  `^`, `\sqrt`, `n!`, `\binom`, `%`, `\pi`, `e`, `\times`/`\cdot`, mixed fractions, `\text{}` units,
  degree marks, thousands separators, tuples, sets, `x = value` assignments) with big-integer rationals
  and the same 1e-10 tolerance for inexact values. Anything symbolic compares as normalized text, so
  `x^2 - x^2` vs `0`, `x^2+1` vs `1+x^2` or `x<2` vs `2>x` score INCORRECT where Python proves them
  CORRECT (`math_scorer_known_deviation_symbolic_answers_compare_as_text`). There is no parse timeout or
  worker thread; parsing is bounded by the static limits instead, so the `target_timeout` /
  `answer_timeout` statuses never occur.
- **Scorers cannot decline.** The C# `Scorer` delegate returns a non-null `Score`, so `cascade` and
  `multi_scorer` treat an unscored (NaN) score as Python's `None`; `precomputed_scores` returns
  `Score.Unscored(explanation: ...)` for a sample without a record instead of omitting the score.
- **`precomputed_scores` reads local paths and `file://` URIs only**; other schemes throw
  `NotSupportedException` (no fsspec). Metric dictionaries keyed by subscore are not supported (the
  `metrics` parameter is a flat list, as everywhere in the .NET port).
- **Randomness** (`bootstrap_stderr`, `ci(method: "bootstrap")`) uses `System.Random` (optionally
  injected) rather than numpy's global state, so resampled values differ from Python run to run.
- **Dict- and list-valued metric entries carry `group`** (the metric's name), as Python's `scorer_for_metrics`
  writes; `params` stays `{}` because `MetricDef` has no registry params.
- **`frequency` keys use `ScoreValue.Text`**, which renders `1.0` as `1` (there is no int/float
  distinction in `ScoreValue`); Python would report `1` and `1.0` as separate categories.
- **`f1`** case-folds with `ToLowerInvariant` (Python `casefold` also folds ß→ss) and, like the rest of
  the port, a score of `1.0` renders as `1` (Python `"1.0"`).
- Validation errors surface as `ArgumentException` (Python `ValueError`/`TypeError`); reducer/epoch
  mismatches use `PrerequisiteError` as in Python.
- `krippendorff_alpha` warnings, perplexity metadata warnings and `value_to_float` warnings go through
  `ProviderLogger.Warning`.

## Not ported

- `math()` symbolic equivalence (SymPy / latex2sympy2), matrices, complex numbers, intervals (they
  compare as text only) and the parse-time budget.
- `perplexity()` / `target_perplexity()` scorers (they need logprobs from the model API; only the two
  aggregating metrics are here) and `answer()`/`choice()` scorers (outside this area).
- `metric_create` / registry replay by name for metrics, `ScoreEdit`, `Reference`, and
  `sample_metadata_as` (pydantic-specific).
- `StrEnum` categories for `frequency` (pass the label sequence instead).
