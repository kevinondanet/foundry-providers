using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Structured;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/structured.py</c> <c>class Color(BaseModel)</c>: the structured output the <c>rgb_color</c>
/// task asks for. Deviation: a System.Text.Json record stands in for the pydantic model; the <c>required</c>
/// members and <see cref="JsonPropertyNameAttribute"/>s give <c>JsonSchemaGenerator.JsonSchemaOf&lt;Color&gt;()</c>
/// the same <c>red</c>/<c>green</c>/<c>blue</c> integer schema <c>json_schema(Color)</c> produces, and make
/// <c>JsonSerializer.Deserialize&lt;Color&gt;</c> reject a missing field as <c>Color.model_validate_json</c> does.
/// </summary>
public sealed record Color
{
    [JsonPropertyName("red")]
    public required int Red { get; init; }

    [JsonPropertyName("green")]
    public required int Green { get; init; }

    [JsonPropertyName("blue")]
    public required int Blue { get; init; }
}

/// <summary>
/// Port of the tasks and scorers of <c>examples/structured.py</c>: <c>rgb_color</c> asks for an RGB colour as JSON
/// through <c>GenerateConfig(response_schema=ResponseSchema(name="color", json_schema=json_schema(Color)))</c> and
/// validates the completion in <c>score_json_color</c>; <c>rgb_color_regex</c>, <c>rgb_color_choice</c> and
/// <c>rgb_color_grammar</c> do the same through vLLM / SGLang guided decoding fields passed in <c>extra_body</c>,
/// scored by <c>score_regex_color</c>. Deviation: the three guided-decoding tasks are not runnable on Foundry: this
/// port has only the azureai and anthropic Foundry apis, so they raise "Unsupported provider: ..." (a
/// <see cref="PrerequisiteError"/> in place of Python's <c>ValueError</c>) exactly where the Python does for any
/// provider but vllm / sglang, and <c>GenerateConfig.ExtraBody</c> is carried on the config without being written
/// into a Foundry request.
/// </summary>
public static partial class StructuredTasks
{
    public const string RgbColorName = "rgb_color";

    public const string RgbColorRegexName = "rgb_color_regex";

    public const string RgbColorChoiceName = "rgb_color_choice";

    public const string RgbColorGrammarName = "rgb_color_grammar";

    /// <summary>The guided-decoding regex of <c>rgb_color_regex</c> and the pattern of <c>score_regex_color</c>, verbatim.</summary>
    public const string RgbPattern = @"RGB: (\d{1,3}),(\d{1,3}),(\d{1,3})";

    /// <summary>The EBNF grammar of <c>rgb_color_grammar</c>, verbatim (a Python triple-quoted string, leading and trailing newline included).</summary>
    public const string Grammar = "\nroot ::= rgb_color\nrgb_color ::= \"RGB: \" rgb_values\nrgb_values ::= number \",\" number \",\" number\nnumber ::= digit | digit digit | digit digit digit\ndigit ::= \"0\" | \"1\" | \"2\" | \"3\" | \"4\" | \"5\" | \"6\" | \"7\" | \"8\" | \"9\"\n";

    /// <summary>The choices of <c>rgb_color_choice</c>, verbatim.</summary>
    public static readonly IReadOnlyList<string> Choices = ["RGB: 255,255,255", "RGB: 0,0,0"];

    /// <summary>Port of <c>@task def rgb_color()</c>: structured output through a response schema.</summary>
    [Task(RgbColorName)]
    public static EvalTask RgbColor() => new()
    {
        Name = RgbColorName,
        Dataset = ColorDataset(),
        Solver = Solvers.Generate(),
        Scorers = [ScoreJsonColor()],
        Config = new GenerateConfig
        {
            ResponseSchema = new ResponseSchema(name: "color", jsonSchema: JsonSchemaGenerator.JsonSchemaOf<Color>()),
        },
    };

    /// <summary>Port of <c>@task(vllm=True, sglang=True) def rgb_color_regex()</c> for the model the eval runs with (see <see cref="ModelApi"/>).</summary>
    [Task(RgbColorRegexName, "vllm=true", "sglang=true")]
    public static EvalTask RgbColorRegex() => RgbColorRegex(ModelApi());

    /// <summary>The body of <c>rgb_color_regex</c> for a model whose <c>ModelName(model).api</c> is <paramref name="api"/>: guided decoding by regex.</summary>
    public static EvalTask RgbColorRegex(string api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var guidedName = api switch
        {
            "vllm" => "guided_regex",
            "sglang" => "regex",
            _ => throw new PrerequisiteError($"Unsupported provider: {api}"),
        };

        return GuidedTask(RgbColorRegexName, new JsonObject { [guidedName] = RgbPattern });
    }

    /// <summary>Port of <c>@task(vllm=True) def rgb_color_choice()</c> for the model the eval runs with (see <see cref="ModelApi"/>).</summary>
    [Task(RgbColorChoiceName, "vllm=true")]
    public static EvalTask RgbColorChoice() => RgbColorChoice(ModelApi());

    /// <summary>The body of <c>rgb_color_choice</c> for a model whose <c>ModelName(model).api</c> is <paramref name="api"/>: guided decoding by choice (vLLM only).</summary>
    public static EvalTask RgbColorChoice(string api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (api != "vllm")
        {
            throw new PrerequisiteError("Choice is only supported for vLLM");
        }

        return GuidedTask(RgbColorChoiceName, new JsonObject { ["guided_choice"] = new JsonArray(Choices.Select(choice => (JsonNode)JsonValue.Create(choice)).ToArray()) });
    }

    /// <summary>Port of <c>@task(vllm=True, sglang=True) def rgb_color_grammar()</c> for the model the eval runs with (see <see cref="ModelApi"/>).</summary>
    [Task(RgbColorGrammarName, "vllm=true", "sglang=true")]
    public static EvalTask RgbColorGrammar() => RgbColorGrammar(ModelApi());

    /// <summary>The body of <c>rgb_color_grammar</c> for a model whose <c>ModelName(model).api</c> is <paramref name="api"/>: guided decoding by an EBNF grammar.</summary>
    public static EvalTask RgbColorGrammar(string api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var guidedName = api switch
        {
            "vllm" => "guided_grammar",
            "sglang" => "ebnf",
            _ => throw new PrerequisiteError($"Unsupported provider: {api}"),
        };

        return GuidedTask(RgbColorGrammarName, new JsonObject { [guidedName] = Grammar });
    }

    /// <summary>Port of <c>@scorer(metrics=[accuracy(), stderr()]) def score_json_color()</c>.</summary>
    public static ScorerDef ScoreJsonColor() => Scorers.Custom("score_json_color", ScoreJsonColorAsync, Metrics.Accuracy(), Metrics.Stderr());

    /// <summary>Port of <c>@scorer(metrics=[accuracy(), stderr()]) def score_regex_color()</c>.</summary>
    public static ScorerDef ScoreRegexColor() => Scorers.Custom("score_regex_color", ScoreRegexColorAsync, Metrics.Accuracy(), Metrics.Stderr());

    /// <summary>
    /// <c>score_json_color</c>'s <c>score</c>: the completion parsed as a <see cref="Color"/> and compared with the
    /// target as <c>red,green,blue</c>; a completion that is not a colour is INCORRECT with the explanation
    /// "Error parsing response: ..." (Deviation: the JsonException message, not pydantic's ValidationError text).
    /// </summary>
    public static Task<Score> ScoreJsonColorAsync(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = state.Output.Completion;
        try
        {
            var color = JsonSerializer.Deserialize<Color>(completion) ?? throw new JsonException("Input should be a valid dictionary or object to extract fields from");
            var value = $"{color.Red},{color.Green},{color.Blue}" == target.Text ? ScoreConstants.Correct : ScoreConstants.Incorrect;
            return Task.FromResult(new Score(value) { Answer = completion });
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = completion, Explanation = $"Error parsing response: {ex.Message}" });
        }
    }

    /// <summary>
    /// <c>score_regex_color</c>'s <c>score</c>: the first <c>RGB: r,g,b</c> in the completion (Python's <c>re.search</c>)
    /// compared with the target; no match is INCORRECT with "Output does not match RGB format (r,g,b)".
    /// </summary>
    public static Task<Score> ScoreRegexColorAsync(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = state.Output.Completion;
        try
        {
            // Check if the output matches the expected RGB format
            var match = RgbFormat().Match(completion);
            if (match.Success)
            {
                // Extract the RGB values
                var rgbOutput = match.Value["RGB: ".Length..];
                var value = rgbOutput == target.Text ? ScoreConstants.Correct : ScoreConstants.Incorrect;
                return Task.FromResult(new Score(value) { Answer = rgbOutput });
            }

            return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = completion, Explanation = "Output does not match RGB format (r,g,b)" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = completion, Explanation = $"Error processing response: {ex.Message}" });
        }
    }

    /// <summary>
    /// Port of <c>ModelName(get_model()).api</c> as the guided tasks evaluate it when they are built: the api of
    /// <paramref name="model"/> when one is given (the examples runner passes the model it resolved), else the
    /// active sample's model, else the api part of <c>INSPECT_EVAL_MODEL</c> (<c>api/name</c>, the <c>inspectai</c>
    /// CLI's <c>--model</c> environment fallback), else Python's <c>get_model()</c> error as a
    /// <see cref="PrerequisiteError"/>. Deviation: the CLI builds a task before it binds the model, so nothing but
    /// the environment variable can name the model at build time there.
    /// </summary>
    public static string ModelApi(Model? model = null)
    {
        var resolved = model ?? SampleContext.Current?.ActiveModel;
        if (resolved is not null)
        {
            return new ModelName(resolved).Api;
        }

        var spec = Environment.GetEnvironmentVariable("INSPECT_EVAL_MODEL")?.Split(',')[0];
        if (!string.IsNullOrWhiteSpace(spec))
        {
            return new ModelName(spec).Api;
        }

        throw new PrerequisiteError("No model specified (and no model environment variable defined)");
    }

    /// <summary>The two samples every task uses (a fresh dataset each time, as the Python builds one per task call).</summary>
    public static MemoryDataset ColorDataset() => new(
    [
        new Sample("What is the RGB color for white?") { Target = "255,255,255" },
        new Sample("What is the RGB color for black?") { Target = "0,0,0" },
    ]);

    private static EvalTask GuidedTask(string name, JsonObject extraBody) => new()
    {
        Name = name,
        Dataset = ColorDataset(),
        Solver = Solvers.Generate(),
        Scorers = [ScoreRegexColor()],
        Config = new GenerateConfig { ExtraBody = extraBody },
    };

    /// <summary>Python's <c>re.search(r"RGB: (\d{1,3}),(\d{1,3}),(\d{1,3})", ...)</c>.</summary>
    [GeneratedRegex(RgbPattern)]
    private static partial Regex RgbFormat();
}
