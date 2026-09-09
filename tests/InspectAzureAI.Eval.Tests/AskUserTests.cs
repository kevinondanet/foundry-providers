using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>The <c>ask_user</c> tool (<c>tool/_tools/_ask_user.py</c>), <c>request_input</c> and the console handler (<c>util/_input/</c>) against a scripted console.</summary>
public sealed class AskUserTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

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

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The kitchen-sink form of <c>examples/ask_user/demo.py</c>, built with the typed records.</summary>
    private static ElicitationSchema KitchenSink() => new()
    {
        Properties =
        {
            ["name"] = new ElicitationStringProperty { Title = "Your name", Description = "Free-form text, min 2 characters.", MinLength = 2 },
            ["color"] = new ElicitationStringProperty
            {
                Title = "Favourite color",
                Description = "Pick one (renders as a Select).",
                OneOf = [new EnumOption("red", "Red"), new EnumOption("green", "Green"), new EnumOption("blue", "Blue")],
            },
            ["count"] = new ElicitationIntegerProperty { Title = "Quantity", Description = "Between 1 and 100.", Minimum = 1, Maximum = 100 },
            ["ratio"] = new ElicitationNumberProperty { Title = "Discount (optional)", Description = "Decimal — leave blank for none." },
            ["confirm"] = new ElicitationBooleanProperty { Title = "Confirm", Description = "Tick to confirm the order." },
            ["tags"] = new ElicitationMultiSelectProperty(new TitledMultiSelectItems([new EnumOption("rush", "Rush delivery"), new EnumOption("gift", "Gift wrap"), new EnumOption("signed", "Signature required")]))
            {
                Title = "Tags",
                Description = "Pick one or more.",
                MinItems = 1,
            },
        },
        Required = ["name", "color", "count", "confirm", "tags"],
    };

    private static (InputConsole Console, StringWriter Output) ScriptedConsole(params string[] lines)
    {
        var output = new StringWriter();
        return (new InputConsole(new StringReader(string.Join("\n", lines) + "\n"), output), output);
    }

    private static async Task<ChatMessageTool> Execute(ToolDef tool, string message, JsonObject schema)
    {
        var call = new ToolCall("c1", "ask_user", new JsonObject { ["message"] = message, ["schema"] = schema });
        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("go"), new ChatMessageAssistant("", toolCalls: [call])], [tool]);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    // ----------------------------------------------------------------------------------------------------------
    // tool definition
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_tool_matches_the_python_tool_info()
    {
        var tool = BuiltinTools.AskUser();

        Assert.Equal("ask_user", tool.Name);
        Assert.StartsWith("Ask the human operator a structured question and return their answer.\n\nThe operator is shown", tool.Description, StringComparison.Ordinal);
        Assert.EndsWith("or `items.enum` for bare string choices", tool.Description, StringComparison.Ordinal);
        Assert.Contains("\n  {\"type\": \"object\",\n   \"properties\": {\"name\": {\"type\": \"string\", \"description\": \"Your name\"}},\n   \"required\": [\"name\"]}\n", tool.Description, StringComparison.Ordinal);
        var expected = Json("""{"type": "object", "properties": {"message": {"type": "string", "description": "The prompt to show the operator."}, "schema": {"type": "object", "description": "JSON-Schema-shaped dict describing the answer fields.", "additionalProperties": {}}}, "required": ["message", "schema"], "additionalProperties": false}""");
        Assert.True(JsonNode.DeepEquals(expected, tool.Parameters.ToJson()), tool.Parameters.ToJson().ToJsonString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // round trip through the console handler
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_kitchen_sink_form_round_trips_and_records_an_input_event()
    {
        using var scope = new SampleContextScope();
        var (console, output) = ScriptedConsole("Al", "green", "5", "", "y", "1,3");
        var tool = BuiltinTools.AskUser(new ConsoleInputHandler(console));

        var message = await Execute(tool, "Please fill out your order details.", KitchenSink().ToJson());

        Assert.Null(message.Error);
        var expected = Json("""{"name":"Al","color":"green","count":5,"confirm":true,"tags":["rush","signed"]}""");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(message.Text)), message.Text);
        Assert.Equal(expected.ToJsonString(), message.Text);

        var text = output.ToString();
        Assert.Contains("Please fill out your order details.\n(Type :decline at any prompt to decline.)\n", text, StringComparison.Ordinal);
        Assert.Contains("Free-form text, min 2 characters.\nYour name: ", text, StringComparison.Ordinal);
        Assert.Contains("  red: Red\n  green: Green\n  blue: Blue\nFavourite color: ", text, StringComparison.Ordinal);
        Assert.Contains("(>= 1, <= 100)\nQuantity: ", text, StringComparison.Ordinal);
        Assert.Contains("Confirm [y/n]: ", text, StringComparison.Ordinal);
        Assert.Contains("  1: Rush delivery (rush)\n  2: Gift wrap (gift)\n  3: Signature required (signed)\n(min 1)\nTags (comma-separated indices): ", text, StringComparison.Ordinal);

        var input = Assert.Single(scope.Transcript.Events.OfType<InputEvent>());
        Assert.Equal("accepted", input.Outcome);
        Assert.Equal("Please fill out your order details.", input.Message);
        Assert.Equal(["name", "color", "count", "ratio", "confirm", "tags"], input.Fields!.Select(f => f.Name));
        Assert.Equal(["string", "string", "integer", "number", "boolean", "array"], input.Fields!.Select(f => f.Type));
        Assert.Equal("Between 1 and 100.", input.Fields![2].Description);
        Assert.Equal("Please fill out your order details.\n  name: Al\n  color: green\n  count: 5\n  confirm: True\n  tags: ['rush', 'signed']", input.Input);
        Assert.Equal(input.Input, input.InputAnsi);
        Assert.Equal(5L, input.Content!["count"]);
        Assert.False(input.Content.ContainsKey("ratio"));
    }

    [Fact]
    public async Task invalid_answers_re_prompt_with_pythons_messages()
    {
        var (console, output) = ScriptedConsole(
            "A", "Al",
            "purple", "green",
            "0", "500", "x", "5",
            "abc", "1.5",
            "maybe", "n",
            "9", "a,b", "", "1,1,2");
        var handler = new ConsoleInputHandler(console);

        var result = await InputHandlers.RequestInputAsync("Order?", KitchenSink(), handler);

        Assert.Equal(InputOutcome.Accepted, result.Outcome);
        Assert.Equal("Al", result.Content!["name"]);
        Assert.Equal("green", result.Content["color"]);
        Assert.Equal(5L, result.Content["count"]);
        Assert.Equal(1.5, result.Content["ratio"]);
        Assert.Equal(false, result.Content["confirm"]);
        Assert.Equal(["rush", "gift"], Assert.IsType<List<string>>(result.Content["tags"]));

        var text = output.ToString();
        Assert.Contains("Must be at least 2 characters.\n", text, StringComparison.Ordinal);
        Assert.Contains("Please choose one of: red, green, blue.\n", text, StringComparison.Ordinal);
        Assert.Contains("Must be >= 1.\n", text, StringComparison.Ordinal);
        Assert.Contains("Must be <= 100.\n", text, StringComparison.Ordinal);
        Assert.Contains("Please enter a valid integer.\n", text, StringComparison.Ordinal);
        Assert.Contains("Please enter a valid number.\n", text, StringComparison.Ordinal);
        Assert.Contains("Please answer y or n.\n", text, StringComparison.Ordinal);
        Assert.Contains("Indices must be between 1 and 3.\n", text, StringComparison.Ordinal);
        Assert.Contains("Enter comma-separated index numbers (e.g. 1,3).\n", text, StringComparison.Ordinal);
        Assert.Contains("Select at least 1.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task required_fields_re_prompt_and_optional_blank_answers_are_omitted()
    {
        var schema = new ElicitationSchema
        {
            Properties =
            {
                ["name"] = new ElicitationStringProperty(),
                ["note"] = new ElicitationStringProperty { Default = "none" },
                ["flag"] = new ElicitationBooleanProperty(),
                ["tags"] = new ElicitationMultiSelectProperty(new StringMultiSelectItems(["a", "b"])),
            },
            Required = ["name", "tags"],
        };
        var (console, output) = ScriptedConsole("", "Al", "", "", "");

        var result = await InputHandlers.RequestInputAsync("Q", schema, new ConsoleInputHandler(console));

        Assert.Equal(InputOutcome.Accepted, result.Outcome);
        Assert.Equal(["name", "note", "tags"], result.Content!.Keys.Order());
        Assert.Equal("none", result.Content["note"]);
        Assert.Empty(Assert.IsType<List<string>>(result.Content["tags"]));
        var text = output.ToString();
        Assert.Contains("name: name is required.\nname: ", text, StringComparison.Ordinal);
        Assert.Contains("note (none): ", text, StringComparison.Ordinal);
        Assert.Contains("  1: a\n  2: b\ntags (comma-separated indices): ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task typing_decline_at_any_prompt_declines_the_question()
    {
        using var scope = new SampleContextScope();
        var (console, _) = ScriptedConsole("Al", ":decline");
        var tool = BuiltinTools.AskUser(new ConsoleInputHandler(console));

        var message = await Execute(tool, "Order?", KitchenSink().ToJson());

        Assert.Equal(BuiltinTools.AskUserDeclined, message.Error!.Message);
        var input = Assert.Single(scope.Transcript.Events.OfType<InputEvent>());
        Assert.Equal("declined", input.Outcome);
        Assert.Null(input.Content);
        Assert.Equal("Order?\n[declined]", input.Input);
    }

    [Fact]
    public async Task exhausted_input_or_cancellation_cancels_the_question()
    {
        using var scope = new SampleContextScope();
        var (console, _) = ScriptedConsole();
        var tool = BuiltinTools.AskUser(new ConsoleInputHandler(console));

        var message = await Execute(tool, "Order?", KitchenSink().ToJson());

        Assert.Equal(BuiltinTools.AskUserCancelled, message.Error!.Message);
        Assert.Equal("cancelled", Assert.Single(scope.Transcript.Events.OfType<InputEvent>()).Outcome);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (blocking, _) = ScriptedConsole("Al");
        var result = await new ConsoleInputHandler(blocking).RequestAsync(new InputRequest("Q", KitchenSink()), cts.Token);
        Assert.Equal(InputOutcome.Cancelled, result.Outcome);
    }

    // ----------------------------------------------------------------------------------------------------------
    // schema validation
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"type":"object","properties":{"when":{"type":"date"}}}""", "Invalid schema: property 'when' has unsupported type 'date'")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"string","min_length":"a"}}}""", "Invalid schema: properties.x.min_length: Input should be a valid integer")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"array","items":{"type":"object"}}}}""", "Invalid schema: property 'x' has unsupported items type 'object'")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"array"}}}""", "Invalid schema: properties.x.items: Field required")]
    [InlineData("""{"type":"object","properties":{"x":{"title":"no type"}}}""", "Invalid schema: properties.x.type: Field required")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"string","one_of":[{"const":"a"}]}}}""", "Invalid schema: properties.x.one_of.0.title: Field required")]
    [InlineData("""{"type":"object","properties":"nope"}""", "Invalid schema: properties: Input should be a valid dictionary")]
    public async Task an_invalid_schema_is_a_tool_error_the_model_can_correct(string schema, string error)
    {
        var handler = new ScriptedInputHandler(InputResult.Accepted(new Dictionary<string, object?>()));

        var message = await Execute(BuiltinTools.AskUser(handler), "Q", Json(schema));

        Assert.Equal(error, message.Error!.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void type_names_are_lowercased_and_plain_json_schema_dicts_parse()
    {
        // examples/inline_cards/question.py passes plain dicts with min_length; Gemini emits upper-case types
        var schema = ElicitationSchema.Parse(Json("""
            {"type": "OBJECT",
             "properties": {
               "answer": {"type": "STRING", "title": "Your answer", "description": "Free-form text.", "min_length": 1},
               "timeout": {"type": "Integer", "minimum": 1, "maximum": 300, "default": 30},
               "ratio": {"type": "number", "minimum": 0.5},
               "ok": {"type": "boolean", "default": true},
               "status": {"type": "array", "items": {"enum": ["draft", "pub"]}, "max_items": 1, "default": ["pub"]},
               "color": {"type": "string", "enum": ["red", "green"], "extra": "ignored"}
             },
             "required": ["answer"]}
            """));

        Assert.Equal(["answer", "timeout", "ratio", "ok", "status", "color"], schema.Properties.Keys);
        var answer = Assert.IsType<ElicitationStringProperty>(schema.Properties["answer"]);
        Assert.Equal(1, answer.MinLength);
        Assert.Equal("Your answer", answer.Title);
        var timeout = Assert.IsType<ElicitationIntegerProperty>(schema.Properties["timeout"]);
        Assert.Equal(1L, timeout.Minimum);
        Assert.Equal(300L, timeout.Maximum);
        Assert.Equal(30L, timeout.Default);
        Assert.Equal(0.5, Assert.IsType<ElicitationNumberProperty>(schema.Properties["ratio"]).Minimum);
        Assert.True(Assert.IsType<ElicitationBooleanProperty>(schema.Properties["ok"]).Default);
        var status = Assert.IsType<ElicitationMultiSelectProperty>(schema.Properties["status"]);
        Assert.Equal(["draft", "pub"], Assert.IsType<StringMultiSelectItems>(status.Items).Enum);
        Assert.Equal(1, status.MaxItems);
        Assert.Equal(["pub"], status.Default);
        Assert.Equal(["red", "green"], Assert.IsType<ElicitationStringProperty>(schema.Properties["color"]).Enum);
        Assert.True(schema.IsRequired("answer"));
        Assert.False(schema.IsRequired("timeout"));

        // ToJson round-trips through Parse
        var again = ElicitationSchema.Parse(schema.ToJson());
        Assert.True(JsonNode.DeepEquals(schema.ToJson(), again.ToJson()));
        Assert.Equal("string", schema.ToJson()["properties"]!["answer"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void validation_predicates_match_the_python_ones()
    {
        static string? Error<T>((T? Value, string? Error) result) => result.Error;

        var text = new ElicitationStringProperty { MinLength = 2, MaxLength = 4, Pattern = "[a-z]+" };
        Assert.Equal(("abc", (string?)null), InputValidation.ValidateString(text, "abc"));
        Assert.Equal("Must be at least 2 characters.", Error(InputValidation.ValidateString(text, "a")));
        Assert.Equal("Must be at most 4 characters.", Error(InputValidation.ValidateString(text, "abcde")));
        Assert.Equal("Must match pattern: [a-z]+", Error(InputValidation.ValidateString(text, "ab1")));
        Assert.Equal("Please choose one of: a, b.", Error(InputValidation.ValidateString(new ElicitationStringProperty { Enum = ["a", "b"] }, "c")));

        var number = new ElicitationNumberProperty { Minimum = 1, Maximum = 2.5 };
        Assert.Equal("Must be >= 1.0.", Error(InputValidation.ValidateNumber(number, "0.5")));
        Assert.Equal("Must be <= 2.5.", Error(InputValidation.ValidateNumber(number, "3")));
        Assert.Equal((2.0, (string?)null), InputValidation.ValidateNumber(number, "2"));
        Assert.Equal((7L, (string?)null), InputValidation.ValidateInteger(new ElicitationIntegerProperty(), " 7 "));
        Assert.Equal("Please enter a valid integer.", Error(InputValidation.ValidateInteger(new ElicitationIntegerProperty(), "7.5")));

        var multi = new ElicitationMultiSelectProperty(new StringMultiSelectItems(["a", "b", "c"])) { MinItems = 2, MaxItems = 2 };
        Assert.Equal("'z' is not a valid choice.", Error(InputValidation.ValidateMultiSelect(multi, ["a", "z"])));
        Assert.Equal("Select at least 2.", Error(InputValidation.ValidateMultiSelect(multi, ["a", "a"])));
        Assert.Equal("Select at most 2.", Error(InputValidation.ValidateMultiSelect(multi, ["a", "b", "c"])));
        var (values, error) = InputValidation.ValidateMultiSelect(multi, ["b", "a", "b"]);
        Assert.Null(error);
        Assert.Equal(["b", "a"], values);
        Assert.Equal([("a", "a"), ("b", "b"), ("c", "c")], InputValidation.MultiSelectOptions(multi));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the handler seam and an end-to-end eval
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_default_handler_is_used_when_none_is_given()
    {
        var previous = InputHandlers.Default;
        try
        {
            var handler = new ScriptedInputHandler(InputResult.Accepted(new Dictionary<string, object?> { ["answer"] = "sk-123" }));
            InputHandlers.Default = handler;

            var message = await Execute(BuiltinTools.AskUser(), "What's the API key?", Json("""{"type":"object","properties":{"answer":{"type":"string","min_length":1}},"required":["answer"]}"""));

            Assert.Equal("""{"answer":"sk-123"}""", message.Text);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("What's the API key?", request.Message);
            Assert.IsType<ElicitationStringProperty>(Assert.Single(request.Schema.Properties).Value);
        }
        finally
        {
            InputHandlers.Default = previous;
        }

        Assert.Throws<ArgumentNullException>(() => InputHandlers.Default = null!);
    }

    [Fact]
    public async Task an_eval_using_ask_user_logs_the_input_event()
    {
        var (console, _) = ScriptedConsole("staging", "next week");
        var tool = BuiltinTools.AskUser(new ConsoleInputHandler(console));
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("ask_user", new
            {
                message = "A couple more details before I file the key:",
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        environment = new { type = "string", title = "Environment", min_length = 1 },
                        expiry = new { type = "string", title = "Expiry", min_length = 1 },
                    },
                    required = new[] { "environment", "expiry" },
                },
            }),
            ScriptedTurn.Text("ok"));
        var task = new EvalTask
        {
            Name = "question_demo",
            Dataset = new MemoryDataset([new Sample("Collect the details, then submit 'ok'.") { Target = "ok" }]),
            Solver = Solvers.Chain(Solvers.UseTools(tool), Solvers.Generate()),
            Scorers = [Scorers.Includes()],
        };

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, MaxSamples = 1, LogFormat = LogFormat.Json });

        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        var toolMessage = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Equal("""{"environment":"staging","expiry":"next week"}""", toolMessage.Text);
        var input = Assert.Single(sample.Events.OfType<InputEvent>());
        Assert.Equal("accepted", input.Outcome);
        Assert.Equal("staging", input.Content!["environment"]);
        Assert.Equal(["environment", "expiry"], input.Fields!.Select(f => f.Name));
    }

    private sealed class ScriptedInputHandler(InputResult result) : IInputHandler
    {
        public List<InputRequest> Requests { get; } = [];

        public Task<InputResult> RequestAsync(InputRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
