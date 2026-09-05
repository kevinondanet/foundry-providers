using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;
using AgentPrompt = InspectAzureAI.Swe.Util.AgentPrompt;

namespace InspectAzureAI.Swe.MiniSwe;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of the output dictionary of <c>environments/local.py</c> <c>LocalEnvironment.execute</c>.</summary>
public sealed record CommandObservation(string Output, int ReturnCode, string ExceptionInfo)
{
    /// <summary>Port of the <c>not_executed</c> padding of <c>format_toolcall_observation_messages</c>.</summary>
    public static readonly CommandObservation NotExecuted = new("", -1, "action was not executed");
}

/// <summary>
/// Native port of mini-swe-agent's <c>agents/default.py</c> <c>DefaultAgent</c> loop together with the
/// tool-calling pieces of <c>models/litellm_model.py</c> and <c>models/utils/actions_toolcall.py</c>, the
/// command execution of <c>environments/local.py</c>, and the attempts / resume behaviour of inspect_swe
/// <c>mini_swe_agent.py</c> and <c>resumable_agent.py</c>. The model is the sample's <see cref="Model"/>
/// and commands run in the sample sandbox, so no Python package is needed inside the image.
/// <para>
/// This is deliberately not <c>Agents.React</c>: upstream's loop has no submit tool (a command printing the submit
/// marker ends the run), drops malformed assistant turns behind a templated format error with a consecutive-error
/// cap, renders observations as <c>LocalEnvironment</c> JSON with <c>not_executed</c> padding, and resumes from a
/// saved trajectory — semantics react would have to be bent around. It reuses the shared seams instead: the
/// ambient tool approval (<see cref="ToolApproval"/>), compaction (<see cref="MiniSweAgentOptions.Compaction"/>)
/// and the prompt cache (<see cref="MiniSweAgentOptions.Cache"/>). See <c>docs/ports/showcase-wiring.md</c>.
/// </para>
/// </summary>
public sealed class MiniSweAgent
{
    public const string ExitStatusKey = "mini_swe_agent_exit_status";

    public const string SubmissionKey = "mini_swe_agent_submission";

    /// <summary>Store key of the last trajectory (the file inspect_swe's resumable agent reloads on resume).</summary>
    public const string TrajectoryKey = "mini_swe_agent_trajectory";

    /// <summary>Store key of the model call count (<c>info.model_stats.api_calls</c> of the trajectory file).</summary>
    public const string ApiCallsKey = "mini_swe_agent_api_calls";

    public const string TranscriptSource = "mini_swe_agent";

    /// <summary>Port of <c>BASH_TOOL</c>.</summary>
    public static readonly ToolInfo BashTool = new("bash", "Execute a bash command")
    {
        Parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["command"] = ToolParam.Of("string", "The bash command to execute") },
            Required = ["command"],
        },
    };

    private static readonly IReadOnlyList<ToolInfo> Tools = [BashTool];

    private readonly MiniSweAgentOptions _options;

    public MiniSweAgent(MiniSweAgentOptions? options = null)
    {
        _options = options ?? new MiniSweAgentOptions();
    }

    public MiniSweAgentOptions Options => _options;

    /// <summary>
    /// Port of the inspect_swe <c>execute</c> closure: builds the task prompt, runs the loop once per attempt
    /// (resuming the stored trajectory after the first) and scores between attempts. The returned state carries
    /// the full mini-swe conversation and the last model output (its completion is the submission).
    /// </summary>
    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var model = _options.Model ?? context.ActiveModel;
        var sandbox = context.Sandbox(_options.Sandbox);
        var cwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, _options.User, _options.Cwd, cancellationToken).ConfigureAwait(false);

        var (prompt, hasAssistantResponse) = AgentPrompt.BuildUserPrompt(state.Messages);
        var systemMessages = state.Messages.OfType<ChatMessageSystem>().Select(m => m.Text).ToList();
        if (_options.SystemPrompt is not null)
        {
            systemMessages.Add(_options.SystemPrompt);
        }

        if (systemMessages.Count > 0)
        {
            prompt = $"System instructions:\n{string.Join("\n\n", systemMessages)}\n\nTask:\n{prompt}";
        }

        var session = new Session(_options, context, model, sandbox, cwd);
        var agentPrompt = prompt;
        var attemptCount = 0;
        try
        {
            while (true)
            {
                var isResume = hasAssistantResponse || attemptCount > 0;
                await session.RunAsync(agentPrompt, isResume, cancellationToken).ConfigureAwait(false);

                attemptCount++;
                if (attemptCount >= _options.Attempts.Attempts)
                {
                    break;
                }

                var scores = await session.ScoreAsync(prompt).ConfigureAwait(false);
                var toFloat = _options.Attempts.ScoreValue ?? ValueToFloat.Default;
                if (toFloat(scores[0].Value) == 1.0)
                {
                    break;
                }

                agentPrompt = _options.Attempts.IncorrectMessage;
            }
        }
        finally
        {
            // A limit, cancellation or error still leaves the partial trajectory on the state (as the bridge does
            // for Claude Code), so the sample is logged and scored with what the agent did.
            if (session.Messages.Count > 0)
            {
                state.Messages = session.Messages;
            }

            if (session.Output is { } output)
            {
                state.Output = output;
            }
        }

        return state;
    }

    /// <summary>
    /// Port of <c>parse_toolcall_actions</c>: every call must be <c>bash</c> with a <c>command</c> argument;
    /// otherwise the rendered format error is returned instead of actions. The template's <c>finish_reason</c>
    /// is the litellm value (see <see cref="FinishReason"/>).
    /// </summary>
    internal static (IReadOnlyList<BashAction> Actions, string? Error) ParseActions(ModelOutput output)
    {
        var finishReason = FinishReason(output.StopReason);
        var calls = output.Message.ToolCalls ?? [];
        if (calls.Count == 0)
        {
            return ([], MiniSweTemplates.RenderFormatError(MiniSweTemplates.NoToolCallsError, hasToolCalls: false, finishReason));
        }

        var actions = new List<BashAction>(calls.Count);
        foreach (var call in calls)
        {
            var error = "";
            var arguments = call.Arguments;
            if (call.ParseError is { } parseError)
            {
                error = $"Error parsing tool call arguments: {parseError}.";
                arguments = new JsonObject();
            }

            if (call.Function != BashTool.Name)
            {
                error += $"Unknown tool '{call.Function}'.";
            }

            if (!arguments.ContainsKey("command"))
            {
                error += "Missing 'command' argument in bash tool call.";
            }

            if (error.Length > 0)
            {
                return ([], MiniSweTemplates.RenderFormatError(error.Trim(), hasToolCalls: true, finishReason));
            }

            var command = arguments["command"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : arguments["command"]?.ToJsonString() ?? "null";
            actions.Add(new BashAction(command, call.Id, call));
        }

        return (actions, null);
    }

    /// <summary>
    /// The <c>finish_reason</c> the format-error template sees. Upstream mini-swe-agent reads litellm's value, where a
    /// response cut off by <c>max_tokens</c> is <c>length</c>; both Inspect truncation stops (<c>MaxTokens</c> and the
    /// context-window <c>ModelLength</c>) map to it here so the template's token-limit branch fires as its author
    /// intended. Inspect's own bridge (<c>openai_finish_reason</c>) would report <c>max_tokens</c> as <c>stop</c>.
    /// </summary>
    internal static string FinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.MaxTokens or StopReason.ModelLength => "length",
        StopReason.ToolCalls => "tool_calls",
        StopReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    /// <summary>
    /// Port of <c>LocalEnvironment._check_finished</c>: the submission is everything after a first non-blank
    /// line reading exactly the submit marker, provided the command succeeded.
    /// </summary>
    internal static string? CheckFinished(CommandObservation observation)
    {
        var lines = SplitLinesKeepEnds(observation.Output.TrimStart());
        if (lines.Count > 0 && lines[0].Trim() == MiniSweTemplates.SubmitMarker && observation.ReturnCode == 0)
        {
            return string.Concat(lines.Skip(1));
        }

        return null;
    }

    /// <summary>
    /// Port of <c>resumable_agent.py</c> <c>_fix_dangling_tool_calls</c>: a trailing assistant message whose
    /// tool calls were never answered loses them (keeping the text, or "Task completed." when there is none)
    /// so the resumed conversation still alternates correctly.
    /// </summary>
    internal static void FixDanglingToolCalls(List<ChatMessage> messages)
    {
        if (messages.Count == 0 || messages[^1] is not ChatMessageAssistant { ToolCalls: { Count: > 0 } calls } last)
        {
            return;
        }

        var answered = messages.OfType<ChatMessageTool>().Select(m => m.ToolCallId).ToHashSet(StringComparer.Ordinal);
        if (calls.All(call => answered.Contains(call.Id)))
        {
            return;
        }

        var empty = last.Content.IsString ? last.Content.Text!.Length == 0 : last.Content.Items!.Count == 0;
        messages[^1] = last with { ToolCalls = null, Content = empty ? "Task completed." : last.Content };
    }

    /// <summary>Port of <c>str.splitlines(keepends=True)</c>, including Python's extra line boundaries.</summary>
    internal static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                lines.Add(text[start..(i + 2)]);
                i += 2;
                start = i;
            }
            else if (IsLineBoundary(c))
            {
                lines.Add(text[start..(i + 1)]);
                i++;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private static bool IsLineBoundary(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>Port of the <c>{"command", "tool_call_id"}</c> action dictionary; <paramref name="Call"/> is the model's call, for the approval gate.</summary>
    internal sealed record BashAction(string Command, string ToolCallId, ToolCall? Call = null);

    private enum OutcomeKind
    {
        Clean,
        FormatError,
        Exit,
    }

    /// <summary>What a step produced: a clean step, a <c>FormatError</c> message, or an exit note.</summary>
    private sealed record Outcome(OutcomeKind Kind, ChatMessageUser? Message = null, string? ExitStatus = null, string Submission = "")
    {
        public static readonly Outcome Clean = new(OutcomeKind.Clean);

        public static Outcome FormatError(string content) => new(OutcomeKind.FormatError, new ChatMessageUser(content));

        public static Outcome Exit(string status, string submission = "") => new(OutcomeKind.Exit, ExitStatus: status, Submission: submission);
    }

    /// <summary>One <c>DefaultAgent</c> instance: the conversation, counters and clock of a run.</summary>
    private sealed class Session
    {
        private readonly MiniSweAgentOptions _options;

        private readonly SampleContext _context;

        private readonly Model _model;

        private readonly ISandboxEnvironment _sandbox;

        private readonly string _cwd;

        private readonly IReadOnlyDictionary<string, string> _env;

        private readonly List<ChatMessage> _messages = [];

        private readonly Stopwatch _clock = new();

        private int _nCalls;

        private int _nConsecutiveFormatErrors;

        private ModelOutput? _output;

        private ICompact? _compact;

        public Session(MiniSweAgentOptions options, SampleContext context, Model model, ISandboxEnvironment sandbox, string cwd)
        {
            _options = options;
            _context = context;
            _model = model;
            _sandbox = sandbox;
            _cwd = cwd;
            var env = new Dictionary<string, string>(MiniSweTemplates.DefaultEnvironment, StringComparer.Ordinal);
            foreach (var (name, value) in options.Env ?? new Dictionary<string, string>())
            {
                env[name] = value;
            }

            _env = env;
        }

        public List<ChatMessage> Messages => _messages;

        public ModelOutput? Output => _output;

        /// <summary>Port of <c>DefaultAgent.run</c> (and the resume branch of <c>ResumableAgent.run</c>).</summary>
        public async Task RunAsync(string task, bool resume, CancellationToken cancellationToken)
        {
            _clock.Restart();
            _nConsecutiveFormatErrors = 0;
            if (resume)
            {
                Resume(task);
            }
            else
            {
                await StartAsync(task, cancellationToken).ConfigureAwait(false);
            }

            // the compaction prefix (system + task) is derived from the conversation at the start of each run, as react does
            _compact = _options.Compaction?.Invoke(_messages.ToArray(), Tools, _model);

            try
            {
                while (true)
                {
                    Outcome outcome;
                    try
                    {
                        outcome = await StepAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordExit(ex.GetType().Name, "");
                        throw;
                    }

                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Clean:
                            _nConsecutiveFormatErrors = 0;
                            break;
                        case OutcomeKind.FormatError:
                            _nConsecutiveFormatErrors++;
                            _messages.Add(outcome.Message!);
                            if (0 < _options.MaxConsecutiveFormatErrors && _options.MaxConsecutiveFormatErrors <= _nConsecutiveFormatErrors)
                            {
                                RecordExit("RepeatedFormatError", "");
                                return;
                            }

                            break;
                        default:
                            RecordExit(outcome.ExitStatus!, outcome.Submission);
                            return;
                    }
                }
            }
            finally
            {
                Save();
            }
        }

        /// <summary>Port of <c>score(state)</c> for an agent state: the task scorers over a task state carrying this conversation.</summary>
        public async Task<IReadOnlyList<Score>> ScoreAsync(string input)
        {
            var scorer = _context.Scorer
                ?? throw new InvalidOperationException("The score() function can only be called while executing a task with a scorer.");
            var taskState = _context.SampleState?.WithMessages(_messages, _output ?? new ModelOutput { Model = _model.Name })
                ?? new TaskState(_model.Name, sampleId: "mini-swe-agent", epoch: 1, input: input, messages: _messages, output: _output, store: _context.Store);
            var scores = await scorer(taskState).ConfigureAwait(false);
            return scores.Count > 0 ? scores : throw new InvalidOperationException("The task scorer returned no scores.");
        }

        private async Task StartAsync(string task, CancellationToken cancellationToken)
        {
            var variables = await TemplateVariablesAsync(task, cancellationToken).ConfigureAwait(false);
            _messages.Clear();
            _nCalls = 0;
            _messages.Add(new ChatMessageSystem(TemplateRenderer.Render(_options.SystemTemplate, variables)));
            _messages.Add(new ChatMessageUser(TemplateRenderer.Render(_options.InstanceTemplate, variables)));
        }

        private void Resume(string task)
        {
            if (_messages.Count == 0)
            {
                var trajectory = _context.Store.Get(TrajectoryKey) as IReadOnlyList<ChatMessage>
                    ?? throw new InvalidOperationException(
                        "Cannot resume: no mini-swe-agent trajectory was found for this sample. The first run may not have completed successfully.");
                _messages.AddRange(trajectory);
                _nCalls = _context.Store.Get(ApiCallsKey, 0);
            }

            FixDanglingToolCalls(_messages);
            _messages.Add(new ChatMessageUser($"{task}\n\n{MiniSweTemplates.ResumeReminder}"));
        }

        /// <summary>Port of <c>DefaultAgent.step</c> / <c>query</c>: limits, one model call, action parsing, execution.</summary>
        private async Task<Outcome> StepAsync(CancellationToken cancellationToken)
        {
            if (0 < _options.StepLimit && _options.StepLimit <= _nCalls)
            {
                return Outcome.Exit("LimitsExceeded");
            }

            if (0 < _options.WallTimeLimitSeconds && _options.WallTimeLimitSeconds <= (int)_clock.Elapsed.TotalSeconds)
            {
                return Outcome.Exit("TimeExceeded");
            }

            IReadOnlyList<ChatMessage> input = _messages.ToArray();
            if (_compact is { } compact)
            {
                var compacted = await compact.CompactInputAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
                input = compacted.Input;
                if (compacted.Message is { } notice)
                {
                    _messages.Add(notice);
                }
            }

            _nCalls++;
            var output = await _model.GenerateAsync(input, Tools, ToolChoice.Auto, cache: _options.Cache, cancellationToken: cancellationToken).ConfigureAwait(false);
            _output = output;
            if (_compact is { } recordTo)
            {
                await recordTo.RecordOutputAsync(input, output, cancellationToken).ConfigureAwait(false);
            }

            // A context-window overflow is recovered by a forced compaction of the trajectory (the failed turn was
            // never added); without compaction it falls through to the format error upstream would render.
            if (output.StopReason == StopReason.ModelLength
                && _compact is { } recover
                && await Compaction.TryRecoverOverflowAsync(recover, _messages.ToArray(), cancellationToken).ConfigureAwait(false) is { } recovered)
            {
                _messages.Clear();
                _messages.AddRange(recovered);
                return Outcome.Clean;
            }

            // Upstream raises FormatError before the assistant message is added, so only the format error
            // message enters the trajectory (the response itself lives in the model event).
            var (actions, error) = ParseActions(output);
            if (error is not null)
            {
                return Outcome.FormatError(error);
            }

            _messages.Add(output.Message);
            return await ExecuteActionsAsync(actions, output.Message.Text, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Port of <c>execute_actions</c> + <c>format_toolcall_observation_messages</c>, with Inspect's tool approval
        /// applied to each call first (the ambient policies of the eval or task, as <c>execute_tools</c> applies them):
        /// a rejected command is not run and its observation carries the rejection (the tool message records an
        /// <c>approval</c> error), a modified call runs the approver's command, and <c>terminate</c> ends the sample.
        /// </summary>
        private async Task<Outcome> ExecuteActionsAsync(IReadOnlyList<BashAction> actions, string assistantText, CancellationToken cancellationToken)
        {
            var observations = new List<CommandObservation>(actions.Count);
            var errors = new List<ToolCallError?>(actions.Count);
            string? submission = null;
            foreach (var action in actions)
            {
                var command = action.Command;
                if (action.Call is { } call && ToolApproval.HaveToolApproval)
                {
                    var (approved, approval) = await ToolApproval.ApplyAsync(assistantText, call, null, _messages, cancellationToken).ConfigureAwait(false);
                    if (!approved)
                    {
                        if (approval?.Decision == ApprovalDecision.Terminate)
                        {
                            throw new TerminateSampleException("Tool call approver requested termination.");
                        }

                        var message = new ToolApprovalError(approval?.Explanation).Message;
                        observations.Add(new CommandObservation("", -1, message));
                        errors.Add(new ToolCallError("approval", message));
                        _context.Transcript.Info(TranscriptSource, new JsonObject { ["command"] = command, ["approval"] = ApprovalDecision.Reject.ToPython() });
                        continue;
                    }

                    if (approval?.Modified is { } modified && modified.Arguments["command"] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        command = text;
                    }
                }

                var observation = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                observations.Add(observation);
                errors.Add(null);
                if (CheckFinished(observation) is { } submitted)
                {
                    submission = submitted;
                    break;
                }
            }

            while (observations.Count < actions.Count)
            {
                observations.Add(CommandObservation.NotExecuted);
                errors.Add(null);
            }

            for (var i = 0; i < actions.Count; i++)
            {
                _messages.Add(new ChatMessageTool(MiniSweTemplates.RenderObservation(observations[i]), toolCallId: actions[i].ToolCallId, function: BashTool.Name, error: errors[i]));
            }

            return submission is null ? Outcome.Clean : Outcome.Exit("Submitted", submission);
        }

        /// <summary>Port of <c>LocalEnvironment.execute</c>: any failure to run becomes an observation, never an exception.</summary>
        private async Task<CommandObservation> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            CommandObservation observation;
            try
            {
                var result = await _sandbox.ExecAsync(
                    ["bash", "-c", "exec 2>&1\n" + command],
                    cwd: _cwd,
                    env: _env,
                    user: _options.User,
                    timeout: _options.CommandTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                observation = new CommandObservation(result.Stdout + result.Stderr, result.ReturnCode, "");
            }
            catch (SandboxTimeoutException ex)
            {
                var seconds = _options.CommandTimeout.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
                observation = new CommandObservation(
                    ex.TruncatedOutput,
                    -1,
                    $"An error occurred while executing the command: Command '{command}' timed out after {seconds} seconds");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observation = new CommandObservation("", -1, $"An error occurred while executing the command: {ex.Message}");
            }

            _context.Transcript.Info(TranscriptSource, new JsonObject { ["command"] = command, ["returncode"] = observation.ReturnCode });
            return observation;
        }

        /// <summary>The <c>role: exit</c> message of upstream: status and submission go to the store and transcript.</summary>
        private void RecordExit(string status, string submission)
        {
            _context.Store.Set(ExitStatusKey, status);
            _context.Store.Set(SubmissionKey, submission);
            _context.Transcript.Info(TranscriptSource, new JsonObject { ["exit_status"] = status, ["submission"] = submission });
            if (status == "Submitted" && _output is { } output)
            {
                _output = output with { Completion = submission };
            }
        }

        /// <summary>Port of <c>DefaultAgent.save</c>: the trajectory inspect_swe keeps in the sandbox lives in the store here.</summary>
        private void Save()
        {
            _context.Store.Set(TrajectoryKey, _messages.ToArray());
            _context.Store.Set(ApiCallsKey, _nCalls);
        }

        /// <summary>Port of <c>LocalEnvironment.get_template_vars</c>: <c>platform.uname()</c> read from the sandbox.</summary>
        private async Task<IReadOnlyDictionary<string, string>> TemplateVariablesAsync(string task, CancellationToken cancellationToken)
        {
            string[] uname = [];
            try
            {
                var result = await _sandbox.ExecAsync(
                    ["bash", "-c", "uname -s; uname -n; uname -r; uname -v; uname -m"],
                    user: _options.User,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                uname = result.Stdout.Split('\n');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProviderLogger.Warning($"Unable to read uname from the sandbox: {ex.Message}");
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["task"] = task,
                ["system"] = uname.ElementAtOrDefault(0) ?? "",
                ["node"] = uname.ElementAtOrDefault(1) ?? "",
                ["release"] = uname.ElementAtOrDefault(2) ?? "",
                ["version"] = uname.ElementAtOrDefault(3) ?? "",
                ["machine"] = uname.ElementAtOrDefault(4) ?? "",
            };
        }
    }
}
