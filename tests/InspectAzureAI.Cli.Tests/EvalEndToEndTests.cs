using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Cli.Models;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>End-to-end runs through <see cref="InspectCli.RunAsync"/> with a <c>scripted/*</c> model provider over <see cref="ScriptedModelApi"/>, writing real <c>.eval</c> logs into a temp directory.</summary>
public sealed class EvalEndToEndTests : IDisposable
{
    /// <summary>When set, <c>scripted/phased</c> fails every generate; cleared, it answers <c>ok</c>. Lets a failed run be retried.</summary>
    private static volatile bool _phasedFails;

    private readonly string _dir = Directory.CreateTempSubdirectory("inspectai-e2e").FullName;

    static EvalEndToEndTests()
    {
        ModelProviders.Register("scripted", spec => new Model(ScriptedApi(spec), spec.Config));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task eval_writes_an_eval_log_and_prints_results()
    {
        var (code, output, error) = await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        var file = Assert.Single(Directory.GetFiles(_dir, "*.eval"));
        var log = EvalLogFiles.ReadEvalLog(file);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("hello", log.Eval.Task);
        Assert.Equal("scripted/quiz", log.Eval.Model);
        Assert.Equal(2, log.Samples!.Count);
        var score = Assert.Single(log.Results!.Scores);
        Assert.Equal("includes", score.Name);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
        Assert.Contains("Results for hello (scripted/quiz)", output);
        Assert.Contains("accuracy: 1", output);
        Assert.Contains("sample", output);
        Assert.Contains($"Log: {file}", output);
    }

    [Fact]
    public async Task eval_json_format_task_args_and_quiet_display()
    {
        var (code, output, _) = await Run("eval", "parametrized", "--model", "scripted/quiz", "--log-dir", _dir, "--log-format", "json", "-T", "count=3", "-T", "target=ok", "-T", "tags=a,b", "--display", "none");
        Assert.Equal(0, code);
        var file = Assert.Single(Directory.GetFiles(_dir, "*.json"));
        var log = EvalLogFiles.ReadEvalLog(file);
        Assert.Equal(3, log.Samples!.Count);
        Assert.Equal("3", JsonSerializer.Serialize(log.Eval.TaskArgs["count"]));
        Assert.Equal("[\"a\",\"b\"]", JsonSerializer.Serialize(log.Eval.TaskArgs["tags"]));
        Assert.DoesNotContain("sample 1", output);
        Assert.Contains("Results for parametrized", output);
    }

    [Fact]
    public async Task eval_limit_epochs_reducer_and_sample_id()
    {
        var limited = Path.Combine(_dir, "limited");
        Assert.Equal(0, (await Run("eval", "parametrized", "--model", "scripted/quiz", "--log-dir", limited, "--limit", "1", "--display", "none")).Code);
        Assert.Single(EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(limited))).Samples!);

        var epochs = Path.Combine(_dir, "epochs");
        Assert.Equal(0, (await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", epochs, "--epochs", "2", "--epochs-reducer", "max", "--display", "none")).Code);
        var epochLog = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(epochs)));
        Assert.Equal(4, epochLog.Samples!.Count);
        Assert.Equal(2, epochLog.Eval.Config.Epochs);
        Assert.Equal("max", Assert.Single(epochLog.Results!.Scores).Reducer);

        var byId = Path.Combine(_dir, "by-id");
        Assert.Equal(0, (await Run("eval", "parametrized", "--model", "scripted/quiz", "--log-dir", byId, "-T", "count=3", "--sample-id", "2", "--display", "none")).Code);
        var idLog = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(byId)));
        Assert.Equal("2", Assert.Single(idLog.Samples!).Id.ToString());

        var (code, _, error) = await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir, "--limit", "1-2");
        Assert.Equal(2, code);
        Assert.Contains("--limit ranges", error);
    }

    [Fact]
    public async Task eval_with_sample_errors_exits_1_and_records_the_error()
    {
        var (code, output, _) = await Run("eval", "hello", "--model", "scripted/failing", "--log-dir", _dir);
        Assert.Equal(1, code);
        var log = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Equal(EvalStatus.Error, log.Status);
        Assert.Contains(log.Samples!, sample => sample.Error is not null);
        Assert.Contains("status: error", output);

        var tolerant = Path.Combine(_dir, "tolerant");
        (code, _, _) = await Run("eval", "hello", "--model", "scripted/failing", "--log-dir", tolerant, "--no-fail-on-error", "--display", "none");
        Assert.Equal(0, code);
        Assert.Equal(EvalStatus.Success, EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(tolerant))).Status);
    }

    [Fact]
    public async Task eval_with_the_builtin_mockllm_provider()
    {
        var (code, _, _) = await Run("eval", "hello", "--model", "mockllm/model", "--log-dir", _dir, "--display", "none");
        Assert.Equal(0, code);
        var log = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Equal("mockllm/model", log.Eval.Model);
        Assert.Equal(MockLlmModelApi.DefaultOutput, log.Samples![0].Output.Completion);
        Assert.Equal(0.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);

        var scripted = Path.Combine(_dir, "scripted");
        (code, _, _) = await Run("eval", "hello", "--model", "mockllm/model", "-M", "custom_outputs=[ok, ok]", "--log-dir", scripted, "--display", "none");
        Assert.Equal(0, code);
        Assert.Equal(1.0, Assert.Single(EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(scripted))).Results!.Scores).Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task eval_reports_unknown_tasks_models_and_arguments_as_exit_2()
    {
        var (code, _, error) = await Run("eval", "nosuch", "--model", "scripted/quiz", "--log-dir", _dir);
        Assert.Equal(2, code);
        Assert.Contains("Task 'nosuch' not found", error);

        (code, _, error) = await Run("eval", "hello", "--model", "unknown/x", "--log-dir", _dir);
        Assert.Equal(2, code);
        Assert.Contains("Unknown model provider 'unknown'", error);

        (code, _, error) = await Run("eval", "parametrized", "--model", "scripted/quiz", "--log-dir", _dir, "-T", "bogus=1");
        Assert.Equal(2, code);
        Assert.Contains("Unknown argument(s) for task 'parametrized': bogus", error);

        (code, _, error) = await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir, "--solver", "nope");
        Assert.Equal(2, code);
        Assert.Contains("Solver 'nope' not found", error);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task eval_honours_solver_scorer_hooks_and_cache_options()
    {
        var before = CountingHook.TaskStarts;
        var (code, _, _) = await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir, "--solver", "system_message", "-S", "template=Be brief", "--hooks", nameof(CountingHook), "--display", "none");
        Assert.Equal(0, code);
        Assert.True(CountingHook.TaskStarts > before);
        var log = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Contains(log.Samples![0].Messages, message => message is ChatMessageSystem system && system.Text == "Be brief");

        var options = new Commands.EvalOptionSet();
        var command = new System.CommandLine.Command("eval");
        options.AddTo(command, evalSet: false);
        var parsed = command.Parse(["hello", "--model", "scripted/quiz", "--cache", "3", "--max-connections", "2", "--temperature", "0.5", "--log-dir", _dir]);
        var plan = Commands.EvalCommands.Plan.Build(options, parsed, new CliIo(new StringWriter(), new StringWriter()));
        var model = Assert.Single(plan.Models);
        var cache = Assert.IsType<Eval.Model.Cache.CachePolicy>(model.Config.Cache);
        Assert.Equal("3D", cache.Expiry);
        Assert.Equal(2, model.Config.MaxConnections);
        Assert.Equal(0.5, model.Config.Temperature);
        Assert.Equal("hello", Assert.Single(plan.Tasks).Name);
    }

    [Fact]
    public async Task log_commands_read_the_written_log()
    {
        Assert.Equal(0, (await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir, "--display", "none")).Code);
        var file = Assert.Single(Directory.GetFiles(_dir, "*.eval"));

        var (code, output, _) = await Run("log", "list", "--log-dir", _dir);
        Assert.Equal(0, code);
        Assert.Equal(Path.GetRelativePath(Directory.GetCurrentDirectory(), file), output.Trim());

        (code, output, _) = await Run("log", "list", "--log-dir", _dir, "--json", "--absolute");
        Assert.Equal(0, code);
        var entry = Assert.Single(JsonNode.Parse(output)!.AsArray())!.AsObject();
        Assert.Equal(file, (string?)entry["name"]);
        Assert.Equal("hello", (string?)entry["task"]);

        (code, output, _) = await Run("list", "logs", "--log-dir", _dir, "--status", "error");
        Assert.Equal(0, code);
        Assert.Equal("", output.Trim());

        (code, output, _) = await Run("log", "dump", file, "--header-only");
        Assert.Equal(0, code);
        var header = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("hello", (string?)header["eval"]!["task"]);
        Assert.Null(header["samples"]);

        (code, output, _) = await Run("log", "dump", file);
        Assert.Equal(0, code);
        Assert.Equal(2, JsonNode.Parse(output)!["samples"]!.AsArray().Count);

        (code, output, _) = await Run("log", "headers", file, file);
        Assert.Equal(0, code);
        Assert.Equal(2, JsonNode.Parse(output)!.AsArray().Count);

        (code, output, _) = await Run("info", "log-file", file, "--header-only");
        Assert.Equal(0, code);
        Assert.Null(JsonNode.Parse(output)!["samples"]);

        var converted = Path.Combine(_dir, "converted");
        (code, _, _) = await Run("log", "convert", file, "--to", "json", "--output-dir", converted);
        Assert.Equal(0, code);
        var json = Assert.Single(Directory.GetFiles(converted, "*.json"));
        Assert.Equal(2, EvalLogFiles.ReadEvalLog(json).Samples!.Count);

        string error;
        (code, _, error) = await Run("log", "convert", file, "--to", "json", "--output-dir", converted);
        Assert.Equal(2, code);
        Assert.Contains("already exists", error);
        Assert.Equal(0, (await Run("log", "convert", file, "--to", "json", "--output-dir", converted, "--overwrite")).Code);

        var roundTrip = Path.Combine(_dir, "round-trip");
        (code, output, _) = await Run("log", "convert", converted, "--to", "eval", "--output-dir", roundTrip);
        Assert.Equal(0, code);
        Assert.Contains("Converting log files...", output);
        Assert.Single(Directory.GetFiles(roundTrip, "*.eval"));

        (code, _, error) = await Run("log", "dump", Path.Combine(_dir, "missing.eval"));
        Assert.Equal(2, code);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public async Task score_rescoring_a_log()
    {
        Assert.Equal(0, (await Run("eval", "hello", "--model", "scripted/quiz", "--log-dir", _dir, "--display", "none")).Code);
        var file = Assert.Single(Directory.GetFiles(_dir, "*.eval"));

        var (code, output, _) = await Run("score", file, "--scorer", "exact", "-S", "ignore_case=false", "--action", "overwrite", "--overwrite");
        Assert.Equal(0, code);
        Assert.Contains("Results for hello", output);
        var overwritten = EvalLogFiles.ReadEvalLog(file);
        var score = Assert.Single(overwritten.Results!.Scores);
        Assert.Equal("exact_match", score.Name);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);

        (code, output, _) = await Run("score", file, "--scorer", "includes", "--metric", "mean");
        Assert.Equal(0, code);
        var scoredFile = Assert.Single(Directory.GetFiles(_dir, "*-scored.eval"));
        Assert.Contains($"Log: {scoredFile}", output);
        Assert.Equal(2, EvalLogFiles.ReadEvalLog(scoredFile).Results!.Scores.Count);
        Assert.Single(EvalLogFiles.ReadEvalLog(file).Results!.Scores);

        (code, _, _) = await Run("score", file, "--overwrite", "--action", "overwrite");
        Assert.Equal(0, code);
        Assert.Equal("exact_match", Assert.Single(EvalLogFiles.ReadEvalLog(file).Results!.Scores).Name);

        string error;
        (code, _, error) = await Run("score", file, "--scorer", "nope");
        Assert.Equal(2, code);
        Assert.Contains("Scorer 'nope' not found", error);

        (code, _, error) = await Run("score", file, "--stream");
        Assert.Equal(2, code);
        Assert.Contains("--stream is not supported", error);

        (code, _, error) = await Run("score", Path.Combine(_dir, "missing.eval"));
        Assert.Equal(2, code);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public async Task eval_retry_reruns_a_failed_log()
    {
        _phasedFails = true;
        var (code, _, _) = await Run("eval", "hello", "--model", "scripted/phased", "--log-dir", _dir, "--display", "none");
        Assert.Equal(1, code);
        var failed = Assert.Single(Directory.GetFiles(_dir, "*.eval"));
        Assert.Equal(EvalStatus.Error, EvalLogFiles.ReadEvalLog(failed).Status);

        _phasedFails = false;
        var retryDir = Path.Combine(_dir, "retry");
        string output;
        (code, output, _) = await Run("eval-retry", failed, "--log-dir", retryDir, "--display", "none");
        Assert.Equal(0, code);
        var retried = EvalLogFiles.ReadEvalLog(Assert.Single(Directory.GetFiles(retryDir, "*.eval")));
        Assert.Equal(EvalStatus.Success, retried.Status);
        Assert.Equal(2, retried.Samples!.Count);
        Assert.All(retried.Samples, sample => Assert.Null(sample.Error));
        Assert.Contains("Results for hello", output);

        string error;
        (code, _, error) = await Run("eval-retry", Path.Combine(_dir, "missing.eval"));
        Assert.Equal(2, code);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public async Task eval_set_runs_every_task_and_reports_success()
    {
        var setDir = Path.Combine(_dir, "set");
        var (code, output, _) = await Run("eval-set", "hello", "parametrized", "--model", "scripted/quiz", "--log-dir", setDir, "--display", "none", "--retry-attempts", "1");
        Assert.Equal(0, code);
        Assert.Contains("Results for hello", output);
        Assert.Contains("Results for parametrized", output);
        var logs = Directory.GetFiles(setDir, "*.eval");
        Assert.Equal(2, logs.Length);
        Assert.All(logs, log => Assert.Equal(EvalStatus.Success, EvalLogFiles.ReadEvalLog(log, headerOnly: true).Status));

        var failingDir = Path.Combine(_dir, "failing-set");
        (code, _, _) = await Run("eval-set", "hello", "--model", "scripted/failing", "--log-dir", failingDir, "--display", "none", "--retry-attempts", "1", "--retry-wait", "0");
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task view_delegates_to_python_inspect_or_explains_how_to_install_it()
    {
        var (code, _, error) = await Run("view", "--log-dir", _dir);
        Assert.Equal(2, code);
        Assert.Contains("pip install inspect-ai", error);
        Assert.Contains($"inspect view --log-dir {_dir}", error);

        IReadOnlyList<string>? launched = null;
        string? executable = null;
        var services = new CliServices
        {
            FindOnPath = name => name == "inspect" ? "/opt/bin/inspect" : null,
            RunProcessAsync = (file, arguments, _) =>
            {
                executable = file;
                launched = arguments;
                return Task.FromResult(7);
            },
        };
        code = await InspectCli.RunAsync(["view", "--log-dir", _dir, "--port", "7575"], new CliIo(new StringWriter(), new StringWriter()), services);
        Assert.Equal(7, code);
        Assert.Equal("/opt/bin/inspect", executable);
        Assert.Equal(["view", "--log-dir", _dir, "--port", "7575"], launched);
    }

    [Fact]
    public async Task cache_commands_report_the_cache_directory()
    {
        var (code, output, _) = await Run("cache", "path");
        Assert.Equal(0, code);
        Assert.EndsWith("generate", output.Trim());

        (code, output, _) = await Run("cache", "list");
        Assert.Equal(0, code);
        Assert.Contains("Cache Sizes", output);
        Assert.Contains("Model", output);

        (code, output, _) = await Run("cache", "list", "--pruneable");
        Assert.Equal(0, code);
        Assert.True(output.Contains("No expired cache entries.", StringComparison.Ordinal) || output.Contains("can be pruned", StringComparison.Ordinal));
    }

    private static ScriptedModelApi ScriptedApi(ModelSpec spec)
    {
        const int turns = 64;
        return spec.ModelName switch
        {
            "quiz" => new ScriptedModelApi(Enumerable.Range(0, turns).Select(_ => ScriptedTurn.Text("ok", new ModelUsage(1, 1, 2))), spec.Name),
            "failing" => new ScriptedModelApi(Enumerable.Range(0, turns).Select(_ => ScriptedTurn.Throw(new InvalidOperationException("scripted failure"))), spec.Name),
            "phased" => new ScriptedModelApi(
                Enumerable.Range(0, turns).Select(_ => ScriptedTurn.From((_, _) => _phasedFails
                    ? throw new InvalidOperationException("phased failure")
                    : ModelOutput.FromContent(spec.Name, "ok") with { Usage = new ModelUsage(1, 1, 2) })),
                spec.Name),
            _ => throw new PrerequisiteError($"Unknown scripted model '{spec.ModelName}'."),
        };
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await InspectCli.RunAsync(args, new CliIo(output, error), new CliServices { FindOnPath = _ => null });
        return (code, output.ToString(), error.ToString());
    }
}
