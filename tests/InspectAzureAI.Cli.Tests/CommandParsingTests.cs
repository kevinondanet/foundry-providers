using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;
using InspectAzureAI.Cli.Commands;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;

namespace InspectAzureAI.Cli.Tests;

/// <summary>Argument parsing for every command: the bound values, click's bare-flag-or-value options, choices, and the exit-code contract for usage errors.</summary>
public class CommandParsingTests
{
    private static ParseResult Parse(params string[] args) => InspectCli.Build(new CliIo(new StringWriter(), new StringWriter())).Parse(args);

    private static bool Specified(ParseResult result, string option) => result.GetResult(option) is OptionResult { Implicit: false };

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await InspectCli.RunAsync(args, new CliIo(output, error), new CliServices { FindOnPath = _ => null });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void env_option_sets_the_raw_value_and_an_empty_value_unsets_the_variable()
    {
        var name = "INSPECTAI_TEST_ENV_" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new CommonOptions();
            var command = new RootCommand();
            options.AddTo(command);
            options.Process(command.Parse(["--env", $"{name}=/a,/b", "--env", $"{name}_FLAG=true"]));
            Assert.Equal("/a,/b", Environment.GetEnvironmentVariable(name));
            Assert.Equal("true", Environment.GetEnvironmentVariable($"{name}_FLAG"));

            options.Process(command.Parse(["--env", $"{name}="]));
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable($"{name}_FLAG", null);
        }
    }

    [Fact]
    public void eval_binds_every_option()
    {
        var result = Parse(
            "eval", "hello", "other",
            "--assembly", "tasks.dll", "--model", "mockllm/model", "--model-base-url", "http://x", "-M", "a=1", "-M", "b=2", "--model-config", "m.yaml",
            "--model-role", "grader=mockllm/model", "-T", "count=3", "-T", "target=yes", "--task-config", "t.yaml", "--solver", "generate", "-S", "x=1", "--solver-config", "s.yaml",
            "--approval", "approval.yaml", "--hooks", "h1,h2", "--sandbox", "docker:compose.yml", "--no-sandbox-cleanup", "--limit", "10", "--sample-id", "1,2",
            "--epochs", "3", "--epochs-reducer", "mean,max", "--max-connections", "4", "--adaptive-connections", "2-8", "--max-retries", "5", "--timeout", "60",
            "--attempt-timeout", "9", "--stream-idle-timeout", "8", "--max-samples", "2", "--max-tasks", "3", "--message-limit", "20", "--token-limit", "1000", "--turn-limit", "7",
            "--cost-limit", "1.5", "--model-cost-config", "cost.yaml", "--time-limit", "30", "--working-limit", "25", "--fail-on-error", "0.5", "--continue-on-fail", "--retry-on-error", "3",
            "--generate-config", "g.yaml", "--max-tokens", "100", "--system-message", "sys", "--best-of", "2", "--frequency-penalty", "0.1", "--presence-penalty", "0.2",
            "--logit-bias", "42=10,43=-10", "--seed", "42", "--stop-seqs", "a,b", "--temperature", "0.2", "--top-p", "0.9", "--top-k", "5", "--num-choices", "2", "--logprobs",
            "--top-logprobs", "3", "--no-parallel-tool-calls", "--no-internal-tools", "--max-tool-output", "512", "--cache-prompt", "auto", "--fallback-models", "x,y",
            "--verbosity", "low", "--effort", "high", "--reasoning-effort", "medium", "--reasoning-mode", "pro", "--reasoning-tokens", "9", "--reasoning-summary", "auto",
            "--reasoning-history", "last", "--cache", "3", "--log-format", "json", "--log-dir", "./out/", "--display", "none", "--log-level", "info", "--env", "A=1");

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "hello", "other" }, result.GetValue<string[]>("tasks"));
        Assert.Equal(new[] { "tasks.dll" }, result.GetValue<string[]>("--assembly"));
        Assert.Equal("mockllm/model", result.GetValue<string?>("--model"));
        Assert.Equal("http://x", result.GetValue<string?>("--model-base-url"));
        Assert.Equal(new[] { "a=1", "b=2" }, result.GetValue<string[]>("-M"));
        Assert.Equal("m.yaml", result.GetValue<string?>("--model-config"));
        Assert.Equal(new[] { "grader=mockllm/model" }, result.GetValue<string[]>("--model-role"));
        Assert.Equal(new[] { "count=3", "target=yes" }, result.GetValue<string[]>("-T"));
        Assert.Equal("t.yaml", result.GetValue<string?>("--task-config"));
        Assert.Equal("generate", result.GetValue<string?>("--solver"));
        Assert.Equal(new[] { "x=1" }, result.GetValue<string[]>("-S"));
        Assert.Equal("s.yaml", result.GetValue<string?>("--solver-config"));
        Assert.Equal("approval.yaml", result.GetValue<string?>("--approval"));
        Assert.Equal(new[] { "h1,h2" }, result.GetValue<string[]>("--hooks"));
        Assert.Equal("docker:compose.yml", result.GetValue<string?>("--sandbox"));
        Assert.True(result.GetValue<bool>("--no-sandbox-cleanup"));
        Assert.Equal("10", result.GetValue<string?>("--limit"));
        Assert.Equal("1,2", result.GetValue<string?>("--sample-id"));
        Assert.Equal(3, result.GetValue<int?>("--epochs"));
        Assert.Equal("mean,max", result.GetValue<string?>("--epochs-reducer"));
        Assert.Equal(4, result.GetValue<int?>("--max-connections"));
        Assert.Equal("2-8", result.GetValue<string?>("--adaptive-connections"));
        Assert.Equal(5, result.GetValue<int?>("--max-retries"));
        Assert.Equal(60, result.GetValue<int?>("--timeout"));
        Assert.Equal(9, result.GetValue<int?>("--attempt-timeout"));
        Assert.Equal(8, result.GetValue<int?>("--stream-idle-timeout"));
        Assert.Equal(2, result.GetValue<int?>("--max-samples"));
        Assert.Equal(3, result.GetValue<int?>("--max-tasks"));
        Assert.Equal(20, result.GetValue<int?>("--message-limit"));
        Assert.Equal("1000", result.GetValue<string?>("--token-limit"));
        Assert.Equal(7, result.GetValue<int?>("--turn-limit"));
        Assert.Equal(1.5, result.GetValue<double?>("--cost-limit"));
        Assert.Equal("cost.yaml", result.GetValue<string?>("--model-cost-config"));
        Assert.Equal(30, result.GetValue<int?>("--time-limit"));
        Assert.Equal(25, result.GetValue<int?>("--working-limit"));
        Assert.Equal("0.5", result.GetValue<string?>("--fail-on-error"));
        Assert.True(result.GetValue<bool>("--continue-on-fail"));
        Assert.Equal("3", result.GetValue<string?>("--retry-on-error"));
        Assert.Equal("g.yaml", result.GetValue<string?>("--generate-config"));
        Assert.Equal(100, result.GetValue<int?>("--max-tokens"));
        Assert.Equal("sys", result.GetValue<string?>("--system-message"));
        Assert.Equal(2, result.GetValue<int?>("--best-of"));
        Assert.Equal(0.1, result.GetValue<double?>("--frequency-penalty"));
        Assert.Equal(0.2, result.GetValue<double?>("--presence-penalty"));
        Assert.Equal("42=10,43=-10", result.GetValue<string?>("--logit-bias"));
        Assert.Equal(42, result.GetValue<int?>("--seed"));
        Assert.Equal("a,b", result.GetValue<string?>("--stop-seqs"));
        Assert.Equal(0.2, result.GetValue<double?>("--temperature"));
        Assert.Equal(0.9, result.GetValue<double?>("--top-p"));
        Assert.Equal(5, result.GetValue<int?>("--top-k"));
        Assert.Equal(2, result.GetValue<int?>("--num-choices"));
        Assert.True(result.GetValue<bool>("--logprobs"));
        Assert.Equal(3, result.GetValue<int?>("--top-logprobs"));
        Assert.True(result.GetValue<bool>("--no-parallel-tool-calls"));
        Assert.True(result.GetValue<bool>("--no-internal-tools"));
        Assert.Equal(512, result.GetValue<int?>("--max-tool-output"));
        Assert.Equal("auto", result.GetValue<string?>("--cache-prompt"));
        Assert.Equal("x,y", result.GetValue<string?>("--fallback-models"));
        Assert.Equal("low", result.GetValue<string?>("--verbosity"));
        Assert.Equal("high", result.GetValue<string?>("--effort"));
        Assert.Equal("medium", result.GetValue<string?>("--reasoning-effort"));
        Assert.Equal("pro", result.GetValue<string?>("--reasoning-mode"));
        Assert.Equal(9, result.GetValue<int?>("--reasoning-tokens"));
        Assert.Equal("auto", result.GetValue<string?>("--reasoning-summary"));
        Assert.Equal("last", result.GetValue<string?>("--reasoning-history"));
        Assert.Equal("3", result.GetValue<string?>("--cache"));
        Assert.Equal("json", result.GetValue<string?>("--log-format"));
        Assert.Equal("./out/", result.GetValue<string?>("--log-dir"));
        Assert.Equal("none", result.GetValue<string?>("--display"));
        Assert.Equal("info", result.GetValue<string?>("--log-level"));
        Assert.Equal(new[] { "A=1" }, result.GetValue<string[]>("--env"));
    }

    [Fact]
    public void bare_flag_or_value_options()
    {
        var bare = Parse("eval", "hello", "--fail-on-error", "--retry-on-error", "--cache");
        Assert.Empty(bare.Errors);
        Assert.True(Specified(bare, "--fail-on-error"));
        Assert.Null(bare.GetValue<string?>("--fail-on-error"));
        Assert.True(Specified(bare, "--retry-on-error"));
        Assert.Null(bare.GetValue<string?>("--retry-on-error"));
        Assert.True(Specified(bare, "--cache"));

        var valued = Parse("eval", "hello", "--fail-on-error=0.2", "--retry-on-error", "4", "--cache", "policy.yaml");
        Assert.Empty(valued.Errors);
        Assert.Equal("0.2", valued.GetValue<string?>("--fail-on-error"));
        Assert.Equal("4", valued.GetValue<string?>("--retry-on-error"));
        Assert.Equal("policy.yaml", valued.GetValue<string?>("--cache"));

        var absent = Parse("eval", "hello");
        Assert.False(Specified(absent, "--fail-on-error"));
        Assert.True(((OptionResult)absent.GetResult("--retry-on-error")!).Implicit);
    }

    [Fact]
    public void fail_on_error_resolution_follows_python()
    {
        Assert.Null(EvalCommands.ResolveFailOnError(FlagValue.NotGiven, noFailOnError: false));
        Assert.Equal(FailOnError.Never, EvalCommands.ResolveFailOnError(FlagValue.NotGiven, noFailOnError: true));
        Assert.Equal(FailOnError.Always, EvalCommands.ResolveFailOnError(new FlagValue(true, null), noFailOnError: false));
        Assert.Equal(FailOnError.Always, EvalCommands.ResolveFailOnError(new FlagValue(true, "0"), noFailOnError: false));
        Assert.Equal(FailOnError.Threshold(0.5), EvalCommands.ResolveFailOnError(new FlagValue(true, "0.5"), noFailOnError: false));
        Assert.Equal(FailOnError.Threshold(3), EvalCommands.ResolveFailOnError(new FlagValue(true, "3"), noFailOnError: false));
        Assert.Throws<UsageError>(() => EvalCommands.ResolveFailOnError(new FlagValue(true, "many"), noFailOnError: false));
    }

    [Fact]
    public void retry_on_error_resolution_follows_python()
    {
        Assert.Null(EvalCommands.ResolveRetryOnError(FlagValue.NotGiven));
        Assert.Equal(1, EvalCommands.ResolveRetryOnError(new FlagValue(true, null)));
        Assert.Equal(1, EvalCommands.ResolveRetryOnError(new FlagValue(true, "true")));
        Assert.Equal(3, EvalCommands.ResolveRetryOnError(new FlagValue(true, "3")));
        Assert.Null(EvalCommands.ResolveRetryOnError(new FlagValue(true, "false")));
        Assert.Null(EvalCommands.ResolveRetryOnError(new FlagValue(true, "0")));
        Assert.Throws<UsageError>(() => EvalCommands.ResolveRetryOnError(new FlagValue(true, "later")));
    }

    [Fact]
    public void cache_resolution_follows_python()
    {
        Assert.Null(EvalCommands.ResolveCache(FlagValue.NotGiven));
        Assert.Equal("7D", EvalCommands.ResolveCache(new FlagValue(true, null))!.Expiry);
        Assert.Equal("7D", EvalCommands.ResolveCache(new FlagValue(true, "true"))!.Expiry);
        Assert.Equal("3D", EvalCommands.ResolveCache(new FlagValue(true, "3"))!.Expiry);
        Assert.Null(EvalCommands.ResolveCache(new FlagValue(true, "false")));

        var dir = Directory.CreateTempSubdirectory("inspectai-cache");
        try
        {
            var file = Path.Combine(dir.FullName, "policy.yaml");
            File.WriteAllText(file, "expiry: 2h\nper_epoch: false\nscopes:\n  run: a\n");
            var policy = EvalCommands.ResolveCache(new FlagValue(true, file))!;
            Assert.Equal("2h", policy.Expiry);
            Assert.False(policy.PerEpoch);
            Assert.Equal("a", policy.Scopes["run"]);
            Assert.Equal(CachePolicy.ParseExpiry("2h"), policy.ExpirySeconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        Assert.Throws<Provider.Core.PrerequisiteError>(() => EvalCommands.ResolveCache(new FlagValue(true, "missing-policy.yaml")));
    }

    [Fact]
    public void adaptive_connections_and_hooks_resolution()
    {
        Assert.Null(EvalCommands.ParseAdaptive(null));
        Assert.NotNull(EvalCommands.ParseAdaptive("2-8"));
        Assert.NotNull(EvalCommands.ParseAdaptive("false"));
        Assert.Throws<UsageError>(() => EvalCommands.ParseAdaptive("lots"));

        Assert.Null(EvalCommands.ResolveHooks(null));
        Assert.Null(EvalCommands.ResolveHooks([""]));
        var hooks = EvalCommands.ResolveHooks([nameof(CountingHook)]);
        Assert.IsType<CountingHook>(Assert.Single(hooks!));
        Assert.Throws<Provider.Core.PrerequisiteError>(() => EvalCommands.ResolveHooks(["no/such-hook"]));
    }

    [Fact]
    public void eval_set_binds_its_extra_options()
    {
        var result = Parse("eval-set", "hello", "--model", "mockllm/model", "--retry-attempts", "3", "--retry-wait", "5", "--retry-connections", "0.5", "--no-retry-cleanup", "--log-dir-allow-dirty", "--id", "abc", "--max-tasks", "2", "--log-dir", "sets");
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.GetValue<int?>("--retry-attempts"));
        Assert.Equal(5, result.GetValue<int?>("--retry-wait"));
        Assert.Equal(0.5, result.GetValue<double?>("--retry-connections"));
        Assert.True(result.GetValue<bool>("--no-retry-cleanup"));
        Assert.True(result.GetValue<bool>("--log-dir-allow-dirty"));
        Assert.Equal("abc", result.GetValue<string?>("--id"));
        Assert.Equal(2, result.GetValue<int?>("--max-tasks"));
        Assert.NotEmpty(Parse("eval", "hello", "--retry-attempts", "3").Errors);
    }

    [Fact]
    public void eval_retry_binds_its_options()
    {
        var result = Parse("eval-retry", "a.eval", "b.eval", "--max-samples", "3", "--no-sandbox-cleanup", "--fail-on-error", "--retry-on-error", "2", "--max-connections", "5", "--max-retries", "2", "--timeout", "10", "--attempt-timeout", "5", "--stream-idle-timeout", "3", "--adaptive-connections", "false", "--log-dir", "logs", "--assembly", "t.dll");
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a.eval", "b.eval" }, result.GetValue<string[]>("log_files"));
        Assert.Equal(3, result.GetValue<int?>("--max-samples"));
        Assert.True(result.GetValue<bool>("--no-sandbox-cleanup"));
        Assert.True(Specified(result, "--fail-on-error"));
        Assert.Equal("2", result.GetValue<string?>("--retry-on-error"));
        Assert.Equal(5, result.GetValue<int?>("--max-connections"));
        Assert.Equal(2, result.GetValue<int?>("--max-retries"));
        Assert.Equal(10, result.GetValue<int?>("--timeout"));
        Assert.Equal(5, result.GetValue<int?>("--attempt-timeout"));
        Assert.Equal(3, result.GetValue<int?>("--stream-idle-timeout"));
        Assert.Equal("false", result.GetValue<string?>("--adaptive-connections"));
        Assert.Equal("logs", result.GetValue<string?>("--log-dir"));
        Assert.NotEmpty(Parse("eval-retry").Errors);
        Assert.NotEmpty(Parse("eval-retry", "a.eval", "--epochs", "2").Errors);
    }

    [Fact]
    public void score_binds_its_options()
    {
        var result = Parse("score", "log.eval", "--model", "mockllm/model", "--model-base-url", "http://u", "-M", "a=1", "--model-role", "g=mockllm/model", "--scorer", "includes", "-S", "ignore_case=false", "--metric", "accuracy", "--metric", "stderr", "--action", "overwrite", "--overwrite", "--output-file", "out.eval", "--stream", "2", "--log-dir", "d");
        Assert.Empty(result.Errors);
        Assert.Equal("log.eval", result.GetValue<string>("log-file"));
        Assert.Equal("mockllm/model", result.GetValue<string?>("--model"));
        Assert.Equal("http://u", result.GetValue<string?>("--model-base-url"));
        Assert.Equal(new[] { "a=1" }, result.GetValue<string[]>("-M"));
        Assert.Equal(new[] { "g=mockllm/model" }, result.GetValue<string[]>("--model-role"));
        Assert.Equal("includes", result.GetValue<string?>("--scorer"));
        Assert.Equal(new[] { "ignore_case=false" }, result.GetValue<string[]>("-S"));
        Assert.Equal(new[] { "accuracy", "stderr" }, result.GetValue<string[]>("--metric"));
        Assert.Equal("overwrite", result.GetValue<string?>("--action"));
        Assert.True(result.GetValue<bool>("--overwrite"));
        Assert.Equal("out.eval", result.GetValue<string?>("--output-file"));
        Assert.Equal("2", result.GetValue<string?>("--stream"));
        Assert.NotEmpty(Parse("score").Errors);
        Assert.NotEmpty(Parse("score", "log.eval", "--action", "merge").Errors);
    }

    [Fact]
    public void list_binds_its_options()
    {
        var tasks = Parse("list", "tasks", "a.dll", "b.dll", "-F", "light=true", "-F", "draft~=false", "--absolute", "--json");
        Assert.Empty(tasks.Errors);
        Assert.Equal(new[] { "a.dll", "b.dll" }, tasks.GetValue<string[]>("paths"));
        Assert.Equal(new[] { "light=true", "draft~=false" }, tasks.GetValue<string[]>("-F"));
        Assert.True(tasks.GetValue<bool>("--absolute"));
        Assert.True(tasks.GetValue<bool>("--json"));

        var logs = Parse("list", "logs", "--status", "success", "--absolute", "--json", "--no-recursive", "--log-dir", "d");
        Assert.Empty(logs.Errors);
        Assert.Equal("success", logs.GetValue<string?>("--status"));
        Assert.True(logs.GetValue<bool>("--no-recursive"));
    }

    [Fact]
    public void log_binds_its_options()
    {
        var list = Parse("log", "list", "--status", "Error", "--log-dir", "d");
        Assert.Empty(list.Errors);
        Assert.Equal("Error", list.GetValue<string?>("--status"));

        var dump = Parse("log", "dump", "p.eval", "--header-only", "--resolve-attachments", "full");
        Assert.Empty(dump.Errors);
        Assert.Equal("p.eval", dump.GetValue<string>("path"));
        Assert.True(dump.GetValue<bool>("--header-only"));
        Assert.Equal("full", dump.GetValue<string?>("--resolve-attachments"));

        var bareResolve = Parse("log", "dump", "p.eval", "--resolve-attachments");
        Assert.Empty(bareResolve.Errors);
        Assert.True(Specified(bareResolve, "--resolve-attachments"));
        Assert.Null(bareResolve.GetValue<string?>("--resolve-attachments"));

        var headers = Parse("log", "headers", "a.eval", "b.json");
        Assert.Empty(headers.Errors);
        Assert.Equal(new[] { "a.eval", "b.json" }, headers.GetValue<string[]>("files"));

        var convert = Parse("log", "convert", "p", "--to", "eval", "--output-dir", "o", "--overwrite", "--resolve-attachments", "--stream");
        Assert.Empty(convert.Errors);
        Assert.Equal("eval", convert.GetValue<string?>("--to"));
        Assert.Equal("o", convert.GetValue<string?>("--output-dir"));
        Assert.True(convert.GetValue<bool>("--overwrite"));
        Assert.NotEmpty(Parse("log", "convert", "p", "--to", "json").Errors);
        Assert.NotEmpty(Parse("log", "convert", "p", "--to", "xml", "--output-dir", "o").Errors);
        Assert.Empty(Parse("log", "schema").Errors);
        Assert.Empty(Parse("log", "convert", "p", "--to", "JSON", "--output-dir", "o").Errors);
    }

    [Fact]
    public void cache_info_and_view_bind_their_options()
    {
        Assert.True(Parse("cache", "clear", "--all").GetValue<bool>("--all"));
        Assert.Equal(new[] { "a", "b" }, Parse("cache", "clear", "--model", "a", "--model", "b").GetValue<string[]>("--model"));
        Assert.Equal(new[] { "x" }, Parse("cache", "prune", "--model", "x").GetValue<string[]>("--model"));
        Assert.True(Parse("cache", "list", "--pruneable").GetValue<bool>("--pruneable"));
        Assert.Empty(Parse("cache", "path").Errors);

        Assert.True(Parse("info", "version", "--json").GetValue<bool>("--json"));
        Assert.Empty(Parse("info", "log-schema").Errors);
        var logFile = Parse("info", "log-file", "p.eval", "--header-only", "5");
        Assert.Empty(logFile.Errors);
        Assert.Equal("5", logFile.GetValue<string?>("--header-only"));

        var view = Parse("view", "--log-dir", "x", "--port", "7575", "start");
        Assert.Empty(view.Errors);
        Assert.Equal(new[] { "--log-dir", "x", "--port", "7575", "start" }, ViewCommand.PassthroughArguments(view));
    }

    [Fact]
    public void choices_validate_case_insensitively_where_python_does()
    {
        Assert.Empty(Parse("eval", "hello", "--log-format", "JSON", "--log-level", "INFO", "--display", "Plain").Errors);
        Assert.NotEmpty(Parse("eval", "hello", "--log-format", "xml").Errors);
        Assert.NotEmpty(Parse("eval", "hello", "--verbosity", "LOW").Errors);
        Assert.NotEmpty(Parse("eval", "hello", "--reasoning-effort", "extreme").Errors);
    }

    [Fact]
    public void multi_valued_options_read_their_environment_variable_split_on_whitespace()
    {
        const string variable = "INSPECT_EVAL_TASK_ARGS";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "a=1  b=2");
            Assert.Equal(new[] { "a=1", "b=2" }, Parse("eval", "hello").GetValue<string[]>("-T"));
            Assert.Equal(new[] { "c=3" }, Parse("eval", "hello", "-T", "c=3").GetValue<string[]>("-T"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task usage_errors_exit_2_and_help_exits_0()
    {
        var (code, _, err) = await Run("eval", "hello", "--bogus");
        Assert.Equal(2, code);
        Assert.Contains("--bogus", err);
        Assert.Contains("Try 'inspectai eval --help' for help.", err);

        (code, _, err) = await Run("log", "convert", "x", "--to", "xml", "--output-dir", "o");
        Assert.Equal(2, code);
        Assert.Contains("'xml' is not one of 'eval', 'json'", err);

        (code, _, _) = await Run("nosuch");
        Assert.Equal(2, code);

        (code, _, err) = await Run("eval", "hello", "--debug");
        Assert.Equal(2, code);
        Assert.Contains("not supported", err);

        (code, _, err) = await Run("cache", "clear");
        Assert.Equal(2, code);
        Assert.Contains("Need to specify either --all or --model.", err);

        string output;
        (code, output, _) = await Run();
        Assert.Equal(0, code);
        Assert.Contains("eval-retry", output);

        (code, output, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("Usage:", output);

        (code, output, _) = await Run("eval", "--help");
        Assert.Equal(0, code);
        Assert.Contains("--model", output);

        (code, output, _) = await Run("--version");
        Assert.Equal(0, code);
        Assert.Equal(InfoCommands.Version(), output.Trim());
    }

    [Fact]
    public async Task info_version_prints_version_and_path()
    {
        var (code, output, _) = await Run("info", "version");
        Assert.Equal(0, code);
        Assert.StartsWith("version: ", output);
        Assert.Contains("path: ", output);

        (code, output, _) = await Run("info", "version", "--json");
        Assert.Equal(0, code);
        var node = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(InfoCommands.Version(), (string?)node["version"]);
        Assert.Equal(InfoCommands.InstallPath(), (string?)node["path"]);
    }

    [Fact]
    public async Task log_schema_prints_the_openapi_document()
    {
        var (code, output, _) = await Run("info", "log-schema");
        Assert.Equal(0, code);
        Assert.StartsWith("{", output.TrimStart());
        Assert.Contains("\"openapi\"", output);
        Assert.Contains("\"EvalLog\"", output);
        var (code2, output2, _) = await Run("log", "schema");
        Assert.Equal(0, code2);
        Assert.Equal(output, output2);
    }
}

/// <summary>A hook the <c>--hooks</c> option instantiates by type name.</summary>
public sealed class CountingHook : Eval.Hooks.Hooks
{
    public static int TaskStarts;

    public override Task OnTaskStartAsync(Eval.Hooks.TaskStart data, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref TaskStarts);
        return Task.CompletedTask;
    }
}
