using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Examples.TextEditor;

// The example's namespace shares its last segment with the engine's TextEditor tool class, so the tool is aliased.
using EditorTool = InspectAzureAI.Eval.Tools.TextEditor;

/// <summary>
/// Port of <c>examples/text_editor.py</c> <c>text_editor_task</c> and its <c>verify_edit</c> scorer: a single
/// sample driving the built-in <c>text_editor</c> tool through four <c>generate()</c> turns (create the file,
/// view it, <c>str_replace</c> Hello with Goodbye, <c>insert</c> an author line), interleaved with
/// <c>user_message()</c> prompts, in a Docker sandbox; the scorer reads <c>/tmp/greeting.py</c> back from the
/// sandbox and checks the edits landed.
/// </summary>
public static class TextEditorTask
{
    /// <summary>The task name (<c>@task def text_editor_task</c>).</summary>
    public const string TaskName = "text_editor_task";

    /// <summary>The file the task edits.</summary>
    public const string FilePath = "/tmp/greeting.py";

    /// <summary>The <c>Sample(input=...)</c>, verbatim.</summary>
    public const string Input =
        "Use the text_editor tool to create a file at /tmp/greeting.py with a Python function called `greet` that takes a `name` parameter and returns the string 'Hello, {name}!'.";

    /// <summary>The first <c>user_message(...)</c>, verbatim.</summary>
    public const string ViewMessage = "Now use the text_editor to view the file /tmp/greeting.py and confirm its contents.";

    /// <summary>The second <c>user_message(...)</c>, verbatim.</summary>
    public const string ReplaceMessage = "Now use the text_editor str_replace command to change 'Hello' to 'Goodbye' in /tmp/greeting.py.";

    /// <summary>The third <c>user_message(...)</c>, verbatim.</summary>
    public const string InsertMessage = "Now use the text_editor insert command to insert the line '# Author: Inspector' after line 1 of /tmp/greeting.py.";

    /// <summary>Port of <c>@task def text_editor_task()</c> (<c>sandbox="docker"</c>), discoverable by the <c>inspectai</c> CLI.</summary>
    [Task(TaskName)]
    public static EvalTask Create() => Build(new SandboxSpec("docker"));

    /// <summary>Builds <c>text_editor_task</c> for <paramref name="sandbox"/> (the Python task is fixed to <c>"docker"</c>; the examples runner substitutes <c>--sandbox</c>).</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(Input) { Target = "Goodbye" }]),
            Solver = Solvers.Chain(
                Solvers.UseTools(EditorTool.Create()),
                Solvers.Generate(),
                Solvers.UserMessage(ViewMessage),
                Solvers.Generate(),
                Solvers.UserMessage(ReplaceMessage),
                Solvers.Generate(),
                Solvers.UserMessage(InsertMessage),
                Solvers.Generate()),
            Scorers = [VerifyEdit()],
            Sandbox = sandbox,
        };
    }

    /// <summary>Port of <c>@scorer(metrics=[accuracy()]) def verify_edit()</c>.</summary>
    public static ScorerDef VerifyEdit() => Scorers.Custom("verify_edit", VerifyEditScore, Metrics.Accuracy());

    /// <summary>
    /// The <c>score</c> function of <c>verify_edit</c>: reads <c>/tmp/greeting.py</c> from the sandbox and scores
    /// 1.0 when it contains <c>Goodbye</c> and <c># Author: Inspector</c> and no longer contains <c>Hello</c>, 0.0
    /// otherwise; a missing file (Python's <c>FileNotFoundError</c>) scores 0.0 with the file-not-found answer.
    /// </summary>
    public static async Task<Score> VerifyEditScore(TaskState state, Target target, CancellationToken cancellationToken)
    {
        try
        {
            var content = await SampleContext.Require().Sandbox().ReadFileAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var hasGoodbye = content.Contains("Goodbye", StringComparison.Ordinal);
            var hasNoHello = !content.Contains("Hello", StringComparison.Ordinal);
            var hasAuthor = content.Contains("# Author: Inspector", StringComparison.Ordinal);
            var correct = hasGoodbye && hasNoHello && hasAuthor;
            return new Score(correct ? 1.0 : 0.0)
            {
                Answer = content,
                Explanation = correct ? "File contains expected edit." : "Edit not applied correctly.",
            };
        }
        catch (FileNotFoundException)
        {
            return new Score(0.0)
            {
                Answer = "File not found",
                Explanation = "The file /tmp/greeting.py was not created.",
            };
        }
    }
}
