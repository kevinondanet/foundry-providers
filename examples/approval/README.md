# Approval Mode Demo

## Introduction

This is a demonstration of Inspect's [Approval Mode](https://inspect.aisi.org.uk/approval.html) whereby tool calls are approved by a combination of approvers and approval policies. It is a C# port of the `examples/approval` example of the inspect_ai repository (`approval.py`, `approval.yaml` and its README): the task, the two custom approvers and the approval policy are the same, run on this repository's eval engine.

## Running it

There are two ways to run the demonstration.

### The examples runner

The example is `approval` in the examples project (`ApprovalExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that issues the tool calls the prompts ask for, a `local` sandbox, and every escalation to the human approver rejected automatically (so nothing waits at the terminal):

```bash
dotnet run --project examples -- approval --fake --sandbox local
```

Against a Foundry deployment, in a Docker sandbox (the example's default, as in the Python task), which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); the human approver then prompts at the terminal for the calls the allowlists escalate:

```bash
dotnet run --project examples -- approval --model <deployment>
```

The approval policy defaults to this folder's `approval.json`; `--approval <file or approver name>` replaces it. `--sandbox fake` is not available for this example (it has no scripted sandbox: the bash and python tools run for real under `local`), and `--sandbox none` is a prerequisite error because the tools need a sandbox. `dotnet run --project examples -- approval --help` prints the flags, the task and the deviations.

The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("approval_demo")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval approval.py --approval approval.yaml` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval approval_demo \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --approval examples/approval/approval.json \
  --model azureai/<deployment>
```

## Task Setup

The task we'll run asks the model to demonstrate running some bash and python commands in a Docker container:

```csharp
[Task("approval_demo")]
public static EvalTask ApprovalDemoTask() => Build(new SandboxSpec("docker"));

public static EvalTask Build(SandboxSpec sandbox)
{
    ExampleApprovers.Register();
    return new EvalTask
    {
        Name = "approval_demo",
        Dataset = new MemoryDataset(
        [
            new Sample("Please use the bash tool to demonstrate the use of the bash ls command, then demonstrate the use of the bash rm command."),
            new Sample("Please use the python tool to the use of the Python print function, then demonstrate the math.factorial function, then demonstrate the use of the shutil.rmtree function."),
        ]),
        Solver = Agents.AsSolver(Agents.React(
            prompt: new AgentPrompt(Instructions: "\n\"You will be asked to demonstrate various uses of the bash and python tools. Please make only one tool call at a time rather than attempting to demonstrate multiple uses in a single call."),
            tools: [SandboxTools.Bash(), SandboxTools.Python()])),
        Sandbox = sandbox,
    };
}
```

`ExampleApprovers.Register()` stands in for what Python's `@approver` decorator does at import time: it registers `bash_allowlist` and `python_allowlist` by name so the approval policy can reference them. It has to run before the eval starts, because the policy is resolved against the registry once the task object exists. The runner builds the task through `ApprovalExample` (`ApprovalDemo.Build(ctx.Sandbox)`, with a message limit of 40 under `--fake` so a rejected submit cannot loop the scripted model).

## Approval Policy

We'll evaluate this task using the approval policy defined in `approval.json`:

```bash
dotnet run --project examples -- approval --model <deployment> --approval examples/approval/approval.json
```

Here is the approval configuration:

```json
{
  "approvers": [
    {
      "name": "bash_allowlist",
      "tools": "*bash*",
      "allowed_commands": ["ls", "echo", "cat"]
    },
    {
      "name": "python_allowlist",
      "tools": "*python*",
      "allowed_functions": ["print"],
      "allowed_modules": ["math"]
    },
    {
      "name": "human",
      "tools": "*"
    }
  ]
}
```

This port reads policy files as JSON only (there is no YAML parser in the solution); the structure is the same as the original `approval.yaml`, shown here for comparison:

```yaml
approvers:
  - name: bash_allowlist
    tools: "*bash*"
    allowed_commands: ["ls", "echo", "cat"]

  - name: python_allowlist
    tools: "*python*"
    allowed_functions: ["print"]
    allowed_modules: ["math"]

  - name: human
    tools: "*"
```

The list of approvers is applied in order and bound to the tools that match the globs in the `tools` configuration. Note that the `bash_allowlist` and `python_allowlist` approvers are custom approvers defined in this example (they aren't included in the eval library). These approvers will make one of the following approval decisions for each tool call they are configured to handle:

1) Allow the tool call (based on the various configured options)
2) Disallow the tool call (because it is considered dangerous under all conditions)
3) Escalate the tool call to the human approver.

Note that the human approver is last and is bound to all tools, so escalations from the bash and python allowlist approvers will end up prompting the human approver.

Every decision is recorded as an `ApprovalEvent` in the sample's events in the eval log, with the approver's name, the decision and its explanation.

## Custom Approvers

The eval library includes two built-in approvers: `human` for interactive approval at the terminal and `auto` for automatically approving or rejecting specific tools. The code above uses two custom approvers, which you can see the source code of in [Approvers/BashAllowlist.cs](./Approvers/BashAllowlist.cs) and [Approvers/PythonAllowlist.cs](./Approvers/PythonAllowlist.cs). Here is the basic form of a custom approver: a factory that returns an `ApproverDef` (the registry name plus the `Approver` delegate that decides each call), in place of Python's `@approver` function returning an `Approver`:

```csharp
public static ApproverDef BashAllowlist(
    IReadOnlyList<string> allowedCommands,
    bool allowSudo = false,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? commandSpecificRules = null)
{
    // Create an approver that checks if a bash command is in an allowed list.

    Task<Approval> Approve(
        string message,
        ToolCall call,
        ToolCallView view,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        // Make approval decision

        ...
    }

    return new ApproverDef("bash_allowlist", Approve);
}
```

For a policy file to reference the approver by name, register a factory that builds it from the policy entry's parameters (a `JsonObject` of the keys other than `name` and `tools`):

```csharp
ApproverRegistry.Register("bash_allowlist", ExampleApprovers.BashAllowlistFromParams);
```

`ExampleApprovers.Register()` does this for both custom approvers.

See the documentation on [Approval Mode](https://inspect.aisi.org.uk/approval.html) for additional information on using approvals and defining custom approvers.

## Deviations from Python

- The approval policy is `approval.json` rather than `approval.yaml`: this port reads JSON policy files only (a YAML file is a `NotSupportedException`). The structure and values are identical.
- `python_allowlist` uses a lightweight source scanner (`PythonSyntax`) instead of Python's `ast` module, so it approximates `SyntaxError` detection (the `Invalid Python syntax: ...` messages differ from CPython's) and reports the first violation in source order rather than in `ast.walk` order.
- Both approvers read the call's first argument as Python's `str()` would (`None`, `True`/`False`, the text of a string); a call with no arguments is rejected as empty (`Empty command` / `Empty code`) where Python's `next(iter(...))` would error the sample.
- The `--fake` scripted model, the scripted human prompter (rejects every escalated bash/python call, approves submit) and the `local` sandbox option are additions for running the demonstration offline; the Python example only runs through `inspect eval`.
- The Python task is fixed to `sandbox="docker"`; here the sandbox comes from the runner (`--sandbox`, default `docker`), and `--sandbox none` is refused because the tools need one.
