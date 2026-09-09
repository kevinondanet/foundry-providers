using System.Diagnostics;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Tests;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The showcase console app driven in-process through <c>Cli.RunAsync</c>: <c>--fake</c> runs on the local sandbox
/// (no network, no Docker) for both offline-capable agents, the refusal for claude-code, the <c>list</c> and
/// <c>show</c> commands, the usage errors, and one Docker-gated run that builds the showcase image.
/// </summary>
public sealed class ShowcaseOfflineTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-showcase-tests", Guid.NewGuid().ToString("N"));

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

    [Python3Fact]
    public async Task fake_mini_swe_solves_the_first_hello_swe_sample_on_the_local_sandbox()
    {
        var run = await RunAsync("run", "--fake", "--sandbox", "local", "--task", "hello-swe", "--agent", "mini-swe", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("local", log.Eval.Sandbox!.Type);
        Assert.Equal("scripted", log.Eval.Model);
        Assert.Equal(3, log.Results!.TotalSamples);
        Assert.Equal(3, log.Results.CompletedSamples);
        var accuracy = Accuracy(log, "exec_check");
        Assert.True(accuracy > 0, $"accuracy was {accuracy}");
        var first = log.Samples!.Single(sample => (int)sample.Id == 1);
        Assert.Equal("C", first.Scores!["exec_check"].Text);
        Assert.Contains(first.Messages, message => message is ChatMessageTool { Function: "bash" });
        Assert.Contains("hello.py", first.Output.Completion);
        Assert.Contains("exec_check", run.Stdout);
        Assert.Contains("accuracy", run.Stdout);
        Assert.Contains("sample 1 (epoch 1) completed", run.Stdout);
    }

    [Python3Fact]
    public async Task fake_basic_agent_solves_the_first_hello_swe_sample_on_the_local_sandbox()
    {
        var run = await RunAsync("run", "--fake", "--sandbox", "local", "--task", "hello-swe", "--agent", "basic", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.True(Accuracy(log, "exec_check") > 0);
        var first = log.Samples!.Single(sample => (int)sample.Id == 1);
        Assert.Equal("C", first.Scores!["exec_check"].Text);
        Assert.Contains(first.Messages, message => message is ChatMessageTool { Function: "submit" });
        Assert.Contains(first.Messages, message => message is ChatMessageTool { Function: "bash" });
        Assert.Contains(first.Messages, message => message is ChatMessageSystem);
    }

    [Python3Fact]
    public async Task fake_maf_agent_solves_the_first_hello_swe_sample_on_the_local_sandbox()
    {
        var run = await RunAsync("run", "--fake", "--sandbox", "local", "--task", "hello-swe", "--agent", "maf", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.True(Accuracy(log, "exec_check") > 0);
        var first = log.Samples!.Single(sample => (int)sample.Id == 1);
        Assert.Equal("C", first.Scores!["exec_check"].Text);
        Assert.Contains(first.Messages, message => message is ChatMessageTool { Function: "submit" });
        Assert.Contains(first.Messages, message => message is ChatMessageTool { Function: "bash" });
        Assert.Contains(first.Messages, message => message is ChatMessageSystem);
        Assert.Contains("hello.py", first.Output.Completion);
        Assert.Contains(first.Events, e => e is Eval.Context.ToolEvent { Function: "bash" });
    }

    [Python3Fact]
    public async Task fake_run_limited_to_the_first_sample_over_two_epochs_scores_full_accuracy()
    {
        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--epochs", "2", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal("local", log.Eval.Sandbox!.Type);
        Assert.Equal(2, log.Eval.Config.Epochs);
        Assert.Equal(2, log.Results!.TotalSamples);
        Assert.Equal(1.0, Accuracy(log, "exec_check"));
        Assert.All(log.Samples!, sample => Assert.Equal(1, (int)sample.Id));
    }

    [Fact]
    public async Task fake_system_explorer_is_graded_by_the_scripted_judge()
    {
        var run = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal(EvalStatus.Success, log.Status);
        var first = log.Samples!.Single(sample => (int)sample.Id == 1);
        var second = log.Samples!.Single(sample => (int)sample.Id == 2);
        Assert.Equal("C", first.Scores!["model_graded_qa"].Text);
        Assert.Equal("I", second.Scores!["model_graded_qa"].Text);
        Assert.Contains("GRADE: C", first.Scores["model_graded_qa"].Explanation);
        Assert.Equal(0.5, Accuracy(log, "model_graded_qa"));
    }

    [Fact]
    public async Task fake_claude_code_is_refused_as_a_usage_error()
    {
        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "claude-code", "--log-dir", _logDir);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("claude-code", run.Stderr);
        Assert.Contains("usage:", run.Stderr);
        Assert.False(Directory.Exists(_logDir));
    }

    [Fact]
    public async Task fake_copilot_is_refused_as_a_usage_error()
    {
        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "copilot", "--log-dir", _logDir);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("copilot", run.Stderr);
        Assert.Contains("usage:", run.Stderr);
        Assert.False(Directory.Exists(_logDir));
    }

    [Fact]
    public async Task list_prints_the_built_in_tasks_and_agents()
    {
        var run = await RunAsync("list");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("hello-swe", run.Stdout);
        Assert.Contains("3 samples", run.Stdout);
        Assert.Contains("pytest-fix", run.Stdout);
        Assert.Contains("system-explorer", run.Stdout);
        Assert.Contains("model_graded_qa", run.Stdout);
        Assert.Contains("claude-code", run.Stdout);
        Assert.Contains("copilot", run.Stdout);
        Assert.Contains("maf", run.Stdout);
    }

    [Python3Fact]
    public async Task show_prints_the_scores_and_samples_of_a_written_log()
    {
        var written = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "basic", "--limit", "1", "--log-dir", _logDir);
        Assert.True(written.ExitCode == 0, written.Stderr + written.Stdout);
        var path = Assert.Single(Directory.GetFiles(_logDir, "*.eval"));

        var run = await RunAsync("show", path);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("task     : hello-swe", run.Stdout);
        Assert.Contains("exec_check", run.Stdout);
        Assert.Contains("samples:", run.Stdout);
        Assert.Contains("1 (epoch 1): exec_check=C", run.Stdout);
    }

    [Theory]
    [InlineData(new[] { "run", "--fake", "--task", "nope", "--agent", "basic" }, "unknown task")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe" }, "--agent")]
    [InlineData(new[] { "run", "--fake", "--agent", "basic" }, "--task")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe", "--agent", "basic", "--limit", "x" }, "--limit")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe", "--agent", "basic", "--sandbox", "vm" }, "--sandbox")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe", "--agent", "basic", "--reasoning-effort", "loads" }, "--reasoning-effort")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe", "--agent", "basic", "--bogus" }, "--bogus")]
    [InlineData(new[] { "run", "--fake", "--task", "hello-swe", "--agent", "basic", "extra" }, "extra")]
    [InlineData(new[] { "show" }, "show expects")]
    [InlineData(new[] { "show", "/nonexistent/log.json" }, "not found")]
    [InlineData(new[] { "frobnicate" }, "unknown command")]
    public async Task bad_command_lines_are_usage_errors_with_the_help_text(string[] args, string expected)
    {
        var run = await RunAsync(args);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(expected, run.Stderr);
        Assert.Contains("usage:", run.Stderr);
    }

    [Fact]
    public async Task no_arguments_and_help_print_the_help_text()
    {
        var bare = await RunAsync();
        var help = await RunAsync("--help");

        Assert.Equal(0, bare.ExitCode);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("usage:", bare.Stdout);
        Assert.Contains("--fake", help.Stdout);
    }

    [DockerFact]
    public async Task fake_mini_swe_run_in_docker_builds_the_showcase_image_and_completes()
    {
        var run = await RunAsync("run", "--fake", "--sandbox", "docker", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", _logDir);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var log = ReadSingleLog();
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("docker", log.Eval.Sandbox!.Type);
        Assert.EndsWith("sandbox", log.Eval.Sandbox.Config);
        Assert.Equal(1.0, Accuracy(log, "exec_check"));
    }

    private static async Task<CliRun> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (previousOut, previousError) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await Cli.RunAsync(args);
            return new CliRun(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private EvalLog ReadSingleLog() => EvalLogWriter.Read(Assert.Single(Directory.GetFiles(_logDir, "*.eval")));

    private static double Accuracy(EvalLog log, string scorer) =>
        log.Results!.Scores.Single(score => score.Name == scorer).Metrics["accuracy"].Value;

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr);
}

/// <summary>A fact that runs only when <c>python3</c> is on the PATH: the hello-swe checks run it on the host under the local sandbox.</summary>
public sealed class Python3FactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public Python3FactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "python3 is not on the PATH; the local-sandbox showcase runs need it.";
        }
    }

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(TimeSpan.FromSeconds(20)) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
