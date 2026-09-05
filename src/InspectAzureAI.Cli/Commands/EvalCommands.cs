using System.CommandLine;
using System.Globalization;
using System.Reflection;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Cli.Models;
using InspectAzureAI.Cli.Registry;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Commands;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_cli/eval.py</c>: the option set shared by <c>eval</c> and <c>eval-set</c> (<c>eval_options</c>) and
/// the extra options of <c>eval-set</c>. Only the options this runner can honour are defined; Python's help text and
/// environment variables are kept.
/// </summary>
internal sealed class EvalOptionSet
{
    public const int DefaultRetryOnError = 1;

    public const int DefaultCacheDays = 7;

    public CommonOptions Common { get; } = new();

    public Argument<string[]> Tasks { get; } = new Argument<string[]>("tasks") { Arity = ArgumentArity.ZeroOrMore, Description = "Task name(s) or assembly@name (every discovered task when none is given)." }.RejectOptionLike();

    public Option<string[]> Assembly { get; } = Opt.Multi("--assembly", "Assembly (.dll) to load [Task] methods from (can be specified multiple times).", "INSPECT_EVAL_ASSEMBLY");

    public Option<string?> Model { get; } = Opt.String("--model", "Model used to evaluate tasks.", "INSPECT_EVAL_MODEL");

    public Option<string?> ModelBaseUrl { get; } = Opt.String("--model-base-url", "Base URL for for model API");

    public Option<string[]> M { get; } = Opt.Multi("-M", "One or more native model arguments (e.g. -M arg=value)", "INSPECT_EVAL_MODEL_ARGS");

    public Option<string?> ModelConfig { get; } = Opt.String("--model-config", "YAML or JSON config file with model arguments.", "INSPECT_EVAL_MODEL_CONFIG");

    public Option<string[]> ModelRole { get; } = Opt.Multi("--model-role", "Named model role with model name or YAML/JSON config, e.g. --model-role critic=openai/gpt-4o or --model-role grader=\"{model: mockllm/model, temperature: 0.5}\". Bind multiple models to a role with a comma-separated list of names or a YAML/JSON list of configs.", "INSPECT_EVAL_MODEL_ROLE");

    public Option<string[]> T { get; } = Opt.Multi("-T", "One or more task arguments (e.g. -T arg=value)", "INSPECT_EVAL_TASK_ARGS");

    public Option<string?> TaskConfig { get; } = Opt.String("--task-config", "YAML or JSON config file with task arguments.", "INSPECT_EVAL_TASK_CONFIG");

    public Option<string?> Solver { get; } = Opt.String("--solver", "Solver to execute (overrides task default solver)", "INSPECT_EVAL_SOLVER");

    public Option<string[]> S { get; } = Opt.Multi("-S", "One or more solver arguments (e.g. -S arg=value)", "INSPECT_EVAL_SOLVER_ARGS");

    public Option<string?> SolverConfig { get; } = Opt.String("--solver-config", "YAML or JSON config file with solver arguments.", "INSPECT_EVAL_SOLVER_CONFIG");

    public Option<string?> Approval { get; } = Opt.String("--approval", "Config file for tool call approval.", "INSPECT_EVAL_APPROVAL");

    public Option<string[]> Hooks { get; } = Opt.Multi("--hooks", "Hook(s) to enable for this run: a name registered with HookRegistry or the type name of a Hooks subclass (comma separated or repeated).", "INSPECT_EVAL_HOOKS");

    public Option<string?> Sandbox { get; } = Opt.String("--sandbox", "Sandbox environment type (with optional config file). e.g. 'docker' or 'docker:compose.yml'", "INSPECT_EVAL_SANDBOX");

    public Option<bool> NoSandboxCleanup { get; } = Opt.Flag("--no-sandbox-cleanup", "Do not cleanup sandbox environments after task completes", "INSPECT_EVAL_NO_SANDBOX_CLEANUP");

    public Option<string?> Limit { get; } = Opt.String("--limit", "Limit samples to evaluate e.g. 10 or 10-20", "INSPECT_EVAL_LIMIT");

    public Option<string?> SampleId { get; } = Opt.String("--sample-id", "Evaluate specific sample(s) (comma separated list of ids)", "INSPECT_EVAL_SAMPLE_ID");

    public Option<int?> Epochs { get; } = Opt.Int("--epochs", "Number of times to repeat dataset (defaults to 1) ", "INSPECT_EVAL_EPOCHS");

    public Option<string?> EpochsReducer { get; } = Opt.String("--epochs-reducer", "Method for reducing per-epoch sample scores into a single score. Built in reducers include 'mean', 'median', 'mode', 'max', and 'at_least_{n}'.", "INSPECT_EVAL_EPOCHS_REDUCER");

    public Option<bool> NoEpochsReducer { get; } = Opt.Flag("--no-epochs-reducer", "Do not reduce per-epoch sample scores.", "INSPECT_EVAL_NO_EPOCHS_REDUCER");

    public Option<int?> MaxConnections { get; } = Opt.Int("--max-connections", "Maximum number of concurrent connections to Model API (defaults to 10)", "INSPECT_EVAL_MAX_CONNECTIONS");

    public Option<string?> AdaptiveConnections { get; } = Opt.String("--adaptive-connections", "Adaptive concurrency for Model API connections, automatically scaling between bounds based on rate-limit feedback: true/false, a max, or min-max / min-start-max.", "INSPECT_EVAL_ADAPTIVE_CONNECTIONS");

    public Option<int?> MaxRetries { get; } = Opt.Int("--max-retries", "Maximum number of times to retry model API requests (defaults to unlimited)", "INSPECT_EVAL_MAX_RETRIES");

    public Option<int?> Timeout { get; } = Opt.Int("--timeout", "Model API request timeout in seconds (defaults to no timeout)", "INSPECT_EVAL_TIMEOUT");

    public Option<int?> AttemptTimeout { get; } = Opt.Int("--attempt-timeout", "Timeout (in seconds) for any given attempt (if exceeded, will abandon attempt and retry according to max_retries).", "INSPECT_EVAL_ATTEMPT_TIMEOUT");

    public Option<int?> StreamIdleTimeout { get; } = Opt.Int("--stream-idle-timeout", "Timeout (in seconds) on silence within a streaming response (if a streaming attempt delivers no chunk for this long, will abandon attempt and retry according to max_retries).", "INSPECT_EVAL_STREAM_IDLE_TIMEOUT");

    public Option<int?> MaxSamples { get; } = Opt.Int("--max-samples", "Maximum number of samples to run in parallel (default is running all samples in parallel)", "INSPECT_EVAL_MAX_SAMPLES");

    public Option<int?> MaxTasks { get; } = Opt.Int("--max-tasks", "Maximum number of tasks to run in parallel (default is 1 for eval and 10 for eval-set)", "INSPECT_EVAL_MAX_TASKS");

    public Option<int?> MessageLimit { get; } = Opt.Int("--message-limit", "Limit on total messages used for each sample.", "INSPECT_EVAL_MESSAGE_LIMIT");

    public Option<string?> TokenLimit { get; } = Opt.String("--token-limit", "Limit on tokens used for each sample (e.g. 500000).", "INSPECT_EVAL_TOKEN_LIMIT");

    public Option<int?> TurnLimit { get; } = Opt.Int("--turn-limit", "Limit on total turns (model generations) used for each sample.", "INSPECT_EVAL_TURN_LIMIT");

    public Option<double?> CostLimit { get; } = Opt.Double("--cost-limit", "Limit on total cost (in dollars) for each sample.", "INSPECT_EVAL_COST_LIMIT");

    public Option<string?> ModelCostConfig { get; } = Opt.String("--model-cost-config", "YAML or JSON file with model prices for cost tracking.", "INSPECT_EVAL_MODEL_COST_CONFIG");

    public Option<int?> TimeLimit { get; } = Opt.Int("--time-limit", "Limit on total running time for each sample.", "INSPECT_EVAL_TIME_LIMIT");

    public Option<int?> WorkingLimit { get; } = Opt.Int("--working-limit", "Limit on total working time (e.g. model generation, tool calls, etc.) for each sample.", "INSPECT_EVAL_WORKING_LIMIT");

    public Option<string?> FailOnError { get; } = Opt.FlagOrValue("--fail-on-error", "Threshold of sample errors to tolerage (by default, evals fail when any error occurs). Value between 0 to 1 to set a proportion; value greater than 1 to set a count.", "INSPECT_EVAL_FAIL_ON_ERROR");

    public Option<bool> NoFailOnError { get; } = Opt.Flag("--no-fail-on-error", "Do not fail the eval if errors occur within samples (instead, continue running other samples)", "INSPECT_EVAL_NO_FAIL_ON_ERROR");

    public Option<bool> ContinueOnFail { get; } = Opt.Flag("--continue-on-fail", "Do not immediately fail the eval if the error threshold is exceeded (instead, continue running other samples until the eval completes, and then possibly fail the eval).", "INSPECT_EVAL_CONTINUE_ON_FAIL");

    public Option<string?> RetryOnError { get; } = Opt.FlagOrValue("--retry-on-error", "Retry samples if they encounter errors (by default, no retries occur). Specify --retry-on-error to retry a single time, or specify e.g. `--retry-on-error=3` to retry multiple times.", "INSPECT_EVAL_RETRY_ON_ERROR");

    public Option<string?> GenerateConfigFile { get; } = Opt.String("--generate-config", "YAML or JSON config file with GenerateConfig (alternatively, use the options for individual config values).", "INSPECT_EVAL_GENERATE_CONFIG");

    public Option<int?> MaxTokens { get; } = Opt.Int("--max-tokens", "The maximum number of tokens that can be generated in the completion (default is model specific)", "INSPECT_EVAL_MAX_TOKENS");

    public Option<string?> SystemMessage { get; } = Opt.String("--system-message", "Override the default system message.", "INSPECT_EVAL_SYSTEM_MESSAGE");

    public Option<int?> BestOf { get; } = Opt.Int("--best-of", "Generates best_of completions server-side and returns the 'best' (the one with the highest log probability per token). OpenAI only.", "INSPECT_EVAL_BEST_OF");

    public Option<double?> FrequencyPenalty { get; } = Opt.Double("--frequency-penalty", "Number between -2.0 and 2.0. Positive values penalize new tokens based on their existing frequency in the text so far, decreasing the model's likelihood to repeat the same line verbatim.", "INSPECT_EVAL_FREQUENCY_PENALTY");

    public Option<double?> PresencePenalty { get; } = Opt.Double("--presence-penalty", "Number between -2.0 and 2.0. Positive values penalize new tokens based on whether they appear in the text so far, increasing the model's likelihood to talk about new topics.", "INSPECT_EVAL_PRESENCE_PENALTY");

    public Option<string?> LogitBias { get; } = Opt.String("--logit-bias", "Map token Ids to an associated bias value from -100 to 100 (e.g. \"42=10,43=-10\").", "INSPECT_EVAL_LOGIT_BIAS");

    public Option<int?> Seed { get; } = Opt.Int("--seed", "Random seed.", "INSPECT_EVAL_SEED");

    public Option<string?> StopSeqs { get; } = Opt.String("--stop-seqs", "Sequences where the API will stop generating further tokens. The returned text will not contain the stop sequence.", "INSPECT_EVAL_STOP_SEQS");

    public Option<double?> Temperature { get; } = Opt.Double("--temperature", "What sampling temperature to use, between 0 and 2. Higher values like 0.8 will make the output more random, while lower values like 0.2 will make it more focused and deterministic.", "INSPECT_EVAL_TEMPERATURE");

    public Option<double?> TopP { get; } = Opt.Double("--top-p", "An alternative to sampling with temperature, called nucleus sampling, where the model considers the results of the tokens with top_p probability mass.", "INSPECT_EVAL_TOP_P");

    public Option<int?> TopK { get; } = Opt.Int("--top-k", "Randomly sample the next word from the top_k most likely next words.", "INSPECT_EVAL_TOP_K");

    public Option<int?> NumChoices { get; } = Opt.Int("--num-choices", "How many chat completion choices to generate for each input message.", "INSPECT_EVAL_NUM_CHOICES");

    public Option<bool> Logprobs { get; } = Opt.Flag("--logprobs", "Return log probabilities of the output tokens.", "INSPECT_EVAL_LOGPROBS");

    public Option<int?> TopLogprobs { get; } = Opt.Int("--top-logprobs", "Number of most likely tokens (0-20) to return at each token position, each with an associated log probability.", "INSPECT_EVAL_TOP_LOGPROBS");

    public Option<bool> ParallelToolCalls { get; } = Opt.Flag("--parallel-tool-calls", "Enable parallel function calling during tool use (the default).", "INSPECT_EVAL_PARALLEL_TOOL_CALLS");

    public Option<bool> NoParallelToolCalls { get; } = Opt.Flag("--no-parallel-tool-calls", "Disable parallel function calling during tool use.");

    public Option<bool> InternalTools { get; } = Opt.Flag("--internal-tools", "Automatically map tools to model internal implementations (the default).", "INSPECT_EVAL_INTERNAL_TOOLS");

    public Option<bool> NoInternalTools { get; } = Opt.Flag("--no-internal-tools", "Do not map tools to model internal implementations.");

    public Option<int?> MaxToolOutput { get; } = Opt.Int("--max-tool-output", "Maximum size of tool output (in bytes). Defaults to 16 * 1024.", "INSPECT_EVAL_MAX_TOOL_OUTPUT");

    public Option<string?> CachePrompt { get; } = Opt.Choice("--cache-prompt", "Whether to cache the prompt prefix. Enabled by default. Set to False to disable. Anthropic only.", ["auto", "true", "false"], "INSPECT_EVAL_CACHE_PROMPT");

    public Option<string?> FallbackModels { get; } = Opt.String("--fallback-models", "Fallback models (comma-separated, tried in order) when the model's safety classifiers refuse the request.", "INSPECT_EVAL_FALLBACK_MODELS");

    public Option<string?> Verbosity { get; } = Opt.Choice("--verbosity", "Constrains the verbosity of the model's response.", ["low", "medium", "high"], "INSPECT_EVAL_VERBOSITY");

    public Option<string?> Effort { get; } = Opt.Choice("--effort", "Control how many tokens are used for a response, trading off between response thoroughness and token efficiency.", ["low", "medium", "high", "xhigh", "max"], "INSPECT_EVAL_EFFORT");

    public Option<string?> ReasoningEffort { get; } = Opt.Choice("--reasoning-effort", "Constrains effort on reasoning. Defaults vary by provider and model and not all models support all values.", ["none", "minimal", "low", "medium", "high", "xhigh", "max"], "INSPECT_EVAL_REASONING_EFFORT");

    public Option<string?> ReasoningMode { get; } = Opt.Choice("--reasoning-mode", "Reasoning mode. \"pro\" performs more model work for greater reliability on difficult tasks, at higher latency and token usage.", ["standard", "pro"], "INSPECT_EVAL_REASONING_MODE");

    public Option<int?> ReasoningTokens { get; } = Opt.Int("--reasoning-tokens", "Maximum number of tokens to use for reasoning. Anthropic Claude models only.", "INSPECT_EVAL_REASONING_TOKENS");

    public Option<string?> ReasoningSummary { get; } = Opt.Choice("--reasoning-summary", "Provide summary of reasoning steps (OpenAI reasoning models only).", ["none", "concise", "detailed", "auto"], "INSPECT_EVAL_REASONING_SUMMARY");

    public Option<string?> ReasoningHistory { get; } = Opt.Choice("--reasoning-history", "Include reasoning in chat message history sent to generate (defaults to \"auto\", which uses the recommended default for each provider)", ["none", "all", "last", "auto"], "INSPECT_EVAL_REASONING_HISTORY");

    public Option<string?> Cache { get; } = Opt.FlagOrValue("--cache", "Policy for caching of model generations. Specify --cache to cache with 7 day expiration (7D). Specify a number of days (e.g. --cache=3) or a YAML/JSON cache policy file (expiry, per_epoch, scopes). --cache=false disables it.", "INSPECT_EVAL_CACHE");

    public Option<string?> LogFormat { get; } = Opt.Choice("--log-format", "Format for writing log files.", ["eval", "json"], caseInsensitive: true);

    public Option<int?> RetryAttempts { get; } = Opt.Int("--retry-attempts", "Maximum number of retry attempts before giving up (defaults to 10).", "INSPECT_EVAL_RETRY_ATTEMPTS");

    public Option<int?> RetryWait { get; } = Opt.Int("--retry-wait", "Time in seconds wait between attempts, increased exponentially (defaults to 30, capped at 1 hour).", "INSPECT_EVAL_RETRY_WAIT");

    public Option<double?> RetryConnections { get; } = Opt.Double("--retry-connections", "Reduce max_connections at this rate with each retry (defaults to 1.0, which results in no reduction).", "INSPECT_EVAL_RETRY_CONNECTIONS");

    public Option<bool> NoRetryCleanup { get; } = Opt.Flag("--no-retry-cleanup", "Do not cleanup failed log files after retries", "INSPECT_EVAL_NO_RETRY_CLEANUP");

    public Option<bool> LogDirAllowDirty { get; } = Opt.Flag("--log-dir-allow-dirty", "Do not fail if the log-dir contains files that are not part of the eval set.", "INSPECT_EVAL_LOG_DIR_ALLOW_DIRTY");

    public Option<string?> Id { get; } = Opt.String("--id", "ID for the eval set. If not specified, a unique ID will be generated.", "INSPECT_EVAL_SET_ID");

    public EvalOptionSet()
    {
        LogFormat.DefaultValueFactory = _ => Opt.EnvText(LogFormats.FormatEnvironmentVariables.ToArray());
    }

    /// <summary>The options of <c>eval_options</c>, in Python's order.</summary>
    public IEnumerable<Option> EvalOptions =>
    [
        Assembly, Model, ModelBaseUrl, M, ModelConfig, ModelRole, T, TaskConfig, Solver, S, SolverConfig, Approval, Hooks, Sandbox, NoSandboxCleanup,
        Limit, SampleId, Epochs, EpochsReducer, NoEpochsReducer, MaxConnections, AdaptiveConnections, MaxRetries, Timeout, AttemptTimeout, StreamIdleTimeout,
        MaxSamples, MaxTasks, MessageLimit, TokenLimit, TurnLimit, CostLimit, ModelCostConfig, TimeLimit, WorkingLimit, FailOnError, NoFailOnError, ContinueOnFail, RetryOnError,
        GenerateConfigFile, MaxTokens, SystemMessage, BestOf, FrequencyPenalty, PresencePenalty, LogitBias, Seed, StopSeqs, Temperature, TopP, TopK, NumChoices, Logprobs, TopLogprobs,
        ParallelToolCalls, NoParallelToolCalls, InternalTools, NoInternalTools, MaxToolOutput, CachePrompt, FallbackModels, Verbosity, Effort, ReasoningEffort, ReasoningMode,
        ReasoningTokens, ReasoningSummary, ReasoningHistory, Cache, LogFormat,
    ];

    public IEnumerable<Option> EvalSetOptions => [RetryAttempts, RetryWait, RetryConnections, NoRetryCleanup, LogDirAllowDirty, Id];

    public void AddTo(Command command, bool evalSet)
    {
        command.Arguments.Add(Tasks);
        foreach (var option in EvalOptions)
        {
            command.Options.Add(option);
        }

        if (evalSet)
        {
            foreach (var option in EvalSetOptions)
            {
                command.Options.Add(option);
            }
        }

        Common.AddTo(command);
    }
}

/// <summary>The options of <c>eval-retry</c>: the subset of <see cref="EvalOptionSet"/> Python lets a retry override.</summary>
internal sealed class RetryOptionSet
{
    public CommonOptions Common { get; } = new();

    public Argument<string[]> LogFiles { get; } = new Argument<string[]>("log_files") { Arity = ArgumentArity.OneOrMore, Description = "Log file(s) to retry." }.RejectOptionLike();

    public Option<string[]> Assembly { get; } = Opt.Multi("--assembly", "Assembly (.dll) to load [Task] methods from (can be specified multiple times).", "INSPECT_EVAL_ASSEMBLY");

    public Option<int?> MaxSamples { get; } = Opt.Int("--max-samples", "Maximum number of samples to run in parallel (default is running all samples in parallel)", "INSPECT_EVAL_MAX_SAMPLES");

    public Option<int?> MaxTasks { get; } = Opt.Int("--max-tasks", "Maximum number of tasks to run in parallel (default is 1 for eval and 10 for eval-set)", "INSPECT_EVAL_MAX_TASKS");

    public Option<bool> NoSandboxCleanup { get; } = Opt.Flag("--no-sandbox-cleanup", "Do not cleanup sandbox environments after task completes", "INSPECT_EVAL_NO_SANDBOX_CLEANUP");

    public Option<string?> FailOnError { get; } = Opt.FlagOrValue("--fail-on-error", "Threshold of sample errors to tolerage (by default, evals fail when any error occurs). Value between 0 to 1 to set a proportion; value greater than 1 to set a count.", "INSPECT_EVAL_FAIL_ON_ERROR");

    public Option<bool> NoFailOnError { get; } = Opt.Flag("--no-fail-on-error", "Do not fail the eval if errors occur within samples (instead, continue running other samples)", "INSPECT_EVAL_NO_FAIL_ON_ERROR");

    public Option<bool> ContinueOnFail { get; } = Opt.Flag("--continue-on-fail", "Do not immediately fail the eval if the error threshold is exceeded (instead, continue running other samples until the eval completes, and then possibly fail the eval).", "INSPECT_EVAL_CONTINUE_ON_FAIL");

    public Option<string?> RetryOnError { get; } = Opt.FlagOrValue("--retry-on-error", "Retry samples if they encounter errors (by default, no retries occur). Specify --retry-on-error to retry a single time, or specify e.g. `--retry-on-error=3` to retry multiple times.", "INSPECT_EVAL_RETRY_ON_ERROR");

    public Option<int?> MaxConnections { get; } = Opt.Int("--max-connections", "Maximum number of concurrent connections to Model API (defaults to 10)", "INSPECT_EVAL_MAX_CONNECTIONS");

    public Option<string?> AdaptiveConnections { get; } = Opt.String("--adaptive-connections", "Adaptive concurrency for Model API connections: true/false, a max, or min-max / min-start-max.", "INSPECT_EVAL_ADAPTIVE_CONNECTIONS");

    public Option<int?> MaxRetries { get; } = Opt.Int("--max-retries", "Maximum number of times to retry model API requests (defaults to unlimited)", "INSPECT_EVAL_MAX_RETRIES");

    public Option<int?> Timeout { get; } = Opt.Int("--timeout", "Model API request timeout in seconds (defaults to no timeout)", "INSPECT_EVAL_TIMEOUT");

    public Option<int?> AttemptTimeout { get; } = Opt.Int("--attempt-timeout", "Timeout (in seconds) for any given attempt (if exceeded, will abandon attempt and retry according to max_retries).", "INSPECT_EVAL_ATTEMPT_TIMEOUT");

    public Option<int?> StreamIdleTimeout { get; } = Opt.Int("--stream-idle-timeout", "Timeout (in seconds) on silence within a streaming response.", "INSPECT_EVAL_STREAM_IDLE_TIMEOUT");

    public void AddTo(Command command)
    {
        command.Arguments.Add(LogFiles);
        foreach (var option in new Option[] { Assembly, MaxSamples, MaxTasks, NoSandboxCleanup, FailOnError, NoFailOnError, ContinueOnFail, RetryOnError, MaxConnections, AdaptiveConnections, MaxRetries, Timeout, AttemptTimeout, StreamIdleTimeout })
        {
            command.Options.Add(option);
        }

        Common.AddTo(command);
    }
}

/// <summary>Port of <c>eval_exec</c> / <c>eval_retry_command</c>: resolves the parsed options into tasks, models and <see cref="EvalOptions"/>, runs, prints the results and maps the log statuses to the exit code.</summary>
internal static class EvalCommands
{
    public static Command BuildEval(CliIo io)
    {
        var options = new EvalOptionSet();
        var command = new Command("eval", "Evaluate tasks.");
        options.AddTo(command, evalSet: false);
        command.SetAction((result, cancellationToken) => RunEvalAsync(options, result, io, cancellationToken));
        return command;
    }

    public static Command BuildEvalSet(CliIo io)
    {
        var options = new EvalOptionSet();
        var command = new Command("eval-set", "Evaluate a set of tasks with retries.");
        options.AddTo(command, evalSet: true);
        command.SetAction((result, cancellationToken) => RunEvalSetAsync(options, result, io, cancellationToken));
        return command;
    }

    public static Command BuildEvalRetry(CliIo io)
    {
        var options = new RetryOptionSet();
        var command = new Command("eval-retry", "Retry failed evaluation(s).");
        options.AddTo(command);
        command.SetAction((result, cancellationToken) => RunRetryAsync(options, result, io, cancellationToken));
        return command;
    }

    internal static async Task<int> RunEvalAsync(EvalOptionSet o, ParseResult result, CliIo io, CancellationToken cancellationToken)
    {
        var plan = Plan.Build(o, result, io);
        var logs = new List<EvalLog>();
        foreach (var task in plan.Tasks)
        {
            foreach (var model in plan.Models)
            {
                var log = await Eval.RunAsync(task, plan.Options with { Model = model }, cancellationToken).ConfigureAwait(false);
                ResultsPrinter.Print(io.Out, log);
                logs.Add(log);
            }
        }

        return ExitCode(logs);
    }

    internal static async Task<int> RunEvalSetAsync(EvalOptionSet o, ParseResult result, CliIo io, CancellationToken cancellationToken)
    {
        var plan = Plan.Build(o, result, io);
        var setOptions = new EvalSetOptions
        {
            Eval = plan.Options,
            Models = plan.Models,
            RetryAttempts = result.GetValue(o.RetryAttempts) ?? 10,
            RetryWait = result.GetValue(o.RetryWait) is { } wait ? TimeSpan.FromSeconds(wait) : null,
            RetryConnections = result.GetValue(o.RetryConnections),
            RetryCleanup = !result.GetValue(o.NoRetryCleanup),
            MaxTasks = result.GetValue(o.MaxTasks),
            LogDirAllowDirty = result.GetValue(o.LogDirAllowDirty),
            EvalSetId = result.GetValue(o.Id),
        };
        var set = await EvalSet.RunAsync(plan.Tasks, setOptions, cancellationToken).ConfigureAwait(false);
        foreach (var log in set.Logs)
        {
            ResultsPrinter.Print(io.Out, log);
        }

        return set.Success ? 0 : Math.Max(1, ExitCode(set.Logs));
    }

    internal static async Task<int> RunRetryAsync(RetryOptionSet o, ParseResult result, CliIo io, CancellationToken cancellationToken)
    {
        var common = o.Common.Process(result);
        var registry = TaskRegistry.Discover(result.GetValue(o.Assembly));
        var logFiles = result.GetValue(o.LogFiles) ?? [];
        foreach (var file in logFiles)
        {
            if (!File.Exists(file))
            {
                throw new PrerequisiteError($"Log file '{file}' does not exist.");
            }
        }

        var retryOptions = new EvalRetryOptions
        {
            ResolveTask = log => TaskRegistry.Create(registry.Resolve(log.Eval.TaskRegistryName ?? log.Eval.Task), log.Eval.TaskArgs),
            ResolveModel = spec => ModelProviders.Resolve(spec.Model, spec.ModelGenerateConfig, spec.ModelBaseUrl, spec.ModelArgs),
            ResolveRoleModel = name => ModelProviders.Resolve(name),
            LogDir = Opt.Specified(result, o.Common.LogDir) || Opt.EnvText("INSPECT_LOG_DIR") is not null ? common.LogDir : null,
            MaxSamples = result.GetValue(o.MaxSamples),
            SandboxCleanup = result.GetValue(o.NoSandboxCleanup) ? false : null,
            FailOnError = ResolveFailOnError(Opt.FlagOrValueOf(result, o.FailOnError), result.GetValue(o.NoFailOnError)),
            ContinueOnFail = result.GetValue(o.ContinueOnFail) ? true : null,
            RetryOnError = ResolveRetryOnError(Opt.FlagOrValueOf(result, o.RetryOnError)),
            MaxRetries = result.GetValue(o.MaxRetries),
            Timeout = result.GetValue(o.Timeout),
            AttemptTimeout = result.GetValue(o.AttemptTimeout),
            StreamIdleTimeout = result.GetValue(o.StreamIdleTimeout),
            MaxConnections = result.GetValue(o.MaxConnections),
            AdaptiveConnections = ParseAdaptive(result.GetValue(o.AdaptiveConnections)),
            Reporter = Reporter(common, io),
        };
        var logs = await EvalRetry.RunAsync(logFiles, retryOptions, cancellationToken).ConfigureAwait(false);
        foreach (var log in logs)
        {
            ResultsPrinter.Print(io.Out, log);
        }

        return ExitCode(logs);
    }

    /// <summary>The error contract of docs/ARCHITECTURE.md section 8: 0 when every log succeeded, 3 when one was cancelled, else 1.</summary>
    internal static int ExitCode(IReadOnlyList<EvalLog> logs) =>
        logs.All(log => log.Status == EvalStatus.Success) ? 0
            : logs.Any(log => log.Status == EvalStatus.Cancelled) ? 3
            : 1;

    internal static FailOnError? ResolveFailOnError(FlagValue failOnError, bool noFailOnError)
    {
        if (noFailOnError)
        {
            return FailOnError.Never;
        }

        if (!failOnError.Given)
        {
            return null;
        }

        if (failOnError.IsBare)
        {
            return FailOnError.Always;
        }

        if (!double.TryParse(failOnError.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            throw new UsageError($"Invalid value for '--fail-on-error': '{failOnError.Value}' is not a number.");
        }

        return threshold == 0.0 ? FailOnError.Always : FailOnError.Threshold(threshold);
    }

    internal static int? ResolveRetryOnError(FlagValue retryOnError)
    {
        if (!retryOnError.Given)
        {
            return null;
        }

        int value;
        try
        {
            value = CliArgs.IntOrBoolValue(retryOnError.Value, EvalOptionSet.DefaultRetryOnError);
        }
        catch (FormatException)
        {
            throw new UsageError($"Expected 'true', 'false', or an integer for --retry-on-error. Got: {retryOnError.Value}");
        }

        return value == 0 ? null : value;
    }

    internal static CachePolicy? ResolveCache(FlagValue cache)
    {
        if (!cache.Given)
        {
            return null;
        }

        var value = CliArgs.IntBoolOrStrValue(cache.Value, EvalOptionSet.DefaultCacheDays, null);
        switch (value)
        {
            case null:
                return null;
            case int days:
                return new CachePolicy { Expiry = $"{days}D" };
            case string path:
                var config = CliArgs.ResolveArgs(path);
                var policy = new CachePolicy();
                if (config.TryGetValue("expiry", out var expiry))
                {
                    policy = policy with { Expiry = expiry?.ToString() };
                }

                if (config.TryGetValue("per_epoch", out var perEpoch))
                {
                    policy = policy with { PerEpoch = perEpoch is bool flag ? flag : throw new PrerequisiteError($"Invalid cache policy '{path}': per_epoch must be true or false.") };
                }

                if (config.TryGetValue("scopes", out var scopes))
                {
                    policy = policy with
                    {
                        Scopes = scopes is IReadOnlyDictionary<string, object?> map
                            ? map.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
                            : throw new PrerequisiteError($"Invalid cache policy '{path}': scopes must be a mapping."),
                    };
                }

                return policy;
            default:
                throw new UsageError($"Invalid value for '--cache': {cache.Value}");
        }
    }

    internal static AdaptiveConnections? ParseAdaptive(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return AdaptiveConnections.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new UsageError($"Invalid value for '--adaptive-connections': {ex.Message}");
        }
    }

    internal static IReadOnlyList<Hooks>? ResolveHooks(IEnumerable<string>? names)
    {
        var wanted = (names ?? []).SelectMany(name => name.Split(',')).Select(name => name.Trim()).Where(name => name.Length > 0).ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var hooks = new List<Hooks>();
        foreach (var name in wanted)
        {
            hooks.Add(HookRegistry.Lookup(name) ?? InstantiateHook(name) ?? throw new PrerequisiteError($"Hook '{name}' not found: it is neither registered with HookRegistry nor the name of a loaded Hooks type."));
        }

        return hooks;
    }

    internal static IEvalReporter? Reporter(CommonValues common, CliIo io) => common.Display == "none" ? null : new ConsoleEvalReporter(io.Out);

    private static Hooks? InstantiateHook(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }

            var type = types.FirstOrDefault(candidate => !candidate.IsAbstract && typeof(Hooks).IsAssignableFrom(candidate)
                && (candidate.FullName == name || candidate.Name == name));
            if (type is not null)
            {
                return type.GetConstructor(Type.EmptyTypes) is not null
                    ? (Hooks?)Activator.CreateInstance(type)
                    : throw new PrerequisiteError($"Hook type '{type.FullName}' has no parameterless constructor; register an instance with HookRegistry instead.");
            }
        }

        return null;
    }

    /// <summary>Everything an <c>eval</c> / <c>eval-set</c> run needs, resolved from the parsed options.</summary>
    internal sealed record Plan(IReadOnlyList<EvalTask> Tasks, IReadOnlyList<Model> Models, EvalOptions Options)
    {
        public static Plan Build(EvalOptionSet o, ParseResult result, CliIo io)
        {
            var common = o.Common.Process(result);
            var registry = TaskRegistry.Discover(result.GetValue(o.Assembly));
            var specs = result.GetValue(o.Tasks) ?? [];
            var infos = specs.Length == 0 ? registry.Tasks : specs.Select(registry.Resolve).ToList();
            if (infos.Count == 0)
            {
                throw new PrerequisiteError("No tasks found: pass a task name or --assembly with an assembly that defines [Task] methods.");
            }

            var taskArgs = CliArgs.ParseCliConfig(result.GetValue(o.T), result.GetValue(o.TaskConfig));
            var solverArgs = CliArgs.ParseCliConfig(result.GetValue(o.S), result.GetValue(o.SolverConfig));
            var modelArgs = CliArgs.ParseCliConfig(result.GetValue(o.M), result.GetValue(o.ModelConfig));

            var generate = GenerateConfigOf(o, result);
            if (result.GetValue(o.GenerateConfigFile) is { } generateFile)
            {
                generate = InspectAzureAI.Eval.Model.GenerateConfigExtensions.Merge(GenerateConfigBinding.FromValues(CliArgs.ResolveArgs(generateFile), $"--generate-config '{generateFile}'"), generate);
            }

            if (ResolveCache(Opt.FlagOrValueOf(result, o.Cache)) is { } cache)
            {
                generate = generate with { Cache = cache };
            }

            var adaptive = ParseAdaptive(result.GetValue(o.AdaptiveConnections));
            if (adaptive is not null)
            {
                generate = generate with { AdaptiveConnections = adaptive };
            }

            var baseUrl = result.GetValue(o.ModelBaseUrl);
            var modelNames = CliArgs.ParseCommaSeparated(result.GetValue(o.Model))?.Select(name => name.Trim()).Where(name => name.Length > 0).ToList();
            IReadOnlyList<string?> names = modelNames is { Count: > 0 } ? modelNames.Cast<string?>().ToList() : [null];
            var models = names
                .Select(name => ModelProviders.Resolve(name, generate, baseUrl, modelArgs))
                .ToList();

            var modelRoles = ModelRoleArgs.Parse(result.GetValue(o.ModelRole), (name, config, args) => ModelProviders.Resolve(name, config, null, args));
            var solver = result.GetValue(o.Solver) is { } solverName ? Catalog.CreateSolver(solverName, solverArgs) : null;
            var sandbox = CliArgs.ParseSandbox(result.GetValue(o.Sandbox));

            var epochs = result.GetValue(o.Epochs);
            var reducers = result.GetValue(o.NoEpochsReducer) ? [] : Catalog.CreateReducers(CliArgs.ParseCommaSeparated(result.GetValue(o.EpochsReducer)));

            SamplesLimit? limit;
            try
            {
                limit = CliArgs.ParseSamplesLimit(result.GetValue(o.Limit));
            }
            catch (FormatException ex)
            {
                throw new UsageError(ex.Message);
            }

            if (limit is { IsRange: true })
            {
                throw new PrerequisiteError("--limit ranges (e.g. 10-20) are not supported by this port; pass a count or --sample-id.");
            }

            var tokenLimitText = result.GetValue(o.TokenLimit);
            int? tokenLimit = null;
            if (tokenLimitText is not null)
            {
                if (!int.TryParse(tokenLimitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens))
                {
                    throw new UsageError($"Invalid value for '--token-limit': '{tokenLimitText}' (the 500k / 1m / output: forms are not supported by this port; pass an integer).");
                }

                tokenLimit = tokens;
            }

            var options = new EvalOptions
            {
                Model = models[0],
                Limit = limit?.Count,
                SampleIds = CliArgs.ParseSampleId(result.GetValue(o.SampleId))?.Cast<object>().ToList(),
                MaxSamples = result.GetValue(o.MaxSamples),
                AdaptiveConnections = adaptive,
                FailOnError = ResolveFailOnError(Opt.FlagOrValueOf(result, o.FailOnError), result.GetValue(o.NoFailOnError)),
                ContinueOnFail = result.GetValue(o.ContinueOnFail) ? true : null,
                RetryOnError = ResolveRetryOnError(Opt.FlagOrValueOf(result, o.RetryOnError)),
                LogDir = common.LogDir,
                LogFormat = result.GetValue(o.LogFormat) is { } format ? LogFormats.Parse(format) : null,
                Cleanup = !result.GetValue(o.NoSandboxCleanup),
                MessageLimit = result.GetValue(o.MessageLimit),
                TokenLimit = tokenLimit,
                TimeLimit = result.GetValue(o.TimeLimit) is { } time ? TimeSpan.FromSeconds(time) : null,
                CostLimit = result.GetValue(o.CostLimit),
                ModelCostConfig = result.GetValue(o.ModelCostConfig),
                TurnLimit = result.GetValue(o.TurnLimit),
                WorkingLimit = result.GetValue(o.WorkingLimit) is { } working ? TimeSpan.FromSeconds(working) : null,
                Reporter = Reporter(common, io),
                ModelRoles = modelRoles,
                Hooks = ResolveHooks(result.GetValue(o.Hooks)),
                Approval = result.GetValue(o.Approval) is { } approval ? ApprovalOption.FromSpec(approval) : null,
            };

            var tasks = infos.Select(info =>
            {
                var task = TaskRegistry.Create(info, taskArgs);
                if (solver is not null)
                {
                    task = task with { Solver = solver };
                }

                if (sandbox is not null)
                {
                    task = task with { Sandbox = sandbox };
                }

                if (epochs is { } count)
                {
                    task = task with { Epochs = new Epochs(count, reducers) };
                }

                return task;
            }).ToList();

            return new Plan(tasks, models, options);
        }

        private static GenerateConfig GenerateConfigOf(EvalOptionSet o, ParseResult result) => new()
        {
            MaxRetries = result.GetValue(o.MaxRetries),
            Timeout = result.GetValue(o.Timeout),
            AttemptTimeout = result.GetValue(o.AttemptTimeout),
            StreamIdleTimeout = result.GetValue(o.StreamIdleTimeout),
            MaxConnections = result.GetValue(o.MaxConnections),
            SystemMessage = result.GetValue(o.SystemMessage),
            MaxTokens = result.GetValue(o.MaxTokens),
            TopP = result.GetValue(o.TopP),
            Temperature = result.GetValue(o.Temperature),
            StopSeqs = CliArgs.ParseCommaSeparated(result.GetValue(o.StopSeqs)),
            BestOf = result.GetValue(o.BestOf),
            FrequencyPenalty = result.GetValue(o.FrequencyPenalty),
            PresencePenalty = result.GetValue(o.PresencePenalty),
            Seed = result.GetValue(o.Seed),
            TopK = result.GetValue(o.TopK),
            NumChoices = result.GetValue(o.NumChoices),
            Logprobs = result.GetValue(o.Logprobs) ? true : null,
            TopLogprobs = result.GetValue(o.TopLogprobs),
            ParallelToolCalls = result.GetValue(o.NoParallelToolCalls) ? false : result.GetValue(o.ParallelToolCalls) ? true : null,
            InternalTools = result.GetValue(o.NoInternalTools) ? false : result.GetValue(o.InternalTools) ? true : null,
            MaxToolOutput = result.GetValue(o.MaxToolOutput),
            CachePrompt = result.GetValue(o.CachePrompt) switch { null => null, "true" => true, "false" => false, var other => other },
            FallbackModels = CliArgs.ParseCommaSeparated(result.GetValue(o.FallbackModels)),
            Verbosity = result.GetValue(o.Verbosity),
            Effort = result.GetValue(o.Effort),
            ReasoningEffort = result.GetValue(o.ReasoningEffort),
            ReasoningMode = result.GetValue(o.ReasoningMode),
            ReasoningTokens = result.GetValue(o.ReasoningTokens),
            ReasoningSummary = result.GetValue(o.ReasoningSummary),
            ReasoningHistory = result.GetValue(o.ReasoningHistory),
            LogitBias = ParseLogitBias(result.GetValue(o.LogitBias)),
        };

        private static IReadOnlyDictionary<int, double>? ParseLogitBias(string? text)
        {
            if (text is null)
            {
                return null;
            }

            var bias = new Dictionary<int, double>();
            foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2
                    || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var token)
                    || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    throw new UsageError($"Invalid value for '--logit-bias': expected token=bias pairs (e.g. \"42=10,43=-10\"), got '{text}'.");
                }

                bias[token] = value;
            }

            return bias;
        }
    }
}
