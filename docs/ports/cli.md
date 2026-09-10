# Port: the `inspectai` command line interface

Location: `src/InspectAzureAI.Cli/` (executable `inspectai`; NuGet `System.CommandLine` 2.0.2), plus two additive edits
in the Eval project: `Tasks/TaskAttribute.cs` (new, the `@task` marker) and one line in `Model/Model.cs` (a generate call
that passes no cache policy falls back to `GenerateConfig.Cache`, which is what Python's `Model.generate` does and what
lets `--cache` reach the solvers). Tests: `tests/InspectAzureAI.Cli.Tests/` (96 cases: argument parsing for every
command with reference values computed by the venv's `parse_cli_args`, task discovery over the test assembly, and
end-to-end `eval` / `eval-set` / `eval-retry` / `score` / `log` runs through `InspectCli.RunAsync` with a `scripted/*`
provider over `ScriptedModelApi` writing real `.eval` and `.json` logs).

## What was ported, from where

| Python | C# |
|---|---|
| `_cli/main.py` (`inspect` group, `--version`) | `InspectCli` (`Build`, `RunAsync`, the exit-code mapping) |
| `_cli/eval.py` (`eval_options`, `eval`, `eval-set`, `eval-retry`, `eval_exec`, `parse_comma_separated`) | `Commands/EvalCommands.cs` (`EvalOptionSet`, `RetryOptionSet`, `EvalCommands`, `Plan`) |
| `_cli/common.py` (`common_options`, `process_common_options`, `clean_log_dir`) | `Commands/CommonOptions.cs` |
| `_cli/util.py` (`int_or_bool_flag_callback`, `int_bool_or_str_flag_callback`, `parse_cli_config`, `parse_model_role_cli_args`, `parse_sandbox`), `_util/config.py`, `_util/flag_values.py`, `_util/samples.py` | `Args/CliArgs.cs`, `Args/ModelRoleArgs.cs`, `Args/GenerateConfigBinding.cs`, `Commands/Opt.cs` (click's envvar / choice / bare-flag-or-value conventions) |
| `yaml.safe_load` (the subset `parse_cli_args` and the config-file readers use) | `Args/YamlValue.cs` |
| `_cli/score.py` (`score`, `resolve_action`, `_resolve_output_file`, `print_results`) | `Commands/ScoreCommand.cs`, `Commands/ResultsPrinter.cs` |
| `_cli/list.py`, `_eval/list.py` `list_tasks`, `TaskInfo` | `Commands/ListCommand.cs`, `Registry/TaskRegistry.cs` |
| `_cli/log.py` (`list`, `dump`, `headers`, `convert`, `schema`), `log/_convert.py` | `Commands/LogCommands.cs` (option parsing; delegates to the log-tools port `Log/Tools/LogCommands.cs` and `LogConversion.cs`) |
| `_cli/cache.py` | `Commands/CacheCommands.cs` |
| `_cli/info.py` (`version`, `log-file`, `log-file-headers`, `log-schema`) | `Commands/InfoCommands.cs` |
| `_cli/view.py` | `Commands/ViewCommand.cs` (delegation, see below) |
| the `@task` decorator and the registry lookups of solvers, scorers, metrics and reducers by name | `TaskAttribute`, `TaskRegistry`, `Registry/Catalog.cs`, `Registry/ParameterBinder.cs` |
| `get_model` provider routing, `model/_providers/mockllm.py` | `Models/ModelProviders.cs`, `Models/MockLlmModelApi.cs` |
| `_view/inspect-openapi.json` | embedded resource `Resources/inspect-openapi.json` |

## Public C# API

- `InspectCli.RunAsync(args, CliIo?, CliServices?, ct)` → exit code; `InspectCli.Build(io, services)` → the `RootCommand`.
  `CliIo(out, err)` captures output; `CliServices { FindOnPath, RunProcessAsync }` isolates `view` from the machine.
- `[Task]` / `[Task("name", "key=value", ...)]` on a `public static EvalTask` method; the parameters are the `-T` arguments.
  `TaskRegistry.Discover(assemblyPaths, absolute)`, `.Resolve("name" | "file@name")`, `.List(filter)`,
  `TaskRegistry.Create(info, args)`, `TaskRegistry.AttribFilter(["light=true", "draft~=false"])`.
- `ModelProviders.Register(provider, factory)` / `.Resolve(name, config, baseUrl, modelArgs)`; `MockLlmModelApi`.
- `CliArgs.ParseCliArgs/ParseCliConfig/ResolveArgs/IntOrBoolValue/IntBoolOrStrValue/ParseSamplesLimit/ParseSampleId/ParseSandbox`,
  `YamlValue.Parse`, `ModelRoleArgs.Parse`, `GenerateConfigBinding.FromValues`, `ParameterBinder.Bind/Convert`,
  `Catalog.CreateScorer/CreateSolver/CreateMetric/CreateReducer(s)/ScorersFromLog`.

Commands: `eval`, `eval-set`, `eval-retry`, `score`, `list tasks|logs`, `log list|dump|headers|convert|schema`,
`cache list|clear|prune|path`, `info version|log-schema|log-file|log-file-headers`, `view`. Exit codes follow
docs/ARCHITECTURE.md §8: 0 success; 1 a run whose log is not `success`; 2 usage or prerequisite (parse errors,
unknown task/model/argument, missing files, refused options); 3 sign-in, Azure, sandbox, cancellation or any other failure.

## Deviations from Python, and why

- **Tasks live in assemblies, not `.py` files.** `[Task]` methods are discovered over the loaded assemblies plus
  `--assembly` paths (`INSPECT_EVAL_ASSEMBLY`); a spec is a name or `assembly@name` (Python's `file@task`); the default
  name is the snake_case of the method name; `-T` values are bound to the method's parameters by name with type
  conversion, and an unknown or missing argument is a prerequisite error (Python: a `TypeError`). With no task spec every
  discovered task runs (Python: the tasks of the cwd). `list tasks` takes assembly paths, not directories or globs.
- **Model names.** `mockllm/model` is built in (its `custom_outputs` takes strings); `openai/` and `anthropic/` call the direct services.
  `openai/azure/`, `anthropic/azure/`, `azureai/` and bare deployments select Foundry. The shared `Models` factory
  preserves custom registrations. See [direct providers and migration](../direct-providers.md) for endpoint guards. `--model a,b` runs the models one after another (Python: concurrently).
- **No solver/scorer registry.** `--solver`, `--scorer`, `--metric` and `--epochs-reducer` name the built-in factories
  (`Solvers`, `Scorers`, `Metrics`, `Reducers`) by their Python names with `-S` args bound by parameter; `exact` maps to
  the Eval port's `exact_match`. `score` without `--scorer` re-creates the header's scorers the same way.
- **`--hooks` is this port's option** (Python has only the registry): registered names or `Hooks` type names.
- **`--env NAME=value` keeps the raw text** after the first `=` (an empty value unsets the variable, as .NET stores no
  empty values). Python YAML-parses the value and stores `str()` of the result, so `NAME=a,b` would become
  `['a', 'b']`, `NAME=3.10` → `3.1` and `NAME=` → `None` — repr artifacts no consumer of a variable expects.
- **Options accepted but inert or refused.** `--max-tasks` is accepted (`eval` runs tasks sequentially; `eval-set`
  honours it); `--log-level`, `--traceback-locals`, `--no-ansi` are inert (no Python logger / rich); `--display`
  selects the plain reporter or `none`; `--debug`, `--debug-port`, `--debug-errors` are refused (exit 2); `--limit`
  ranges and the `500k` / `1m` / `output:` token-limit forms are refused (the runner takes a count and an integer).
- **Not defined** (their subsystems are not in this solution): `--tags`, `--metadata`, `--model-spec`, `--run-config`,
  `--trace`, `--notification`, `--sandbox-prebuilt`, `--checkpoint`, `--acp-server`, `--ctl-server`, `--sample-shuffle`,
  `--max-dataset-memory`, `--max-subprocesses`, `--max-sandboxes`, `--score-on-error`, the `--log-*` recorder options,
  `--no-score`, `--no-score-display`, `--json`, `--detach`, the scanner options, `--response-schema`, `--batch`,
  `--modalities`, `--log-level-transcript`, `--bundle-dir`, `--embed-viewer`, `--retry-immediate`, `--generate-config`
  fields beyond `GenerateConfig`. `eval-set` writes no bundle and uses this port's `EvalSet` retry loop.
- **`score` has no prompts**: the action defaults to append when scores exist (the prompt's default) and the output to
  `{stem}-scored.{ext}` unless `--overwrite` / `--output-file`; `--stream` is refused; `.eval` logs are scored in place
  through the format-aware reader (this lifts the JSON-only guard of the score-logs port); the header's model and roles
  are not rebuilt (pass `--model` / `--model-role`).
- **`log convert`** handles local files only; an existing output is exit 2 (Python: an uncaught `FileExistsError`);
  `--stream` is refused; written members are deflate (see the eval-format port). `log list --json` reports `type: file` and `mtime` in milliseconds, as Python does.
- **`view` is not ported**: the viewer is a web app. `inspectai view ...` hands its arguments verbatim to Python's
  `inspect view` when `inspect` is on `PATH` (it reads this port's logs) and otherwise prints how to install it (exit 2).
- **`info version`** prints the assembly's informational version and install directory; `--version` prints the same.
  `info log-schema` / `log schema` print Python's OpenAPI document verbatim (this port's logs conform to it).
- **YAML** is a PyYAML subset: timestamps stay strings, no block scalars, anchors or tags; `yes/no/on/off`, octal,
  hex, sexagesimal and `.inf` resolve as PyYAML does (reference values from the venv are asserted in the tests).
- **Exit codes.** Python exits 0 when a log finishes with status `error`; this port returns 1 (the §8 contract).
  Uncaught exceptions are reported as 3 rather than a traceback with exit 1.
- **Environment variables** keep Python's names; `INSPECT_EVAL_ASSEMBLY`, `INSPECT_EVAL_HOOKS`, `INSPECT_EVAL_SET_ID`
  and `INSPECT_EVAL_RETRY_*` are this port's. `.env` files are not read.

## Not ported

The `acp`, `ctl`, `download`, `sandbox` and `trace` commands; `log recover`, `log export-config`, `log convert-chunked`,
`log types`; `view bundle` / `view embed` natively; the `--json` NDJSON launch protocol; rich panels and progress
displays (results print as plain text).
