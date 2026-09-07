// ============================================================================
//  AN AUTHOR'S TASK FILE  (what a user of Inspect writes)
//
//  Look at the using directives: only un-prefixed packages. The author never
//  sees `_eval`, `_cli`, `_util` or `_providers`, and nothing in those
//  packages knows this file exists. The `[Task]` attribute registers the
//  factory under the name "arithmetic"; the CLI resolves that name at run
//  time through the registry. The layer guard checks that this namespace
//  references no underscore-prefixed package.
//
//  Python equivalent:
//
//      from inspect_ai import Task, task
//      from inspect_ai.dataset import json_dataset
//      from inspect_ai.scorer import includes
//      from inspect_ai.solver import chain, generate, system_message, use_tools
//      from inspect_ai.tool import bash
//
//      @task
//      def arithmetic():
//          return Task(
//              dataset=json_dataset("datasets/arithmetic.jsonl"),
//              solver=[system_message(...), use_tools(calculator(), bash()), generate()],
//              scorer=includes(),
//              sandbox="docker",
//          )
// ============================================================================
using inspect_ai;
using static inspect_ai.dataset.Datasets;
using static inspect_ai.scorer.Scorers;
using static inspect_ai.solver.Solvers;
using static inspect_ai.tool.Tools;

namespace examples;

public static class ArithmeticTask
{
    [Task("arithmetic")]
    public static EvalTask arithmetic() => new(
        dataset: json_dataset("datasets/arithmetic.jsonl"),
        solver: chain(
            system_message("You are a careful assistant. Use the tools when they help, then answer in one sentence."),
            use_tools(calculator(), bash()),
            generate()),
        scorer: includes(),
        sandbox: "container");
}
