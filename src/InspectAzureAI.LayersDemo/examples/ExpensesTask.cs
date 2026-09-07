// ============================================================================
//  A SECOND AUTHOR'S TASK FILE: multi-step tool use, graded by a model
//
//  Compared with arithmetic, three things are harder, and none of them
//  touches a private package:
//
//    1. Each sample takes several tool calls to answer: read a file from the
//       sandbox (bash -> layer 7), add up what it says (calculator), and
//       sometimes divide the result again. The model chooses the order.
//    2. One sample asks about a file that does not exist. The tool error goes
//       back to the model as a tool message; the sample does not crash, and
//       the right answer is to say so instead of inventing a number.
//    3. The scorer is model_graded_fact(): it hands the answer and the target
//       to the eval's own model and asks for a grade. That is a scorer
//       (layer 4) calling the model layer (5), exactly the way solvers do.
//
//  Run it against a real Azure AI Foundry deployment:
//
//      az login
//      export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models
//      inspect-layers eval expenses --model azureai/gpt-5.4-mini --max-samples 2
//
//  The mock provider can still run it offline, but it cannot add, so that
//  only exercises the plumbing.
// ============================================================================
using inspect_ai;
using static inspect_ai.dataset.Datasets;
using static inspect_ai.scorer.Scorers;
using static inspect_ai.solver.Solvers;
using static inspect_ai.tool.Tools;

namespace examples;

public static class ExpensesTask
{
    [Task("expenses")]
    public static EvalTask expenses() => new(
        dataset: json_dataset("datasets/expenses.jsonl"),
        solver: chain(
            system_message(
                "You are a careful bookkeeping assistant working in a sandboxed shell. " +
                "Read files with the bash tool and do every calculation with the calculator tool; never do arithmetic in your head. " +
                "If a file you need does not exist, say so plainly instead of guessing. " +
                "Finish with a short answer that states the numbers you found."),
            use_tools(calculator(), bash()),
            generate()),
        scorer: model_graded_fact(),
        sandbox: "container");
}
