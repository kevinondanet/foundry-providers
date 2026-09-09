using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.Skills;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Skills;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/skills/task.py</c> <c>skills_example</c> (<see cref="SkillsExample"/>): the
/// task's shape, the three skill folders copied beside the assembly, the scripted Ubuntu container of
/// <see cref="FakeSkillsSandbox"/>, the scripted model's skill choice and answers, and the example end to end
/// without Docker (the fake model as agent and grader against the fake sandbox, through <c>Eval.RunAsync</c> and the
/// examples runner).
/// </summary>
public sealed class SkillsTests : IDisposable
{
    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "skills");

    private static readonly string SkillsDirectory = Path.Combine(ExampleDirectory, "skills");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "skills-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ExampleContext Context(SandboxSpec? sandbox = null, bool fake = true) =>
        new(ExampleDirectory, sandbox, fake, new Dictionary<string, string>(), null, null, TextWriter.Null);

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def skills_example) and its skills
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_skills_example()
    {
        var task = SkillsExample.Build(new SandboxSpec("docker", "compose.yaml"), SkillsDirectory);

        Assert.Equal("skills_example", task.Name);
        Assert.Equal(5, task.Dataset.Count);
        Assert.Equal(
            [
                "What Linux distribution is this system running? Include the version.",
                "How many CPU cores does this system have?",
                "What is the total amount of memory (RAM) on this system?",
                "What is the IP address of this system?",
                "How much disk space is available on the root filesystem?",
            ],
            task.Dataset.Select(sample => sample.Input.Text));
        Assert.Equal(
            [
                "The system is running Ubuntu 24.04",
                "The number of CPU cores available on the system",
                "The total RAM available on the system",
                "The IP address(es) configured on the system's network interfaces",
                "The available disk space on the root (/) filesystem",
            ],
            task.Dataset.Select(sample => sample.Target.Values.Single()));
        Assert.Equal("model_graded_qa", Assert.Single(task.Scorers).Name);
        Assert.Equal(new SandboxSpec("docker", "compose.yaml"), task.Sandbox);
        Assert.Null(task.MessageLimit);
        Assert.Equal(
            "You are a Linux system administrator. You have access to skills that provide guidance for system exploration tasks. Use the skill tool to get instructions before attempting tasks, then use bash to execute the appropriate commands.",
            SkillsExample.Prompt);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_the_example_compose_file()
    {
        var method = typeof(SkillsExample).GetMethod(nameof(SkillsExample.SkillsExampleTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("skills_example", method.GetCustomAttribute<TaskAttribute>()?.Name);
        var task = SkillsExample.SkillsExampleTask();
        Assert.Equal(Path.Combine(ExampleDirectory, "compose.yaml"), task.Sandbox?.Config);
        Assert.True(File.Exists(task.Sandbox!.Config!), $"compose.yaml was not copied next to the assembly: {task.Sandbox.Config}");
        var compose = File.ReadAllText(task.Sandbox.Config);
        Assert.Contains("image: ubuntu:24.04", compose);
        Assert.Contains("internal: true", compose);
    }

    [Fact]
    public void the_three_skill_folders_are_copied_verbatim_and_read_as_skills()
    {
        var skills = SkillReader.ReadSkills(SkillsExample.SkillNames.Select(name => SkillSource.FromDirectory(Path.Combine(SkillsDirectory, name))).ToList());

        Assert.Equal(["system-info", "network-info", "disk-usage"], skills.Select(skill => skill.Name));
        Assert.Equal(["sysinfo.sh", "netinfo.sh", "diskinfo.sh"], skills.Select(skill => skill.Scripts.Keys.Single()));
        Assert.StartsWith("Retrieve detailed Linux system information including OS distribution, kernel version, CPU model and core count, memory usage, and uptime.", skills[0].Description);
        Assert.StartsWith("Gather network configuration and connectivity details on Linux", skills[1].Description);
        Assert.StartsWith("Analyze disk space usage, filesystem mounts, and storage allocation on Linux systems.", skills[2].Description);
        Assert.Contains("1. Run `./scripts/sysinfo.sh` for structured output covering OS, CPU, memory, and uptime.", skills[0].Instructions);
        Assert.Equal("#!/bin/bash", File.ReadLines(Path.Combine(SkillsDirectory, "disk-usage", "scripts", "diskinfo.sh")).First());
    }

    [Fact]
    public void the_skill_tool_lists_the_three_skills_beside_bash()
    {
        var tool = SkillTools.Skill(SkillsExample.SkillNames.Select(name => SkillSource.FromDirectory(Path.Combine(SkillsDirectory, name))).ToList());

        Assert.Equal("skill", tool.Name);
        Assert.Contains("<name>system-info</name>", tool.Description);
        Assert.Contains("<name>network-info</name>", tool.Description);
        Assert.Contains("<name>disk-usage</name>", tool.Description);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example, the fake sandbox and the fake model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_declares_the_python_task_a_docker_sandbox_and_a_fake_container()
    {
        var example = Assert.IsType<SkillsExample>(ExampleRegistry.Default.Get("skills"));

        Assert.Equal("skills", example.Name);
        Assert.Equal(["skills_example"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal("compose.yaml", example.Defaults.ComposeFile);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.NotEmpty(example.Deviations);
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
        Assert.Equal(SkillsExample.FakeMessageLimit, example.Tasks[0].Build(Context(new SandboxSpec("fake"))).MessageLimit);
        Assert.Null(example.Tasks[0].Build(Context(new SandboxSpec("local"), fake: false)).MessageLimit);
    }

    [Fact]
    public async Task the_fake_container_answers_pwd_and_the_helper_scripts()
    {
        using var environment = new ScriptedSandboxEnvironment(FakeSkillsSandbox.Create());

        Assert.Equal("/root\n", (await environment.ExecAsync(["sh", "-c", "pwd"])).Stdout);
        Assert.True((await environment.ExecAsync(["chmod", "+x", "/root/skills/system-info/scripts/sysinfo.sh"])).Success);
        Assert.Equal(FakeSkillsSandbox.SysInfoOutput, (await environment.ExecAsync(["bash", "--login", "-c", "./skills/system-info/scripts/sysinfo.sh"])).Stdout);
        Assert.Equal(FakeSkillsSandbox.NetInfoOutput, (await environment.ExecAsync(["bash", "--login", "-c", "bash /root/skills/network-info/scripts/netinfo.sh"])).Stdout);
        Assert.Equal(FakeSkillsSandbox.DiskInfoOutput, (await environment.ExecAsync(["bash", "--login", "-c", "./skills/disk-usage/scripts/diskinfo.sh"])).Stdout);
        var unknown = await environment.ExecAsync(["bash", "--login", "-c", "lscpu"]);
        Assert.False(unknown.Success);
        Assert.Contains("lscpu: command not found", unknown.Stderr);
    }

    [Theory]
    [InlineData("What Linux distribution is this system running? Include the version.", "system-info", "sysinfo.sh")]
    [InlineData("How many CPU cores does this system have?", "system-info", "sysinfo.sh")]
    [InlineData("What is the total amount of memory (RAM) on this system?", "system-info", "sysinfo.sh")]
    [InlineData("What is the IP address of this system?", "network-info", "netinfo.sh")]
    [InlineData("How much disk space is available on the root filesystem?", "disk-usage", "diskinfo.sh")]
    public void the_fake_model_picks_the_skill_for_each_question(string input, string skill, string script)
    {
        Assert.Equal((skill, script), FakeSkillsModel.SkillFor(input));
    }

    [Fact]
    public void the_fake_model_quotes_the_relevant_lines_and_grades_c()
    {
        var sysinfo = new ChatMessageTool(FakeSkillsSandbox.SysInfoOutput, function: "bash");
        Assert.Equal("According to the system: Distribution: Ubuntu 24.04.2 LTS | Kernel: 6.10.14-linuxkit | Architecture: aarch64", FakeSkillsModel.Answer(SkillsExample.Samples[0].Input, sysinfo));
        Assert.Equal("According to the system: CPU: | Processors: 4", FakeSkillsModel.Answer(SkillsExample.Samples[1].Input, sysinfo));
        Assert.Equal("According to the system: Total: 7.75116 GB | Available: 6.94238 GB", FakeSkillsModel.Answer(SkillsExample.Samples[2].Input, sysinfo));
        Assert.Equal("According to the system: 172.19.0.2", FakeSkillsModel.Answer(SkillsExample.Samples[3].Input, new ChatMessageTool(FakeSkillsSandbox.NetInfoOutput, function: "bash")));
        Assert.Equal("According to the system: Filesystem      Size  Used Avail Use% Mounted on | overlay          59G   21G   35G  38% /", FakeSkillsModel.Answer(SkillsExample.Samples[4].Input, new ChatMessageTool(FakeSkillsSandbox.DiskInfoOutput, function: "bash")));
        Assert.StartsWith("The helper script failed: ", FakeSkillsModel.Answer(SkillsExample.Samples[0].Input, new ChatMessageTool("", function: "bash", error: new ToolCallError("unknown", "bash: line 1: nope: command not found"))));

        var grading = new ChatMessageUser("You are assessing a submitted answer on a given task based on a criterion. Here is the data:\n\n[BEGIN DATA]\n***\n[Task]: q\n***\n[Submission]: a\n***\n[Criterion]: c\n***\n[END DATA]\n");
        var output = FakeSkillsModel.Respond([grading], []);
        Assert.EndsWith("GRADE: C", output.Completion);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_run_installs_the_skills_runs_their_scripts_and_grades_every_answer_correct()
    {
        var script = FakeSkillsSandbox.Create();
        var sandbox = ScriptedSandboxProvider.Register(script);
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(FakeSkillsModel.Respond), 40), FakeSkillsModel.ModelName);
        var task = SkillsExample.Build(sandbox, SkillsDirectory) with { MessageLimit = SkillsExample.FakeMessageLimit };

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        Assert.Equal(5, log.Samples!.Count);
        Assert.All(log.Samples, sample => Assert.Equal("C", sample.Scores!["model_graded_qa"].Text));
        // three agent turns and one grading turn per sample
        Assert.Equal(20, api.Requests.Count);
        Assert.Equal(5, api.Requests.Count(request => request.Tools.Count == 0));

        // every sample's container got all three skills installed on the first skill call, scripts made executable
        Assert.Equal(5, script.Environments.Count);
        foreach (var environment in script.Environments)
        {
            Assert.Single(environment.Calls, call => call.Cmd.SequenceEqual(["sh", "-c", "pwd"]));
            foreach (var (name, file) in new[] { ("system-info", "sysinfo.sh"), ("network-info", "netinfo.sh"), ("disk-usage", "diskinfo.sh") })
            {
                Assert.StartsWith($"---\nname: {name}\n", environment.FileText($"/root/skills/{name}/SKILL.md"));
                Assert.Equal(File.ReadAllText(Path.Combine(SkillsDirectory, name, "scripts", file)), environment.FileText($"/root/skills/{name}/scripts/{file}"));
                Assert.Single(environment.Calls, call => call.Cmd.SequenceEqual(["chmod", "+x", $"/root/skills/{name}/scripts/{file}"]));
            }

            Assert.DoesNotContain(environment.Calls, call => call.Cmd[0] == "chown");
        }

        // each sample: skill(<skill>), bash(./skills/<skill>/scripts/<script>), submit(<answer>)
        foreach (var sample in log.Samples)
        {
            var (skill, helper) = FakeSkillsModel.SkillFor(sample.Input.Text!);
            var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
            Assert.Equal(["skill", "bash"], calls.Select(call => call.Function));
            Assert.Equal(skill, calls[0].Arguments["command"]!.GetValue<string>());
            Assert.Equal($"./skills/{skill}/scripts/{helper}", calls[1].Arguments["cmd"]!.GetValue<string>());
            var results = sample.Messages.OfType<ChatMessageTool>().ToList();
            Assert.All(results, result => Assert.Null(result.Error));
            Assert.Contains($"<command-name>{skill}</command-name>\n\nBase Path: /root/skills/{skill}/SKILL.md\n\n", results[0].Text);
            Assert.Equal(FakeSkillsSandbox.ScriptOutput(helper), results[1].Text);
            // the submit turn: its thought, then the submitted answer
            Assert.Contains("According to the system: ", Assert.IsType<ChatMessageAssistant>(sample.Messages[^1]).Text);
        }
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_with_the_fake_container()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["skills", "--fake", "--sandbox", "fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.True(exit == 0, output.ToString());
        Assert.Contains("status    : success (5/5 samples completed)", output.ToString());
        Assert.Contains("model_graded_qa", output.ToString());
        Assert.Single(Directory.EnumerateFiles(_logDir, "*.eval"));
    }
}
