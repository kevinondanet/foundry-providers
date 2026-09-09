using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

// The demo registers a process-wide sandbox provider and writes to Console.Out, so tests in this assembly run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InspectAzureAI.HveDemo.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Builders shared by the scorer and task tests: a sample context over a fake sandbox, and task states with HVE metadata.</summary>
internal static class TestSupport
{
    /// <summary>The metadata of an HVE sample as the dataset loader produces it.</summary>
    public static Dictionary<string, object?> Metadata(
        string kind = "implement",
        string? agent = null,
        string artefact = "textkit/slug.py",
        string check = "python3 -m unittest discover -s tests -q",
        string rubric = "Full credit when the check passes.",
        params string[] components) => new(StringComparer.Ordinal)
    {
        ["kind"] = kind,
        ["agent"] = agent,
        ["artefact"] = artefact,
        ["check"] = check,
        ["rubric"] = rubric,
        ["hve_components"] = components.Cast<object?>().ToList(),
    };

    /// <summary>A task state whose final assistant message is <paramref name="completion"/>.</summary>
    public static TaskState State(IDictionary<string, object?> metadata, string completion = "Done.", string target = "the artefact exists", string sampleId = "s1") =>
        new(
            "test-model",
            sampleId: sampleId,
            epoch: 1,
            input: "do the task",
            messages: [new ChatMessageUser("do the task"), new ChatMessageAssistant(completion)],
            target: new Target(target),
            output: ModelOutput.FromContent("test-model", completion),
            metadata: metadata);

    /// <summary>An ambient sample context over <paramref name="sandbox"/> with <paramref name="model"/> as the active (and grading) model.</summary>
    public static (SampleContext Context, IDisposable Scope) Begin(ISandboxEnvironment sandbox, Model? model = null, Transcript? transcript = null)
    {
        var context = new SampleContext
        {
            ActiveModel = model ?? new Model(new ScriptedModelApi([ScriptedTurn.Text("GRADE: C")])),
            Sandboxes = SandboxEnvironments.Single(sandbox),
            Transcript = transcript ?? new Transcript(),
        };
        return (context, SampleContext.Begin(context));
    }

    public static string ScoreText(Score score) => score.Value switch
    {
        ScoreValue.Str s => s.Value,
        ScoreValue.Num n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => score.Value.ToString() ?? "",
    };
}
