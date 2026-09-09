using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Builtin;

namespace InspectAzureAI.Examples.InlineCards;

/// <summary>
/// Port of <c>examples/inline_cards/question.py</c> <c>question_demo</c>: a demo task for the inline elicitation
/// (<c>ask_user</c>) card. A mockllm-driven react agent fires <c>ask_user</c> twice — a single free-text question,
/// then a two-field form — then submits. Deviation: the inline <c>_ElicitationCard</c> of the Textual/ACP TUI is not
/// ported; the questions are answered at the console (<see cref="ConsoleInputHandler"/>), or by script under
/// <c>--fake</c> (<see cref="Runner.ScriptedInputHandler"/>).
/// </summary>
public static class QuestionDemo
{
    /// <summary>The task name (<c>@task def question_demo</c>).</summary>
    public const string TaskName = "question_demo";

    /// <summary>The <c>Sample(input=...)</c> of <c>question_demo</c>, verbatim.</summary>
    public const string SampleInput = "Use the ask_user tool to collect the API key, then any follow-up details the agent asks for, then submit 'ok'.";

    /// <summary>The <c>Sample(target=["ok"])</c>.</summary>
    public const string SampleTarget = "ok";

    /// <summary>The <c>AgentSubmit(description=...)</c>, verbatim.</summary>
    public const string SubmitDescription = "Submit the final answer once the operator has answered.";

    /// <summary>The first <c>ask_user</c> call's <c>message</c>, verbatim.</summary>
    public const string FirstMessage = "What's the API key for the staging service?";

    /// <summary>The second <c>ask_user</c> call's <c>message</c>, verbatim.</summary>
    public const string SecondMessage = "A couple more details before I file the key:";

    /// <summary>Port of <c>_single_question_schema()</c>: a single required free-text answer (the "Simple required string" shape of <c>ask_user</c>'s docs).</summary>
    public static JsonObject SingleQuestionSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["answer"] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Your answer",
                ["description"] = "Free-form text.",
                ["min_length"] = 1,
            },
        },
        ["required"] = new JsonArray("answer"),
    };

    /// <summary>Port of <c>_two_question_schema()</c>: two required free-text answers.</summary>
    public static JsonObject TwoQuestionSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["environment"] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Environment",
                ["description"] = "Which environment? (staging / prod / …)",
                ["min_length"] = 1,
            },
            ["expiry"] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Expiry",
                ["description"] = "Approximate expiry date (free-form).",
                ["min_length"] = 1,
            },
        },
        ["required"] = new JsonArray("environment", "expiry"),
    };

    /// <summary>The scripted operator's answers under <c>--fake</c>: the key, then the environment and expiry.</summary>
    public static IReadOnlyList<InputResult> FakeAnswers() =>
    [
        InputResult.Accepted(new Dictionary<string, object?>(StringComparer.Ordinal) { ["answer"] = "sk-123" }),
        InputResult.Accepted(new Dictionary<string, object?>(StringComparer.Ordinal) { ["environment"] = "staging", ["expiry"] = "2027-01" }),
    ];

    /// <summary>Port of the <c>get_model("mockllm/model", custom_outputs=[...])</c> of <c>question_demo</c>: two elicitations, then submit.</summary>
    public static InspectAzureAI.Eval.Model.Model Model() => MockLlm.Create(
        () => ScriptedTurn.ToolCall("ask_user", new JsonObject { ["message"] = FirstMessage, ["schema"] = SingleQuestionSchema() }),
        () => ScriptedTurn.ToolCall("ask_user", new JsonObject { ["message"] = SecondMessage, ["schema"] = TwoQuestionSchema() }),
        () => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = SampleTarget }));

    /// <summary>Port of <c>@task def question_demo()</c>, discoverable by the <c>inspectai</c> CLI; the operator answers at the console.</summary>
    [Task(TaskName)]
    public static EvalTask QuestionDemoTask() => Build();

    /// <summary>
    /// The task: one sample, <c>react(tools=[ask_user()], submit=AgentSubmit("submit", ...))</c>, <c>includes()</c>,
    /// <c>message_limit=10</c>, and its own mockllm model. <paramref name="handler"/> is what <c>ask_user</c> asks
    /// (null: the console, through <see cref="InputHandlers.Default"/>).
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
        Model = Model(),
        Sandbox = sandbox,
    };
}
