using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The showcase's wiring of the eval engine's newer subsystems, driven offline through <c>Cli.RunAsync</c>:
/// <c>--approval</c> (reject and terminate, on the native loops), <c>--cache</c> (a second run served from disk),
/// <c>--compaction</c> (accepted for the native loops, refused for Claude Code), <c>--hooks sample-log</c>,
/// <c>--log-format</c> with <c>show</c> reading either format, <c>--model-cost-config</c> / <c>--cost-limit</c>,
/// and the usage errors of every new flag.
/// </summary>
public sealed class ShowcaseWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inspect-showcase-wiring-tests", Guid.NewGuid().ToString("N"));

    private readonly string _logDir;

    public ShowcaseWiringTests()
    {
        Directory.CreateDirectory(_dir);
        _logDir = Path.Combine(_dir, "logs");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Python3Fact]
    public async Task approval_policy_rejects_the_matching_bash_call_of_the_mini_swe_loop()
    {
        var policy = await WritePolicyAsync("bash(command='python3*", "reject");

        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", _logDir, "--approval", policy);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        Assert.Contains($"approval : {policy}", run.Stdout);
        AssertRejected(ReadSingleLog(), "python3 hello.py");
    }

    [Python3Fact]
    public async Task approval_policy_rejects_the_matching_bash_call_of_the_basic_agent()
    {
        var policy = await WritePolicyAsync("bash(cmd='python3*", "reject");

        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "basic", "--limit", "1", "--log-dir", _logDir, "--approval", policy);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        AssertRejected(ReadSingleLog(), "python3 hello.py");
    }

    private void AssertRejected(EvalLog log, string command)
    {
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.NotNull(log.Eval.Config.Approval);
        Assert.Contains("reject", log.Eval.Config.Approval!.ToJsonString());
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["exec_check"].Text);   // hello.py was written by the approved first step; only the agent's own check was refused
        var rejected = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), m => m.Error is not null);
        Assert.Equal("approval", rejected.Error!.Type);
        var approvals = sample.Events.OfType<ApprovalEvent>().ToList();
        Assert.Contains(approvals, e => e.Decision == "reject" && e.Call.Arguments.ToJsonString().Contains(command, StringComparison.Ordinal));
        Assert.Contains(approvals, e => e.Decision == "approve");

        var show = RunAsync("show", log.Location!).GetAwaiter().GetResult();
        Assert.Equal(0, show.ExitCode);
        Assert.Contains("approval : {", show.Stdout);
        Assert.Contains("approvals: ", show.Stdout);
        Assert.Contains("1 reject", show.Stdout);
    }

    [Python3Fact]
    public async Task approval_terminate_ends_the_sample_with_the_operator_limit()
    {
        var policy = await WritePolicyAsync("bash(command='python3*", "terminate");

        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", _logDir, "--approval", policy);

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        var sample = Assert.Single(ReadSingleLog().Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("operator", sample.Limit!.Type);
        Assert.NotNull(sample.Scores);
        Assert.Contains("operator limit", run.Stdout);
    }

    [Python3Fact]
    public async Task cache_serves_a_second_run_from_disk()
    {
        using var env = new EnvVar(CacheOps.CacheDirVar, Path.Combine(_dir, "cache"));
        var first = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", Path.Combine(_logDir, "1"), "--cache", "1D");
        Assert.True(first.ExitCode == 0, first.Stderr + first.Stdout);
        Assert.Contains("cache    : expiry 1D, per epoch", first.Stdout);
        var firstEvents = ReadSingleLog(Path.Combine(_logDir, "1")).Samples![0].Events.OfType<ModelEvent>().ToList();
        Assert.NotEmpty(firstEvents);
        Assert.All(firstEvents, e => Assert.Equal(CacheMode.Write, e.Cache));

        var second = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", Path.Combine(_logDir, "2"), "--cache", "1D");
        Assert.True(second.ExitCode == 0, second.Stderr + second.Stdout);
        var log = ReadSingleLog(Path.Combine(_logDir, "2"));
        var secondEvents = log.Samples![0].Events.OfType<ModelEvent>().ToList();
        Assert.Equal(firstEvents.Count, secondEvents.Count);
        Assert.All(secondEvents, e => Assert.Equal(CacheMode.Read, e.Cache));
        Assert.Equal("C", log.Samples[0].Scores!["exec_check"].Text);

        var show = await RunAsync("show", log.Location!);
        Assert.Contains($"{secondEvents.Count} cache hits", show.Stdout);
    }

    [Python3Fact]
    public async Task compaction_is_accepted_for_the_native_loops()
    {
        var mini = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "mini-swe", "--limit", "1", "--log-dir", Path.Combine(_logDir, "mini"), "--compaction", "edit:0.5");
        Assert.True(mini.ExitCode == 0, mini.Stderr + mini.Stdout);
        Assert.Contains("compact  : edit:0.5", mini.Stdout);
        Assert.Equal("C", ReadSingleLog(Path.Combine(_logDir, "mini")).Samples![0].Scores!["exec_check"].Text);

        var basic = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "basic", "--limit", "1", "--log-dir", Path.Combine(_logDir, "basic"), "--compaction", "trim");
        Assert.True(basic.ExitCode == 0, basic.Stderr + basic.Stdout);
        Assert.Equal("C", ReadSingleLog(Path.Combine(_logDir, "basic")).Samples![0].Scores!["exec_check"].Text);
    }

    [Fact]
    public async Task compaction_is_refused_for_maf()
    {
        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "maf", "--log-dir", _logDir, "--compaction", "edit");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--compaction does not apply to maf", run.Stderr);
        Assert.False(Directory.Exists(_logDir));
    }

    [Fact]
    public async Task compaction_is_refused_for_claude_code()
    {
        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "claude-code", "--log-dir", _logDir, "--compaction", "edit");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--compaction does not apply to claude-code", run.Stderr);
        Assert.False(Directory.Exists(_logDir));
    }

    [Fact]
    public async Task sample_log_hooks_print_the_lifecycle()
    {
        var run = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", _logDir, "--hooks", "sample-log");

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        Assert.Contains("hooks    : sample-log", run.Stdout);
        Assert.Contains("[hook] run ", run.Stdout);
        Assert.Contains("[hook] task system-explorer start: model scripted, 2 samples, plan solver", run.Stdout);
        Assert.Contains("[hook] sample 1 (epoch 1) init", run.Stdout);
        Assert.Contains("[hook] sample 1 (epoch 1) start", run.Stdout);
        Assert.Contains("[hook] model scripted: ", run.Stdout);
        Assert.Contains("scoring", run.Stdout);
        Assert.Matches(@"\[hook\] sample 1 \(epoch 1\) end: model_graded_qa=C \(\d+ tokens, \d+ events\)", run.Stdout);
        Assert.Contains("[hook] task system-explorer end: success", run.Stdout);
        Assert.Matches(@"\[hook\] run \S+ end: 1 log\(s\)", run.Stdout);
    }

    [Fact]
    public async Task sample_log_hooks_can_write_to_a_file()
    {
        var file = Path.Combine(_dir, "hooks", "sample.log");

        var run = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", _logDir, "--hooks", $"sample-log={file}");

        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        Assert.DoesNotContain("[hook]", run.Stdout);
        var lines = await File.ReadAllLinesAsync(file);
        Assert.All(lines, line => Assert.StartsWith("[hook] ", line));
        Assert.Contains(lines, line => line.Contains("sample 1 (epoch 1) end: model_graded_qa=C", StringComparison.Ordinal));
    }

    [Fact]
    public async Task log_format_json_writes_json_and_show_reads_either_format()
    {
        var json = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", _logDir, "--log-format", "json");
        Assert.True(json.ExitCode == 0, json.Stderr + json.Stdout);
        Assert.Contains("log fmt  : json", json.Stdout);
        var jsonPath = Assert.Single(Directory.GetFiles(_logDir));
        Assert.EndsWith(".json", jsonPath);
        var showJson = await RunAsync("show", jsonPath);
        Assert.Equal(0, showJson.ExitCode);
        Assert.Contains("format   : json", showJson.Stdout);
        Assert.Contains("1 (epoch 1): model_graded_qa=C", showJson.Stdout);

        var eval = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", _logDir);
        Assert.True(eval.ExitCode == 0, eval.Stderr + eval.Stdout);
        Assert.Contains("log fmt  : eval", eval.Stdout);
        var evalPath = Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
        var showEval = await RunAsync("show", evalPath);
        Assert.Equal(0, showEval.ExitCode);
        Assert.Contains("format   : eval", showEval.Stdout);
        Assert.Contains("1 (epoch 1): model_graded_qa=C", showEval.Stdout);
        Assert.Equal(LogFormat.Eval, LogFormats.ForLocation(evalPath));

        var bogus = Path.Combine(_dir, "bogus.eval");
        await File.WriteAllTextAsync(bogus, "not a zip");
        var showBogus = await RunAsync("show", bogus);
        Assert.Equal(2, showBogus.ExitCode);
        Assert.Contains("is not an eval log", showBogus.Stderr);
    }

    [Fact]
    public async Task a_price_file_prices_the_run_and_a_cost_limit_trips()
    {
        var prices = Path.Combine(_dir, "prices.json");
        await File.WriteAllTextAsync(prices, """{ "scripted": { "input": 10.0, "output": 30.0, "input_cache_write": 0, "input_cache_read": 0 } }""");

        var priced = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", Path.Combine(_logDir, "priced"), "--model-cost-config", prices);
        Assert.True(priced.ExitCode == 0, priced.Stderr + priced.Stdout);
        Assert.Contains($"pricing  : {prices}", priced.Stdout);
        Assert.Matches(@"cost     : \$0\.\d{6}", priced.Stdout);
        var log = ReadSingleLog(Path.Combine(_logDir, "priced"));
        var cost = log.Stats.ModelUsage["scripted"].TotalCost;
        Assert.True(cost > 0, $"cost was {cost}");
        var show = await RunAsync("show", log.Location!);
        Assert.Matches(@"1 \(epoch 1\): model_graded_qa=C \| \d+ tokens \| \$0\.\d{6} \|", show.Stdout);

        var limited = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", Path.Combine(_logDir, "limited"), "--model-cost-config", prices, "--cost-limit", "0.0000001");
        Assert.True(limited.ExitCode == 0, limited.Stderr + limited.Stdout);
        Assert.Contains("cost lim : $0.0000001 per sample", limited.Stdout);
        var sample = Assert.Single(ReadSingleLog(Path.Combine(_logDir, "limited")).Samples!);
        Assert.Equal("cost", sample.Limit!.Type);
    }

    [Fact]
    public async Task a_cost_limit_without_pricing_is_a_prerequisite_error()
    {
        ModelInfoLookup.ClearModelInfoCache();

        var run = await RunAsync("run", "--fake", "--task", "system-explorer", "--agent", "basic", "--limit", "1", "--log-dir", _logDir, "--cost-limit", "1");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("cost", run.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ModelCostConfig.EnvironmentVariable, run.Stderr);
    }

    [Theory]
    [InlineData("--cache", "soon")]
    [InlineData("--compaction", "squash")]
    [InlineData("--compaction", "edit:2.5")]
    [InlineData("--compaction", "edit:")]
    [InlineData("--hooks", "nope")]
    [InlineData("--hooks", "sample-log=")]
    [InlineData("--approval", "/nonexistent/policy.json")]
    [InlineData("--cost-limit", "-1")]
    [InlineData("--log-format", "xml")]
    [InlineData("--model-cost-config", "/nonexistent/prices.json")]
    [InlineData("--model-cost-config", "malformed-prices.json")]
    public async Task bad_values_of_the_new_flags_are_usage_errors(string flag, string value)
    {
        if (value == "malformed-prices.json")
        {
            value = Path.Combine(_dir, value);
            await File.WriteAllTextAsync(value, """{ "scripted": { "input": 1 } }""");
        }

        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "basic", "--log-dir", _logDir, flag, value);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(flag, run.Stderr);
        Assert.Contains("usage:", run.Stderr);
        Assert.False(Directory.Exists(_logDir));
    }

    [Fact]
    public async Task a_malformed_policy_file_is_a_usage_error()
    {
        var policy = Path.Combine(_dir, "policy.json");
        await File.WriteAllTextAsync(policy, """{"approvers": [{"name": "nope", "tools": "*"}]}""");

        var run = await RunAsync("run", "--fake", "--task", "hello-swe", "--agent", "basic", "--log-dir", _logDir, "--approval", policy);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--approval", run.Stderr);
    }

    private async Task<string> WritePolicyAsync(string tools, string decision)
    {
        var path = Path.Combine(_dir, $"{decision}.json");
        await File.WriteAllTextAsync(path, $$"""{"approvers": [{"name": "auto", "tools": "{{tools}}", "decision": "{{decision}}"}, {"name": "auto", "tools": "*", "decision": "approve"}]}""");
        return path;
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

    private EvalLog ReadSingleLog(string? logDir = null) => EvalLogWriter.Read(Assert.Single(Directory.GetFiles(logDir ?? _logDir, "*.eval")));

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr);

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;

        private readonly string? _previous;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
