using System.ComponentModel;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Reasoning;

/// <summary>
/// Port of <c>examples/reasoning.py</c>: the <c>reasoning</c> task, a react agent with the trivial <c>validate</c>
/// tool and a prompt that requires reasoning before each of two algebra problems, run under
/// <c>GenerateConfig(reasoning_effort="medium", reasoning_tokens=8192, max_tokens=16384)</c> so reasoning content
/// flows through the agent loop and into the log.
/// </summary>
public static class ReasoningTask
{
    public const string TaskName = "reasoning";

    /// <summary>The sample input, verbatim from <c>reasoning.py</c>.</summary>
    public const string Input =
        "Solve 3*x^3-5*x=1, then call the validate() tool validate your answer. Then after that, solve x^2 - 5x + 6 = 0 and once again call the validate() tool to validate your answer.";

    /// <summary>The react prompt, verbatim from <c>reasoning.py</c>.</summary>
    public const string Prompt =
        "Note that you must use reasoning before solving each problem presented. Do not attempt to solve a problem without reasoning first.";

    /// <summary>Port of <c>@tool def validate()</c>: a tool that validates any answer (it always returns <c>True</c>).</summary>
    public static ToolDef Validate() => ToolDef.FromMethod((Func<string, Task<bool>>)Execute, name: "validate");

    /// <summary>Port of <c>@task def reasoning()</c>.</summary>
    [Task(TaskName)]
    public static EvalTask Reasoning() => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(Input)]),
        Solver = Agents.AsSolver(Agents.React(prompt: Prompt, tools: [Validate()])),
        Config = new GenerateConfig { ReasoningEffort = "medium", ReasoningTokens = 8192, MaxTokens = 16384 },
    };

    /// <summary>
    /// The tool's <c>execute</c>: Python's docstring ("Validate the answer to a mathematical question." and the
    /// <c>answer: Answer to validate</c> argument) is carried by the <see cref="DescriptionAttribute"/>s, which is
    /// what this port's <see cref="ToolDef.FromMethod(Delegate, string?, string?, bool)"/> reads in its place.
    /// </summary>
    [Description("Validate the answer to a mathematical question.")]
    private static Task<bool> Execute([Description("Answer to validate")] string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Task.FromResult(true);
    }
}
