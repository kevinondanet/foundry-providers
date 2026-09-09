using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>
/// Port of <c>agent/_human/commands/submit.py</c> <c>SessionEndCommand</c>: the shared half of submit and quit
/// that collects the <c>script</c> session logs from <see cref="HumanAgentInstall.RecordSessionDir"/> (never failing).
/// </summary>
public abstract class SessionEndCommand(ISandboxEnvironment sandbox, bool recordSession) : HumanAgentCommand
{
    private readonly ISandboxEnvironment _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));

    public override int Group => 1;

    /// <summary>Whether the session is being recorded (and its logs collected at the end).</summary>
    protected bool RecordSession { get; } = recordSession;

    /// <summary>Port of <c>_read_session_logs</c>: every file under the session directory, by name; problems are warnings.</summary>
    protected async Task<IReadOnlyDictionary<string, string>> ReadSessionLogsAsync(CancellationToken cancellationToken)
    {
        var logs = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = await _sandbox.ExecAsync(["ls", "-1", HumanAgentInstall.RecordSessionDir], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            ProviderLogger.Warning($"Error listing human agent session logs: {result.Stderr}");
            return logs;
        }

        foreach (var sessionLog in result.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')))
        {
            try
            {
                logs[sessionLog] = await _sandbox.ReadFileAsync($"{HumanAgentInstall.RecordSessionDir}/{sessionLog}", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ProviderLogger.Warning($"Error reading human agent session log: {ex.Message}");
            }
        }

        return logs;
    }

    /// <summary>Ends the task: collects the logs when recording, stops the clock and records the answer.</summary>
    protected async Task EndSessionAsync(HumanAgentState state, string answer, CancellationToken cancellationToken)
    {
        if (RecordSession)
        {
            state.Logs = await ReadSessionLogsAsync(cancellationToken).ConfigureAwait(false);
        }

        state.SetRunning(false);
        state.Answer = answer;
    }
}

/// <summary>Port of <c>QuitCommand</c>: <c>task quit</c> ends the task without an answer (the answer becomes the empty string).</summary>
public sealed class QuitCommand(ISandboxEnvironment sandbox, bool recordSession) : SessionEndCommand(sandbox, recordSession)
{
    public override string Name => "quit";

    public override string Description => "Quit the task without submitting an answer.";

    public override string CliSource => """
        def quit(args: Namespace) -> None:
            # verify that the user wants to proceed
            action = "quit the task without submitting an answer (ending the exercise)"
            try:
                while True:
                    response = (
                        input(
                            f"\nDo you definitely want to {action}?\n\nThis will disconnect you from the task environment and you won't be able to reconnect.\n\nYes (y) or No (n): "
                        )
                        .lower()
                        .strip()
                    )
                    if response in ["yes", "y"]:
                        break
                    elif response in ["no", "n"]:
                        return
                    else:
                        print("Please enter yes or no.")
            except EOFError:
                return

            # thank the user!
            print(
                "\nThank you for working on this task!\n\n"
                + "Your task will now be scored and you will be disconnected from this container.\n"
            )

            call_human_agent("quit")
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => async (_, cancellationToken) =>
    {
        await EndSessionAsync(state, "", cancellationToken).ConfigureAwait(false);
        return null;
    };

    public override string? Activity(JsonObject parameters, JsonNode? result) => "Task quit without an answer.";
}

/// <summary>Port of <c>SubmitCommand</c>: <c>task submit [answer]</c> validates, confirms, then ends the task with the answer.</summary>
public sealed class SubmitCommand(ISandboxEnvironment sandbox, bool recordSession) : SessionEndCommand(sandbox, recordSession)
{
    public override string Name => "submit";

    public override string Description => "Submit your final answer for the task.";

    public override IReadOnlyList<HumanAgentCliArg> CliArgs =>
        [new HumanAgentCliArg("answer", "Answer to submit for scoring (optional, not required for all tasks)")];

    public override string CliSource => """
        def submit(args: Namespace) -> None:
            # read cli args
            call_args = vars(args)

            # first validate (print and exit if we get a str back)
            error = call_human_agent("validate", **call_args)
            if error:
                print(error)
                return

            # verify that the user wants to proceed
            answer = call_args.get("answer", None)
            answer_text = f" '{answer}'" if answer else ""
            action = f"end the task and submit{answer_text}"

            try:
                while True:
                    response = (
                        input(
                            f"\nDo you definitely want to {action}?\n\nThis will disconnect you from the task environment and you won't be able to reconnect.\n\nYes (y) or No (n): "
                        )
                        .lower()
                        .strip()
                    )
                    if response in ["yes", "y"]:
                        break
                    elif response in ["no", "n"]:
                        return
                    else:
                        print("Please enter yes or no.")
            except EOFError:
                return

            # thank the user!
            print(
                "\nThank you for working on this task!\n\n"
                + "Your task will now be scored and you will be disconnected from this container.\n"
            )

            call_human_agent("submit", **call_args)
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => async (parameters, cancellationToken) =>
    {
        var answer = StringParam(parameters, "answer") ?? "";
        await EndSessionAsync(state, answer, cancellationToken).ConfigureAwait(false);
        return null;
    };

    public override string? Activity(JsonObject parameters, JsonNode? result)
    {
        var answer = StringParam(parameters, "answer");
        return string.IsNullOrEmpty(answer) ? "Task submitted without an explicit answer." : $"Answer submitted: '{answer}'";
    }
}

/// <summary>
/// Port of <c>ValidateCommand</c>: the service-only <c>validate</c> method submit and score call first. It fails
/// when the clock is stopped, when an answer is required but missing, or when a pattern is set and the answer
/// does not match it from the start (Python <c>re.match</c>). Deviation: Python's <c>answer: bool | str</c> is
/// split into <paramref name="answer"/> (required or not) and <paramref name="answerPattern"/> (which implies required).
/// </summary>
public sealed class ValidateCommand(bool answer, string? answerPattern = null) : HumanAgentCommand
{
    private readonly bool _answerRequired = answer || answerPattern is not null;

    private readonly Regex? _pattern = answerPattern is null ? null : new Regex($@"\A(?:{answerPattern})", RegexOptions.None, TimeSpan.FromSeconds(1));

    public override string Name => "validate";

    public override string Description => "Validate a task submission.";

    public override IReadOnlyList<HumanAgentCommandContext> Contexts => [HumanAgentCommandContext.Service];

    public override SandboxServiceMethod Service(HumanAgentState state) => (parameters, _) =>
    {
        if (!state.IsRunning())
        {
            return Task.FromResult<JsonNode?>(Text(HumanAgentText.Failed("Task is stopped (use 'task start' to start)")));
        }

        if (_answerRequired)
        {
            var value = StringParam(parameters, "answer")?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return Task.FromResult<JsonNode?>(Text(HumanAgentText.Failed("An explicit answer is required for scoring this task.")));
            }

            if (_pattern is not null && !_pattern.IsMatch(value))
            {
                return Task.FromResult<JsonNode?>(Text(HumanAgentText.Failed("Your answer was not in the required format (please review the task instructions)")));
            }
        }

        // made it through verification
        return Task.FromResult<JsonNode?>(null);
    };
}
