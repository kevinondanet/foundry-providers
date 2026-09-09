using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.Structured;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Structured;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/structured.py</c> (<see cref="StructuredTasks"/>, <see cref="StructuredExample"/>):
/// the <c>Color</c> schema, the four tasks' shapes (the response schema of <c>rgb_color</c>, the guided-decoding
/// <c>extra_body</c> of the others per provider, and their "Unsupported provider" errors), both scorers' branches,
/// and the offline runs with the scripted model through <c>Eval.RunAsync</c> and the examples runner, including
/// the skip of the guided tasks under <c>--fake</c>.
/// </summary>
public sealed class StructuredTests : IDisposable
{
    private const string White = "{\"red\":255,\"green\":255,\"blue\":255}";

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "structured-" + Guid.NewGuid().ToString("N"));

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

    // ----------------------------------------------------------------------------------------------------------
    // Color (port of class Color(BaseModel)) and rgb_color
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void color_has_the_schema_json_schema_of_the_pydantic_model_produces()
    {
        var schema = JsonSchemaGenerator.JsonSchemaOf<Color>();

        Assert.Equal(["object"], schema.Type);
        Assert.Equal(["red", "green", "blue"], schema.Properties!.Keys);
        Assert.All(schema.Properties.Values, property => Assert.Equal(["integer"], property.Type));
        Assert.Equal(["red", "green", "blue"], schema.Required);
        Assert.Equal(false, schema.AdditionalProperties);
    }

    [Fact]
    public void rgb_color_is_named_and_shaped_like_the_python_task()
    {
        var task = StructuredTasks.RgbColor();

        Assert.Equal("rgb_color", task.Name);
        AssertColorDataset(task);
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("score_json_color", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(metric => metric.Name));
        var schema = task.Config.ResponseSchema!;
        Assert.Equal("color", schema.Name);
        Assert.Equal(["red", "green", "blue"], schema.JsonSchema.Properties!.Keys);
        Assert.Null(task.Config.ExtraBody);
        Assert.Null(task.Sandbox);
    }

    [Theory]
    [InlineData("rgb_color", new string[0])]
    [InlineData("rgb_color_regex", new[] { "vllm=true", "sglang=true" })]
    [InlineData("rgb_color_choice", new[] { "vllm=true" })]
    [InlineData("rgb_color_grammar", new[] { "vllm=true", "sglang=true" })]
    public void the_task_methods_are_discoverable_by_the_cli_with_their_attribs(string name, string[] attribs)
    {
        var methodName = name switch
        {
            "rgb_color" => nameof(StructuredTasks.RgbColor),
            "rgb_color_regex" => nameof(StructuredTasks.RgbColorRegex),
            "rgb_color_choice" => nameof(StructuredTasks.RgbColorChoice),
            _ => nameof(StructuredTasks.RgbColorGrammar),
        };
        var method = typeof(StructuredTasks).GetMethod(methodName, Type.EmptyTypes)!;

        var attribute = Assert.IsType<TaskAttribute>(Assert.Single(method.GetCustomAttributes(typeof(TaskAttribute), inherit: false)));
        Assert.Equal(name, attribute.Name);
        Assert.Equal(attribs, attribute.Attribs);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the guided-decoding tasks per provider
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void rgb_color_regex_picks_the_guided_field_of_the_provider_or_rejects_it()
    {
        var vllm = StructuredTasks.RgbColorRegex("vllm");
        Assert.Equal("rgb_color_regex", vllm.Name);
        AssertColorDataset(vllm);
        Assert.Equal("score_regex_color", Assert.Single(vllm.Scorers).Name);
        Assert.Equal(StructuredTasks.RgbPattern, Assert.Single(vllm.Config.ExtraBody!).Value!.GetValue<string>());
        Assert.Equal("guided_regex", Assert.Single(vllm.Config.ExtraBody!).Key);
        Assert.Null(vllm.Config.ResponseSchema);

        Assert.Equal("regex", Assert.Single(StructuredTasks.RgbColorRegex("sglang").Config.ExtraBody!).Key);

        foreach (var api in new[] { "azureai", "anthropic", "scripted", "openai" })
        {
            var error = Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorRegex(api));
            Assert.Equal($"Unsupported provider: {api}", error.Message);
        }
    }

    [Fact]
    public void rgb_color_choice_is_vllm_only()
    {
        var vllm = StructuredTasks.RgbColorChoice("vllm");
        Assert.Equal("rgb_color_choice", vllm.Name);
        AssertColorDataset(vllm);
        Assert.Equal("score_regex_color", Assert.Single(vllm.Scorers).Name);
        var choice = Assert.Single(vllm.Config.ExtraBody!);
        Assert.Equal("guided_choice", choice.Key);
        Assert.Equal(["RGB: 255,255,255", "RGB: 0,0,0"], choice.Value!.AsArray().Select(node => node!.GetValue<string>()));

        foreach (var api in new[] { "sglang", "azureai", "anthropic" })
        {
            var error = Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorChoice(api));
            Assert.Equal("Choice is only supported for vLLM", error.Message);
        }
    }

    [Fact]
    public void rgb_color_grammar_picks_the_guided_field_of_the_provider_or_rejects_it()
    {
        var vllm = StructuredTasks.RgbColorGrammar("vllm");
        Assert.Equal("rgb_color_grammar", vllm.Name);
        AssertColorDataset(vllm);
        Assert.Equal("score_regex_color", Assert.Single(vllm.Scorers).Name);
        var grammar = Assert.Single(vllm.Config.ExtraBody!);
        Assert.Equal("guided_grammar", grammar.Key);
        Assert.Equal(StructuredTasks.Grammar, grammar.Value!.GetValue<string>());
        Assert.StartsWith("\nroot ::= rgb_color\nrgb_color ::= \"RGB: \" rgb_values\n", StructuredTasks.Grammar);
        Assert.EndsWith("digit ::= \"0\" | \"1\" | \"2\" | \"3\" | \"4\" | \"5\" | \"6\" | \"7\" | \"8\" | \"9\"\n", StructuredTasks.Grammar);

        Assert.Equal("ebnf", Assert.Single(StructuredTasks.RgbColorGrammar("sglang").Config.ExtraBody!).Key);

        var error = Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorGrammar("azureai"));
        Assert.Equal("Unsupported provider: azureai", error.Message);
    }

    [Fact]
    public void model_api_reads_the_given_model_then_inspect_eval_model_then_fails_like_get_model()
    {
        Assert.Equal("scripted", StructuredTasks.ModelApi(FakeStructuredModel.Create()));

        var previous = Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("INSPECT_EVAL_MODEL", "vllm/meta-llama/Llama-3.1-8B-Instruct,openai/gpt-4o");
            Assert.Equal("vllm", StructuredTasks.ModelApi());
            Assert.Equal("scripted", StructuredTasks.ModelApi(FakeStructuredModel.Create()));

            Environment.SetEnvironmentVariable("INSPECT_EVAL_MODEL", "azureai/gpt-5.4-mini");
            Assert.Equal("azureai", StructuredTasks.ModelApi());
            Assert.Equal("Unsupported provider: azureai", Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorRegex()).Message);

            Environment.SetEnvironmentVariable("INSPECT_EVAL_MODEL", null);
            var error = Assert.Throws<PrerequisiteError>(() => StructuredTasks.ModelApi());
            Assert.Equal("No model specified (and no model environment variable defined)", error.Message);
            Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorChoice());
            Assert.Throws<PrerequisiteError>(() => StructuredTasks.RgbColorGrammar());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSPECT_EVAL_MODEL", previous);
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scorers
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(White, "255,255,255", "C", null)]
    [InlineData("{\"red\": 0, \"green\": 0, \"blue\": 0}", "0,0,0", "C", null)]
    [InlineData("{\"red\":0,\"green\":0,\"blue\":1}", "0,0,0", "I", null)]
    [InlineData("not json", "0,0,0", "I", "Error parsing response: ")]
    [InlineData("{\"red\":1}", "1,1,1", "I", "Error parsing response: ")]
    [InlineData("{\"red\":\"255\",\"green\":255,\"blue\":255}", "255,255,255", "I", "Error parsing response: ")]
    [InlineData("null", "0,0,0", "I", "Error parsing response: ")]
    [InlineData("", "0,0,0", "I", "Error parsing response: ")]
    public async Task score_json_color_parses_the_completion_as_a_color(string completion, string target, string value, string? explanationPrefix)
    {
        var score = await StructuredTasks.ScoreJsonColorAsync(State(completion), new Target(target), CancellationToken.None);

        Assert.Equal(new ScoreValue.Str(value), score.Value);
        Assert.Equal(completion, score.Answer);
        if (explanationPrefix is null)
        {
            Assert.Null(score.Explanation);
        }
        else
        {
            Assert.StartsWith(explanationPrefix, score.Explanation);
        }
    }

    [Theory]
    [InlineData("RGB: 255,255,255", "255,255,255", "C", "255,255,255", null)]
    [InlineData("The color is RGB: 0,0,0.", "0,0,0", "C", "0,0,0", null)]
    [InlineData("RGB: 0,0,1", "0,0,0", "I", "0,0,1", null)]
    [InlineData("RGB: 255, 255, 255", "255,255,255", "I", "RGB: 255, 255, 255", "Output does not match RGB format (r,g,b)")]
    [InlineData("white is (255,255,255)", "255,255,255", "I", "white is (255,255,255)", "Output does not match RGB format (r,g,b)")]
    [InlineData("", "0,0,0", "I", "", "Output does not match RGB format (r,g,b)")]
    public async Task score_regex_color_reads_the_first_rgb_triple(string completion, string target, string value, string answer, string? explanation)
    {
        var score = await StructuredTasks.ScoreRegexColorAsync(State(completion), new Target(target), CancellationToken.None);

        Assert.Equal(new ScoreValue.Str(value), score.Value);
        Assert.Equal(answer, score.Answer);
        Assert.Equal(explanation, score.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task rgb_color_runs_with_the_scripted_model_and_sends_the_response_schema()
    {
        var model = FakeStructuredModel.Create();
        var api = (ScriptedModelApi)model.Api;

        var log = await Eval.RunAsync(StructuredTasks.RgbColor(), new EvalOptions { Model = model, LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(2, log.Samples!.Count);
        foreach (var sample in log.Samples)
        {
            var score = sample.Scores!["score_json_color"];
            Assert.Equal("C", score.Text);
            Assert.Null(score.Explanation);
            Assert.Equal(sample.Output.Completion, score.Answer);
            var color = System.Text.Json.JsonSerializer.Deserialize<Color>(sample.Output.Completion)!;
            Assert.Equal(sample.Target.Text, $"{color.Red},{color.Green},{color.Blue}");
        }

        var results = Assert.Single(log.Results!.Scores);
        Assert.Equal("score_json_color", results.Name);
        Assert.Equal(1.0, results.Metrics["accuracy"].Value);
        Assert.Equal(0.0, results.Metrics["stderr"].Value);

        // The response schema reached the api on every request (the providers forward it as response_format / output_format).
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, request =>
        {
            var schema = request.Config.ResponseSchema!;
            Assert.Equal("color", schema.Name);
            Assert.Equal(["red", "green", "blue"], schema.JsonSchema.Required);
        });
    }

    [Fact]
    public async Task a_malformed_reply_hits_the_error_parsing_branch()
    {
        var log = await Eval.RunAsync(
            StructuredTasks.RgbColor(),
            new EvalOptions { Model = FakeStructuredModel.Create(FakeStructuredModel.Reply.Malformed), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.All(log.Samples!, sample =>
        {
            var score = sample.Scores!["score_json_color"];
            Assert.Equal("I", score.Text);
            Assert.StartsWith("Error parsing response: ", score.Explanation);
        });
        Assert.Equal(0.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task a_guided_task_built_for_vllm_runs_offline_with_the_extra_body_carried_on_the_config()
    {
        var model = FakeStructuredModel.Create(FakeStructuredModel.Reply.Rgb);
        var api = (ScriptedModelApi)model.Api;

        var log = await Eval.RunAsync(StructuredTasks.RgbColorRegex("vllm"), new EvalOptions { Model = model, LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.All(log.Samples!, sample =>
        {
            var score = sample.Scores!["score_regex_color"];
            Assert.Equal("C", score.Text);
            Assert.Equal(sample.Target.Text, score.Answer);
        });
        Assert.Equal(1.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);
        Assert.All(api.Requests, request => Assert.Equal(StructuredTasks.RgbPattern, request.Config.ExtraBody!["guided_regex"]!.GetValue<string>()));
    }

    [Fact]
    public async Task the_runner_runs_rgb_color_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["structured", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : rgb_color", text);
        Assert.Contains("model     : structured-scripted (scripted, offline)", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("status    : success (2/2 samples completed)", text);
        Assert.Contains("score_json_color         accuracy                  1.000", text);
        Assert.Contains("score_json_color         stderr                    0.000", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task the_runner_passes_reply_malformed_to_the_scripted_model_and_rejects_a_bad_reply()
    {
        var output = new StringWriter();
        var exit = await ExampleRunner.MainAsync(["structured", "--fake", "--log-dir", _logDir, "-T", "reply=malformed"], ExampleRegistry.Default, output, output);
        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task args : reply=malformed", text);
        Assert.Contains("score_json_color         accuracy                  0.000", text);

        var error = new StringWriter();
        Assert.Equal(2, await ExampleRunner.MainAsync(["structured", "--fake", "--log-dir", _logDir, "-T", "reply=bogus"], ExampleRegistry.Default, TextWriter.Null, error));
        Assert.Contains("-T reply expects json, rgb or malformed", error.ToString());
    }

    [Theory]
    [InlineData("rgb_color_regex")]
    [InlineData("rgb_color_choice")]
    [InlineData("rgb_color_grammar")]
    public async Task the_guided_tasks_are_skipped_under_fake_with_a_prerequisite_error(string task)
    {
        var error = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["structured", "--fake", "--task", task, "--log-dir", _logDir], ExampleRegistry.Default, TextWriter.Null, error);

        Assert.Equal(2, exit);
        Assert.Contains($"{task} is skipped under --fake", error.ToString());
        Assert.False(Directory.Exists(_logDir) && Directory.GetFiles(_logDir, "*.eval").Length > 0, "no log should be written for a skipped task");
    }

    [Fact]
    public void the_example_is_registered_with_the_four_python_tasks_and_no_sandbox()
    {
        var example = Assert.IsType<StructuredExample>(ExampleRegistry.Default.Find("structured"));
        var context = new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

        Assert.Equal(["rgb_color", "rgb_color_regex", "rgb_color_choice", "rgb_color_grammar"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotNull(example.Defaults.ModelHint);
        Assert.Contains(example.Deviations, deviation => deviation.Contains("not runnable on Foundry"));
        Assert.Null(example.FakeSandbox(context));
        Assert.Equal("structured-scripted", example.CreateFakeModel(context).Name);
        Assert.Equal("rgb_color", example.Tasks[0].Build(context).Name);

        // Against a live model the guided tasks fail as the Python does for any provider but vllm / sglang.
        var live = context with { Fake = false, ResolvedModel = FakeStructuredModel.Create() };
        Assert.Equal("Unsupported provider: scripted", Assert.Throws<PrerequisiteError>(() => example.Tasks[1].Build(live)).Message);
        Assert.Equal("Choice is only supported for vLLM", Assert.Throws<PrerequisiteError>(() => example.Tasks[2].Build(live)).Message);
        Assert.Equal("Unsupported provider: scripted", Assert.Throws<PrerequisiteError>(() => example.Tasks[3].Build(live)).Message);
    }

    private static void AssertColorDataset(EvalTask task)
    {
        Assert.Equal(2, task.Dataset.Count);
        Assert.Equal(["What is the RGB color for white?", "What is the RGB color for black?"], task.Dataset.Select(sample => sample.Input.Text));
        Assert.Equal(["255,255,255", "0,0,0"], task.Dataset.Select(sample => sample.Target.Text));
    }

    private static TaskState State(string completion) =>
        new("scripted", 1, 1, "q", [new ChatMessageUser("q")], output: ModelOutput.FromContent("scripted", completion));
}
