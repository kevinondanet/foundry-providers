using System.Numerics;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Cli.Models;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Tests;

/// <summary>
/// Ports of the <c>-T</c> / <c>-S</c> / <c>-M</c> parsing rules. The expected values were computed with the inspect_ai
/// venv (<c>parse_cli_args([...])</c> over <c>yaml.safe_load</c>) and are asserted here verbatim.
/// </summary>
public class ArgsParsingTests
{
    public static TheoryData<string, object?> ScalarCases => new()
    {
        { "x=1", 1L },
        { "x=1.5", 1.5 },
        { "x=true", true },
        { "x=True", true },
        { "x=yes", true },
        { "x=no", false },
        { "x=on", true },
        { "x=off", false },
        { "x=No", false },
        { "x=TRUE", true },
        { "x=null", null },
        { "x=NULL", null },
        { "x=~", null },
        { "x=", null },
        { "x=hello", "hello" },
        { "x=  spaced  ", "spaced" },
        { "x=a=b", "a=b" },
        { "x=1e3", "1e3" },
        { "x=1e-3", "1e-3" },
        { "x=0x1F", 31L },
        { "x=1_000", 1000L },
        { "x=012", 10L },
        { "x=0o17", "0o17" },
        { "x=1:30", 90L },
        { "x=+5", 5L },
        { "x=-5", -5L },
        { "x=5.", 5.0 },
        { "x=.5", 0.5 },
        { "x=1.0", 1.0 },
        { "x=Infinity", "Infinity" },
        { "x=nan", "nan" },
        { "x=y", "y" },
        { "x=n", "n" },
        { "x=.inf", double.PositiveInfinity },
        { "x=-.inf", double.NegativeInfinity },
        { "x=2024-01-01", "2024-01-01" },
        { "x='it''s'", "it's" },
        { "x=\"quoted \\\"inner\\\"\"", "quoted \"inner\"" },
    };

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void parse_cli_args_resolves_scalars_like_yaml_safe_load(string arg, object? expected)
    {
        var parsed = CliArgs.ParseCliArgs([arg]);
        Assert.Equal(expected, parsed["x"]);
    }

    [Fact]
    public void parse_cli_args_nan_and_big_integers()
    {
        Assert.True(CliArgs.ParseCliArgs(["x=.nan"])["x"] is double d && double.IsNaN(d));
        Assert.Equal(BigInteger.Parse("100000000000000000000"), CliArgs.ParseCliArgs(["x=100000000000000000000"])["x"]);
    }

    [Fact]
    public void parse_cli_args_splits_strings_on_commas()
    {
        Assert.Equal(new List<object?> { "a", "b" }, CliArgs.ParseCliArgs(["x=a,b"])["x"]);
        Assert.Equal(new List<object?> { "a", " b" }, CliArgs.ParseCliArgs(["x=a, b"])["x"]);
        Assert.Equal(new List<object?> { "a", "b" }, CliArgs.ParseCliArgs(["x='a,b'"])["x"]);
        Assert.Equal(new List<object?> { "1", "" }, CliArgs.ParseCliArgs(["x=1,"])["x"]);
        Assert.Equal("hello", CliArgs.ParseCliArgs(["x=hello"])["x"]);
    }

    [Fact]
    public void parse_cli_args_flow_collections_and_block_forms()
    {
        Assert.Equal(new List<object?> { 1L, 2L }, CliArgs.ParseCliArgs(["x=[1,2]"])["x"]);
        Assert.Equal(new List<object?> { "a", "b" }, CliArgs.ParseCliArgs(["x=[a, b]"])["x"]);
        Assert.Equal(new List<object?>(), CliArgs.ParseCliArgs(["x=[]"])["x"]);
        Assert.Equal(new List<object?> { 1L, new List<object?> { 2L, 3L } }, CliArgs.ParseCliArgs(["x=[1, [2, 3]]"])["x"]);
        Assert.Equal(new List<object?> { "a" }, CliArgs.ParseCliArgs(["x=- a"])["x"]);

        var map = Assert.IsType<Dictionary<string, object?>>(CliArgs.ParseCliArgs(["x={a: 1, b: two}"])["x"]);
        Assert.Equal(1L, map["a"]);
        Assert.Equal("two", map["b"]);
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L }, CliArgs.ParseCliArgs(["x={\"a\": 1}"])["x"]);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(CliArgs.ParseCliArgs(["x={}"])["x"]));
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "b" }, CliArgs.ParseCliArgs(["x=a: b"])["x"]);

        var nested = Assert.IsType<Dictionary<string, object?>>(CliArgs.ParseCliArgs(["x={a: [1, 2], b: {c: d}}"])["x"]);
        Assert.Equal(new List<object?> { 1L, 2L }, nested["a"]);
        Assert.Equal(new Dictionary<string, object?> { ["c"] = "d" }, nested["b"]);
    }

    [Fact]
    public void parse_cli_args_key_rules()
    {
        Assert.Equal(1L, CliArgs.ParseCliArgs(["my-arg=1"])["my_arg"]);
        Assert.Empty(CliArgs.ParseCliArgs(["noequals"]));
        Assert.Empty(CliArgs.ParseCliArgs(null));
        Assert.Throws<FormatException>(() => CliArgs.ParseCliArgs(["x=,"]));
    }

    [Fact]
    public void parse_cli_config_merges_a_file_under_the_arguments()
    {
        var dir = Directory.CreateTempSubdirectory("inspectai-args");
        try
        {
            var yaml = Path.Combine(dir.FullName, "task.yaml");
            File.WriteAllText(yaml, "count: 5\ntarget: file\nnested:\n  a: 1\n  b: [x, y]\nlist:\n  - one\n  - two\n# comment\n");
            var config = CliArgs.ParseCliConfig(["count=7"], yaml);
            Assert.Equal(7L, config["count"]);
            Assert.Equal("file", config["target"]);
            Assert.Equal(new Dictionary<string, object?> { ["a"] = 1L, ["b"] = new List<object?> { "x", "y" } }, config["nested"]);
            Assert.Equal(new List<object?> { "one", "two" }, config["list"]);

            var json = Path.Combine(dir.FullName, "task.json");
            File.WriteAllText(json, "{\"count\": 3, \"flag\": true, \"ratio\": 0.25, \"name\": null}");
            var fromJson = CliArgs.ParseCliConfig(null, json);
            Assert.Equal(3L, fromJson["count"]);
            Assert.Equal(true, fromJson["flag"]);
            Assert.Equal(0.25, fromJson["ratio"]);
            Assert.Null(fromJson["name"]);

            Assert.Throws<PrerequisiteError>(() => CliArgs.ParseCliConfig(null, Path.Combine(dir.FullName, "missing.yaml")));
            Assert.Throws<ArgumentException>(() => CliArgs.ReadConfigObject("- just a list"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 5, 0, true, 5)]
    [InlineData("true", 5, 0, true, 5)]
    [InlineData("yes", 5, 0, true, 5)]
    [InlineData("1", 5, 0, true, 5)]
    [InlineData("1", 5, 0, false, 1)]
    [InlineData("false", 5, 0, true, 0)]
    [InlineData("no", 5, 9, true, 9)]
    [InlineData("0", 5, 9, true, 9)]
    [InlineData("3", 5, 0, true, 3)]
    public void int_or_bool_value(string? value, int trueValue, int falseValue, bool isOneTrue, int expected)
    {
        Assert.Equal(expected, CliArgs.IntOrBoolValue(value, trueValue, falseValue, isOneTrue));
    }

    [Fact]
    public void int_or_bool_value_rejects_text()
    {
        Assert.Throws<FormatException>(() => CliArgs.IntOrBoolValue("later", 1));
    }

    [Fact]
    public void int_bool_or_str_value()
    {
        Assert.Equal(7, CliArgs.IntBoolOrStrValue(null, 7));
        Assert.Equal(7, CliArgs.IntBoolOrStrValue("true", 7));
        Assert.Null(CliArgs.IntBoolOrStrValue("false", 7));
        Assert.Equal(3, CliArgs.IntBoolOrStrValue("3", 7));
        Assert.Equal("policy.yaml", CliArgs.IntBoolOrStrValue("policy.yaml", 7));
    }

    [Fact]
    public void samples_limit_sample_id_and_comma_separated()
    {
        Assert.Null(CliArgs.ParseSamplesLimit(null));
        Assert.Equal(new SamplesLimit(10, null, null), CliArgs.ParseSamplesLimit("10"));
        Assert.Equal(new SamplesLimit(null, 9, 20), CliArgs.ParseSamplesLimit("10-20"));
        Assert.Throws<FormatException>(() => CliArgs.ParseSamplesLimit("1-2-3"));
        Assert.Throws<FormatException>(() => CliArgs.ParseSamplesLimit("ten"));

        Assert.Equal(new[] { "1", "b", "c" }, CliArgs.ParseSampleId("1, b ,c"));
        Assert.Null(CliArgs.ParseSampleId(null));
        Assert.Equal(new[] { "a", " b" }, CliArgs.ParseCommaSeparated("a, b"));
    }

    [Fact]
    public void parse_sandbox()
    {
        Assert.Null(CliArgs.ParseSandbox(null));
        Assert.Equal(new Eval.Sandbox.SandboxSpec("docker"), CliArgs.ParseSandbox("docker"));
        Assert.Equal(new Eval.Sandbox.SandboxSpec("docker", "compose.yml"), CliArgs.ParseSandbox("docker:compose.yml"));
        Assert.Equal(new Eval.Sandbox.SandboxSpec("docker", "a:b"), CliArgs.ParseSandbox("docker:a:b"));
    }

    [Fact]
    public void env_args_keep_the_raw_text_after_the_first_equals()
    {
        var env = CliArgs.ParseEnvArgs(["PYTHONPATH=/a,/b", "EMPTY=", "PAIR=x=y", "MY-VAR=3.10", "FLAG=true", "noequals"]);
        Assert.Equal(new Dictionary<string, string> { ["PYTHONPATH"] = "/a,/b", ["EMPTY"] = "", ["PAIR"] = "x=y", ["MY_VAR"] = "3.10", ["FLAG"] = "true" }, env);
        Assert.Empty(CliArgs.ParseEnvArgs(null));
        Assert.Empty(CliArgs.ParseEnvArgs([]));
    }

    [Fact]
    public void model_roles_names_lists_and_mappings()
    {
        var created = new List<(string? Name, GenerateConfig? Config, IReadOnlyDictionary<string, object?>? Args)>();
        var roles = ModelRoleArgs.Parse(
            ["grader=mockllm/model", "critic=mockllm/a,mockllm/b, ", "judge={model: mockllm/j, temperature: 0.5, model_args: {k: v}}"],
            (name, config, args) =>
            {
                created.Add((name, config, args));
                return ModelProviders.Resolve(name ?? "mockllm/model", config);
            })!;

        // a plain name is built by the factory too (Python: get_model(name)); a string result would send it to the runner's Foundry default
        Assert.Equal("mockllm/model", Assert.IsType<Eval.Model.Model>(roles["grader"]).Name);
        Assert.Equal(["mockllm/a", "mockllm/b"], Assert.IsType<List<object>>(roles["critic"]).Select(model => Assert.IsType<Eval.Model.Model>(model).Name));
        Assert.IsType<Eval.Model.Model>(roles["judge"]);
        Assert.Equal(["mockllm/model", "mockllm/a", "mockllm/b", "mockllm/j"], created.Select(call => call.Name));
        Assert.All(created.Take(3), call => Assert.Null(call.Config));
        Assert.All(created.Take(3), call => Assert.Null(call.Args));
        var (modelName, generateConfig, modelArgs) = created[^1];
        Assert.Equal("mockllm/j", modelName);
        Assert.Equal(0.5, generateConfig!.Temperature);
        Assert.Equal("v", modelArgs!["k"]);

        Assert.Null(ModelRoleArgs.Parse(null, (_, _, _) => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => ModelRoleArgs.Parse(["judge={model: x, model_args: 3}"], (_, _, _) => throw new InvalidOperationException()));
        Assert.Throws<PrerequisiteError>(() => ModelRoleArgs.Parse(["judge={model: x, temperatur: 1}"], (_, _, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void generate_config_binding_reads_snake_case_fields_and_rejects_unknown_ones()
    {
        var config = GenerateConfigBinding.FromValues(new Dictionary<string, object?> { ["max_tokens"] = 5L, ["temperature"] = 0.1, ["stop_seqs"] = new List<object?> { "a" }, ["system_message"] = "sys" }, "test");
        Assert.Equal(5, config.MaxTokens);
        Assert.Equal(0.1, config.Temperature);
        Assert.Equal(new[] { "a" }, config.StopSeqs);
        Assert.Equal("sys", config.SystemMessage);
        Assert.Contains("max_connections", GenerateConfigBinding.FieldNames);
        var error = Assert.Throws<PrerequisiteError>(() => GenerateConfigBinding.FromValues(new Dictionary<string, object?> { ["max_token"] = 5L }, "test"));
        Assert.Contains("max_token", error.Message);
    }

    [Fact]
    public void yaml_rejects_malformed_input()
    {
        Assert.Throws<FormatException>(() => YamlValue.Parse("[1, 2"));
        Assert.Throws<FormatException>(() => YamlValue.Parse("{a: 1"));
        Assert.Throws<FormatException>(() => YamlValue.Parse("'open"));
        Assert.Throws<FormatException>(() => YamlValue.Parse("[1] trailing"));
        Assert.Throws<FormatException>(() => YamlValue.Parse("a: 1\n  b: 2\n c: 3"));
    }
}
