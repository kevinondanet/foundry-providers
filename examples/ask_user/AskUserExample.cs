using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.AskUser;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/ask_user/demo.py</c>: a demo eval for the <c>ask_user</c> question panel. A scripted
/// (<c>mockllm</c>) model emits two tool calls: <c>ask_user</c> with a kitchen-sink schema (string + enum string +
/// integer + number + boolean + multi-select), then <c>submit</c> with "ok" whatever the operator entered, and
/// <c>includes()</c> scores it. Deviation: the Textual "Question" tab (<c>display="full"</c>) is not ported; the form
/// is answered field by field at the console (<see cref="ConsoleInputHandler"/>, the handler Python falls back to
/// without a Textual display), and under <c>--fake</c> by the <see cref="ScriptedInputHandler"/> so nothing waits on
/// the terminal. The Python script builds an anonymous <c>Task</c> in <c>main()</c>, which Inspect names
/// <c>task</c>; that name is kept here.
/// </summary>
public sealed class AskUserExample : IExample
{
    /// <summary>The Python example's name (its folder).</summary>
    public const string ExampleName = "ask_user";

    /// <summary>The task name: Python's default for a <c>Task</c> created outside a <c>@task</c> function.</summary>
    public const string TaskName = "task";

    /// <summary>The <c>Sample(input=...)</c> of the demo, verbatim.</summary>
    public const string SampleInput = "Use the ask_user tool to collect order details from the operator, then submit the order summary.";

    /// <summary>The <c>Sample(target=["ok"])</c> of the demo.</summary>
    public const string SampleTarget = "ok";

    /// <summary>The <c>ask_user</c> call's <c>message</c> argument, verbatim.</summary>
    public const string AskMessage = "Please fill out your order details.";

    /// <summary>The <c>AgentSubmit(description=...)</c> of the demo, verbatim.</summary>
    public const string SubmitDescription = "Submit the final summary once the form is filled.";

    /// <summary>The <c>-T</c> key that makes the scripted operator decline the form (the <c>User declined to answer the question.</c> tool error path).</summary>
    public const string DeclineArg = "decline";

    /// <summary>The scripted model's name, as it appears in the banner and the log (Python: <c>mockllm/model</c>).</summary>
    public const string FakeModelName = "ask-user-mockllm";

    /// <summary>More turns than a run needs (one sample; the script is two turns per epoch).</summary>
    private const int FakeTurnBudget = 1000;

    public string Name => ExampleName;

    public string Description => "The ask_user tool: a scripted agent asks the operator a kitchen-sink form (string, select, integer, number, boolean, multi-select) at the console, then submits";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, ctx => Build(ctx), "one sample; the scripted agent calls ask_user with the kitchen-sink schema, then submit(\"ok\"), scored by includes()"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The Textual \"Question\" tab (eval(..., display=\"full\")) is not ported: the form is answered one field at a time at the console (ConsoleInputHandler, the handler Python itself falls back to when no Textual display is active); type :decline at any prompt to decline. There is no display=\"full\"; --display conversation prints the model turns instead.",
        "Under --fake the operator is scripted too (ScriptedInputHandler): the form is answered with name=Ada, color=green, count=3, confirm=true, tags=[rush] (ratio left blank), or declined with -T decline=true, so an offline run never blocks. The Python demo always waits for the operator.",
        "The Python script builds an anonymous Task in main() and runs it with eval(); Inspect names such a task \"task\", and so does this port (the [Task] attribute registers it with the inspectai CLI as ask_user). The mockllm model of the Python is the runner's --fake model here; a Foundry --model runs the same task with a real model at the console.",
        "The kitchen-sink schema is built from the port's ElicitationSchema records and serialised with ToJson(), which omits unset keys, where the Python's model_dump(mode=\"json\") writes them as null (min_length: null, ...); both parse to the same form.",
        "The task takes the sandbox the runner resolves (--sandbox); the Python task declares none, and none is the default here too.",
    ];

    /// <summary>Port of the <c>get_model("mockllm/model", custom_outputs=[...])</c> of <c>main()</c>: the two scripted turns.</summary>
    public Model CreateFakeModel(ExampleContext ctx) => CreateFakeModel();

    /// <summary>The demo needs no sandbox.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>Port of <c>main()</c>'s <c>Task</c>, discoverable by the <c>inspectai</c> CLI (<c>eval ask_user --assembly ...</c>); the operator answers at the console.</summary>
    [Task(ExampleName)]
    public static EvalTask AskUserTask() => Build(sandbox: null, handler: null);

    /// <summary>
    /// The scripted model of <c>main()</c>: turn one calls <c>ask_user</c> with the kitchen-sink schema, every later
    /// turn calls <c>submit</c> with <c>"ok"</c> so the scorer passes whatever the operator entered. Turns are keyed
    /// on the conversation, so epochs and retries stay deterministic.
    /// </summary>
    public static Model CreateFakeModel() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), FakeTurnBudget), FakeModelName));

    /// <summary>
    /// Port of <c>_kitchen_sink_schema()</c>: per the ACP RFD and MCP elicitation spec the conversational ask lives on
    /// the request's <c>message</c>; schema-level title/description are omitted and per-property title/description
    /// carry the field labels. Returned as the JSON the tool receives (<c>model_dump(mode="json")</c>).
    /// </summary>
    public static JsonObject KitchenSinkSchema() => KitchenSink().ToJson();

    /// <summary>The typed form of <see cref="KitchenSinkSchema"/> (Python's <c>ElicitationSchema(...)</c> model).</summary>
    public static ElicitationSchema KitchenSink() => new()
    {
        Properties = new OrderedDictionary<string, ElicitationProperty>(StringComparer.Ordinal)
        {
            ["name"] = new ElicitationStringProperty
            {
                Title = "Your name",
                Description = "Free-form text, min 2 characters.",
                MinLength = 2,
            },
            ["color"] = new ElicitationStringProperty
            {
                Title = "Favourite color",
                Description = "Pick one (renders as a Select).",
                OneOf =
                [
                    new EnumOption("red", "Red"),
                    new EnumOption("green", "Green"),
                    new EnumOption("blue", "Blue"),
                ],
            },
            ["count"] = new ElicitationIntegerProperty
            {
                Title = "Quantity",
                Description = "Between 1 and 100.",
                Minimum = 1,
                Maximum = 100,
            },
            ["ratio"] = new ElicitationNumberProperty
            {
                Title = "Discount (optional)",
                Description = "Decimal — leave blank for none.",
            },
            ["confirm"] = new ElicitationBooleanProperty
            {
                Title = "Confirm",
                Description = "Tick to confirm the order.",
            },
            ["tags"] = new ElicitationMultiSelectProperty(new TitledMultiSelectItems(
            [
                new EnumOption("rush", "Rush delivery"),
                new EnumOption("gift", "Gift wrap"),
                new EnumOption("signed", "Signature required"),
            ]))
            {
                Title = "Tags",
                Description = "Pick one or more.",
                MinItems = 1,
            },
        },
        Required = ["name", "color", "count", "confirm", "tags"],
    };

    /// <summary>The scripted operator's answer under <c>--fake</c>: every required field, the optional ratio left blank.</summary>
    public static IReadOnlyDictionary<string, object?> FakeAnswer() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["name"] = "Ada",
        ["color"] = "green",
        ["count"] = 3L,
        ["confirm"] = true,
        ["tags"] = new List<string> { "rush" },
    };

    /// <summary>
    /// Port of <c>main()</c>'s <c>Task(...)</c>: one sample, <c>react(tools=[ask_user()], submit=AgentSubmit("submit", ...))</c>,
    /// <c>includes()</c>, <c>message_limit=10</c>. <paramref name="handler"/> is what <c>ask_user</c> asks (null: the
    /// console, through <see cref="InputHandlers.Default"/>).
    /// </summary>
    public static EvalTask Build(SandboxSpec? sandbox = null, IInputHandler? handler = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(SampleInput) { Target = new Target([SampleTarget]) }]),
        Solver = Agents.AsSolver(Agents.React(
            tools: [BuiltinTools.AskUser(handler)],
            submit: new AgentSubmit { Name = Agents.DefaultSubmitName, Description = SubmitDescription })),
        Scorers = [Scorers.Includes()],
        MessageLimit = 10,
        Sandbox = sandbox,
    };

    /// <summary>The runner's build: the console operator, or under <c>--fake</c> the scripted one (accepting, or declining with <c>-T decline=true</c>).</summary>
    public static EvalTask Build(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IInputHandler? handler = null;
        if (ctx.Fake)
        {
            handler = ctx.TaskArgBool(DeclineArg, false)
                ? ScriptedInputHandler.Declining(ctx.Out)
                : ScriptedInputHandler.Accepting(FakeAnswer(), ctx.Out, times: FakeTurnBudget);
        }

        return Build(ctx.Sandbox, handler);
    }

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> _)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        var turn = step == 0
            ? ScriptedTurn.ToolCall("ask_user", new JsonObject { ["message"] = AskMessage, ["schema"] = KitchenSinkSchema() })
            : ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = SampleTarget });
        return turn.Output!;
    }
}
