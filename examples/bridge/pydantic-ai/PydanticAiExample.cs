using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Maf;
using InspectAzureAI.Provider.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = InspectAzureAI.Provider.Core.ChatMessage;

namespace InspectAzureAI.Examples.Bridge.PydanticAi;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of <c>examples/bridge/pydantic-ai/agent.py</c> <c>AnswerToQueryOutput</c>: the agent's typed output (<c>answer: str = Field(description="The answer to the query")</c>).</summary>
public sealed record AnswerToQueryOutput(
    [property: JsonPropertyName("answer")] [property: Description("The answer to the query")] string Answer)
{
    /// <summary>The JSON-schema name the model is asked to follow (the Pydantic model's name).</summary>
    public const string SchemaName = "AnswerToQueryOutput";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The response format handed to the framework agent: <c>output_type=AnswerToQueryOutput</c> as a JSON-schema response format, which the bridge forwards as the request's <c>ResponseSchema</c>.</summary>
    public static ChatResponseFormat ResponseFormat => ChatResponseFormat.ForJsonSchema<AnswerToQueryOutput>(SerializerOptions, SchemaName, "The answer to the query");

    /// <summary>Port of <c>result.output.model_dump_json()</c>: <c>{"answer":"..."}</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Pydantic's output validation: parses the model's final text as the typed output; the failure reason is
    /// returned instead when the text is not JSON or has no <c>answer</c> string.
    /// </summary>
    public static bool TryParse(string text, out AnswerToQueryOutput? output, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        output = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<AnswerToQueryOutput>(text, SerializerOptions);
            if (parsed?.Answer is null)
            {
                error = "answer: Field required [type=missing]";
                return false;
            }

            output = parsed;
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message} [type=json_invalid]";
            return false;
        }
    }
}

/// <summary>
/// Port of <c>examples/bridge/pydantic-ai/agent.py</c> <c>web_research_agent</c>: a Pydantic AI agent with the
/// <c>WebSearch()</c> capability, the system prompt "You help users find information by searching the web.",
/// <c>output_type=AnswerToQueryOutput</c> and <c>retries={"output": 3}</c>, whose typed result is written back as
/// <c>bridge.state.output.completion</c>. Deviation: Pydantic AI has no C# form, so the agent is a Microsoft Agent
/// Framework <c>ChatClientAgent</c> over <see cref="InspectChatClient"/> (<see cref="AgentFramework.Agent"/>) whose
/// <c>ChatOptions.ResponseFormat</c> is the output's JSON schema (forwarded to the model as a <c>ResponseSchema</c>);
/// the output retries are a small loop here that feeds a validation-feedback user message back to the agent, as
/// Pydantic AI does, up to <see cref="OutputRetries"/> times. The <c>_pydantic_bridge_model()</c> provider switch is
/// unnecessary: the in-process client is provider-agnostic. <c>WebSearch()</c> is the engine's <c>web_search</c> tool.
/// </summary>
public static class WebResearchAgent
{
    /// <summary>The Python <c>@agent</c> function's name.</summary>
    public const string AgentName = "web_research_agent";

    /// <summary>The agent's docstring.</summary>
    public const string AgentDescription = "Pydantic AI Web Research Agent.";

    /// <summary>Pydantic AI's <c>retries={"output": 3}</c>.</summary>
    public const int OutputRetries = 3;

    /// <summary><c>web_research_agent()</c>; <paramref name="searchTool"/> replaces the <c>WebSearch()</c> capability (null builds the engine's <c>web_search</c> over Tavily).</summary>
    public static AgentDef Create(ToolDef? searchTool = null, int outputRetries = OutputRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputRetries);
        var framework = AgentFramework.Agent(new MafAgentOptions
        {
            Name = AgentName,
            Description = AgentDescription,
            Instructions = WebResearch.Instructions,
            Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool(WebResearch.DefaultProvider))],
            // the typed final output is the answer: no submit tool
            Submit = false,
            AgentFactory = (client, tools) => new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = AgentName,
                Description = AgentDescription,
                ChatOptions = new ChatOptions
                {
                    Instructions = WebResearch.Instructions,
                    Tools = tools,
                    ResponseFormat = AnswerToQueryOutput.ResponseFormat,
                },
            }),
        });

        return new AgentDef(AgentName, AgentDescription, (state, cancellationToken) => ExecuteAsync(framework, state, outputRetries, cancellationToken));
    }

    /// <summary>
    /// Port of the <c>run_agent</c> body: <c>agent.run(user_prompt(state.messages).text)</c> with the output retries.
    /// Deviation: the run starts from a new <see cref="AgentState"/> holding only the user prompt and that run's
    /// conversation is returned, where Python's bridge writes the run into the sample's own state object.
    /// </summary>
    private static async Task<AgentState> ExecuteAsync(AgentDef framework, AgentState state, int outputRetries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        // result = await agent.run(user_prompt(state.messages).text)
        var run = new AgentState([new ChatMessageUser(UserPrompt(state.Messages).Text)]);
        for (var attempt = 0; ; attempt++)
        {
            run = await framework.Execute(run, cancellationToken).ConfigureAwait(false);
            if (AnswerToQueryOutput.TryParse(run.Output.Completion, out var output, out var error))
            {
                // bridge.state.output.completion = result.output.model_dump_json()
                run.Output = run.Output with { Completion = output!.ToJson() };
                return run;
            }

            if (attempt >= outputRetries)
            {
                throw new InvalidOperationException($"Exceeded maximum retries ({outputRetries}) for output validation: {error}");
            }

            run.Messages.Add(new ChatMessageUser($"Validation feedback:\n{error}\n\nFix the errors and try again."));
        }
    }

    /// <summary>Port of <c>inspect_ai.model.user_prompt</c>: the last user message of the conversation (a <see cref="InvalidOperationException"/> when there is none).</summary>
    public static ChatMessageUser UserPrompt(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.OfType<ChatMessageUser>().LastOrDefault() ?? throw new InvalidOperationException("No user prompt in the conversation.");
    }
}

/// <summary>Port of <c>examples/bridge/pydantic-ai/task.py</c> <c>research</c>: <c>dataset.json</c>, the agent, <c>model_graded_fact()</c>.</summary>
public static class ResearchTask
{
    public const string TaskName = "research";

    /// <summary>Where the project copied <c>dataset.json</c> next to the assembly.</summary>
    public static string DefaultDatasetPath => Path.Combine(Path.GetDirectoryName(typeof(ResearchTask).Assembly.Location) ?? AppContext.BaseDirectory, "bridge", "pydantic-ai", "dataset.json");

    [Task(TaskName, "bridge=pydantic-ai")]
    public static EvalTask Research() => Build(DefaultDatasetPath, WebResearchAgent.Create());

    public static EvalTask Build(string datasetPath, AgentDef agent) => new()
    {
        Name = TaskName,
        Dataset = WebResearch.Dataset(datasetPath),
        Solver = Agents.AsSolver(agent),
        Scorers = [Scorers.ModelGradedFact()],
    };
}

/// <summary>
/// The <c>bridge/pydantic-ai</c> example for the runner: the <c>research</c> task with the Agent Framework
/// stand-in for the Pydantic AI agent. <c>-T provider=exa</c> swaps the live search provider. Under <c>--fake</c>
/// the model answers with the typed output's JSON (the scripted model does not apply the response schema, so the
/// text is hand-written JSON), the search is canned, and the same scripted model grades (<c>GRADE: C</c>).
/// </summary>
public sealed class PydanticAiExample : IExample
{
    public string Name => "bridge/pydantic-ai";

    public string Description => "A Pydantic AI-style web research agent (Agent Framework stand-in) with a typed AnswerToQueryOutput result and a web_search tool, scored by model_graded_fact";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ResearchTask.TaskName, Build, "three web-research questions answered as {\"answer\": ...} through a JSON-schema response format"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "a deployment that supports json_schema response formats (TAVILY_API_KEY for the live search)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Pydantic AI (Agent, capabilities.WebSearch, output_type, retries) has no C# form: the agent is a Microsoft Agent Framework ChatClientAgent over InspectChatClient (the in-process agent_bridge) whose ChatOptions.ResponseFormat is AnswerToQueryOutput's JSON schema, forwarded to the model as a ResponseSchema; the typed result is still written back as the completion ({\"answer\": ...}).",
        "retries={\"output\": 3} is a hand-written loop: a final message that is not valid AnswerToQueryOutput JSON is answered with a 'Validation feedback: ... Fix the errors and try again.' user message and the agent runs again, up to three times, then the sample errors (Pydantic AI raises UnexpectedModelBehavior). Each run starts from a new AgentState holding only the user prompt and the returned state is that run's conversation (Python's bridge writes the run into the sample's own state object; the retry there happens inside one agent.run).",
        "_pydantic_bridge_model() picks openai-responses:/anthropic:/google: so Pydantic AI's HTTP client matches the patched library; the in-process client is provider-agnostic, so there is no switch.",
        "WebSearch() (the provider's hosted search) is the engine's web_search tool over an external provider (Tavily by default, TAVILY_API_KEY; -T provider=exa for Exa): the Agent Framework bridge carries no internal-provider marker.",
        "Under --fake the search is a canned web_search tool (no key, no network), the scripted model writes the output JSON by hand (it ignores the response schema) and the same scripted model grades; the Python example only runs live.",
        "Python resolves dataset.json relative to task.py; the [Task] method reads the copy next to the built assembly (bridge/pydantic-ai/dataset.json).",
        "The three bridge examples share the Python task name research, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=pydantic-ai` tells them apart and the examples runner (`-- bridge/pydantic-ai`) runs this one.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) =>
        FakeResearchModel.Create(question => new AnswerToQueryOutput(CannedWebSearch.Answer(question) ?? "I could not find an answer.").ToJson());

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx) =>
        ResearchTask.Build(ctx.DataPath("dataset.json"), WebResearchAgent.Create(WebResearch.SearchTool(ctx)));
}
