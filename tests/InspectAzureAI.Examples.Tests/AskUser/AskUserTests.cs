using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.AskUser;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.AskUser;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/ask_user/demo.py</c> (<see cref="AskUserExample"/>): the task's shape, the
/// kitchen-sink schema, the scripted operator, and the demo end to end without a terminal — the scripted model
/// fires <c>ask_user</c> then <c>submit</c>, the <see cref="ScriptedInputHandler"/> answers (or declines) the form,
/// and the log carries the structured <see cref="InputEvent"/> and the <c>includes()</c> score.
/// </summary>
public sealed class AskUserTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "ask-user-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of main()'s Task)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_shaped_like_the_python_demo()
    {
        var task = AskUserExample.AskUserTask();

        Assert.Equal("task", task.Name);
        Assert.Single(task.Dataset);
        Assert.Equal("Use the ask_user tool to collect order details from the operator, then submit the order summary.", task.Dataset[0].Input.Text);
        Assert.Equal(["ok"], task.Dataset[0].Target.Values);
        Assert.Equal(["includes"], task.Scorers.Select(scorer => scorer.Name));
        Assert.Equal(10, task.MessageLimit);
        Assert.Null(task.Sandbox);
        // The mockllm model is passed to eval() in Python, not to the Task.
        Assert.Null(task.Model);
        Assert.Null(task.Approval);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var method = typeof(AskUserExample).GetMethod(nameof(AskUserExample.AskUserTask));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("ask_user", method.GetCustomAttribute<TaskAttribute>()!.Name);
    }

    [Fact]
    public void the_example_is_discovered_by_the_runner_with_one_task_and_no_sandbox()
    {
        var example = Assert.IsType<AskUserExample>(ExampleRegistry.Default.Find("ask_user"));

        Assert.Equal(["task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context(fake: true)));
        Assert.NotEmpty(example.Deviations);
        Assert.Equal(AskUserExample.FakeModelName, example.CreateFakeModel(Context(fake: true)).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the kitchen-sink schema (port of _kitchen_sink_schema)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_kitchen_sink_schema_has_the_six_fields_and_their_constraints()
    {
        var schema = AskUserExample.KitchenSinkSchema();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(["name", "color", "count", "ratio", "confirm", "tags"], properties.Select(pair => pair.Key));
        Assert.Equal(["name", "color", "count", "confirm", "tags"], schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));

        Assert.Equal("string", properties["name"]!["type"]!.GetValue<string>());
        Assert.Equal("Your name", properties["name"]!["title"]!.GetValue<string>());
        Assert.Equal("Free-form text, min 2 characters.", properties["name"]!["description"]!.GetValue<string>());
        Assert.Equal("2", properties["name"]!["min_length"]!.ToJsonString());

        Assert.Equal(["red", "green", "blue"], properties["color"]!["one_of"]!.AsArray().Select(option => option!["const"]!.GetValue<string>()));
        Assert.Equal(["Red", "Green", "Blue"], properties["color"]!["one_of"]!.AsArray().Select(option => option!["title"]!.GetValue<string>()));

        Assert.Equal("integer", properties["count"]!["type"]!.GetValue<string>());
        Assert.Equal("1", properties["count"]!["minimum"]!.ToJsonString());
        Assert.Equal("100", properties["count"]!["maximum"]!.ToJsonString());

        Assert.Equal("number", properties["ratio"]!["type"]!.GetValue<string>());
        Assert.Equal("Discount (optional)", properties["ratio"]!["title"]!.GetValue<string>());
        Assert.Equal("boolean", properties["confirm"]!["type"]!.GetValue<string>());

        Assert.Equal("array", properties["tags"]!["type"]!.GetValue<string>());
        Assert.Equal("1", properties["tags"]!["min_items"]!.ToJsonString());
        Assert.Equal(["rush", "gift", "signed"], properties["tags"]!["items"]!["any_of"]!.AsArray().Select(option => option!["const"]!.GetValue<string>()));

        // Schema-level title/description are omitted, per the ACP RFD comment in the Python.
        Assert.Null(schema["title"]);
        Assert.Null(schema["description"]);
    }

    [Fact]
    public void the_kitchen_sink_schema_round_trips_through_the_engine_parser()
    {
        var parsed = ElicitationSchema.Parse(AskUserExample.KitchenSinkSchema());

        Assert.Equal(["name", "color", "count", "ratio", "confirm", "tags"], parsed.Properties.Keys);
        Assert.True(parsed.IsRequired("name"));
        Assert.False(parsed.IsRequired("ratio"));
        Assert.Equal(2, Assert.IsType<ElicitationStringProperty>(parsed.Properties["name"]).MinLength);
        Assert.Equal(3, Assert.IsType<ElicitationStringProperty>(parsed.Properties["color"]).OneOf!.Count);
        Assert.Equal(100, Assert.IsType<ElicitationIntegerProperty>(parsed.Properties["count"]).Maximum);
        Assert.IsType<ElicitationNumberProperty>(parsed.Properties["ratio"]);
        Assert.IsType<ElicitationBooleanProperty>(parsed.Properties["confirm"]);
        var tags = Assert.IsType<ElicitationMultiSelectProperty>(parsed.Properties["tags"]);
        Assert.Equal(1, tags.MinItems);
        Assert.Equal(3, Assert.IsType<TitledMultiSelectItems>(tags.Items).AnyOf.Count);
        Assert.Equal(AskUserExample.KitchenSinkSchema().ToJsonString(), parsed.ToJson().ToJsonString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted operator and model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_handler_answers_in_order_then_declines_and_records_every_request()
    {
        var output = new StringWriter();
        var handler = new ScriptedInputHandler([InputResult.Accepted(AskUserExample.FakeAnswer())], output);
        var request = new InputRequest("Q?", AskUserExample.KitchenSink());

        var first = await handler.RequestAsync(request, CancellationToken.None);
        var second = await handler.RequestAsync(request, CancellationToken.None);

        Assert.Equal(InputOutcome.Accepted, first.Outcome);
        Assert.Equal("""{"name":"Ada","color":"green","count":3,"confirm":true,"tags":["rush"]}""", first.ContentJson());
        Assert.Equal(InputOutcome.Declined, second.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("[ask_user] Q?", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ratio:number?", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(InputOutcome.Declined, (await ScriptedInputHandler.Declining(TextWriter.Null).RequestAsync(request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task the_scripted_model_asks_first_then_submits_ok()
    {
        var model = AskUserExample.CreateFakeModel();

        var first = await model.GenerateAsync([new ChatMessageUser(AskUserExample.SampleInput)], cancellationToken: CancellationToken.None);
        var call = Assert.Single(first.Message.ToolCalls!);
        Assert.Equal("ask_user", call.Function);
        Assert.Equal(AskUserExample.AskMessage, call.Arguments["message"]!.GetValue<string>());
        Assert.Equal(AskUserExample.KitchenSinkSchema().ToJsonString(), call.Arguments["schema"]!.ToJsonString());

        var second = await model.GenerateAsync([new ChatMessageUser(AskUserExample.SampleInput), first.Message], cancellationToken: CancellationToken.None);
        var submit = Assert.Single(second.Message.ToolCalls!);
        Assert.Equal(Agents.DefaultSubmitName, submit.Function);
        Assert.Equal("ok", submit.Arguments["answer"]!.GetValue<string>());
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (python examples/ask_user/demo.py, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_demo_runs_offline_with_the_form_answered_and_logs_the_input_event()
    {
        var handler = ScriptedInputHandler.Accepting(AskUserExample.FakeAnswer(), TextWriter.Null);

        var log = await RunAsync(AskUserExample.Build(handler: handler));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(1.0, log.Results!.Scores[0].Metrics["accuracy"].Value);

        // The question the operator was asked, and the structured event Python's request_input records.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(AskUserExample.AskMessage, request.Message);
        var input = Assert.Single(sample.Events.OfType<InputEvent>());
        Assert.Equal(AskUserExample.AskMessage, input.Message);
        Assert.Equal("accepted", input.Outcome);
        Assert.Equal(["name", "color", "count", "ratio", "confirm", "tags"], input.Fields!.Select(field => field.Name));
        Assert.Equal(["string", "string", "integer", "number", "boolean", "array"], input.Fields!.Select(field => field.Type));
        Assert.Equal("Ada", input.Content!["name"]?.ToString());
        Assert.StartsWith(AskUserExample.AskMessage + "\n  name: Ada\n  color: green\n  count: 3", input.Input, StringComparison.Ordinal);

        // The answer reached the model as a JSON object, then the agent submitted.
        // The react loop ends on the submit call (no tool message is appended for it); the transcript shows both calls.
        var tools = sample.Messages.OfType<ChatMessageTool>().ToArray();
        Assert.Equal(["ask_user"], tools.Select(message => message.Function));
        Assert.Equal(["ask_user", Agents.DefaultSubmitName], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
        Assert.Null(tools[0].Error);
        Assert.Equal("""{"name":"Ada","color":"green","count":3,"confirm":true,"tags":["rush"]}""", tools[0].Text);
        var askEvent = Assert.Single(sample.Events.OfType<ToolEvent>(), e => e.Function == "ask_user");
        Assert.Equal(AskUserExample.KitchenSinkSchema().ToJsonString(), askEvent.Arguments["schema"]!.ToJsonString());
    }

    [Fact]
    public async Task a_declined_form_is_a_tool_error_and_the_agent_still_submits()
    {
        var log = await RunAsync(AskUserExample.Build(handler: ScriptedInputHandler.Declining(TextWriter.Null)));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        var ask = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), message => message.Function == "ask_user");
        Assert.NotNull(ask.Error);
        Assert.Equal(BuiltinTools.AskUserDeclined, ask.Error!.Message);
        Assert.Equal("declined", Assert.Single(sample.Events.OfType<InputEvent>()).Outcome);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_with_the_scripted_operator()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["ask_user", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("[ask_user] Please fill out your order details.", text, StringComparison.Ordinal);
        Assert.Contains("-> accepted", text, StringComparison.Ordinal);
        Assert.Contains("status    : success", text, StringComparison.Ordinal);

        var declined = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["ask_user", "--fake", "-T", "decline=true", "--log-dir", _logDir], ExampleRegistry.Default, declined, declined));
        Assert.Contains("-> declined", declined.ToString(), StringComparison.Ordinal);
    }

    private async Task<EvalLog> RunAsync(EvalTask task) =>
        await Eval.RunAsync(
            task,
            new EvalOptions
            {
                Model = AskUserExample.CreateFakeModel(),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

    private static ExampleContext Context(bool fake, IReadOnlyDictionary<string, string>? args = null) =>
        new(Path.Combine(AppContext.BaseDirectory, "ask_user"), null, fake, args ?? new Dictionary<string, string>(), null, null, TextWriter.Null);
}
