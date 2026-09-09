# inline cards demos

A C# port of the `examples/inline_cards` folder of the inspect_ai repository: four mockllm-driven react demo tasks for the interactive inline cards of Inspect's Textual/ACP TUI — the tool-call approval card, the elicitation (`ask_user`) card, tool-call cancellation (`^L`) and sample cancellation (`^N`). In Python each also runs with `--acp-server` so an attached `inspect acp` client renders the cards.

The TUI and its cards are not ported. Each card has a console analogue in this port:

| Python file | Task | Card | Console analogue |
| --- | --- | --- | --- |
| `approval.py` | `approval_demo` | `_ApprovalCard` (a `dangerous_action` call vetted by `approval="human"`) | the human approver's console prompt: Approve (a), Reject (r), Terminate (t) |
| `question.py` | `question_demo` | `_ElicitationCard` (`ask_user` twice: one free-text field, then a two-field form) | the `ask_user` console prompt, one field at a time (`:decline` declines) |
| `cancel_tool.py` | `cancel_tool_demo` | `^L` → `inspect/cancel_tool_call` on a 60s tool | Enter at the console while the tool sleeps: the model gets the `timeout` tool error and submits |
| `cancel_sample.py` | `cancel_sample_demo` | `^N` → `_CancelCard` (Score / Error / Back) on a 600s tool | Enter at the console, then `Score (s), Error (e), or Back (b)` |

Every task carries its own scripted model (Python's `Task(model=get_model("mockllm/model", custom_outputs=[...]))`), so no deployment is needed: what is "live" is the operator at the terminal. Under `--fake` the operator is scripted too, and the demos run unattended.

## Running it

### Offline (the examples runner, everything scripted)

The example is `inline_cards` in the examples project (see [examples/README.md](../README.md) for the runner and its flags). The default task is `approval_demo`:

```bash
dotnet run --project examples -- inline_cards --fake                                   # approval_demo: the scripted human approves
dotnet run --project examples -- inline_cards --fake -T decision=reject               # ... rejects (the model sees an approval error, then submits)
dotnet run --project examples -- inline_cards --fake -T decision=terminate            # ... terminates the sample (scored with an "operator" limit)
dotnet run --project examples -- inline_cards --fake --task question_demo             # ask_user answered with sk-123, then staging / 2027-01
dotnet run --project examples -- inline_cards --fake --task question_demo -T decline=true
dotnet run --project examples -- inline_cards --fake --task cancel_tool_demo          # ^L fired after 1s (-T cancel_after=<seconds>)
dotnet run --project examples -- inline_cards --fake --task cancel_sample_demo        # ^N after 1s, Score picked (-T resolution=score|error|back, comma-separated for several cards)
dotnet run --project examples -- inline_cards --fake --task cancel_sample_demo -T resolution=back,score -T cancel_after=0.2
```

The exit code is 0 when the log reports success (Score and Terminate end the sample cleanly and still score it), 1 when it does not (`-T resolution=error` makes the sample error, as picking Error does in Python), 2 for a usage error, 3 on cancellation.

### At the console (you are the operator)

Without `--fake` (and with `--model`, or `AZUREAI_BASE_URL` set, since the runner otherwise implies `--fake`) the same tasks wait for you at the terminal; the scripted model still drives the agent, `--model` only changes the banner:

```bash
dotnet run --project examples -- inline_cards --model x --task approval_demo          # Approve (a), Reject (r), Terminate (t)
dotnet run --project examples -- inline_cards --model x --task question_demo          # type the answers (:decline to decline)
dotnet run --project examples -- inline_cards --model x --task cancel_tool_demo       # press Enter to cancel the 60s tool call
dotnet run --project examples -- inline_cards --model x --task cancel_sample_demo     # press Enter, then s / e / b
```

### The `inspectai` CLI

The tasks are `[Task]`-attributed, so the CLI discovers them in the built assembly and the operator uses the console, exactly as `inspect eval examples/inline_cards/<file>.py` does:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval inline_cards_approval_demo --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll
dotnet run --project src/InspectAzureAI.Cli -- eval question_demo --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll
dotnet run --project src/InspectAzureAI.Cli -- eval cancel_tool_demo --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll
dotnet run --project src/InspectAzureAI.Cli -- eval cancel_sample_demo --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll
```

(`examples/approval` registers `approval_demo` in the same assembly and the CLI resolves bare names per assembly, so this folder's approval task is discovered as `inline_cards_approval_demo`; its own name, in the log, is `approval_demo`.)

## The tasks

All four share the shape of the Python tasks: one sample with `target=["ok"]`, `react(tools=[...], submit=AgentSubmit(name="submit", description=...))`, `includes()`, `message_limit=10` and their own model. `approval.py`:

```csharp
public static EvalTask Build(SandboxSpec? sandbox = null, IApprovalPrompter? prompter = null) => new()
{
    Name = "approval_demo",
    Dataset = new MemoryDataset([new Sample("Use the dangerous_action tool to clean up /tmp/example, then submit 'ok'.") { Target = new Target(["ok"]) }]),
    Solver = Agents.AsSolver(Agents.React(
        tools: [DangerousAction()],
        submit: new AgentSubmit { Name = "submit", Description = "Submit the final answer once the action has run." })),
    Scorers = [Scorers.Includes()],
    Approval = prompter is null
        ? ApprovalOption.FromSpec("human")                                                        // approval="human"
        : ApprovalOption.FromPolicies(new ApprovalPolicy(Approvers.Human(prompter: prompter), "*")),   // --fake: the scripted human
    MessageLimit = 10,
    Model = Model(),                                                                              // Task(model=model)
    Sandbox = sandbox,
};
```

The tools are `ToolDef.FromMethod` over methods whose `[Description]` attributes carry the Python docstrings verbatim (`dangerous_action(action)`, `long_running_task(seconds)`, `slow_busy_work(seconds)`). The two sleeping tools take an `IOperator` (`Operator.cs`), the stand-in for the TUI keybindings:

- `long_running_task` races `Task.Delay(seconds)` against `IOperator.WaitForCancelToolCallAsync` (`^L`). When the operator wins it records the `user_cancel` interrupt event (with the running call's id) and raises a `TimeoutException`, which the engine's tool executor turns into `ToolCallError("timeout", "Command timed out before completing.")` — the error Python synthesises for a cancelled call — so the react loop continues and the mock model submits.
- `slow_busy_work` races its sleep against `IOperator.WaitForCancelSampleAsync` (`^N`, the cancel card). `Score` raises the engine's `TerminateSampleException`: the sample ends with an `operator` limit and is scored on what the agent produced so far (nothing includes "ok", so `includes` is `I`), as in Python. `Error` raises `OperatorCancelledException`, which becomes the sample's error. `Back` returns to sleeping until the operator opens the card again.

`ConsoleOperator` reads Enter at the console and then, for the cancel card, `Score (s), Error (e), or Back (b) [s/e/b] (b):` (an empty line is Back, an unknown answer re-prompts). `ScriptedOperator` (`--fake`) fires after `-T cancel_after` seconds and picks `-T resolution` entries in order; once they run out the card is never opened again.

## Files

- `InlineCardsExample.cs`: the `IExample` (four tasks, the `-T` arguments, the console or scripted operators).
- `ApprovalDemo.cs`, `QuestionDemo.cs`, `CancelToolDemo.cs`, `CancelSampleDemo.cs`: one file per Python module (tool, scripted model, `[Task]` method, `Build`).
- `MockLlm.cs`: the `mockllm/model` stand-in (a `ScriptedModelApi` keyed on the number of assistant turns).
- `Operator.cs`: `IOperator`, `ConsoleOperator`, `ScriptedOperator`, `CancelResolution`, `OperatorCancelledException`.
- The scripted `ask_user` operator is `examples/Runner/ScriptedInputHandler.cs` (shared runner support).

## Deviations from Python

- The Textual/ACP TUI, its inline cards (`_ApprovalCard`, `_ElicitationCard`, `_CancelCard`) and the `--acp-server` flag are not ported. `approval_demo` uses the human approver's console prompt (Approve/Reject/Terminate at the terminal); `question_demo` uses the `ask_user` console prompt (one field at a time, `:decline` to decline); the cancel demos read Enter at the console in place of `^L` / `^N`, and the cancel card is the prompt "Score (s), Error (e), or Back (b)".
- Tool-call cancellation (`^L`) is implemented by the `long_running_task` tool itself: it races its sleep against the operator, records the `user_cancel` interrupt event and raises the engine's timeout error, so the model sees the same "Command timed out before completing." tool error Python synthesises for a cancelled call and goes on to submit.
- Sample cancellation (`^N`) is implemented by the `slow_busy_work` tool: Score raises the engine's `TerminateSampleException` (the sample ends cleanly with the engine's "operator" limit and is scored on what it has; Python's Score action likewise completes and scores the sample), Error raises an exception that becomes the sample's error (the eval then reports an error status, exit code 1), Back leaves the tool sleeping until the operator opens the card again.
- Under `--fake` every operator action is scripted so the demos run unattended: the human approver answers `-T decision` (default approve, submit always approved); `ask_user` is answered with `sk-123` then `environment=staging, expiry=2027-01` (or declined with `-T decline=true`); the cancel demos fire after `-T cancel_after` seconds (default 1) and pick `-T resolution` (default score; a comma-separated list, e.g. `back,score`, for several cards). The Python demos always wait for the operator.
- Each task carries its own scripted mockllm model (Python's `Task(model=...)`), which wins over the runner's model: `--model` changes the banner only. The CLI discovers the approval task as `inline_cards_approval_demo` (`examples/approval` already registers `approval_demo` in the same assembly and the CLI resolves bare names per assembly, where Python resolves them per file); the task's own name is `approval_demo`.
- The tasks take the sandbox the runner resolves (`--sandbox`); the Python tasks declare none, and none is the default here too.
