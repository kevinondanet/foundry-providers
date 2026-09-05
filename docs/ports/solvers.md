# Port: multiple choice, chain of thought, self critique, fork, assistant message, choice and answer scorers

Branch `port/solvers`. Ports the solver/scorer surface around multiple choice questions and the small
prompt/critique/fork solvers of inspect_ai into `InspectAzureAI.Eval`.

## What was ported, from where

| Python | C# |
|---|---|
| `solver/_task_state.py` `Choice`, `Choices` (mark, shuffle, prompt) | `Solvers/Choices.cs`: `Choice` record, `Choices` class (`IReadOnlyList<Choice>`) |
| `_util/answer.py` `answer_character`, `answer_index` | `AnswerLabels.Character` / `Index` (internal, `Solvers/Choices.cs`) |
| `solver/_task_state.py` `TaskState.choices` | `TaskState.Choices` is now `Choices` (was `IReadOnlyList<string>`); constructor still takes the sample's strings; new `TaskState.Copy()` (`deepcopy`) |
| `solver/_multiple_choice.py` templates, `prompt`, `parse_answers`, `pretend_we_didnt_shuffle`, `multiple_choice` | `MultipleChoiceTemplate` constants (verbatim), `Solvers.MultipleChoice(template, cot, multipleCorrect, maxTokens, shuffle)` in `Solvers/MultipleChoice.cs` |
| `solver/_prompt.py` `chain_of_thought`, `DEFAULT_COT_TEMPLATE`, `assistant_message` | `Solvers.ChainOfThought(template)`, `Solvers.DefaultCotTemplate`, `Solvers.AssistantMessage(template, parameters)` in `Solvers/ChainOfThought.cs` |
| `solver/_critique.py` | `Solvers.SelfCritique(critiqueTemplate, completionTemplate, model)`, `Solvers.DefaultCritiqueTemplate`, `Solvers.DefaultCritiqueCompletionTemplate` in `Solvers/SelfCritique.cs` |
| `solver/_fork.py` | `Solvers.Fork(state, solver, generate, ct)` and `Solvers.Fork(state, solvers, generate, ct)` in `Solvers/Fork.cs` |
| `scorer/_choice.py` | `Scorers.Choice()` in `Scorers/ChoiceScorers.cs` |
| `scorer/_answer.py`, `_util/pattern.py` | `Scorers.Answer("letter" \| "word" \| "line")`, `AnswerPattern.Letter/Word/Line` |
| `dataset/_dataset.py` `MemoryDataset.shuffle_choices`, `_remap_target` | `IDataset.ShuffleChoices(seed)` / `MemoryDataset.ShuffleChoices(seed)` |
| Python `str.format(**kwargs)` used by the above | `PythonFormat.Format` (internal, `Solvers/PythonFormat.cs`) — strict: unknown name → `KeyNotFoundException`, lone brace → `FormatException` |

Answer parsing (strict whole-line `ANSWER:` match then the lenient fallback, last match wins, `AB` / `A,B` /
`A B` / `A and B` / Oxford and trailing commas, case-insensitive, trailing full stop), unshuffling, the
"pretend we didn't shuffle" rewrite of the prompt and the last message, the choice scorer's target parsing
(letters per character, multi-digit labels kept whole, out-of-range target is an error, no choices scores
incorrect) and the shuffled explanation text are line-for-line ports.

Tests: `tests/InspectAzureAI.Eval.Tests/MultipleChoiceTests.cs` replicates `tests/solver/test_multiple_choice.py`,
`tests/scorer/test_choice.py` and `tests/scorer/test_answer.py` with `ScriptedModelApi` + the real
`GenerateLoop`, plus fork / self-critique / chain-of-thought / assistant-message / `ShuffleChoices` cases,
including error paths and cancellation. The shuffle cases pin Python's exact `Random(4)` orders by replaying
the `randbelow` draws Python makes (`[0,1]` → `[2,1,0]`, `[1,1,0]` → `[2,0,3,1]`, verified with the venv).

## Deviations from Python and why

- **Shuffle randomness.** `Choices.Shuffle(Random?)` and `ShuffleChoices(int? seed)` use Python's
  `random.shuffle` algorithm (Fisher–Yates from the end, `j = randbelow(i + 1)`) over `System.Random`, so the
  same draws give the same order, but a *seed* does not reproduce Python's Mersenne Twister order. Same
  caveat as the existing `MemoryDataset.Shuffle`.
- **`multiple_choice(shuffle=...)`** takes a `Random?` (`null` = no shuffle) instead of `bool | Random`; pass
  `Random.Shared` for an unseeded shuffle. Python's one-time deprecation warning is not emitted.
- **Errors.** Python `ValueError`s become `ArgumentException` (bad template, bad answer label, target beyond the
  choices, unshufflable dataset target) or `InvalidOperationException` (solver run on a sample without choices,
  `self_critique` with no model and no active sample). `Answer("bogus")` throws instead of Python's silent `None`.
- **`fork` takes the `Generate` delegate explicitly** rather than reading it from a context variable, so it also
  works outside a running sample (then without a span). Branches get a copy of the state whose `Store` is also the
  ambient `SampleContext.Store`, and run under a `subtask` span named `chain` for a chain and `fork` otherwise
  (Python uses the solver's registry name, which delegates do not have). No `SubtaskEvent` or `StateEvent` is
  emitted — the port has no such event types yet.
- **`fork(state, solvers)` fails fast like `tg_collect`** (`AsyncUtil.TgCollect`, the port of `_util/_async.py`):
  the first branch to throw cancels every other branch through the token it receives, waits for them to settle,
  and its exception is rethrown unchanged (a branch's `OperationCanceledException` caused by that cancellation is
  not a failure). Cancelling the caller's token cancels every branch and wins over a branch failure. Python's
  functions take no arguments; the C# branches receive the group's token, so a branch that ignores it keeps running
  until its next cancellable await. `tg_collect(exception_group=True)` is not ported.
- **`TaskState.Copy()`** copies the message list, metadata, tools, choices, scores and store *dictionary*;
  values inside them are shared (Python's `deepcopy` copies them too). Messages and outputs are immutable
  records, so that only matters for mutable metadata/store values.
- **Templates are strings, not resources**: like the existing prompt solvers, no `resource()` file-path
  resolution; `null`/empty selects the default template (Python `or` semantics).
- **`assistant_message`** uses the lenient `format_template` port (unknown placeholders kept), exactly as
  Python does; `chain_of_thought`, `multiple_choice` and `self_critique` use strict `str.format` semantics as in
  Python. Positional fields, attribute/index access, `!r`/`!a` conversions and format specs raise
  `NotSupportedException` instead of being evaluated.
- **`AnswerPattern.Line`** ends in `\z` — the .NET spelling of Python's `\Z` (end of string only); `.NET`'s `\Z`
  would also match before a trailing newline.
- **Log shape.** `EvalSample.choices` is still the sample's (post-`ShuffleChoices`) strings, as in Python; the
  marked/shuffled `Choices` object is not logged (Python does not log it either).

## Not ported

- `Choices.__getitem__` slicing (use LINQ), the deprecated-kwargs plumbing, `warn_once`.
- `SubtaskEvent` / `StateEvent` transcript records for fork branches (see above).
- Python's Mersenne Twister, so seeded orders are stable only within .NET.
