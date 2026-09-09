# ask_user demo

A C# port of `examples/ask_user/demo.py` of the inspect_ai repository: a demo eval for the `ask_user` question panel.

The script wires a scripted (`mockllm`) model that emits two tool calls:

1. `ask_user` with a kitchen-sink schema (string + enum string + integer + number + boolean + multi-select).
2. `submit` echoing back "ok" whatever fields the user entered, so `includes()` scores it.

When the agent fires the first call, Python mounts the "Question" tab on the Textual display and waits for you to fill out the form and press Submit (or Decline). In this port the form is asked at the console, one field at a time (`ConsoleInputHandler`, the handler Python itself falls back to when no Textual display is active): type a value for each field, or `:decline` at any prompt to decline. The answer flows back into the model as a JSON object, which then submits via the second tool call and the eval completes.

## Running it

### Offline (the examples runner)

The example is `ask_user` in the examples project (see [examples/README.md](../README.md) for the runner and its flags). With `--fake` the operator is scripted too, so nothing waits at the terminal: the form is answered with `name=Ada, color=green, count=3, confirm=true, tags=[rush]` (the optional `ratio` left blank):

```bash
dotnet run --project examples -- ask_user --fake
```

To see the declined path (`ask_user` returns the tool error `User declined to answer the question.` and the agent still submits):

```bash
dotnet run --project examples -- ask_user --fake -T decline=true
```

### Live (a Foundry deployment, the form at the console)

Python's demo is offline too (its model is a mock); the C# port additionally runs the same task against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"). The `ask_user` questions the model asks are then answered at the terminal:

```bash
dotnet run --project examples -- ask_user --model <deployment>
```

`--display conversation` prints every model turn. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage error, 3 on cancellation.

### The `inspectai` CLI

The task is marked `[Task("ask_user")]`, so the CLI discovers it in the built assembly (the operator answers at the console):

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval ask_user \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
public static EvalTask Build(SandboxSpec? sandbox = null, IInputHandler? handler = null) => new()
{
    Name = "task",
    Dataset = new MemoryDataset([new Sample("Use the ask_user tool to collect order details from the operator, then submit the order summary.") { Target = new Target(["ok"]) }]),
    Solver = Agents.AsSolver(Agents.React(
        tools: [BuiltinTools.AskUser(handler)],
        submit: new AgentSubmit { Name = "submit", Description = "Submit the final summary once the form is filled." })),
    Scorers = [Scorers.Includes()],
    MessageLimit = 10,
    Sandbox = sandbox,
};
```

The kitchen-sink schema is built from the engine's `ElicitationSchema` records (the port of the ACP `ElicitationSchema` pydantic models) and serialised with `ToJson()` as the `schema` argument of the scripted `ask_user` call:

```csharp
new ElicitationSchema
{
    Properties =
    {
        ["name"] = new ElicitationStringProperty { Title = "Your name", Description = "Free-form text, min 2 characters.", MinLength = 2 },
        ["color"] = new ElicitationStringProperty { Title = "Favourite color", Description = "Pick one (renders as a Select).", OneOf = [new("red", "Red"), new("green", "Green"), new("blue", "Blue")] },
        ["count"] = new ElicitationIntegerProperty { Title = "Quantity", Description = "Between 1 and 100.", Minimum = 1, Maximum = 100 },
        ["ratio"] = new ElicitationNumberProperty { Title = "Discount (optional)", Description = "Decimal — leave blank for none." },
        ["confirm"] = new ElicitationBooleanProperty { Title = "Confirm", Description = "Tick to confirm the order." },
        ["tags"] = new ElicitationMultiSelectProperty(new TitledMultiSelectItems([new("rush", "Rush delivery"), new("gift", "Gift wrap"), new("signed", "Signature required")])) { Title = "Tags", Description = "Pick one or more.", MinItems = 1 },
    },
    Required = ["name", "color", "count", "confirm", "tags"],
}
```

The scripted model (`AskUserExample.CreateFakeModel()`) is the port of `get_model("mockllm/model", custom_outputs=[...])`: its first turn calls `ask_user` with `message = "Please fill out your order details."` and the schema above, every later turn calls `submit` with `{"answer": "ok"}`.

## Files

- `AskUserExample.cs`: the `IExample`, the `[Task]` method, the task, the schema and the scripted model.
- The scripted operator behind `--fake` is shared runner support, `examples/Runner/ScriptedInputHandler.cs` (an `IInputHandler` answering from a list: `ScriptedInputHandler.Accepting(...)` / `.Declining(...)`; also used by `inline_cards`, and the runner installs a declining one for every `--fake` run).

## Deviations from Python

- The Textual "Question" tab (`eval(..., display="full")`) is not ported: the form is answered one field at a time at the console (`ConsoleInputHandler`, the handler Python itself falls back to when no Textual display is active); type `:decline` at any prompt to decline. There is no `display="full"`; `--display conversation` prints the model turns instead.
- Under `--fake` the operator is scripted too (`ScriptedInputHandler`): the form is answered with `name=Ada, color=green, count=3, confirm=true, tags=[rush]` (`ratio` left blank), or declined with `-T decline=true`, so an offline run never blocks. The Python demo always waits for the operator.
- The Python script builds an anonymous `Task` in `main()` and runs it with `eval()`; Inspect names such a task `task`, and so does this port (the `[Task]` attribute registers it with the `inspectai` CLI as `ask_user`). The mockllm model of the Python is the runner's `--fake` model here; a Foundry `--model` runs the same task with a real model at the console.
- The kitchen-sink schema is built from the port's `ElicitationSchema` records and serialised with `ToJson()`, which omits unset keys, where the Python's `model_dump(mode="json")` writes them as `null` (`min_length: null`, ...); both parse to the same form.
- The task takes the sandbox the runner resolves (`--sandbox`); the Python task declares none, and none is the default here too.
