using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.ModelMatrix.Tests;

/// <summary>
/// The matrix on eval sets, offline: every deployment is its own eval set under the log directory, a second run
/// reuses complete logs, an incomplete log is resumed with its completed samples, prices turn into a cost column,
/// throughput is reported, hooks are prefixed per deployment, and the new flags parse.
/// </summary>
public sealed class MatrixWiringTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("model-matrix-wiring-");

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Logs => Path.Combine(_dir.FullName, "logs");

    private async Task<(int Exit, string Console, MatrixReport Report)> RunAsync(string outName, params string[] extra)
    {
        var output = new StringWriter();
        var outPath = Path.Combine(_dir.FullName, outName);
        var exit = await MatrixCli.RunAsync(["--fake", "--task", "hello-swe", "--log-dir", Logs, "--out", outPath, .. extra], output, CancellationToken.None);
        var console = output.ToString();
        Assert.True(File.Exists(outPath), console);
        return (exit, console, MatrixReport.FromJson(await File.ReadAllTextAsync(outPath)));
    }

    [Fact]
    public async Task each_deployment_is_an_eval_set_and_a_second_run_reuses_its_complete_log()
    {
        ModelInfoLookup.ClearModelInfoCache();   // prices registered by another test are process-wide
        var (exit1, console1, first) = await RunAsync("m1.json");
        Assert.True(exit1 == 0, console1);
        Assert.Contains("fake-gpt: ok accuracy 1.000", console1);
        foreach (var name in new[] { "fake-gpt", "fake-claude" })
        {
            var dir = MatrixRunner.DeploymentLogDir(Logs, name);
            Assert.True(File.Exists(Path.Combine(dir, "eval-set.json")), dir);
            var log = Assert.Single(EvalSetLogs.ListAllEvalLogs(dir));
            Assert.Equal(name, log.Header.Eval.Model);   // one fake model per deployment, so the identifiers differ
            Assert.NotNull(log.Header.Eval.EvalSetId);
        }

        var gpt = first.Rows.Single(r => r.Deployment == "fake-gpt");
        Assert.False(gpt.Reused);
        Assert.True(gpt.TokensPerSecond > 0, $"tok/s was {gpt.TokensPerSecond}");
        Assert.Null(gpt.Cost);
        Assert.StartsWith(MatrixRunner.DeploymentLogDir(Logs, "fake-gpt"), gpt.Log);
        Assert.Equal(2, ((System.Text.Json.JsonElement)first.Config["retryAttempts"]!).GetInt32());

        var (exit2, console2, second) = await RunAsync("m2.json");
        Assert.True(exit2 == 0, console2);
        Assert.Contains("fake-gpt: reused ok accuracy 1.000", console2);
        Assert.Contains("fake-claude: reused ok", console2);
        var reused = second.Rows.Single(r => r.Deployment == "fake-gpt");
        Assert.True(reused.Reused);
        Assert.Equal("reused", reused.Note);
        Assert.Equal(MatrixRow.Ok, reused.Status);
        Assert.Equal(gpt.Tokens, reused.Tokens);
        Assert.Equal(gpt.Log, reused.Log);
        Assert.Single(reused.Samples);
        Assert.Single(Directory.GetFiles(MatrixRunner.DeploymentLogDir(Logs, "fake-gpt"), "*.eval"));
        Assert.Contains("| reused |", second.RenderMarkdown());
        Assert.Contains("2 run (2 ok,", console2);
    }

    [Fact]
    public async Task an_incomplete_log_is_resumed_under_the_same_task_id()
    {
        var (exit1, console1, first) = await RunAsync("m1.json", "--only", "fake-gpt");
        Assert.True(exit1 == 0, console1);
        var path = first.Rows.Single().Log!;
        var previous = EvalLogWriter.Read(path);
        var previousUuid = Assert.Single(previous.Samples!).Uuid;   // .eval samples load lazily; read them before the resume cleans the file up
        // an errored log whose sample completed: the eval set re-runs the task reusing that sample instead of starting over
        EvalLogFiles.WriteEvalLog(previous with { Status = EvalStatus.Error, Error = new EvalError("boom") }, path);

        var (exit2, console2, second) = await RunAsync("m2.json", "--only", "fake-gpt");
        Assert.True(exit2 == 0, console2);
        Assert.DoesNotContain("reused", console2);
        var row = second.Rows.Single();
        Assert.Equal(MatrixRow.Ok, row.Status);
        Assert.False(row.Reused);
        // Python names logs {created}_{task}_{task_id} at second resolution, so a resume within the same second lands on
        // the failed log's own path (the .eval recorder resumes into it) rather than a new file; assert the name, not distinctness.
        Assert.EndsWith("_" + previous.Eval.TaskId + ".eval", row.Log);
        var resumed = EvalLogWriter.Read(row.Log!);
        Assert.Equal(previous.Eval.TaskId, resumed.Eval.TaskId);
        Assert.Equal(previous.Eval.EvalSetId, resumed.Eval.EvalSetId);
        Assert.Equal(previousUuid, Assert.Single(resumed.Samples!).Uuid);   // the completed sample was reused as logged
        Assert.Single(Directory.GetFiles(MatrixRunner.DeploymentLogDir(Logs, "fake-gpt"), "*.eval"));   // retry_cleanup removed the superseded log
    }

    [Fact]
    public async Task a_price_file_adds_cost_per_deployment_and_the_summary()
    {
        var prices = Path.Combine(_dir.FullName, "prices.json");
        await File.WriteAllTextAsync(prices, """{ "fake-gpt": { "input": 10, "output": 30, "input_cache_write": 0, "input_cache_read": 0 }, "fake-claude": { "input": 20, "output": 60, "input_cache_write": 0, "input_cache_read": 0 } }""");
        var markdown = Path.Combine(_dir.FullName, "m.md");

        var (exit, console, report) = await RunAsync("m.json", "--model-cost-config", prices, "--markdown", markdown);

        Assert.True(exit == 0, console);
        var gpt = report.Rows.Single(r => r.Deployment == "fake-gpt");
        var claude = report.Rows.Single(r => r.Deployment == "fake-claude");
        Assert.True(gpt.Cost > 0, $"cost was {gpt.Cost}");
        Assert.True(claude.Cost > gpt.Cost, "claude is priced at twice the rate");
        Assert.Equal(gpt.Samples.Sum(s => s.Cost ?? 0), gpt.Cost!.Value, 12);
        Assert.NotNull(report.Summary.Cost);
        Assert.NotNull(report.Summary.TokensPerSecond);
        Assert.Matches(@"fake-gpt: ok accuracy 1\.000 \(\d+ tokens, \$0\.\d{4}, \d+\.\ds\)", console);
        Assert.Matches(@"2 skipped; \d+ tokens, \$0\.\d{4}, \d+ tok/s", console);
        var table = await File.ReadAllTextAsync(markdown);
        Assert.Contains("| deployment | format | route | status | accuracy | tokens | cost | tok/s | time | note |", table);
        Assert.Matches(@"\| fake-gpt \| OpenAI \| models \| ok \| 1\.000 \| \d+ \| \$0\.\d{4} \| \d+ \| \d+\.\ds \|", table);
        Assert.Contains("\"cost\":", report.ToJson());
        Assert.Contains("\"modelCostConfig\":", report.ToJson());
    }

    [Fact]
    public async Task hook_lines_are_prefixed_with_the_deployment()
    {
        var (exit, console, _) = await RunAsync("m.json", "--only", "fake-gpt", "--hooks", "sample-log", "--cache", "off", "--log-format", "eval");

        Assert.True(exit == 0, console);
        Assert.Contains("fake-gpt: [hook] sample 1 (epoch 1) end: exec_check=C", console);
        Assert.Contains("fake-gpt: [hook] task hello-swe end: success", console);
    }

    [Fact]
    public async Task compaction_is_refused_for_claude_code_and_new_flags_parse()
    {
        var output = new StringWriter();
        Assert.Equal(2, await MatrixCli.RunAsync(["--fake", "--agent", "claude-code", "--compaction", "edit", "--log-dir", Logs], output, CancellationToken.None));

        var options = MatrixOptions.Parse(["--retry-attempts", "3", "--cache", "3D", "--compaction", "edit:0.5", "--hooks", "sample-log", "--cost-limit", "0.5", "--log-format", "json"]);
        Assert.Equal(3, options.RetryAttempts);
        Assert.Equal("3D", options.Run.Cache!.Expiry);
        Assert.Equal("edit:0.5", options.Run.Compaction!.ToString());
        Assert.Equal(["sample-log"], options.Run.Hooks.Select(h => h.ToString()));
        Assert.Equal(0.5, options.Run.CostLimit);
        Assert.Equal(LogFormat.Json, options.Run.LogFormat);
        Assert.Equal(MatrixOptions.DefaultRetryAttempts, MatrixOptions.Parse([]).RetryAttempts);
        Assert.Throws<UsageError>(() => MatrixOptions.Parse(["--retry-attempts", "-1"]));
        Assert.Throws<UsageError>(() => MatrixOptions.Parse(["--retry-attempts", "many"]));
    }

    [Theory]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("my/dep:1", "my_dep_1")]
    [InlineData("", "_")]
    public void deployment_log_dirs_are_path_safe(string deployment, string expected)
    {
        Assert.Equal(Path.Combine("logs", expected), MatrixRunner.DeploymentLogDir("logs", deployment));
    }
}
