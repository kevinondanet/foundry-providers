using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human;

/// <summary>
/// Port of <c>agent/_human/view.py</c> <c>HumanAgentView</c>: what the human agent tells the operator while a
/// person works in the sandbox. Deviation: <see cref="Activity"/> is an addition so the console can print
/// notes, intermediate scores and the submission as they arrive (Python's Textual panel reads them from the
/// state itself).
/// </summary>
public interface IHumanAgentView
{
    /// <summary>Port of <c>connect(connection)</c>: the sandbox is installed and ready for login.</summary>
    void Connect(SandboxConnection connection);

    /// <summary>Port of <c>update_state(state)</c>: called on every service poll with the current state.</summary>
    void UpdateState(HumanAgentState state);

    /// <summary>A command the person ran inside the sandbox (note, score, submit, quit) described in one message.</summary>
    void Activity(string message);
}

/// <summary>
/// Port of <c>ConsoleView</c>, the fallback for a non-fullscreen display: prints the login command, then one
/// status line whenever the clock starts or stops or a score is taken, each activity message, and the final
/// answer. Deviation: Python's <c>ConsoleView.update_state</c> prints nothing and the <c>HumanAgentPanel</c>
/// (Textual) and its VS Code login links are not ported; this console view stands in for both.
/// </summary>
public sealed class ConsoleHumanAgentView(TextWriter? writer = null) : IHumanAgentView
{
    private const string Prefix = "[human_agent] ";

    private readonly TextWriter _writer = writer ?? Console.Out;

    private readonly Lock _sync = new();

    private bool? _running;

    private int _scorings = -1;

    private bool _answered;

    public void Connect(SandboxConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_sync)
        {
            _writer.WriteLine(
                "You are completing a task on a Linux system (task instructions will be presented "
                + "when you login). Login to the system with the following command:\n");
            _writer.WriteLine($"{connection.Command}\n");
            _writer.Flush();
        }
    }

    public void UpdateState(HumanAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var running = state.IsRunning();
        var scorings = state.Scorings.Count;
        var answer = state.Answer;
        lock (_sync)
        {
            if (_running != running || _scorings != scorings)
            {
                _running = running;
                _scorings = scorings;
                _writer.WriteLine(Prefix + HumanAgentText.RenderStatus(state).Split('\n')[0]);
            }

            if (answer is not null && !_answered)
            {
                _answered = true;
                _writer.WriteLine(Prefix + (answer.Length == 0 ? "Task ended without an answer." : $"Final answer: {answer}"));
            }

            _writer.Flush();
        }
    }

    public void Activity(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            _writer.WriteLine(Prefix + message);
            _writer.Flush();
        }
    }
}
