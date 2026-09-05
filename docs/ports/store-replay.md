# Port: store and state change tracking, JSON changes, replay, subtasks

## What was ported

| Python source (`src/inspect_ai/`) | .NET (`src/InspectAzureAI.Eval/Context/` unless noted) |
|---|---|
| `_util/json.py` `JsonChange`, `json_changes`, `_get_tracked_containers`, `_get_active_container`, `_apply_fast_list_op` | `JsonChange` (`Op`, `Path`, `From`, `Value`, `Replaced`, `ToJson`/`FromJson`), `JsonChangeOp`, `JsonChanges.Diff` / `MakePatch` / `Apply` / `ToPatchOp` / `ToJson` / `FromJson` (`JsonChanges.cs`) |
| `jsonpatch` library (`make_patch` / `DiffBuilder`, `apply_patch`, the six operations and their errors) and `jsonpointer` | `JsonChanges.DiffBuilder` (private), `JsonPatch.Apply`, `JsonPointer`, `JsonPatchException` / `InvalidJsonPatchException` / `JsonPatchConflictException` / `JsonPatchTestFailedException` / `JsonPointerException`, `JsonValues` (Python `==` vs `json.dumps` equality, `JsonKind`) (`JsonPatch.cs`) |
| `util/_store.py` `store_jsonable`, `dict_jsonable`; `solver/_task_state.py` `state_jsonable` | `Jsonable.FromStore` / `FromDictionary` / `FromState` / `FromValue` (`Jsonable.cs`) |
| `util/_store.py` `store_changes`; `log/_transcript.py` `track_store_changes` | `StoreChanges.Between`, `StoreChanges.Track` (`StoreChanges.cs`); `Transcript.Span` opens a `Track` scope so every span records a `StoreEvent` (`Transcript.cs`) |
| `util/_store.py` `store_from_events`, `store_from_events_as`, `_apply_store_event`, `_json_change_to_patch_op` | `StoreReplay.StoreFromEvents`, `StoreFromEventsAs<TModel>`, `ApplyStoreEvent` (`StoreReplay.cs`) |
| `event/_tree.py` `event_tree`, `event_sequence`, `event_tree_walk` | `EventTree.Build` / `Sequence` / `Walk`, `EventTreeSpan`, `EventTreeItem` (`EventTree.cs`) |
| `util/_store_model.py` `StoreModel`, `store_as`; `TaskState.store_as` | `StoreModel` (`Create<T>`, `Get<T>`/`Set<T>`, `NamespacedName`, `FieldName`, `ToDictionary`, `ToJson`), `StoreModelException`, `Store.As<T>`, `Store.StoreAs<T>` (ambient), `TaskState.StoreAs<T>` (`StoreModel.cs`, additive members in `Store.cs`, `Solvers/TaskState.cs`) |
| `event/_store.py`, `event/_state.py` | existing `StoreEvent` / `StateEvent` gain `FromChanges(IEnumerable<JsonChange>)` and `GetChanges()` (`TranscriptEventTypes.cs`) |
| `solver/_transcript.py` `SolverTranscript`, `solver_transcript` | `SolverTranscript` (`Complete`, `RunAsync`), `Solvers.LogName` / `IsChain` (`SolverTranscript.cs`, `Solvers/SolverNames.cs`) |
| `solver/_chain.py` `Chain.__call__`, `solver/_plan.py` step loop, `solver/_fork.py` `solver_subtask` | `ChainSolver` runs each step through `SolverTranscript.RunAsync`; `SampleRunner.RunSolverAsync` records a `StateEvent` for a plain top-level solver; `Solvers.Fork` runs branches as `Subtask` of type `fork` with a nested `solver` span |
| `util/_subtask.py` `subtask`; `event/_subtask.py`; `log/_transcript.py` `_event_updated`; `util/_store.py` `init_subtask_store` | `Subtask.RunAsync` (`Subtask.cs`), `Transcript.Record` / `Transcript.Update`, `SampleContext.WithStore` |

Cross-checks: `StoreReplayTests.json_changes_match_python_for_every_reference_document` runs the venv's `json_changes`
over 24 before/after documents (the cases of `tests/util/test_json.py` plus moves, root replacement, escaped keys, int/float/bool
distinctions, unicode) and asserts the port produces the same operations, `replaced` values and list-append convention
(`/x/2`, never `-`). The remaining tests port `tests/util/test_store_from_events.py`, `tests/solver/test_store_model.py`,
`test_subtask.py`, `test_transcript.py` and `test_state_jsonable.py`.

## Behaviour notes

- Every `Transcript.Span` snapshots the ambient `SampleContext.Store` on entry and, on dispose, records a `StoreEvent`
  (stamped with the span id, before the `SpanEndEvent`) when the store changed — exactly where Python's `span()` wraps its
  body in `track_store_changes()`. A `Transcript` used without a `SampleContext` records no store events.
- `StateEvent`s are recorded at Python's points: after each chain step (inside its `solver` span), after a plain top-level
  solver, and inside a fork branch that is not a chain. The snapshot (`Jsonable.FromState`) has Python's key order
  (`messages`, `tools`, `tool_choice`, `store`, `output`, `completed`, `metadata`) with `tool_choice: null` when unset.
- `Subtask.RunAsync` swaps the ambient store (`SampleContext.WithStore`), opens a `subtask` span, records a pending
  `SubtaskEvent` and updates it in place (`Transcript.Update`, keyed by uuid) with `result`, `completed` and
  `working_time` (`(completed - timestamp) - waiting time`). On failure or cancellation the span ends and the context is
  restored while the event stays pending, as in Python.
- `StoreModel` keys are `{TypeName}:{instance}:{field}` with the field in snake_case (`MyModel:my_field`), so a .NET model
  reads the store of a Python log and vice versa. Binding writes defaults for absent keys (Python's `model_post_init`);
  a key deleted later yields the model's last known value without being re-added (Python's `__dict__`). Values with
  another runtime shape (replayed `List<object?>`, `long`) are coerced through a JSON round trip and written back.
- `StoreReplay.StoreFromEvents` applies only the `StoreEvent`s at the root or directly inside a root-level span, as Python does.

## Deviations from Python (and why)

- Python's `span()` skips the `StoreEvent` when the span body raises (the generator never resumes past `yield`); a C#
  `using` cannot tell, so the port records the changes in that case too. Replay is unaffected (root spans still cover them).
- Python's `jsonpatch` visits dictionary keys in `set` order (hash-seed dependent), so the relative order of operations
  across dictionary keys varies between Python processes; the port visits keys in document order. Operations within one
  list keep list order on both sides, and the cross-check compares operation sets.
- `_get_tracked_containers` keeps only the first structural container matching a `replace` (in set order); the port keeps
  every match, which is deterministic and cannot resolve a shifted index wrongly.
- `StoreModel._coerce_value` returns the raw value when pydantic coercion fails; the port raises `StoreModelException`
  (silent coercion is disallowed here). Python re-validates the whole model on every assignment; C# properties are typed.
- `Store.StoreAs<T>()` outside a `SampleContext` is an `InvalidOperationException`; Python falls back to a process-wide
  default `Store()`. Likewise `Subtask.RunAsync` without a context runs the function with no ambient store and no events.
- Fork subtasks and their `SubtaskEvent` are named `"fork"` (or `"chain"`) rather than the solver's registry name, and
  a chain step span is named after the delegate's method (`Solvers.LogName`) instead of `registry_log_name`.
- `SubtaskEvent.input` is passed explicitly (Python derives it from the call arguments).

## Not ported

- `SpanRotationScope`, span-id providers and the `_SpanCell` machinery of `util/_span.py`.
- `SampleInitEvent` emission by the runner (Python passes `state_jsonable(state)`; `Jsonable.FromState` is ready for it).
- `Transcript` history/eviction, attachments and pending-event bookkeeping of `log/_transcript.py`.
