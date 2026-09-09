using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>
/// Port of <c>agent/_human/commands/score.py</c> <c>ScoreCommand</c>: <c>task score [answer]</c> validates, then
/// runs the task's scorers (<see cref="Agents.ScoreAsync"/>) over the agent state, with the answer swapped in as
/// a <c>human_agent</c> output when given, and records an <see cref="IntermediateScoring"/>.
/// </summary>
public sealed class ScoreCommand(AgentState state) : HumanAgentCommand
{
    private readonly AgentState _state = state ?? throw new ArgumentNullException(nameof(state));

    public override string Name => "score";

    public override string Description => "Score the task to check progress.";

    public override int Group => 1;

    public override IReadOnlyList<HumanAgentCliArg> CliArgs =>
        [new HumanAgentCliArg("answer", "Answer to submit for scoring (optional, not required for all tasks)")];

    public override string CliSource => """
        def score(args: Namespace) -> None:
            # first validate (print and exit if we get a str back)
            call_args = vars(args)
            error = call_human_agent("validate", **call_args)
            if error:
                print(error)
                return

            print(call_human_agent("score", **call_args))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => async (parameters, cancellationToken) =>
    {
        var answer = StringParam(parameters, "answer");
        IReadOnlyList<Score> result;
        if (!string.IsNullOrEmpty(answer))
        {
            // make a copy of the agent state, add the answer, then score
            var copy = new AgentState(_state.Messages) { Output = ModelOutput.FromContent(HumanCli.ModelName, answer) };
            result = await Agents.ScoreAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await Agents.ScoreAsync(_state, cancellationToken).ConfigureAwait(false);
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("The task's scorers returned no score.");
        }

        // record the scoring action in our state
        state.Scorings = [.. state.Scorings, new IntermediateScoring(state.Time(), result)];

        // notify user
        return Text($"Answer: {result[0].Answer}, Score: {HumanAgentText.ScoreValueText(result[0])}");
    };

    public override string? Activity(JsonObject parameters, JsonNode? result) =>
        result is JsonValue value && value.TryGetValue<string>(out var text) ? $"Intermediate score. {text}" : null;
}
