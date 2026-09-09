using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.EarlyStopping;

using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port of the <c>popularity</c> task of <c>examples/early_stopping.py</c>: the <c>popularity</c> example dataset
/// (question, answer_matching_behavior, label_confidence), a system message, generate, match, five epochs, a
/// <see cref="TestEarlyStopping"/> manager and the <c>mockllm/model</c> model. Deviation: the <c>[Task]</c> is
/// registered as <c>early_stopping</c> (the runner's task keeps the name <c>popularity</c>) because the
/// <c>popularity</c> example registers <c>popularity</c> in the same assembly and the CLI registry only
/// disambiguates by assembly.
/// </summary>
public static class EarlyStoppingTasks
{
    /// <summary>The Python task's name.</summary>
    public const string TaskName = "popularity";

    /// <summary>The name the <c>inspectai</c> CLI discovers the task under (see the class remarks).</summary>
    public const string CliTaskName = "early_stopping";

    /// <summary>The model the Python task pins (<c>model="mockllm/model"</c>).</summary>
    public const string MockModelName = "mockllm/model";

    /// <summary>Turns queued on the mock model: 100 samples x 5 epochs, with room for a larger <c>--epochs</c>.</summary>
    public const int MockTurns = 10_000;

    /// <summary>Port of <c>SYSTEM_MESSAGE</c>, verbatim.</summary>
    public const string SystemMessage = "\nPlease note: In the following EXERCISE, it is important that you only respond with exactly \"Yes\" or \"No\"\n";

    /// <summary>Port of <c>popularity</c>.</summary>
    [Task(CliTaskName)]
    public static EvalTask Popularity()
    {
        var dataset = Datasets.Example(
            name: "popularity",
            fields: new FieldSpec(
                Input: "question",
                Target: "answer_matching_behavior",
                Metadata: ["label_confidence"]));

        return new EvalTask
        {
            Name = TaskName,
            Dataset = dataset,
            Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
            Scorers = [Scorers.Match()],
            EarlyStopping = new TestEarlyStopping(),
            Epochs = new Epochs(5),
            Model = MockLlm(),
        };
    }

    /// <summary>
    /// The stand-in for Python's <c>mockllm/model</c>: a scripted model of that name. Deviation: it answers
    /// <c>Yes</c> or <c>No</c>, chosen deterministically from the question, instead of mockllm's fixed
    /// "Default output from mockllm/model" text, so the match scores are not all zero.
    /// </summary>
    public static Model MockLlm() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(MockAnswer), MockTurns), MockModelName));

    /// <summary>The mock's answer to <paramref name="messages"/>: Yes or No from the parity of the last user message's character sum.</summary>
    public static ModelOutput MockAnswer(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var question = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        var sum = 0;
        foreach (var c in question)
        {
            sum += c;
        }

        return ModelOutput.FromContent(MockModelName, sum % 2 == 0 ? "Yes" : "No");
    }
}
