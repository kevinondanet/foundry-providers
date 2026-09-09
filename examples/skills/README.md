# Agent Skills Example

A C# port of `examples/skills/` of the inspect_ai repository (`task.py`, its README, `compose.yaml` and the three skill folders, all copied verbatim). It demonstrates how to use the `skill` tool (`SkillTools.Skill`) to provide specialized instructions and scripts to agents for Linux system exploration tasks.

## Overview

Agent skills are structured packages of instructions, scripts, and reference materials that agents can invoke on-demand. When an agent encounters a task that matches a skill's description, it can invoke the skill to receive detailed guidance.

Each skill consists of:
- **SKILL.md**: Instructions with YAML frontmatter (name, description) and markdown body
- **scripts/**: Optional executable scripts the agent can run
- **references/**: Optional additional documentation
- **assets/**: Optional static resources (templates, data files)

## Skills Included

This example includes three Linux system exploration skills (`skills/`):

| Skill | Description |
|-------|-------------|
| `system-info` | Get OS, kernel, CPU, and memory information |
| `network-info` | Explore network interfaces, routes, and DNS configuration |
| `disk-usage` | Analyze disk space and filesystem usage |

Each skill includes a helper bash script that provides structured output.

## Running the Example

### Offline

```bash
dotnet run --project examples -- skills --fake --sandbox fake
```

A scripted model (`FakeSkillsModel`) follows the prompt for each of the five questions: `skill("<the matching skill>")`, then `bash("./skills/<skill>/scripts/<script>.sh")` as the skill's instructions suggest, then `submit` with the lines of the script's output that answer the question. The task names no grading model, so `model_graded_qa` grades with the same scripted model, which recognises the grading template and answers `GRADE: C`. The sandbox is a scripted Ubuntu 24.04 container (`FakeSkillsSandbox`): `sh -c pwd` answers `/root` (so the skills are installed into `/root/skills/<name>/` of the sample's file store, scripts made executable), and the bash tool's command answers with what each helper script prints on a bare Ubuntu container on an internal network. No Docker is involved.

`--sandbox local` installs the skills into a temporary working directory on this host and runs the helper scripts for real; they fall back gracefully where the Linux tools (`lscpu`, `free`, `ip`, `ss`) are absent, so on macOS the answers describe this machine loosely. Add `--display conversation` to watch the skill instructions, the scripts' output and the grading.

### Against a Foundry deployment

```bash
# Run with default model
dotnet run --project examples -- skills --model <deployment>

# Run with a specific sample
dotnet run --project examples -- skills --model <deployment> --limit 1
```

Requirements: Docker (the `ubuntu:24.04` image is pulled), `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), and a tool-calling deployment, which also grades the answers. The evaluation runs in a Docker container (Ubuntu 24.04) where the agent can execute system commands. Its internal Docker network provides an interface for the network-information task but does not permit Internet access.

### The `inspectai` CLI

The task is marked `[Task("skills_example")]`, so the CLI can discover it in the built assembly, as `inspect eval examples/skills/task.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval skills_example \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## How It Works

1. The `skill` tool registers available skills with the agent
2. When the agent receives a task, it can invoke a skill by name
3. The skill returns instructions and the path to any scripts
4. The agent follows the instructions and uses `bash` to execute commands
5. Skills are installed into the sandbox at `./skills/<skill-name>/`

## The task

```csharp
[Task("skills_example")]
public static EvalTask SkillsExampleTask()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "skills");
    return Build(new SandboxSpec("docker", Path.Combine(directory, "compose.yaml")), Path.Combine(directory, "skills"));
}

public static EvalTask Build(SandboxSpec sandbox, string skillsDir) => new()
{
    Name = "skills_example",
    Dataset = new MemoryDataset(
    [
        new Sample("What Linux distribution is this system running? Include the version.") { Target = "The system is running Ubuntu 24.04" },
        new Sample("How many CPU cores does this system have?") { Target = "The number of CPU cores available on the system" },
        new Sample("What is the total amount of memory (RAM) on this system?") { Target = "The total RAM available on the system" },
        new Sample("What is the IP address of this system?") { Target = "The IP address(es) configured on the system's network interfaces" },
        new Sample("How much disk space is available on the root filesystem?") { Target = "The available disk space on the root (/) filesystem" },
    ]),
    Solver = Agents.AsSolver(Agents.React(
        prompt: new AgentPrompt(Instructions:
            "You are a Linux system administrator. You have access to skills "
            + "that provide guidance for system exploration tasks. Use the skill "
            + "tool to get instructions before attempting tasks, then use bash "
            + "to execute the appropriate commands."),
        tools:
        [
            SkillTools.Skill(
            [
                SkillSource.FromDirectory(Path.Combine(skillsDir, "system-info")),
                SkillSource.FromDirectory(Path.Combine(skillsDir, "network-info")),
                SkillSource.FromDirectory(Path.Combine(skillsDir, "disk-usage")),
            ]),
            SandboxTools.Bash(),
        ])),
    Scorers = [Scorers.ModelGradedQa()],
    Sandbox = sandbox,
};
```

## Creating Your Own Skills

To create a new skill:

1. Create a directory with your skill name (lowercase, hyphens allowed):
   ```
   my-skill/
   ├── SKILL.md
   └── scripts/
       └── helper.sh
   ```

2. Add YAML frontmatter to SKILL.md:
   ```markdown
   ---
   name: my-skill
   description: Brief description of what this skill does
   ---

   # My Skill

   Instructions for using this skill...
   ```

3. Add the skill to your task:
   ```csharp
   using InspectAzureAI.Eval.Tools.Skills;

   SkillTools.Skill([SkillSource.FromDirectory("path/to/my-skill")])
   ```

## Skill Specification

Skills follow the [Agent Skills Specification](https://agentskills.io/specification). Key requirements:

- **name**: Lowercase letters, numbers, and hyphens (max 64 chars)
- **description**: What the skill does and when to use it (max 1024 chars)
- **instructions**: Step-by-step guidance in the markdown body

Optional frontmatter fields:
- `license`: License identifier
- `compatibility`: Environment requirements
- `metadata`: Custom key-value data
- `allowed-tools`: Pre-approved tools for the skill

## Deviations from Python

- Python resolves the skill folders relative to `task.py` (`SKILLS_DIR = Path(__file__).parent / "skills"`); here they are read from the example's output folder (`<example dir>/skills/<name>`), where the project copies them, and the compose file is passed explicitly as `SandboxSpec("docker", "<example dir>/compose.yaml")`.
- The `--fake` scripted model (`skill(<matching skill>)`, `bash(./skills/<skill>/scripts/<script>.sh)`, `submit(<the relevant lines of the script's output>)`; the same scripted model grades every submission `GRADE: C` when `model_graded_qa` asks it) and the fake sandbox (`pwd` is `/root`, the scripts answer with canned Ubuntu 24.04 output) are additions for running offline; the Python example only runs through `inspect eval` against the container.
- `--sandbox local` installs the skills into a temporary working directory on this host and runs the helper scripts for real (they fall back gracefully where the Linux tools are absent, so on macOS the answers describe this machine loosely); `--sandbox none` is refused because `skill` and `bash` need a sandbox.
- A message limit of 20 guards the fake run; the Python task has none.
