# InspectAzureAI.SandboxContractDemo

A small C# console app that teaches the sandbox contract from
[docs/container-orchestration.md §2](../../docs/container-orchestration.md#2-the-contract-four-small-types)
by exercising it: the four types in `src/InspectAzureAI.Eval/Sandbox/` that everything else
in the container-orchestration subsystem hangs off.

```
dotnet run --project src/InspectAzureAI.SandboxContractDemo            # all six lessons
dotnet run --project src/InspectAzureAI.SandboxContractDemo -- 2       # one lesson
dotnet run --project src/InspectAzureAI.SandboxContractDemo -- 1 4 6   # a selection (always run ascending)
dotnet run --project src/InspectAzureAI.SandboxContractDemo -- list    # lesson titles
```

Every lesson is self-contained, so any subset works. It runs offline in about a second. Lesson 6 starts real host processes through the `local`
provider (`cat` on macOS/Linux, `cmd /c type` on Windows) in a per-sample temp directory
that is deleted at the end.

## What it walks through

| Lesson | Type | What you see happen |
|---|---|---|
| 1 | `SandboxSpec` | Value equality feeding `Distinct()` (one `TaskInit` per spec); the `"Docker"`/`"docker"` pitfall; the task-wins-type / sample-may-override-config merge rule |
| 2 | `ISandboxEnvironment` | Write / read / exec; argv not shell (`$HOME` and `*.txt` stay literal); the error contract: ran-and-failed and missing-executable are `ExecResult`s, timeout and destroyed sandbox are exceptions |
| 3 | `SandboxEnvironments` | First entry is the default; `Single()`; empty set throws; `Cleanup` is a closure and its `bool` is the runner's cleanup flag, not pass/fail |
| 4 | `ISandboxProvider` | One task, three concurrent samples, driven by hand in `Eval.RunAsync` order; the provider's own call log printed back |
| 5 | `SandboxRegistry` | Built-in types, `Register`, case-insensitive `Get`, one shared instance, the unknown-type error |
| 6 | all four | One provider-agnostic `RunOneSampleAsync` run against the toy `memory` provider and then the real `local` provider |

## The two files

- `InMemorySandbox.cs` — a complete provider + environment in ~200 lines. Files are a
  dictionary; the "shell" understands `echo`, `cat`, `ls`, `pwd`, `env`, `exit` and `sleep`.
  Read it to see how little a provider must do, and where each contract rule lands.
- `Program.cs` — the six lessons, as top-level statements with a comment above every step.

## Where to next

`docs/container-orchestration.md` §3 (the lifecycle: who calls what, and when) and §4 (the
Docker provider), then `src/InspectAzureAI.Eval/Sandbox/Docker/` with the contract in hand.
